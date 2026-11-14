using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// The road a race runs on: the main road from a point — the nearest drivable edge, then at every
/// junction the edge of the same class or better that carries on straightest, both ways, keeping
/// the longer run — its centreline and width (what "on the tarmac" means), and the
/// <see cref="RaceLine"/> on it. Built off the main thread from the <c>.road</c> tiles.
/// </summary>
public sealed class RaceRoute
{
    public readonly List<Vector3> Centre = new();
    public readonly List<float> Arc = new();
    public readonly List<float> Width = new();
    public RaceLine Line = null!;
    public RoadClass Class;
    /// <summary>
    /// The road behind the start, from the start outward (the other way from the host, the walk
    /// not kept): where a race NPC arriving "from behind" comes up from (<c>World/NpcArrival</c>).
    /// Server-built routes only; empty on one rebuilt from points.
    /// </summary>
    public readonly List<Vector3> Behind = new();
    public readonly List<float> BehindWidth = new();

    /// <summary>
    /// The frame <see cref="Centre"/>, <see cref="Behind"/> and the line are in (#185): the origin
    /// they were built or received in, or a race's own frame on the server. Null: not known yet,
    /// taken to be the first frame <see cref="Follow"/> is given.
    /// </summary>
    public OriginFrame? Frame { get; private set; }

    /// <summary>
    /// Moves every point into <paramref name="now"/>: the origin moved (#185). Whoever holds the
    /// route calls it from its shift handler; a route held by several (a race's runner, its pilot,
    /// an NPC's driver) moves once, on the first call.
    /// </summary>
    public void Follow(OriginFrame now)
    {
        if (Frame is { } was && !was.SameFrame(now))
        {
            var shift = now.Since(was);
            for (int i = 0; i < Centre.Count; i++) Centre[i] = shift.Point(Centre[i]);
            for (int i = 0; i < Behind.Count; i++) Behind[i] = shift.Point(Behind[i]);
        }
        Frame = now;
        Line?.Follow(now);
    }

    public float Length => Line.Length;

    /// <param name="smallest">The narrowest class of road it may take (a truck's probe keeps to Road and wider).</param>
    /// <param name="toward">A point to drive to: the shortest way there by road, and on past it (default: the longer of the two ways along the road).</param>
    public static Task<RaceRoute?> BuildAsync(IChunkSource source, WorldOrigin origin, Vector3 at,
        CancellationToken ct = default, RoadClass smallest = RoadClass.Minor, Vector3? toward = null) =>
        // one origin frame for the whole build, which runs off the main thread (#185)
        BuildAsync(source, origin.Frame, at, ct, smallest, toward);

    /// <summary>
    /// An earth world's roads round a point (#1742): the (2 <paramref name="reach"/> + 1)² leaves round it (a leaf is ~0.6 km),
    /// each leaf's own roads in its anchor frame (<see cref="TilePlace.OnAnchor"/>, which the lane graph places through the
    /// leaf), read three at a time.
    /// </summary>
    private static async Task<List<RoadTile>> LeafTilesAround(System.Func<CubeKey, RoadTile?> leafRoads, OriginFrame frame, Vector3 at,
        int reach, CancellationToken ct)
    {
        var centre = CubeKey.FromEcef(frame.ToGlobal(at).Ecef, Terrain.Leaves.LeafFrames.LeafLevel);
        var keys = new List<CubeKey>();
        for (int dx = -reach; dx <= reach; dx++)
            for (int dy = -reach; dy <= reach; dy++)
                if (centre.Neighbour(dx, dy) is { } k) keys.Add(k);
        using var slots = new SemaphoreSlim(3);
        var loads = keys.Select(async k =>
        {
            await slots.WaitAsync(ct);
            try { return await Task.Run(() => leafRoads(k), ct); }
            catch (System.Exception) { return null; }   // a leaf that would not come: no roads there
            finally { slots.Release(); }
        }).ToList();
        var tiles = new List<RoadTile>();
        int segments = 0;
        for (int i = 0; i < keys.Count; i++)
            if (await loads[i] is { } roads)
            {
                tiles.Add(TilePlace.OnAnchor(roads, keys[i]));
                segments += roads.Segments.Count;
            }
        GD.Print($"[race] the road from the leaves round {centre}: {tiles.Count} of {keys.Count} leaves with roads, {segments} segments");
        return tiles;
    }

    /// <summary>The route from <paramref name="at"/>, a point in <paramref name="frame"/>, which the route is in too (and <paramref name="toward"/>).</summary>
    public static async Task<RaceRoute?> BuildAsync(IChunkSource source, OriginFrame frame, Vector3 at,
        CancellationToken ct = default, RoadClass smallest = RoadClass.Minor, Vector3? toward = null,
        System.Func<CubeKey, RoadTile?>? leafRoads = null)
    {
        var tiles = new List<RoadTile>();
        LaneGraph graph;
        bool Usable(RoadSegment s) => Drivable(s, smallest);
        if (leafRoads != null && frame.Axes != null)
        {
            // the 7 x 7 leaves round the point (~4 km), then 15 x 15 (~9 km, as far as the 9 x 9 LV95 tiles below reach)
            // when none of them has a road to race on: a sparse generated fill has its roads kilometres apart
            tiles = await LeafTilesAround(leafRoads, frame, at, 3, ct);
            graph = LaneGraph.Build(tiles, frame, Usable);
            if (graph.Edges.Count == 0)
            {
                tiles = await LeafTilesAround(leafRoads, frame, at, 7, ct);
                graph = LaneGraph.Build(tiles, frame, Usable);
            }
        }
        else
        {
            var (e, n) = frame.ToLv95(at);
            var here = TileId.FromLv95(e, n);
            // west to east, each column south to north (z runs south in the tile's frame): the graph's order
            for (int dx = -4; dx <= 4; dx++)
                for (int dz = 4; dz >= -4; dz--)
                {
                    try { if (await source.LoadRoadsAsync(here.Neighbour(dx, dz), ct) is { } t) tiles.Add(t); }
                    catch (System.Exception) { /* no roads there */ }
                }
            graph = LaneGraph.Build(tiles, frame, Usable);
        }
        if (StartEdge(graph, at) is not var (best, bestS)) return null;
        var ends = new EndIndex(graph);

        var a = Walk(graph, ends, best, true, bestS);
        var b = Walk(graph, ends, best, false, best.Length - bestS);
        var (pts, back) = a.Count >= b.Count ? (a, b) : (b, a);
        if (toward is { } to && Towards(graph, ends, best, bestS, to, keepRight: false, null) is { } path)
        {
            pts = path;
            back = Flat(a[Mathf.Min(5, a.Count - 1)].P - path[Mathf.Min(5, path.Count - 1)].P).Length() < 1f ? b : a;
        }
        var route = FromPoints(pts.Select(p => p.P).ToList(), pts.Select(p => p.W).ToList(), best.Class, frame);
        foreach (var (p, w) in back.Take(300)) { route.Behind.Add(p); route.BehindWidth.Add(w); }   // 600 m is plenty
        return route;
    }

    private static bool Drivable(RoadSegment s, RoadClass smallest) =>
        s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Major or RoadClass.Road or RoadClass.Minor
        && s.Class <= smallest && (s.Flags & (RoadFlags.Stairs | RoadFlags.Tunnel)) == 0;

    /// <summary>The drivable edge nearest <paramref name="at"/> and how far along it, the bigger road preferred when two are close.</summary>
    private static (LaneEdge Edge, float S)? StartEdge(LaneGraph graph, Vector3 at, RoadClass? along = null, Vector3? toward = null)
    {
        LaneEdge? best = null;
        float bestD = float.MaxValue, bestS = 0;
        foreach (var edge in graph.Edges)
            for (int i = 0; i < edge.Points.Length; i++)
            {
                // a drive kept to one class of road (a motorway): on it, on the carriageway heading toward the destination
                if (along is { } only && edge.Class != only) continue;
                // and on a one-way street (a carriageway) the way it heads toward the destination (#1880): a start on the A1's
                // other carriageway at Bellevue was only joined to the drive by a hop across the median, which no road makes
                if (!Heading(edge, i, toward)) continue;
                // prefer the bigger road when two are close: a farm track beside the pass is not the pass
                float d = Flat(edge.Points[i] - at).Length() + (int)edge.Class * 4f;
                if (d < bestD) { bestD = d; best = edge; bestS = edge.Cumulative[i]; }
            }
        return best == null ? null : (best, bestS);
    }

    // whether the way an edge may be driven at its point i heads toward a place (any way, with no place given)
    private static bool Heading(LaneEdge e, int i, Vector3? toward)
    {
        if (toward is not { } to || e.OneWay == 0) return true;
        var dir = Flat(e.Points[Mathf.Min(i + 1, e.Points.Length - 1)] - e.Points[Mathf.Max(i - 1, 0)]) * e.OneWay;
        return dir.Dot(Flat(to - e.Points[i])) > 0f;
    }

    /// <summary>
    /// A drive to a far destination (#1744): the lane graph of the whole corridor from <paramref name="at"/> to
    /// <paramref name="to"/> (<paramref name="half"/> m each side of the straight line and past both ends), the shortest way
    /// along it as <see cref="BuildAsync"/>'s <c>toward</c> finds it (one-way streets, junction paths), on the rightmost lane of
    /// each street (<see cref="Traffic.KeepRight"/>) and as wide as that lane, the road's class at every point
    /// (<see cref="Classes"/>, for its speed). The roads are the earth world's leaves (<paramref name="leafRoads"/>, as
    /// <see cref="Traffic"/> reads them) or the LV95 tiles of <paramref name="source"/>. Null when no route joins them.
    /// </summary>
    public static async Task<RaceRoute?> ToAsync(IChunkSource source, Func<CubeKey, RoadTile?>? leafRoads, OriginFrame frame,
        Vector3 at, Vector3 to, double half = 2000, Action<string>? log = null, IReadOnlyList<Vector3>? avoid = null,
        RoadClass? along = null, CancellationToken ct = default)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var tiles = new List<RoadTile>();
        if (leafRoads != null)
        {
            foreach (var key in CorridorLeaves(frame.ToGlobal(at), frame.ToGlobal(to), half))
            {
                ct.ThrowIfCancellationRequested();
                if (leafRoads(key) is { Segments.Count: > 0 } roads) tiles.Add(TilePlace.OnAnchor(roads, key));
            }
        }
        else
        {
            foreach (var id in CorridorTiles(frame.ToGlobal(at), frame.ToGlobal(to), half))
            {
                try { if (await source.LoadRoadsAsync(id, ct) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no roads there */ }
            }
        }
        // traffic's own car graph (the trimmed ends joined), but no tunnel (a car meets the ground's collision at an
        // underpass's mouth, on flat and earth alike: Geneva, #1744) and no road closed to through traffic
        var graph = LaneGraph.Build(tiles, frame, s => Traffic.IsCarRoad(s) && (s.Flags & (RoadFlags.Tunnel | RoadFlags.Restricted)) == 0);
        graph.JoinTrimmedEnds();
        string Took() => System.FormattableString.Invariant($"{System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalSeconds:F1} s");
        if (StartEdge(graph, at, along, to) is not var (best, bestS))
        {
            log?.Invoke($"no drivable road in the corridor's {tiles.Count} tiles ({graph.Edges.Count} edges), {Took()}");
            return null;
        }
        var ends = new EndIndex(graph);
        var kinds = new List<(RoadClass, RoadFlags)>();
        var banned = Banned(graph, avoid);
        var path = Towards(graph, ends, best, bestS, to, keepRight: true, kinds, banned: banned, blocks: avoid, along: along);
        string Said(List<(Vector3 P, float W)>? way)
        {
            if (way is not { Count: > 1 }) return "no way";
            var (ee, en) = frame.ToLv95(way[^1].P);
            return System.FormattableString.Invariant($"a way of {way.Count * 2 / 1000f:F1} km ending {Flat(way[^1].P - to).Length():F0} m from the destination (LV95 {ee:F0}/{en:F0})");
        }
        log?.Invoke(System.FormattableString.Invariant(
            $"the corridor: {tiles.Count} tiles, {graph.Edges.Count} edges; the start {Flat(best.Sample(bestS).Pos - at).Length():F0} m off on a {best.Class}; ")
            + Said(path) + $", {Took()}");
        if (path is not { Count: > 1 } || Flat(path[^1].P - to).Length() > 500f)
        {
            // the one-way directions cut the way (on flat and earth alike): a one-way street the wrong way where nothing
            // else joins, at AgainstCost times its time, rather than no drive
            log?.Invoke(Undirected(graph, best, to));
            var againstKinds = new List<(RoadClass, RoadFlags)>();
            var wrong = new List<LaneEdge>();
            if (Towards(graph, ends, best, bestS, to, keepRight: true, againstKinds, against: true, banned, avoid, along, wrong) is { Count: > 1 } wrongWay
                && (path is not { Count: > 1 } || Flat(wrongWay[^1].P - to).Length() < Flat(path[^1].P - to).Length()))
            {
                (path, kinds) = (wrongWay, againstKinds);
                // where: each one-way edge driven against its direction (its class, its middle), for the issue it is
                log?.Invoke("one-way streets taken the wrong way where they cut it: " + Said(path) + ", against "
                    + string.Join("; ", wrong.Select(e =>
                    {
                        var (we, wn) = frame.ToLv95(e.Sample(e.Length * 0.5f).Pos);
                        return System.FormattableString.Invariant($"a {e.Class}{(e.Path != null ? " junction path" : "")} {e.Length:F0} m at LV95 {we:F0}/{wn:F0}");
                    })));
            }
        }
        if (path is not { Count: > 1 }) return null;
        var route = FromPoints(path.Select(p => p.P).ToList(), path.Select(p => p.W).ToList(), best.Class, frame);
        route.Kinds = kinds;
        return route;
    }

    // a drive's re-plan round places found blocked (#1744): every edge passing within BlockReach of one
    private const float BlockReach = 10f;

    private static HashSet<LaneEdge>? Banned(LaneGraph graph, IReadOnlyList<Vector3>? avoid)
    {
        if (avoid is not { Count: > 0 }) return null;
        var banned = new HashSet<LaneEdge>();
        foreach (var e in graph.Edges)
            foreach (var p in e.Points)
                if (avoid.Any(a => Flat(p - a).Length() < BlockReach)) { banned.Add(e); break; }
        return banned;
    }

    // whether, from s m along the start edge, driving it this way passes a blocked place
    private static bool BlockedAhead(LaneEdge start, float s, bool forward, IReadOnlyList<Vector3>? blocks)
    {
        if (blocks is not { Count: > 0 }) return false;
        for (int i = 0; i < start.Points.Length; i++)
        {
            if (!blocks.Any(a => Flat(start.Points[i] - a).Length() < BlockReach)) continue;
            float at = start.Cumulative[i];
            if (forward ? at > s : at < s) return true;
        }
        return false;
    }

    // a drive that ends short (#1744): how near the destination the roads reach from the start if one-way streets are
    // driven both ways: tells a one-way cut from a gap in the network
    private static string Undirected(LaneGraph graph, LaneEdge start, Vector3 to)
    {
        var byEnd = new Dictionary<(int, int), List<LaneEdge>>();
        (int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.X / 18f), Mathf.FloorToInt(p.Z / 18f));
        foreach (var e in graph.Edges)
            foreach (var p in new[] { e.Points[0], e.Points[^1] })
            {
                var c = Cell(p);
                if (!byEnd.TryGetValue(c, out var l)) byEnd[c] = l = new();
                l.Add(e);
            }
        var seen = new HashSet<LaneEdge> { start };
        var queue = new Queue<LaneEdge>();
        queue.Enqueue(start);
        float nearest = float.MaxValue;
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            nearest = Mathf.Min(nearest, Mathf.Min(Flat(e.Points[0] - to).Length(), Flat(e.Points[^1] - to).Length()));
            foreach (var p in new[] { e.Points[0], e.Points[^1] })
            {
                var (cx, cz) = Cell(p);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (byEnd.TryGetValue((cx + dx, cz + dz), out var l))
                            foreach (var n in l)
                                if ((Flat(n.Points[0] - p).Length() < 18f || Flat(n.Points[^1] - p).Length() < 18f) && seen.Add(n)) queue.Enqueue(n);
            }
        }
        return System.FormattableString.Invariant(
            $"one-way ignored, {seen.Count} of {graph.Edges.Count} edges reach to {nearest:F0} m from the destination");
    }

    /// <summary>The road's class and flags at each of <see cref="Centre"/>'s points: a route built by <see cref="ToAsync"/>; null otherwise.</summary>
    public List<(RoadClass Class, RoadFlags Flags)>? Kinds { get; private set; }

    /// <summary>The road's class at arc <paramref name="s"/> (<see cref="Class"/> where none is known per point).</summary>
    public RoadClass ClassAt(float s) => Kinds is { Count: > 0 } k ? k[Mathf.Min(NearestCentreIndexAt(s), k.Count - 1)].Class : Class;

    /// <summary>Whether the road at arc <paramref name="s"/> is in a tunnel (false where nothing is known per point).</summary>
    public bool TunnelAt(float s) => Kinds is { Count: > 0 } k && (k[Mathf.Min(NearestCentreIndexAt(s), k.Count - 1)].Flags & RoadFlags.Tunnel) != 0;

    /// <summary>
    /// The speed a drive keeps to on a class of road (m/s, #1744): Swiss limits, the lower where a class spans town and
    /// country (a Major road through a village is 50, between them 80: 60 suits both at the racing pilot's cornering).
    /// <see cref="ToAsync"/> plans by the time it takes at these speeds, so it keeps to the bigger roads.
    /// </summary>
    public static float DriveSpeed(RoadClass c) => c switch
    {
        RoadClass.Motorway => 110f,
        RoadClass.Expressway => 90f,
        RoadClass.Ramp or RoadClass.Major => 60f,
        RoadClass.Road => 50f,
        RoadClass.Minor => 30f,
        _ => 15f,
    } / 3.6f;

    private const double CorridorStep = 150;

    // every place on a 150 m grid over the corridor: along the straight line from a to b and half on past each end, half each side
    private static IEnumerable<GlobalPos> Corridor(GlobalPos a, GlobalPos b, double half)
    {
        var (east, north, _) = a.OffsetTo(b);
        double length = System.Math.Max(1.0, a.HorizontalDistanceTo(b));
        double ux = east / length, uy = north / length;
        for (double s = -half; s <= length + half; s += CorridorStep)
            for (double w = -half; w <= half; w += CorridorStep)
                yield return a.Moved(ux * s + uy * w, uy * s - ux * w);
    }

    private static List<CubeKey> CorridorLeaves(GlobalPos a, GlobalPos b, double half)
    {
        var seen = new HashSet<CubeKey>();
        var keys = new List<CubeKey>();
        foreach (var p in Corridor(a, b, half))
        {
            var g = p.Geodetic;
            var key = Terrain.Leaves.LeafFrames.LeafAt(g.LatDeg, g.LonDeg);
            if (seen.Add(key)) keys.Add(key);
        }
        return keys;
    }

    private static List<TileId> CorridorTiles(GlobalPos a, GlobalPos b, double half)
    {
        var seen = new HashSet<TileId>();
        var ids = new List<TileId>();
        foreach (var p in Corridor(a, b, half))
        {
            var id = p.Tile;
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// A ski race down the slopes from <paramref name="at"/> (#909, <see cref="World.Snow.PisteCourse"/>):
    /// the slope tiles and heights within 4 km, the nearest run and on down. Null when no run comes
    /// within 300 m (or the region has no slope layer): the caller falls back to the road.
    /// </summary>
    public static async Task<RaceRoute?> BuildPisteAsync(IChunkSource source, OriginFrame frame, Vector3 at, float metres,
        CancellationToken ct = default)
    {
        if (source is Terrain.Leaves.StandInChunkSource) return await BuildPisteOnLeavesAsync(source, frame, at, metres, ct);
        var (e, n) = frame.ToLv95(at);
        var here = TileId.FromLv95(e, n);
        var tiles = new List<PisteTile>();
        for (int dx = -4; dx <= 4; dx++)
            for (int dz = 4; dz >= -4; dz--)
            {
                try { if (await source.LoadPistesAsync(here.Neighbour(dx, dz), ct) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no slopes there */ }
            }
        if (tiles.Count == 0) return null;
        var runs = World.Snow.PisteCourse.Runs(tiles);

        // heights, loaded as the course reaches each tile: full where there is one, else the coarse one
        var grids = new Dictionary<TileId, ChunkGrid?>();
        float Altitude(double pe, double pn)
        {
            var id = TileId.FromLv95(pe, pn);
            if (!grids.TryGetValue(id, out var g))
            {
                try { g = source.LoadChunkAsync(id, ct).GetAwaiter().GetResult() ?? source.LoadCoarseChunkAsync(id, ct).GetAwaiter().GetResult(); }
                catch (System.Exception) { g = null; }
                grids[id] = g;
            }
            if (g == null) return 0f;
            var (x, z) = id.Local(pe, pn);
            int col = Mathf.Clamp((int)System.Math.Round(x), 0, ChunkFormat.GridSize - 1);
            int row = Mathf.Clamp((int)System.Math.Round(z), 0, ChunkFormat.GridSize - 1);
            return (float)g.HeightMetersAt(col, row);
        }

        var course = World.Snow.PisteCourse.Chain(runs, e, n, Altitude, metres);
        if (course == null || course.Count < 2) return null;
        var centre = new List<Vector3>(course.Count);
        var width = new List<float>(course.Count);
        foreach (var (pe, pn, w) in course)
        {
            centre.Add(frame.ToWorld(pe, pn, Altitude(pe, pn)));
            width.Add(w);
        }
        var route = FromPoints(centre, width, RoadClass.Minor, frame);
        // the slope above the start, where race NPCs come down from (World/NpcArrival)
        foreach (var (pe, pn, w) in World.Snow.PisteCourse.Above(runs, course[0].E, course[0].N, Altitude, 600f))
        {
            route.Behind.Add(frame.ToWorld(pe, pn, Altitude(pe, pn)));
            route.BehindWidth.Add(w);
        }
        return route;
    }

    /// <summary>
    /// <see cref="BuildPisteAsync"/> on earth leaves (#1737): the leaves within <see cref="LeafRings"/> of the start's,
    /// through their stand-ins; every run moved into the start leaf's stand-in frame (one frame for the whole course,
    /// its axes the leaf's east and north), the altitude at a point read from the grid of the leaf it is on. The course's
    /// points are that frame's E/N, which <see cref="OriginFrame.ToWorld(double, double, double)"/> places.
    /// </summary>
    private static async Task<RaceRoute?> BuildPisteOnLeavesAsync(IChunkSource source, OriginFrame frame, Vector3 at, float metres,
        CancellationToken ct)
    {
        var start = frame.ToGlobal(at);
        var centre = Terrain.Leaves.LeafStandIn.Id(CubeKey.FromEcef(start.ToEcef(), Terrain.Leaves.LeafFrames.LeafLevel));
        var tiles = new List<PisteTile>();
        for (int dx = -LeafRings; dx <= LeafRings; dx++)
            for (int dz = LeafRings; dz >= -LeafRings; dz--)
            {
                try { if (await source.LoadPistesAsync(centre.Neighbour(dx, dz), ct) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no slopes there */ }
            }
        if (tiles.Count == 0) return null;
        // a point named in one leaf's stand-in, named in the start's
        (double E, double N) Place(TileId tile, double pe, double pn)
        {
            if (tile == centre) return (pe, pn);
            var (ce, cn, _) = GlobalPos.OfTileEn(tile, pe, pn, 0).InTileEn(centre);
            return (ce, cn);
        }
        var runs = World.Snow.PisteCourse.Runs(tiles, Place);

        // the ground's height in the start's frame: from the grid of the leaf the point is on
        var grids = new Dictionary<TileId, ChunkGrid?>();
        float Altitude(double pe, double pn)
        {
            var place = GlobalPos.OfTileEn(centre, pe, pn, 0);
            var id = Terrain.Leaves.LeafStandIn.Id(CubeKey.FromEcef(place.ToEcef(), Terrain.Leaves.LeafFrames.LeafLevel));
            if (!grids.TryGetValue(id, out var g))
            {
                try { g = source.LoadChunkAsync(id, ct).GetAwaiter().GetResult(); }
                catch (System.Exception) { g = null; }
                grids[id] = g;
            }
            if (g == null) return 0f;
            var (le, ln, _) = place.InTileEn(id);
            var ground = GlobalPos.OfTileEn(id, le, ln, g.SampleHeight(le, ln));
            return (float)ground.InTileEn(centre).H;
        }

        var (e, n, _) = start.InTileEn(centre);
        var course = World.Snow.PisteCourse.Chain(runs, e, n, Altitude, metres);
        if (course == null || course.Count < 2) return null;
        var points = new List<Vector3>(course.Count);
        var width = new List<float>(course.Count);
        foreach (var (pe, pn, w) in course)
        {
            points.Add(frame.ToWorld(pe, pn, Altitude(pe, pn)));
            width.Add(w);
        }
        var route = FromPoints(points, width, RoadClass.Minor, frame);
        foreach (var (pe, pn, w) in World.Snow.PisteCourse.Above(runs, course[0].E, course[0].N, Altitude, 600f))
        {
            route.Behind.Add(frame.ToWorld(pe, pn, Altitude(pe, pn)));
            route.BehindWidth.Add(w);
        }
        return route;
    }

    /// <summary>Leaves round the start a ski race reads on the Earth: ~3.7 km, as the 4 km of LV95 tiles (a leaf is ~610 m).</summary>
    private const int LeafRings = 6;

    /// <summary>A route from a centreline already known (sent by the race server, say), in <paramref name="frame"/>.</summary>
    public static RaceRoute FromPoints(IReadOnlyList<Vector3> centre, IReadOnlyList<float> width, RoadClass cls = RoadClass.Road,
        OriginFrame? frame = null)
    {
        var r = new RaceRoute { Class = cls, Frame = frame };
        float s = 0;
        for (int i = 0; i < centre.Count; i++)
        {
            if (i > 0) s += centre[i].DistanceTo(centre[i - 1]);
            r.Centre.Add(centre[i]);
            r.Arc.Add(s);
            r.Width.Add(width[i]);
        }
        r.Line = RaceLine.Build(centre, width, 0.9f);
        r.Line.Frame = frame;
        return r;
    }

    private static List<(Vector3 P, float W)> Walk(LaneGraph graph, EndIndex ends, LaneEdge edge, bool forward, float from)
    {
        var pts = new List<(Vector3, float)>();
        const float Step = 2f, MaxLength = 8000f;
        float total = 0;
        var visited = new HashSet<LaneEdge>();
        while (total < MaxLength && visited.Add(edge))
        {
            for (float s = from; s <= edge.Length; s += Step)
            {
                var (p, _) = edge.Sample(forward ? s : edge.Length - s);
                pts.Add((p, edge.Width));
                total += Step;
            }
            // leave the far end: the next edge of this class or better that turns least
            var (endP, endT) = edge.Sample(forward ? edge.Length : 0);
            var dirIn = forward ? endT : -endT;
            long key = forward ? edge.KeyEnd : edge.KeyStart;
            LaneEdge? next = null;
            bool nextFwd = true;
            float bestScore = -2f;
            // RoadGen --rewrite trims every road back from its junction polygon, so after it the
            // roads meeting at a junction no longer share an endpoint: take any edge that starts
            // or ends within a junction's reach of where this one stopped
            foreach (var (cand, candFwd) in graph.Leaving(key).Concat(ends.Near(endP)))
            {
                if (cand == edge || visited.Contains(cand) || cand.Class > edge.Class) continue;
                var (_, t) = cand.Sample(candFwd ? 1f : cand.Length - 1f);
                float score = (candFwd ? t : -t).Dot(dirIn);
                if (score > bestScore) { bestScore = score; next = cand; nextFwd = candFwd; }
            }
            if (next == null || bestScore < -0.2f) break;
            edge = next;
            forward = nextFwd;
            from = 0;
        }
        return pts;
    }

    /// <summary>
    /// The shortest way by road from <paramref name="start"/> (at <paramref name="s"/> m along it) to the edge passing
    /// nearest <paramref name="to"/>, then on from there as <see cref="Walk"/> goes (a run-out past the finish). For a
    /// course between two given points: the straightest-road walk turned off at the first col. <paramref name="keepRight"/>:
    /// a drive (#1744): the quickest way rather than the shortest (each road at its <see cref="DriveSpeed"/>), the start's
    /// own one-way kept, on the rightmost lane of each street rather than its centreline, as wide as that lane; the road's
    /// class and flags at each point into <paramref name="kinds"/> when given. <paramref name="against"/>: a one-way street may
    /// be driven the wrong way, at <see cref="AgainstCost"/> times its time (only where the data's directions cut the way).
    /// </summary>
    private static List<(Vector3 P, float W)>? Towards(LaneGraph graph, EndIndex ends, LaneEdge start, float s, Vector3 to,
        bool keepRight, List<(RoadClass, RoadFlags)>? kinds, bool against = false, HashSet<LaneEdge>? banned = null,
        IReadOnlyList<Vector3>? blocks = null, RoadClass? along = null, List<LaneEdge>? wrong = null)
    {
        float Cost(LaneEdge e, float metres, bool f) => !keepRight ? metres
            : metres / DriveSpeed(e.Class) * (Against(e, f) ? AgainstCost : 1f);
        // the goal: the road nearest the destination (of the class a drive keeps to, if it keeps to one)
        var goal = graph.Edges.Where(e => along is not { } only || e.Class == only).DefaultIfEmpty(start)
            .MinBy(e => e.Points.Min(p => Flat(p - to).LengthSquared()))!;
        var cost = new Dictionary<(LaneEdge, bool), float>();
        var from = new Dictionary<(LaneEdge, bool), (LaneEdge, bool)>();
        var open = new PriorityQueue<(LaneEdge Edge, bool F), float>();
        foreach (bool f in new[] { true, false })
        {
            if (keepRight && !against && Against(start, f)) continue;
            // the car stopped on a road found blocked: only the way back from where it was blocked
            if (banned?.Contains(start) == true && BlockedAhead(start, s, f, blocks)) continue;
            float c = Cost(start, f ? start.Length - s : s, f);
            cost[(start, f)] = c;
            open.Enqueue((start, f), c);
        }
        (LaneEdge Edge, bool F)? hit = null;
        while (open.TryDequeue(out var cur, out float c))
        {
            if (c > cost[cur]) continue;
            if (cur.Edge == goal) { hit = cur; break; }
            var (endP, endT) = cur.Edge.Sample(cur.F ? cur.Edge.Length : 0);
var endDir = Flat(cur.F ? endT : -endT);
            long key = cur.F ? cur.Edge.KeyEnd : cur.Edge.KeyStart;
            foreach (var next in Onward(graph, ends, cur.Edge, cur.F, key, endP, against))
            {
                if (next.Item1 == cur.Edge || banned?.Contains(next.Item1) == true) continue;
                if (TurnsBackAtMouth(cur.Edge, endDir, next.Item1, next.Item2)) continue;
                // across a gap between two roads only ahead of both (#1880, as LaneGraph.JoinTrimmedEnds): any road end within 18 m let
                // a way hop 13 m from a street's end back to a ramp's foot, under the ramp it then climbed (Place des Nations)
                if (!Joins(next, endP, endDir.Normalized(), cur.Edge)) continue;
                float nc = c + Cost(next.Item1, next.Item1.Length, next.Item2);
                if (cost.TryGetValue(next, out float old) && old <= nc) continue;
                cost[next] = nc;
                from[next] = cur;
                open.Enqueue(next, nc);
            }
        }
        // a drive whose goal is cut off: on to the reachable road nearest the destination (the caller sees it end short)
        if (hit == null && keepRight && cost.Count > 0)
            hit = cost.Keys.MinBy(k => Mathf.Min(Flat(k.Item1.Points[0] - to).LengthSquared(), Flat(k.Item1.Points[^1] - to).LengthSquared()));
        if (hit is not { } last) return null;
        var chain = new List<(LaneEdge Edge, bool F)> { last };
        while (from.TryGetValue(chain[^1], out var prev)) chain.Add(prev);
        chain.Reverse();
        var pts = new List<(Vector3, float)>();
        for (int k = 0; k < chain.Count - 1; k++)
        {
            var (e, f) = chain[k];
            if (wrong != null && Against(e, f)) wrong.Add(e);
            float right = keepRight ? LaneRight(e, f) : 0f;
            float width = keepRight ? LaneWidth(e) : e.Width;
            for (float d = k == 0 ? (f ? s : e.Length - s) : 0f; d <= e.Length; d += 2f)
            {
                var (p, t) = e.Sample(f ? d : e.Length - d);
                var dir = Flat(f ? t : -t).Normalized();
                if (right != 0f) p += dir.Cross(Vector3.Up) * right;
                if (keepRight && d == 0f) Bridge(pts, kinds, p, dir, width, (e.Class, e.Flags));
                pts.Add((p, width));
                kinds?.Add((e.Class, e.Flags));
            }
        }
        // on past the goal: a run-out (a race's), or the destination's street to its end (a drive's)
        var tail = keepRight ? Along(last.Edge, last.F, chain.Count == 1 ? (last.F ? s : last.Edge.Length - s) : 0f)
            : Walk(graph, ends, last.Edge, last.F, chain.Count == 1 ? (last.F ? s : last.Edge.Length - s) : 0f);
        if (keepRight && tail.Count >= 2)
            Bridge(pts, kinds, tail[0].P, Flat(tail[1].P - tail[0].P).Normalized(), tail[0].W, (last.Edge.Class, last.Edge.Flags));
        pts.AddRange(tail);
        for (int k = 0; k < tail.Count; k++) kinds?.Add((last.Edge.Class, last.Edge.Flags));
        return pts;
    }

    /// <summary>
    /// The edges a route may take on from an end: those meeting it, and the road ends within reach (roads trimmed back from an
    /// old-layout cap). A junction path only from its own mouth (within <see cref="MouthJoin"/>), and at a planned core's
    /// mouth nothing farther: never a jump across the core into another arm's path (#1903: from the south mouth 17 m to the
    /// south-east arm's, past the corner).
    /// </summary>
    private static IEnumerable<(LaneEdge, bool)> Onward(LaneGraph graph, EndIndex ends, LaneEdge from, bool forward, long key,
        Vector3 endP, bool against)
    {
        // a path ends on its lane, that far right of its road's centreline end
        float own = from.Path is { } fp ? Mathf.Abs(forward ? fp.ToOffset : fp.FromOffset) : 0f;
        var near = new List<((LaneEdge, bool) End, float D, float Reach)>();
        bool mouth = false;
        foreach (var end in ends.Near(endP, against))
        {
            var p = end.Item2 ? end.Item1.Points[0] : end.Item1.Points[^1];
            float d = Flat(p - endP).Length(), reach = own + MouthJoin;
            if (end.Item1.Path is { } path)
            {
                reach += Mathf.Abs(end.Item2 ? path.FromOffset : path.ToOffset);
                if (d > reach) continue;
                mouth = true;
            }
            near.Add((end, d, reach));
        }
        foreach (var next in graph.Leaving(key))
        {
            if (next.Item1.Path is not null) mouth = true;
            yield return next;
        }
        foreach (var (end, d, reach) in near)
            if (!mouth || d <= reach) yield return end;
    }

    /// <summary>How far from a road's end, past its lane's offset, a junction path may start or end and still be at that road's mouth (m).</summary>
    private const float MouthJoin = 2f;

    /// <summary>
    /// A junction path straight into another one that leaves back the way it came (#1903): every path of an arm meets the
    /// graph at that arm's road end, so out along one arm's exit path and in again along its entry path is a U-turn on the spot
    /// at the mouth, never driving the street (Geneva 2501310,1124903: the route turned there and the car climbed the corner's
    /// kerb). Between connectors alone (junction paths, and the short links <see cref="LaneGraph.JoinTrimmedEnds"/> lays between
    /// road ends, which can sit between the two paths): a street's own dead-end turn is not judged here. Two paths going on (two
    /// cores a 0 m street apart) still join.
    /// </summary>
    internal static bool TurnsBackAtMouth(LaneEdge cur, Vector3 endDir, LaneEdge next, bool nextForward)
    {
        if (!cur.Connector || !next.Connector) return false;
        var (_, t) = next.Sample(nextForward ? 0f : next.Length);
        var startDir = Flat(nextForward ? t : -t);
        if (endDir.LengthSquared() < 1e-6f || startDir.LengthSquared() < 1e-6f) return false;
        return endDir.Normalized().Dot(startDir.Normalized()) < 0f;
    }

    /// <summary>
    /// A drive's way across a junction whose roads stop short of it (RoadGen trims them back): from the last point,
    /// leaving along its road, to the next road's first, arriving along it, as a car turns (a cubic curve every 2 m).
    /// Joined straight, the way cut the corner across the kerb into a building's (Geneva, #1744).
    /// </summary>
    private static void Bridge(List<(Vector3 P, float W)> pts, List<(RoadClass, RoadFlags)>? kinds, Vector3 to, Vector3 toDir,
        float width, (RoadClass, RoadFlags) kind)
    {
        if (pts.Count < 2) return;
        var from = pts[^1].P;
        var fromDir = Flat(from - pts[^2].P);
        float gap = Flat(to - from).Length();
        if (gap < 2.5f || fromDir.LengthSquared() < 1e-6f) return;
        fromDir = fromDir.Normalized();
        var c1 = from + fromDir * (gap / 3f);
        var c2 = to - toDir * (gap / 3f);
        int n = Mathf.CeilToInt(gap / 2f);
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n, u = 1f - t;
            pts.Add((u * u * u * from + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * to, Mathf.Max(width, pts[^1].W)));
            kinds?.Add(kind);
        }
    }

    // one edge from `from` m along it in its direction of travel, on its rightmost lane
    private static List<(Vector3 P, float W)> Along(LaneEdge e, bool f, float from)
    {
        var pts = new List<(Vector3, float)>();
        float right = LaneRight(e, f), width = LaneWidth(e);
        for (float d = from; d <= e.Length; d += 2f)
        {
            var (p, t) = e.Sample(f ? d : e.Length - d);
            if (right != 0f) p += Flat(f ? t : -t).Normalized().Cross(Vector3.Up) * right;
            pts.Add((p, width));
        }
        return pts;
    }

    // metres right of the centreline the drive keeps to: the rightmost lane, as traffic does; a junction path is its own line;
    // a one-way street driven the wrong way, its middle
    private static float LaneRight(LaneEdge e, bool forward) =>
        !OnLane || e.Path != null || e.Connector || Against(e, forward) ? 0f : Traffic.KeepRight(e, forward, 0);

    /// <summary>A drive keeps to its lane (true) or has the whole street (false, as a race).</summary>
    public static bool OnLane = CmdArgs.Value("--driveline") != "road";

    /// <summary>Whether driving <paramref name="e"/> <paramref name="forward"/> (in drawing order) goes against its one-way.</summary>
    private static bool Against(LaneEdge e, bool forward) => e.OneWay == (forward ? -1 : 1);

    /// <summary>A one-way street driven the wrong way costs this many times its time (when <see cref="Towards"/> allows it).</summary>
    private const float AgainstCost = 8f;

    // the width the line may use: the lane (and a little), or a narrow street's half where no lane is known
    private static float LaneWidth(LaneEdge e) => !OnLane ? e.Width : e.LaneWidth > 0f ? e.LaneWidth + 0.6f : Mathf.Clamp(e.Width * 0.5f, 2.6f, 4f);

    /// <summary>
    /// Where an edge may be taken from at a junction whose ends are not shared (RoadGen trims every road back from its
    /// junction polygon): every edge starting, or ending if two-way, within <see cref="Reach"/> of a point. A grid of
    /// the ends, built once per route: the scan over every edge at every junction it replaces was quadratic, minutes on a
    /// city's graph.
    /// </summary>
    /// <summary>
    /// Whether the route may go on from a road's end at <paramref name="at"/> (leaving it along <paramref name="exit"/>) to
    /// <paramref name="next"/>'s end: at the same node always, and a junction path at its mouth by <see cref="Onward"/>'s
    /// rule; across a gap between two roads (#1880) only with the gap ahead of the way out and the next road leaving along it (the trimmed ends RoadGen's junctions leave, as <see cref="LaneGraph.JoinTrimmedEnds"/>
    /// faces them), never a turn back.
    /// </summary>
    private static bool Joins((LaneEdge Edge, bool Forward) next, Vector3 at, Vector3 exit, LaneEdge from)
    {
        // a junction path meets its road's end at its lane's offset, beside it: the mouth's own rule (Onward, #1903)
        if (from.Path is not null || next.Edge.Path is not null) return true;
        var p = next.Forward ? next.Edge.Points[0] : next.Edge.Points[^1];
        var gap = Flat(p - at);
        float d = gap.Length();
        if (d < 1f) return true;
        var dir = gap / d;
        var (_, t) = next.Edge.Sample(next.Forward ? 0f : next.Edge.Length);
        var leaves = Flat(next.Forward ? t : -t).Normalized();
        return exit.Dot(dir) >= 0.3f && leaves.Dot(dir) >= 0.3f;
    }

    private sealed class EndIndex
    {
        private const float Reach = 18f, MaxStep = 3f;   // as LaneGraph.JoinTrimmedEnds
        private readonly Dictionary<(int, int), List<(LaneEdge, bool)>> _cells = new();
        // the ends a one-way street could be entered from the wrong way (asked for only when that is allowed)
        private readonly Dictionary<(int, int), List<(LaneEdge, bool)>> _against = new();

        public EndIndex(LaneGraph graph)
        {
            foreach (var e in graph.Edges)
            {
                Add(e.OneWay != -1 ? _cells : _against, e.Points[0], (e, true));
                Add(e.OneWay != 1 ? _cells : _against, e.Points[^1], (e, false));
            }
        }

        private static (int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.X / Reach), Mathf.FloorToInt(p.Z / Reach));

        private static void Add(Dictionary<(int, int), List<(LaneEdge, bool)>> cells, Vector3 p, (LaneEdge, bool) end)
        {
            var c = Cell(p);
            if (!cells.TryGetValue(c, out var list)) cells[c] = list = new();
            list.Add(end);
        }

        public IEnumerable<(LaneEdge, bool)> Near(Vector3 at, bool against = false)
        {
            foreach (var end in Near(_cells, at)) yield return end;
            if (against)
                foreach (var end in Near(_against, at)) yield return end;
        }

        private static IEnumerable<(LaneEdge, bool)> Near(Dictionary<(int, int), List<(LaneEdge, bool)>> cells, Vector3 at)
        {
            var (cx, cz) = Cell(at);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue((cx + dx, cz + dz), out var list)) continue;
                    foreach (var end in list)
                    {
                        var p = end.Item2 ? end.Item1.Points[0] : end.Item1.Points[^1];
                        // and on the same level: a road in a trench or on a bridge is not joined to the street 5 m above or under it
                        if (Flat(p - at).Length() < Reach && Mathf.Abs(p.Y - at.Y) < MaxStep) yield return end;
                    }
                }
        }
    }

    /// <summary>Distance from the road's centreline, searched near the closest point.</summary>
    public float Off(Vector3 pos)
    {
        int near = NearestCentre(pos);
        float off = float.MaxValue;
        for (int i = Mathf.Max(0, near - 3); i < Mathf.Min(Centre.Count - 1, near + 3); i++)
            off = Mathf.Min(off, DistanceToSegment(Flat(pos), Flat(Centre[i]), Flat(Centre[i + 1])));
        return off;
    }

    public float HalfWidthAt(Vector3 pos) => Width[NearestCentre(pos)] * 0.5f;

    /// <summary>The centreline index at arc length s (clamped).</summary>
    public int NearestCentreIndexAt(float s)
    {
        int i = Arc.BinarySearch(s);
        if (i < 0) i = ~i;
        return Mathf.Clamp(i, 0, Centre.Count - 1);
    }

    /// <summary>Nearest centreline point: a coarse stride over the 2 m polyline, then a fine search around it.</summary>
    public int NearestCentre(Vector3 pos)
    {
        int i = 0;
        float best = float.MaxValue;
        for (int k = 0; k < Centre.Count; k += 16)
        {
            float d = Flat(Centre[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        int c = i;
        for (int k = Mathf.Max(0, c - 16); k < Mathf.Min(Centre.Count, c + 16); k++)
        {
            float d = Flat(Centre[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        return i;
    }

    /// <summary><see cref="MathX.Flat"/>, kept for its many callers; new code calls MathX.</summary>
    public static Vector3 Flat(Vector3 v) => MathX.Flat(v);

    /// <summary>Angle from a to b about +Y, radians; + is anticlockwise from above (to the left).</summary>
    public static float SignedAngle(Vector3 a, Vector3 b) =>
        Mathf.Atan2(a.Z * b.X - a.X * b.Z, a.X * b.X + a.Z * b.Z);

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return p.DistanceTo(a + ab * t);
    }
}
