namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// <c>RoadGen --signal-check --chunks DIR [--at E,N]</c> (#349): every signal plan of a built
/// region, validated (<see cref="SignalPlan.Validate"/>), with counts by shape and cycle, the
/// plans that look wrong (a single phase, a cycle off the targets), and the full plan of the
/// junction nearest a point. Exit 2 when a plan is invalid.
/// </summary>
public static class SignalCheck
{
    public static int Run(string chunkDir, string? at, Action<string> log, bool list = false)
    {
        var c = CultureInfo.InvariantCulture;
        int tiles = 0, junctions = 0, invalid = 0;
        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cycles = new SortedDictionary<int, int>();
        (double Distance, string Text)? nearest = null;
        double atE = 0, atN = 0;
        if (at is not null)
        {
            var en = at.Split(',');
            atE = double.Parse(en[0], c);
            atN = double.Parse(en[1], c);
        }

        foreach (string path in Directory.EnumerateFiles(chunkDir, "roads_*.road").Order(StringComparer.Ordinal))
        {
            RoadTile tile;
            using (var stream = File.OpenRead(path)) tile = RoadCodec.Decode(stream);
            if (tile.Signals.Count == 0) continue;
            tiles++;
            foreach (var s in tile.Signals)
            {
                junctions++;
                var p = s.Plan;
                double e = tile.Id.MinE + s.X, n = tile.Id.MaxN - s.Z;
                string where = string.Create(c, $"LV95 {e:F0},{n:F0}");
                string shape = string.Create(c, $"{p.Arms.Count} arms, {p.Arms.Count(a => a.In)} in, {p.Arms.Count(a => a.LeftPocket)} left pockets");
                shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                int cycle = (int)MathF.Round(p.Cycle);
                cycles[cycle] = cycles.GetValueOrDefault(cycle) + 1;
                var errors = p.Validate();
                if (errors.Count > 0)
                {
                    invalid++;
                    log($"  INVALID {where}: {string.Join("; ", errors.Take(3))}");
                }
                if (p.Cycle < 30) log($"  odd: {where} cycle {p.Cycle:F1} s ({shape})");
                if (list) log(string.Create(c, $"  {where}: {shape}, {p.Arms.Count(a => a.RightPocket)} right pockets, cycle {p.Cycle:F0} s, {p.Groups.Count} groups"));
                if (at is not null)
                {
                    double d = Math.Sqrt((e - atE) * (e - atE) + (n - atN) * (n - atN));
                    if (nearest is null || d < nearest.Value.Distance)
                    {
                        string arms = string.Join("\n", p.Arms.Select((a, i) => string.Create(c,
                            $"    arm {i}: heading {a.Heading * 180 / Math.PI:F0} deg, in {a.In}, out {a.Out}, left pocket {a.LeftPocket}, right pocket {a.RightPocket}, peds {a.Pedestrians}, {a.SpeedKmh:F0} km/h, crossing {a.CrossingM:F1} m, rank {a.Rank}")));
                        var tileId = tile.Id;
                        string poles = string.Join("\n", s.Poles.Select(q => string.Create(c,
                            $"    pole arm {q.Arm} {q.Flags}: LV95 {tileId.MinE + q.X:F1},{tileId.MaxN - q.Z:F1} y {q.Y:F2}, cars face {q.CarHeading * 180 / Math.PI:F0} deg, pedestrians {q.PedHeading * 180 / Math.PI:F0} deg")));
                        nearest = (d, $"  nearest to {at}: {where} ({d:F0} m away), {shape}\n{arms}\n{poles}\n{p.Describe()}");
                    }
                }
            }
        }

        log(string.Create(c, $"signal check: {junctions} signalised junctions on {tiles} tiles, invalid {invalid}"));
        log("  shapes: " + string.Join(", ", shapes.Select(kv => $"{kv.Key} x{kv.Value}")));
        log("  cycles s: " + string.Join(", ", cycles.Select(kv => $"{kv.Key} x{kv.Value}")));
        if (nearest is { } near) log(near.Text);
        log(invalid == 0 ? "[signalcheck] RESULT: ok" : $"[signalcheck] RESULT: FAILED {invalid} invalid plans");
        return invalid == 0 ? 0 : 2;
    }
}
