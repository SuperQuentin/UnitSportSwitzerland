using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck --at E,N</c> in a world with its own endpoints (#1134, the test region's): no endpoints of the probe's, the
/// world's run. At the end, per junction whose generators made anything: per mode what each arm's (corner's, tram line's)
/// generator made, where its movers arrived (by turn for cars and riders), what still waits in its queue; per approach of the
/// junction under test (the one whose cars' endpoints centre nearest <c>--at</c>, or <c>--junction NAME</c>) the longest queue and
/// the shortest in each cycle (a queue that never clears grows from cycle to cycle); movers dropped as stuck; red runs. Fails on a
/// red run (any mode), a junction under test whose cars arrive nowhere, or a queue that stayed over <see cref="GrowingQueue"/> cars
/// through each of the last two cycles.
/// </summary>
public partial class TrafficProbe
{
    /// <summary>Standing cars an approach keeps through a whole cycle, in each of the last two, that fails the run: a queue that does not clear.</summary>
    private const int GrowingQueue = 4;
    /// <summary>The queue counts from here (s): the first cars have reached the lights.</summary>
    private const double QueueFrom = 30;

    private string? _junction;
    private readonly Dictionary<LaneApproach, (int Max, List<int> CycleMins, int Now, double CycleStart)> _queues = new();
    private LaneGraph? _queuesOf;

    /// <summary>Whether the world's endpoints run instead of the probe's (#1134).</summary>
    private bool WorldEndpoints => _stay && _traffic.GeneratorsOnly && _traffic.WorldEndpoints.Count > 0 && _feedTurns is null;

    /// <summary>Every frame with the world's endpoints: the junction under test chosen once, its approaches' queues sampled twice a second.</summary>
    private void WorldTick(double delta)
    {
        if (Origin is not { } origin || _traffic.Roads is not { } roads) return;
        if (_junction is null)
        {
            var (e, n) = origin.ToLv95(_spot);
            _junction = CmdArgs.Value("--junction") ?? _traffic.WorldEndpoints.Where(x => x.Mode == MoverKind.Car)
                .GroupBy(x => _traffic.EndpointLabels[x.Id].Junction)
                .MinBy(g => Math.Pow(g.Average(x => x.E) - e, 2) + Math.Pow(g.Average(x => x.N) - n, 2))?.Key ?? "";
            GD.Print($"[trafficcheck] the world's endpoints (#1134): {_traffic.WorldEndpoints.Count}, the junction under test {_junction}");
        }
        if (_t < QueueFrom || (int)(_t * 2) == (int)((_t - delta) * 2)) return;
        if (_queuesOf != roads)
        {
            _queuesOf = roads;
            _queues.Clear();
            foreach (var a in roads.Approaches.Where(a => a.Site is not null && MathX.Flat(a.Stop - _spot).Length() < 60f))
                _queues[a] = (0, new List<int>(), int.MaxValue, _t);
        }
        foreach (var a in _queues.Keys.ToList())
        {
            var (max, mins, now, start) = _queues[a];
            int standing = _traffic.Standing(a, 200f);
            max = Math.Max(max, standing);
            now = Math.Min(now, standing);
            if (_t - start >= a.Site!.Plan.Cycle)
            {
                mins.Add(now);
                (now, start) = (int.MaxValue, _t);
            }
            _queues[a] = (max, mins, now, start);
        }
    }

    /// <summary>The world endpoints' report at the end (#1134); a failure, or null.</summary>
    private string? WorldReport()
    {
        string? failure = null;
        var labels = _traffic.EndpointLabels;
        var byId = _traffic.WorldEndpoints.ToDictionary(x => x.Id);
        foreach (var junction in _traffic.WorldEndpoints.Where(x => x.Generates && _traffic.GeneratedBy.ContainsKey(x.Id))
                     .Select(x => labels[x.Id].Junction).Distinct().OrderBy(j => j != _junction).ThenBy(j => j, StringComparer.Ordinal))
        {
            foreach (var mode in (ReadOnlySpan<MoverKind>)[MoverKind.Car, MoverKind.Rider, MoverKind.Walker, MoverKind.Tram])
            {
                var gens = _traffic.WorldEndpoints.Where(x => x.Mode == mode && x.Generates && labels[x.Id].Junction == junction).ToList();
                if (gens.Count == 0) continue;
                int made = 0, arrived = 0, waiting = 0;
                var arms = new List<string>();
                foreach (var g in gens)
                {
                    int m = _traffic.GeneratedBy.GetValueOrDefault(g.Id), q = _traffic.QueuedAt(g.Id);
                    var to = _traffic.ArrivedBy.Where(x => x.Key.From == g.Id).ToList();
                    int a = to.Sum(x => x.Value);
                    (made, arrived, waiting) = (made + m, arrived + a, waiting + q);
                    string where = mode is MoverKind.Car or MoverKind.Rider
                        ? string.Join(" ", to.GroupBy(x => byId.TryGetValue(x.Key.To, out var d) && labels[d.Id].Junction == junction
                                ? "LTR"[TurnIndex(g.Heading, d.Heading)].ToString() : byId.TryGetValue(x.Key.To, out var o) ? $"{labels[o.Id].Junction}:{labels[o.Id].Arm}" : "?")
                            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Sum(y => y.Value)}"))
                        : string.Join(" ", to.Select(x => $"{(byId.TryGetValue(x.Key.To, out var d) ? labels[d.Id].Arm : "?")} {x.Value}"));
                    arms.Add($"{labels[g.Id].Arm}{(_traffic.EndpointPlaced(g.Id) ? "" : " (on no way)")} {g.PerMinute * _traffic.GeneratorRate:0.0}/min: made {m}, arrived {a}{(where.Length > 0 ? $" ({where})" : "")}{(q > 0 ? $", {q} waiting" : "")}");
                }
                GD.Print($"[trafficcheck] {junction} {mode.ToString().ToLowerInvariant()}s: made {made}, arrived {arrived}, waiting {waiting}; {string.Join("; ", arms)}");
                if (junction == _junction && mode == MoverKind.Car && made > 0 && arrived == 0)
                    failure ??= $"{junction}: {made} cars made, none arrived";
            }
        }
        if (Origin is { } origin)
            foreach (var (a, (max, mins, _, _)) in _queues.OrderBy(x => x.Key.Arm))
            {
                var (e, n) = origin.ToLv95(a.Stop);
                bool growing = mins.Count >= 2 && mins[^1] > GrowingQueue && mins[^2] > GrowingQueue;
                GD.Print($"[trafficcheck] {_junction} queue at {e:F0},{n:F0} (arm {Mathf.RadToDeg(Mathf.Atan2(-a.Out.Z, a.Out.X)):F0} deg, cycle {a.Site!.Plan.Cycle:F0} s): "
                    + $"longest {max} standing, shortest in each cycle [{string.Join(" ", mins)}]{(growing ? " NOT CLEARING" : "")}");
                if (growing) failure ??= $"{_junction}: the queue at {e:F0},{n:F0} kept over {GrowingQueue} cars through its last two cycles";
            }
        var stuck = _traffic.StuckDropped;
        int bikeRuns = _traffic.Riders.RedRuns, walkRuns = _traffic.WalkerRedStarts, tramRuns = _traffic.TramRedRuns;
        GD.Print($"[trafficcheck] world endpoints: dropped as stuck cars {stuck[(int)MoverKind.Car]}, riders {stuck[(int)MoverKind.Rider]}, walkers {stuck[(int)MoverKind.Walker]}, "
            + $"trams {stuck[(int)MoverKind.Tram]}; red runs cars {_traffic.RedRuns}, riders {bikeRuns}, walkers starting on red {walkRuns}, trams {tramRuns}");
        if (bikeRuns + walkRuns + tramRuns > 0) failure ??= $"red runs: riders {bikeRuns}, walkers {walkRuns}, trams {tramRuns}";
        return failure;
    }
}
