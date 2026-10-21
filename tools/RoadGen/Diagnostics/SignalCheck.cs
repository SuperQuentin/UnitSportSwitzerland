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
        // (#711, the user's review) every approach has its main pole, and one with a left pocket its second head (a pole or an island's)
        int approaches = 0, noMain = 0, pocketArms = 0, noSecond = 0;
        var poleMisses = new List<string>();
        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cycles = new SortedDictionary<int, int>();
        (double Distance, string Text)? nearest = null;
        var lanes = new LaneTally();
        var paths = new PathTally();
        // two-stage crossings (#1030): arms, junctions, and the islands' first walkers waiting over a third of the cycle (reported:
        // the plan stretches to 120 s at most, past that it stays two-stage)
        int stageArms = 0, stageJunctions = 0;
        var longWaits = new List<string>();
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
            CheckPaths(tile, paths, log, list);
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
                for (int a = 0; a < p.Arms.Count; a++)
                {
                    if (!p.Arms[a].In) continue;
                    approaches++;
                    bool main = s.Poles.Any(q => q.Arm == a && (q.Flags & SignalPoleFlags.Main) != 0);
                    bool second = s.Poles.Any(q => q.Arm == a && (q.Flags & SignalPoleFlags.Second) != 0);
                    if (!main) noMain++;
                    if (p.Arms[a].LeftPocket) { pocketArms++; if (!second) noSecond++; }
                    if (!main || p.Arms[a].LeftPocket && !second)
                        poleMisses.Add(string.Create(c, $"    {where} arm {a} heading {p.Arms[a].Heading * 180 / Math.PI:F0} deg: {(main ? "" : "no main pole")}{(!main && p.Arms[a].LeftPocket && !second ? ", " : "")}{(p.Arms[a].LeftPocket && !second ? "no second head" : "")}"));
                }
                int stages = p.Groups.Count(g => g.Kind == SignalGroupKind.Pedestrian && g.Half == CrossingHalf.Arriving);
                if (stages > 0)
                {
                    stageArms += stages;
                    stageJunctions++;
                    // (a plan read from a tile has no half lengths: its walkers' lines give them, else half the road)
                    float Walk(int g) => s.ConflictLines.Where(l => l.Group == g && l.Mover == ConflictMover.Walker).Select(Length).DefaultIfEmpty(p.Arms[p.Groups[g].Arm].CrossingM * 0.5f).Max() / SignalPlan.WalkSpeed;
                    bool lines = s.ConflictLines.Any(l => l.Mover == ConflictMover.Walker);
                    foreach (var w in p.IslandWaits(Walk).Where(w => w.Wait > p.Cycle * SignalPlan.IslandWaitShare + 0.1f))
                        longWaits.Add(string.Create(c, $"    {where} arm {p.Groups[w.Group].Arm} {p.Groups[w.Group].Half}: {w.Wait:F1} s on the island, cycle {p.Cycle:F1} s{(lines ? "" : " (not timed from conflict times: halves guessed as half the road)")}"));
                }
                if (list) log(string.Create(c, $"  {where}: {shape}, {p.Arms.Count(a => a.RightPocket)} right pockets, cycle {p.Cycle:F0} s, {p.Groups.Count} groups{(stages > 0 ? $", {stages} two-stage crossings" : "")}{(p.ConflictTimed ? ", conflict-timed" : "")}"));
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
        log(string.Create(c, $"  two-stage crossings (#1030): {stageArms} at {stageJunctions} junctions; island waits over a third of the cycle {longWaits.Count}"));
        foreach (var wait in longWaits.Take(list ? int.MaxValue : 10)) log(wait);
        if (nearest is { } near) log(near.Text);
        log(string.Create(c, $"  lane records (#353): {lanes.Approaches} approaches ({lanes.Signalised} signalised, every signalised approach has one: {lanes.Missing == 0}), {lanes.Lanes} lanes, banned turns on {lanes.Banned}, bad {lanes.Bad}"));
        foreach (var (_, text) in nearLanes.OrderBy(x => x.Distance).Take(8)) log(text);
        log(string.Create(c, $"  poles (#350, #711): {approaches} approaches, {noMain} without a main pole; {pocketArms} with a left pocket, {noSecond} without its second head"));
        foreach (var miss in poleMisses.Take(list ? int.MaxValue : 30)) log(miss);
        log(string.Create(c, $"  junction paths (#818, JPTH): {paths.Paths} on {paths.Cores} cores; ends off their lane {paths.OffLane}, points off the core {paths.OffCore}"));
        bool ok = invalid == 0 && lanes.Bad == 0 && lanes.Missing == 0 && noMain == 0 && noSecond == 0 && paths.OffLane == 0 && paths.OffCore == 0;
        log(ok ? "[signalcheck] RESULT: ok"
            : $"[signalcheck] RESULT: FAILED {invalid} invalid plans, {lanes.Bad} bad lane records, {lanes.Missing} signalised approaches without one, {noMain} approaches without a main pole, {noSecond} without a second head, {paths.OffLane + paths.OffCore} bad junction paths");
        return ok ? 0 : 2;
    }

    /// <summary>A conflict line's length (m), its points tile-local x, y, z.</summary>
    private static float Length(RoadConflictLine l)
    {
        float sum = 0;
        for (int i = 3; i + 2 < l.Points.Length; i += 3)
            sum += MathF.Sqrt(MathF.Pow(l.Points[i] - l.Points[i - 3], 2) + MathF.Pow(l.Points[i + 2] - l.Points[i - 1], 2));
        return sum;
    }

    private sealed class LaneTally { public int Approaches, Signalised, Lanes, Banned, Bad, Missing; }

    private sealed class PathTally { public int Paths, Cores, OffLane, OffCore; }

    /// <summary>A path's end this far from its lane's centre at the mouth (m), or a point this far off its core, is wrong.</summary>
    private const float PathEndTolerance = 0.3f, PathCoreTolerance = 0.5f;

    /// <summary>
    /// The junction paths of a tile (#818 phase 5, <c>JPTH</c>): each starts and ends on its lane where its road meets the mouth
    /// (the end's point and offset; on a road of the tile ending there, the offset on a lane: a lane strip of a transition piece,
    /// within the carriageway of a plain segment), and every point lies on its core's cap.
    /// </summary>
    private static void CheckPaths(RoadTile tile, PathTally t, Action<string> log, bool list)
    {
        var c = CultureInfo.InvariantCulture;
        t.Cores += tile.JunctionPaths.Select(p => p.Junction).Distinct().Count();
        foreach (var p in tile.JunctionPaths)
        {
            // riders from path to path (#971) run round the core from path to path: no lane at their ends, not on the cap
            if (p.Kind == JunctionPathKind.Path) continue;
            t.Paths++;
            int n = p.PointCount;
            string? bad = null;
            foreach (var (end, at) in new[] { (p.From, 0), (p.To, n - 1) })
            {
                int a0 = at == 0 ? 0 : n - 1, a1 = at == 0 ? 1 : n - 2;
                // the road ending there in this tile, and the arm's way toward the junction along it (else along the path's first
                // step inward, which a turn curving from the mouth tilts)
                // (only a road arriving from outside: one whose way toward its end agrees with the path's, not a piece under the core)
                float px = p.Points[a1 * 3] - p.Points[a0 * 3], pz = p.Points[a1 * 3 + 2] - p.Points[a0 * 3 + 2];
                RoadSegment? road = null;
                int k = -1;
                float dx = px, dz = pz;
                foreach (var seg in tile.Segments)
                {
                    var v = seg.Points;
                    if (seg.PointCount < 2) continue;
                    foreach (int j in new[] { seg.PointCount - 1, 0 })
                    {
                        if (MathF.Abs(v[j * 3] - end.X) >= 1f || MathF.Abs(v[j * 3 + 2] - end.Z) >= 1f) continue;
                        int j2 = j == 0 ? 1 : j - 1;
                        float ux = v[j * 3] - v[j2 * 3], uz = v[j * 3 + 2] - v[j2 * 3 + 2];   // from inside the road out to its end
                        if (ux * px + uz * pz <= 0) continue;
                        (road, k, dx, dz) = (seg, j, ux, uz);
                        break;
                    }
                    if (road is not null) break;
                }
                float len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 1e-6f) continue;
                (dx, dz) = (dx / len, dz / len);
                // right of a driver heading (dx, dz) toward the junction (tile-local x east, z south): (-dz, dx)
                float ex = end.X - dz * end.Offset, ez = end.Z + dx * end.Offset;
                float gap = MathF.Sqrt((p.Points[a0 * 3] - ex) * (p.Points[a0 * 3] - ex) + (p.Points[a0 * 3 + 2] - ez) * (p.Points[a0 * 3 + 2] - ez));
                bool onLane = true;
                if (road is not null)
                {
                    // the offset across the road's drawing: drawn toward the junction (its last point there) keeps its sign
                    float o = k == road.PointCount - 1 ? end.Offset : -end.Offset;
                    if (tile.Transitions.FirstOrDefault(x => ReferenceEquals(x.Segment, road)) is { } tr)
                    {
                        double s = k == 0 ? 0 : tr.Stations[^1];
                        onLane = Enumerable.Range(0, tr.Strips.Count).Any(i => tr.Strips[i].Kind is StripKind.CarLane or StripKind.BikeLane
                            && tr.SpanAt(i, s) is var span && o > span.From - 0.05f && o < span.To + 0.05f);
                    }
                    else onLane = MathF.Abs(o) <= road.Width * 0.5f + 0.05f;
                }
                if (gap > PathEndTolerance || !onLane)
                {
                    t.OffLane++;
                    bad ??= string.Create(c, $"end {(at == 0 ? "from" : "to")} {gap:F2} m from its lane{(onLane ? "" : ", not on a lane")} (offset {end.Offset:F2}{(road is null ? ", road in another tile" : "")})");
                }
            }
            for (int i = 0; i < n; i++)
                if (DistanceToCap(p.Junction, p.Points[i * 3], p.Points[i * 3 + 2]) > PathCoreTolerance)
                {
                    t.OffCore++;
                    bad ??= "a point off its core";
                    break;
                }
            if (bad is not null && (list || t.OffLane + t.OffCore <= 20))
                log(string.Create(c, $"  junction path at LV95 {tile.Id.MinE + p.Points[0]:F1},{tile.Id.MaxN - p.Points[2]:F1} ({p.Move}, {p.Kind}): {bad}"));
        }
    }

    /// <summary>How far a tile-local point lies from a cap's triangles (0 on it).</summary>
    private static float DistanceToCap(RoadJunction cap, float x, float z)
    {
        var v = cap.Vertices;
        float best = float.MaxValue;
        for (int t = 0; t + 2 < cap.Indices.Length; t += 3)
        {
            int a = cap.Indices[t] * 3, b = cap.Indices[t + 1] * 3, cc = cap.Indices[t + 2] * 3;
            float d = (v[b + 2] - v[cc + 2]) * (v[a] - v[cc]) + (v[cc] - v[b]) * (v[a + 2] - v[cc + 2]);
            if (MathF.Abs(d) > 1e-9f)
            {
                float wa = ((v[b + 2] - v[cc + 2]) * (x - v[cc]) + (v[cc] - v[b]) * (z - v[cc + 2])) / d;
                float wb = ((v[cc + 2] - v[a + 2]) * (x - v[cc]) + (v[a] - v[cc]) * (z - v[cc + 2])) / d;
                if (wa >= -1e-4f && wb >= -1e-4f && 1 - wa - wb >= -1e-4f) return 0;
            }
            foreach (var (i, j) in new[] { (a, b), (b, cc), (cc, a) })
            {
                float ex = v[j] - v[i], ez = v[j + 2] - v[i + 2], l2 = ex * ex + ez * ez;
                float u = l2 < 1e-12f ? 0 : Math.Clamp(((x - v[i]) * ex + (z - v[i + 2]) * ez) / l2, 0, 1);
                float dx = v[i] + ex * u - x, dz = v[i + 2] + ez * u - z;
                best = MathF.Min(best, MathF.Sqrt(dx * dx + dz * dz));
            }
        }
        return best;
    }

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
