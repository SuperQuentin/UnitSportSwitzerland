using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck</c>'s part for the traffic modes (#1060, tracking #1058): <c>--mover walker|rider|tram</c> (also car, train)
/// chases one of that kind from 20 s and fails unless that kind spawned and moved; with any mode on (its density, or
/// <c>--mover</c>) every 5 s a line of walkers, riders and trams, their speeds and how many the cars see. Each mode adds its own
/// counts and rules in a file of its own (<c>TrafficProbe.Walkers.cs</c>, <c>.Riders.cs</c>, <c>.Trams.cs</c>) by implementing
/// its two partial methods below: one called every frame, one at the end that may fail the run. Unimplemented, they are nothing.
/// </summary>
public partial class TrafficProbe
{
    /// <summary>The kind <c>--mover</c> names: chased and judged; null without it.</summary>
    private readonly MoverKind? _kind = MoverKinds.Parse(CmdArgs.Value("--mover"));

    /// <summary>Per kind (<see cref="MoverKind"/>): the most live at once, and the highest average speed seen (m/s).</summary>
    private readonly int[] _peakOf = new int[5];
    private readonly float[] _speedOf = new float[5];

    /// <summary>Walkers' own per-frame counting (#1033, <c>TrafficProbe.Walkers.cs</c>).</summary>
    partial void WalkersTick(double delta);
    /// <summary>Walkers' own report at the end: print it, and set <paramref name="failure"/> to fail the run.</summary>
    partial void WalkersReport(ref string? failure);
    /// <summary>Riders' own per-frame counting (#1056, <c>TrafficProbe.Riders.cs</c>).</summary>
    partial void RidersTick(double delta);
    /// <summary>Riders' own report at the end: print it, and set <paramref name="failure"/> to fail the run.</summary>
    partial void RidersReport(ref string? failure);
    /// <summary>Trams' own per-frame counting (#1057, <c>TrafficProbe.Trams.cs</c>).</summary>
    partial void TramsTick(double delta);
    /// <summary>Trams' own report at the end: print it, and set <paramref name="failure"/> to fail the run.</summary>
    partial void TramsReport(ref string? failure);

    /// <summary>Whether any mode runs: a density asks for it, or <c>--mover</c> names one.</summary>
    private bool ModesOn
    {
        get
        {
            var s = GameSettings.Current;
            return _kind is MoverKind.Walker or MoverKind.Rider or MoverKind.Tram || s.TrafficWalkers > 0 || s.TrafficRiders > 0 || s.Trams > 0;
        }
    }

    private void ModesTick(double delta)
    {
        foreach (var kind in (ReadOnlySpan<MoverKind>)[MoverKind.Car, MoverKind.Train, MoverKind.Walker, MoverKind.Rider, MoverKind.Tram])
        {
            _peakOf[(int)kind] = Math.Max(_peakOf[(int)kind], _traffic.CountOf(kind));
            _speedOf[(int)kind] = Mathf.Max(_speedOf[(int)kind], _traffic.AverageSpeedOf(kind));
        }
        WalkersTick(delta);
        RidersTick(delta);
        TramsTick(delta);
        if (ModesOn && (int)(_t / 5) != (int)((_t - delta) / 5))
            GD.Print($"[trafficcheck] t={_t:F0}s modes: walkers {_traffic.CountOf(MoverKind.Walker)} (avg {_traffic.AverageSpeedOf(MoverKind.Walker) * 3.6f:F1} km/h, "
                + $"graph {_traffic.GraphOf(MoverKind.Walker)?.Edges.Count ?? 0} edges), riders {_traffic.CountOf(MoverKind.Rider)} (avg {_traffic.AverageSpeedOf(MoverKind.Rider) * 3.6f:F1} km/h, "
                + $"graph {_traffic.GraphOf(MoverKind.Rider)?.Edges.Count ?? 0} edges), trams {_traffic.CountOf(MoverKind.Tram)} (avg {_traffic.AverageSpeedOf(MoverKind.Tram) * 3.6f:F0} km/h, "
                + $"graph {_traffic.GraphOf(MoverKind.Tram)?.Edges.Count ?? 0} edges); seen by the cars: {_traffic.CrossersOf(MoverKind.Walker)} walkers, "
                + $"{_traffic.CrossersOf(MoverKind.Rider)} riders, {_traffic.CrossersOf(MoverKind.Tram)} tram sections");
    }

    /// <summary>The modes' end of the run: each mode's report, then whether the <c>--mover</c> kind spawned and moved. Null: passed.</summary>
    private string? ModesReport()
    {
        string? failure = null;
        WalkersReport(ref failure);
        RidersReport(ref failure);
        TramsReport(ref failure);
        if (ModesOn)
            GD.Print($"[trafficcheck] modes: peak walkers {_peakOf[(int)MoverKind.Walker]}, riders {_peakOf[(int)MoverKind.Rider]}, trams {_peakOf[(int)MoverKind.Tram]}");
        if (failure is null && _kind is { } kind && (_peakOf[(int)kind] == 0 || _speedOf[(int)kind] < 0.3f))
            failure = _peakOf[(int)kind] == 0
                ? $"no {kind.ToString().ToLowerInvariant()} spawned (its graph: {_traffic.GraphOf(kind)?.Edges.Count ?? 0} edges)"
                : $"no moving {kind.ToString().ToLowerInvariant()} (peak {_peakOf[(int)kind]})";
        return failure;
    }

    /// <summary>The modes' part of the RESULT line: empty without them, so the cars' line stays as it was.</summary>
    private string ModesSummary() => !ModesOn ? ""
        : $", peak {_peakOf[(int)MoverKind.Walker]} walkers, {_peakOf[(int)MoverKind.Rider]} riders, {_peakOf[(int)MoverKind.Tram]} trams";
}
