using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>godot --headless --path . -- --lanequery[,out.txt] --chunks D:/.../terrain_chunks [--at E,N --to E,N] [--around E,N[,R]]</c>
/// (#1758, #1760): traffic's car lane graph read straight off the LV95 road tiles, no window, no world, a few hundred MB
/// (the long drive needs the whole streamed world to find the same). With <c>--at</c>/<c>--to</c>: the long drive's route
/// (<see cref="RaceRoute.ToAsync"/>, its log: where a way that keeps every one-way ends). With <c>--around</c>: every car
/// edge within R m (default 150) of a place, its class, flags and one-way, and each traffic island and signal pole there,
/// with the lane lines (<see cref="Traffic.KeepRight"/>, the way the long drive and traffic's cars keep) that run into an
/// island. Writes to the outfile (default <c>test_output/lanequery.txt</c>) and the log.
/// </summary>
public static class LaneQuery
{
    public static bool Requested => Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--lanequery", StringComparison.Ordinal));

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static (double E, double N, double R)? Place(string? v, double r)
    {
        if (v?.Split(',') is not { Length: >= 2 } p) return null;
        double P(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
        return (P(0), P(1), p.Length > 2 ? P(2) : r);
    }

    public static int Run()
    {
        string arg = OS.GetCmdlineUserArgs().First(a => a.StartsWith("--lanequery", StringComparison.Ordinal));
        string outPath = arg.Contains(',') ? arg[(arg.IndexOf(',') + 1)..] : "test_output/lanequery.txt";
        var src = new LocalChunkSource(CmdArgs.Value("--chunks") ?? "terrain_chunks");
        var sb = new StringBuilder();
        void Log(string s) { GD.Print("[lanequery] " + s); lock (sb) sb.AppendLine(s); }
        bool ok = true;
        List<(double E, double N)>? routeLv = null;

        if (Place(CmdArgs.Value("--at"), 0) is { } at && Place(CmdArgs.Value("--to"), 0) is { } to)
        {
            var frame = OriginFrame.Anchor(Math.Round(at.E), Math.Round(at.N));
            var a = frame.ToWorld(at.E, at.N, 0);
            var b = frame.ToWorld(to.E, to.N, 0);
            // the start's height: the nearest road point's, as the drive starts on the ground there
            var tiles0 = new List<RoadTile>();
            if (src.LoadRoadsAsync(TileId.FromLv95(at.E, at.N)).GetAwaiter().GetResult() is { } t0) tiles0.Add(t0);
            var g0 = LaneGraph.Build(tiles0, frame, Traffic.IsCarRoad);
            float y = g0.Edges.SelectMany(e => e.Points).DefaultIfEmpty(a)
                .MinBy(p => new Vector2(p.X - a.X, p.Z - a.Z).LengthSquared()).Y;
            // on a worker: its awaits would wait for this (the main) thread otherwise
            var route = Task.Run(() => RaceRoute.ToAsync(src, null, frame, a with { Y = y }, b with { Y = y }, 2000, Log)).GetAwaiter().GetResult();
            if (route == null) { Log("no route"); ok = false; }
            else
            {
                routeLv = route.Centre.Select(p => frame.ToLv95(p)).ToList();
                var (ee, en) = frame.ToLv95(route.Centre[^1]);
                Log(Inv($"route: {route.Length / 1000:F2} km, ends at LV95 {ee:F0}/{en:F0}, {new Vector2(route.Centre[^1].X - b.X, route.Centre[^1].Z - b.Z).Length():F0} m from the destination"));
            }
        }

        if (Place(CmdArgs.Value("--around"), 150) is { } around) Around(src, around, Log, routeLv);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
        System.IO.File.WriteAllText(outPath, sb.ToString());
        GD.Print($"[lanequery] RESULT: {(ok ? "ok" : "FAILED")} (written to {outPath})");
        return ok ? 0 : 1;
    }

    private static bool InTri(Vector2 q, Vector2[] t)
    {
        float S(Vector2 a, Vector2 b) => (b.X - a.X) * (q.Y - a.Y) - (b.Y - a.Y) * (q.X - a.X);
        float d0 = S(t[0], t[1]), d1 = S(t[1], t[2]), d2 = S(t[2], t[0]);
        return !((d0 < 0 || d1 < 0 || d2 < 0) && (d0 > 0 || d1 > 0 || d2 > 0));
    }

    private static void Around(LocalChunkSource src, (double E, double N, double R) at, Action<string> log,
        List<(double E, double N)>? routeLv = null)
    {
        var frame = OriginFrame.Anchor(Math.Round(at.E), Math.Round(at.N));
        var c = frame.ToWorld(at.E, at.N, 0);
        var tiles = new List<RoadTile>();
        var home = TileId.FromLv95(at.E, at.N);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (src.LoadRoadsAsync(home.Neighbour(dx, dz)).GetAwaiter().GetResult() is { } t) tiles.Add(t);
        var graph = LaneGraph.Build(tiles, frame, Traffic.IsCarRoad);
        graph.JoinTrimmedEnds();
        float Dist(Vector3 p) => new Vector2(p.X - c.X, p.Z - c.Z).Length();
        string Lv(Vector3 p) { var (e, n) = frame.ToLv95(p); return Inv($"{e:F1}/{n:F1}"); }
        string Head(Vector3 d) => Inv($"{(Mathf.RadToDeg(Mathf.Atan2(d.X, -d.Z)) + 360) % 360:F0}°");

        // the islands (tile-local vertices) and the signal poles within R
        var islands = new List<(Vector2[] Tri, string Name)>();
        foreach (var t in tiles)
        {
            Vector3 W(float x, float y, float z) => frame.TileToWorld(t.Id, new Vector3(x, y, z));
            for (int k = 0; k < t.AreaProps.Count; k++)
            {
                var p = t.AreaProps[k];
                if (p.Type != AreaPropType.Island) continue;
                var vs = Enumerable.Range(0, p.Vertices.Length / 3).Select(i => W(p.Vertices[3 * i], p.Vertices[3 * i + 1], p.Vertices[3 * i + 2])).ToArray();
                if (vs.Length == 0 || vs.Min(Dist) > at.R) continue;
                var mid = vs.Aggregate(Vector3.Zero, (s, v) => s + v) / vs.Length;
                string name = Inv($"island {t.Id.E}_{t.Id.N}#{k} v{p.Variant} at {Lv(mid)}");
                log(Inv($"{name}: {vs.Length} vertices, {p.Height:F2} m"));
                for (int i = 0; i + 2 < p.Indices.Length; i += 3)
                {
                    var tri = new[] { vs[p.Indices[i]], vs[p.Indices[i + 1]], vs[p.Indices[i + 2]] }.Select(v => new Vector2(v.X, v.Z)).ToArray();
                    islands.Add((tri, name));
                }
            }
            // the car segments' own records: what RoadGen stored (one-way, flags), before the lane graph reads them
            if (CmdArgs.Has("--segments"))
                foreach (var sg in t.Segments)
                {
                    if (!Traffic.IsCarRoad(sg) || sg.PointCount < 2) continue;
                    var (e0, n0) = sg.Lv95(t.Id, 0);
                    var (e1, n1) = sg.Lv95(t.Id, sg.PointCount - 1);
                    var w0 = frame.ToWorld(e0, n0, 0);
                    var w1 = frame.ToWorld(e1, n1, 0);
                    if (Math.Min(Dist(w0), Dist(w1)) > at.R) continue;
                    log(Inv($"segment {sg.Class} {sg.Flags} oneway {sg.Attributes.OneWay} attrs {sg.Attributes.Flags} lanes {sg.Attributes.LanesForward}/{sg.Attributes.LanesBackward}, {e0:F1}/{n0:F1} -> {e1:F1}/{n1:F1}"));
                }
            foreach (var s in t.Signals)
                foreach (var pole in s.Poles)
                {
                    var w = W(pole.X, pole.Y, pole.Z);
                    if (Dist(w) <= at.R) log(Inv($"signal pole at {Lv(w)}, arm {pole.Arm}, {pole.Flags}"));
                }
        }

        // the route's lane line here: its points, and where it meets an island or passes a pole
        if (routeLv != null)
        {
            var poles = tiles.SelectMany(t => t.Signals.SelectMany(s => s.Poles.Select(p => frame.TileToWorld(t.Id, new Vector3(p.X, p.Y, p.Z))))).ToList();
            int k = 0;
            foreach (var (re, rn) in routeLv)
            {
                var p = frame.ToWorld(re, rn, 0);
                if (Dist(p) > at.R) continue;
                var q = new Vector2(p.X, p.Z);
                var hit = islands.FirstOrDefault(i => InTri(q, i.Tri));
                float pole = poles.Count == 0 ? float.MaxValue : poles.Min(o => new Vector2(o.X - p.X, o.Z - p.Z).Length());
                if (hit.Tri != null || pole < 1.5f || k++ % 3 == 0)
                    log(Inv($"route at {re:F1}/{rn:F1}{(hit.Tri != null ? " IN " + hit.Name : "")}{(pole < 1.5f ? $" {pole:F2} m from a signal pole" : "")}"));
            }
        }

        var dump = CmdArgs.Value("--dump") is { } dumpPath ? new StringBuilder("kind,id,class,flags,oneway,e,n\n") : null;
        if (dump != null)
        {
            int ii = 0;
            foreach (var (tri, _) in islands) { foreach (var v in tri) { var (e, n) = frame.ToLv95(new Vector3(v.X, 0, v.Y)); dump.AppendLine(Inv($"island,{ii},,,,{e:F2},{n:F2}")); } ii++; }
            foreach (var t in tiles)
                foreach (var sg in t.Signals)
                    foreach (var pole in sg.Poles) { var (e, n) = frame.ToLv95(frame.TileToWorld(t.Id, new Vector3(pole.X, pole.Y, pole.Z))); dump.AppendLine(Inv($"pole,{pole.Arm},,{pole.Flags},,{e:F2},{n:F2}")); }
            if (routeLv != null) foreach (var (e, n) in routeLv) dump.AppendLine(Inv($"route,0,,,,{e:F2},{n:F2}"));
        }
        int ei = 0;
        foreach (var e in graph.Edges)
        {
            if (e.Points.Min(Dist) > at.R) continue;
            if (dump != null)
            {
                string k = e.Path != null ? "path" : e.Connector ? "connector" : "road";
                foreach (var p in e.Points) { var (pe, pn) = frame.ToLv95(p); dump.AppendLine(Inv($"{k},{ei},{e.Class},{e.Flags.ToString().Replace(", ", "|")},{e.OneWay},{pe:F2},{pn:F2}")); }
                ei++;
            }
            string kind = e.Path != null ? "path" : e.Connector ? "connector" : "road";
            log(Inv($"edge {kind} {e.Class} {e.Flags} oneway {e.OneWay} {e.Length:F0} m, {Lv(e.Points[0])} (y {e.Points[0].Y:F1}) -> {Lv(e.Points[^1])} (y {e.Points[^1].Y:F1}), heading {Head(e.Points[^1] - e.Points[0])}, lane {e.LaneWidth:F1} m right {e.RightLane:F2}/{e.RightLaneFwd:F2}/{e.RightLaneBwd:F2}"));
            if (islands.Count == 0) continue;
            foreach (bool f in new[] { true, false })
            {
                if (e.OneWay == (f ? -1 : 1)) continue;
                float right = e.Path != null || e.Connector ? 0f : Traffic.KeepRight(e, f, 1);
                for (float d = 0; d <= e.Length; d += 0.5f)
                {
                    var (p, tan) = e.Sample(f ? d : e.Length - d);
                    var dir = new Vector3(tan.X, 0, tan.Z).Normalized() * (f ? 1 : -1);
                    p += dir.Cross(Vector3.Up) * right;
                    var q = new Vector2(p.X, p.Z);
                    var hit = islands.FirstOrDefault(i => InTri(q, i.Tri));
                    if (hit.Tri == null) continue;
                    log(Inv($"  its lane line {(f ? "forward" : "backward")} (right {right:F2} m) runs into {hit.Name} at {Lv(p)}, {d:F1} m along"));
                    break;
                }
            }
        }
        if (dump != null) System.IO.File.WriteAllText(CmdArgs.Value("--dump")!, dump.ToString());
    }
}
