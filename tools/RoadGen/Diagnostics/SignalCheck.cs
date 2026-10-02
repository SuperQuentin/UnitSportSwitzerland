namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// <c>RoadGen --signal-check --chunks DIR [--at E,N]</c> (#349): every signal plan of a built
/// region, validated (<see cref="SignalPlan.Validate"/>), with counts by shape and cycle, the
/// plans that look wrong (a single phase, a cycle off the targets), and the full plan of the
/// junction nearest a point; the lane records (#353, <c>LANE</c>) checked, and those near the point listed.
/// Exit 2 when a plan or a lane record is invalid.
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
        var lanes = new LaneTally();
        var nearLanes = new List<(double Distance, string Text)>();   // lane records within 60 m of --at
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
            CheckLanes(tile, lanes, log, list);
            if (at is not null)
                foreach (var a in tile.Approaches)
                {
                    double e = tile.Id.MinE + a.X, n = tile.Id.MaxN - a.Z, d = Math.Sqrt((e - atE) * (e - atE) + (n - atN) * (n - atN));
                    if (d < 60) nearLanes.Add((d, Describe(a, e, n)));
                }
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
        log(string.Create(c, $"  lane records (#353): {lanes.Approaches} approaches ({lanes.Signalised} signalised, every signalised approach has one: {lanes.Missing == 0}), {lanes.Lanes} lanes, banned turns on {lanes.Banned}, bad {lanes.Bad}"));
        foreach (var (_, text) in nearLanes.OrderBy(x => x.Distance).Take(8)) log(text);
        bool ok = invalid == 0 && lanes.Bad == 0 && lanes.Missing == 0;
        log(ok ? "[signalcheck] RESULT: ok"
            : $"[signalcheck] RESULT: FAILED {invalid} invalid plans, {lanes.Bad} bad lane records, {lanes.Missing} signalised approaches without one");
        return ok ? 0 : 2;
    }

    private sealed class LaneTally { public int Approaches, Signalised, Lanes, Banned, Bad, Missing; }

    /// <summary>
    /// The lane records (#353) of a tile: a signalised one points at an approach of a junction of
    /// the tile and stands at its stop line; lanes left to right, a car lane among them, every lane
    /// full before the line and opening no later than it is full, a movement each; a pocket in the
    /// plan has its lane. And every signalised approach has exactly one record.
    /// </summary>
    private static void CheckLanes(RoadTile tile, LaneTally t, Action<string> log, bool list)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var a in tile.Approaches)
        {
            t.Approaches++;
            t.Lanes += a.Lanes.Count;
            if (a.Banned != 0) t.Banned++;
            if (list && a.Lanes.Count(l => l.Kind == ApproachLaneKind.Car) > 1)
                log(string.Create(CultureInfo.InvariantCulture, $"  pockets LV95 {tile.Id.MinE + a.X:F0},{tile.Id.MaxN - a.Z:F0} {(a.Signal >= 0 ? "lights" : "no lights")}: {string.Join(" | ", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l => l.Moves))}, banned {a.Banned}"));
            var why = new List<string>();
            if (a.Signal >= 0)
            {
                t.Signalised++;
                if (a.Signal >= tile.Signals.Count || a.SignalArm >= tile.Signals[a.Signal].Plan.Arms.Count) why.Add("no such signal arm");
                else
                {
                    var s = tile.Signals[a.Signal];
                    var arm = s.Plan.Arms[a.SignalArm];
                    if (!arm.In) why.Add("arm has no approach");
                    if (s.Stops[a.SignalArm * 3] != a.X || s.Stops[a.SignalArm * 3 + 2] != a.Z) why.Add("not at the stop line");
                    if (!seen.Add((a.Signal, a.SignalArm))) why.Add("second record");
                    if (arm.LeftPocket && !a.Lanes.Any(l => l.Kind == ApproachLaneKind.Car && l.Moves == SignalMoves.Left)) why.Add("left pocket without its lane");
                    if (arm.RightPocket && !a.Lanes.Any(l => l.Kind == ApproachLaneKind.Car && l.Moves == SignalMoves.Right)) why.Add("right pocket without its lane");
                }
            }
            if (!a.Lanes.Any(l => l.Kind == ApproachLaneKind.Car)) why.Add("no car lane");
            for (int i = 0; i < a.Lanes.Count; i++)
            {
                var l = a.Lanes[i];
                if (i > 0 && l.Offset <= a.Lanes[i - 1].Offset) why.Add($"lane {i} not right of lane {i - 1}");
                if (l.FullFrom < 0 || l.TaperFrom < l.FullFrom || float.IsNaN(l.Offset)) why.Add($"lane {i} distances");
                if (l.Moves == 0) why.Add($"lane {i} no movement");
            }
            if (why.Count == 0) continue;
            t.Bad++;
            if (t.Bad <= 10)
                log(string.Create(CultureInfo.InvariantCulture, $"  BAD lane record LV95 {tile.Id.MinE + a.X:F0},{tile.Id.MaxN - a.Z:F0}: {string.Join("; ", why)}"));
        }
        for (int k = 0; k < tile.Signals.Count; k++)
            for (int i = 0; i < tile.Signals[k].Plan.Arms.Count; i++)
                if (tile.Signals[k].Plan.Arms[i].In && !float.IsNaN(tile.Signals[k].Stops[i * 3]) && !seen.Contains((k, i))) t.Missing++;
    }

    private static string Describe(RoadApproach a, double e, double n)
    {
        var c = CultureInfo.InvariantCulture;
        string lanes = string.Join("\n", a.Lanes.Select(l => string.Create(c,
            $"      {l.Kind} {l.Moves}: offset {l.Offset:+0.00;-0.00} m, full from {l.FullFrom:F1} m, opens {l.TaperFrom:F1} m before the line{(l.StopBehind != 0 ? $", stops {l.StopBehind:+0.0;-0.0} m behind it" : "")}")));
        return string.Create(c, $"    approach LV95 {e:F1},{n:F1} heading {a.Heading * 180 / Math.PI:F0} deg, {(a.Signal >= 0 ? $"signal {a.Signal} arm {a.SignalArm}" : "no lights")}, banned {a.Banned}, lane centre {a.LaneCentre:F2} m\n{lanes}");
    }
}
