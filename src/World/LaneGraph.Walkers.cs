using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// A crossing of the walk graph (#1033): a lit one (its signal and pedestrian group, a two-stage crossing's half each its own,
/// #1030), a zebra without lights (cars yield to the walkers on it), or the island between a two-stage crossing's halves.
/// </summary>
public sealed class WalkCrossing
{
    /// <summary>The lights and pedestrian group a lit crossing goes on; null for a zebra or an island.</summary>
    public SignalSite? Site { get; init; }
    public int Group { get; init; } = -1;
    /// <summary>The island between a two-stage crossing's halves: walkers stand on it, it is no crossing.</summary>
    public bool Island { get; init; }
    /// <summary>
    /// An unmarked crossing over a side road at a junction with neither lights nor a zebra (the user's choice, #1033): walkers cross
    /// on a gap in the cars, the cars do not yield.
    /// </summary>
    public bool Unmarked { get; init; }
    public bool Lit => Site is not null;
    public bool Zebra => Site is null && !Island && !Unmarked;
    /// <summary>A zebra (main thread): the traffic clock when a walker last stood at it to cross or was on it; cars stop for it.</summary>
    public double Claimed = double.MinValue;
}

/// <summary>
/// The walk graph (#1033): sidewalks (one edge per side of every street that has one, on the sidewalk's middle), footpaths and
/// tracks (their centrelines), the crosswalks at the lights (their <c>CPAR</c> walker lines, #979) and the zebras without lights
/// (read off their yellow bars), tied together: a crossing's ends to the nearest sidewalk, a two-stage crossing's halves across
/// their island, a sidewalk or path that stops short (a junction corner, a tile seam) to the nearest walkway it can reach
/// without crossing a carriageway. Built on a worker thread, in one origin frame.
/// </summary>
public sealed partial class LaneGraph
{
    /// <summary>Each crossing edge's crossing (lit, zebra, island); weak, so an edge dropped with its graph drops it too.</summary>
    private static readonly ConditionalWeakTable<LaneEdge, WalkCrossing> CrossingOfEdge = new();

    /// <summary>The crossing a walk edge is (#1033), null for a sidewalk, a path or a link.</summary>
    public static WalkCrossing? CrossingOf(LaneEdge edge) => CrossingOfEdge.TryGetValue(edge, out var c) ? c : null;

    /// <summary>
    /// Per road edge of the cars' graph built with it, where along it (m, in drawing order) a zebra crosses it (#1033): the cars
    /// on it stop there for a walker (<c>Traffic.ZebraWait</c>). Null on any graph but a walk graph.
    /// </summary>
    public Dictionary<LaneEdge, (float Arc, WalkCrossing Zebra)[]>? ZebraHits { get; private set; }

    /// <summary>What the walk graph holds, for the log and the check.</summary>
    public int WalkSidewalks { get; private set; }
    public int WalkPaths { get; private set; }
    public int WalkLit { get; private set; }
    public int WalkZebras { get; private set; }
    public int WalkUnmarked { get; private set; }
    public int WalkIslands { get; private set; }
    public int WalkLinks { get; private set; }
    /// <summary>Crossings left out: an end found no walkway beyond its kerb.</summary>
    public int WalkDropped { get; private set; }

    private enum WalkKind : byte { Sidewalk, Path, Crossing, Link }

    /// <summary>A walkway before it is cut into edges: its line, what it is, the road segment it came from, where it is split.</summary>
    private sealed class WalkProto
    {
        public required List<Vector3> P;
        public WalkKind Kind;
        public RoadClass Class;
        public float Width;
        public WalkCrossing? Crossing;
        /// <summary>The road segment it belongs to (a sidewalk's street; -1 for anything else): its other side is never linked to it.</summary>
        public int Segment = -1;
        public bool Dead;
        public readonly List<float> Splits = new();
    }

    /// <summary>A crossing's end ties to a sidewalk this close; a dangling sidewalk end to a walkway this close (m).</summary>
    private const float CrossingReach = 6f, CornerReach = 30f, PathReach = 10f, IslandReach = 4.5f;

    /// <summary>
    /// An unmarked crossing (#1033) lies this far back from a side road's mouth (m), spans at most <see cref="UnmarkedSpan"/> m kerb
    /// to kerb, and none is made within <see cref="UnmarkedClear"/> m of lights or a zebra (or of another).
    /// </summary>
    private const float UnmarkedBack = 3f, UnmarkedSpan = 14f, UnmarkedClear = 30f;

    /// <summary>A verge walkway out from a lit crossing with no walkway at its kerb runs this far along the arm (m).</summary>
    private const float StubLength = 40f;

    /// <summary>
    /// Builds the walk graph from <paramref name="tiles"/> (the road tiles within reach) and the cars' <paramref name="roads"/> of
    /// the same build (their lights' crossings, and the carriageways no link may cross). <paramref name="around"/>: only what lies
    /// within <paramref name="reach"/> m of it (flat).
    /// </summary>
    public static LaneGraph BuildWalks(IEnumerable<RoadTile> tiles, OriginFrame origin, LaneGraph roads, Vector3 around, float reach)
    {
        var protos = new List<WalkProto>();
        int segId = 0;
        bool Near(Vector3 p) => Mathf.Abs(p.X - around.X) < reach && Mathf.Abs(p.Z - around.Z) < reach;
        var tileList = tiles.ToList();

        // ---- sidewalks and paths
        foreach (var tile in tileList)
            foreach (var seg in tile.Segments)
            {
                segId++;
                int n = seg.PointCount;
                if (n < 2) continue;
                var at = seg.Attributes;
                bool car = seg.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major or RoadClass.Road
                    or RoadClass.Minor or RoadClass.Lane;
                bool path = seg.Class is RoadClass.Path or RoadClass.Track;
                if (!car && !path) continue;
                if (car && at.Left.SidewalkDm == 0 && at.Right.SidewalkDm == 0 && !at.Left.HasTrack && !at.Right.HasTrack) continue;
                var line = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    var (e, nn) = seg.Lv95(tile.Id, i);
                    line[i] = origin.ToWorld(e, nn, seg.Points[i * 3 + 1]);
                }
                if (!Near(line[0]) && !Near(line[^1]) && !Near(line[n / 2])) continue;
                if (path)
                {
                    protos.Add(new WalkProto { P = new List<Vector3>(line), Kind = WalkKind.Path, Class = seg.Class, Width = Mathf.Max(1f, seg.Width) });
                    continue;
                }
                // the plan length along it, for a side's shift (a turn lane's widening)
                var cum = new float[n];
                for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + MathX.FlatDistance(line[i], line[i - 1]);
                if (cum[^1] < 1f) continue;
                foreach (var (side, sign) in (ReadOnlySpan<(RoadSide, float)>)[(at.Right, 1f), (at.Left, -1f)])
                {
                    // the sidewalk's middle, out from the carriageway's edge past any path and its grass (RoadStreetSection.For); with no
                    // sidewalk, a separated path's (walkers share it with the riders)
                    float before, width, lift;
                    if (side.SidewalkDm > 0)
                    {
                        before = side.HasTrack && side.KerbCm > 0 ? (side.VergeDm + side.BikeDm + side.BufferDm) / 10f : 0f;
                        (width, lift) = (side.SidewalkDm / 10f, side.KerbCm / 100f);
                    }
                    else if (side.HasTrack)
                        (before, width, lift) = (side.VergeDm / 10f, side.BikeDm / 10f, RoadStreetSection.TrackHeight(side));
                    else continue;
                    var pts = new List<Vector3>(n);
                    for (int i = 0; i < n; i++)
                    {
                        float off = seg.Width * 0.5f + side.ShiftAt(cum[i] / cum[^1]) + before + width * 0.5f;
                        pts.Add(line[i] + Normal(line, i) * (sign * off) + Vector3.Up * lift);
                    }
                    protos.Add(new WalkProto { P = pts, Kind = WalkKind.Sidewalk, Class = seg.Class, Width = width, Segment = segId });
                }
            }
        int walkways = protos.Count;

        // ---- the crossings at the lights: their walker lines (CPAR), each half of a two-stage crossing its own group
        var litLines = new List<(Vector3 A, Vector3 B)>();
        var islandEnds = new List<(WalkProto Proto, bool AtEnd, SignalSite Site)>();
        foreach (var site in roads.Signals)
            foreach (var (line, points) in site.Lines)
            {
                if (line.Mover != ConflictMover.Walker || points.Length < 2 || !Near(points[0])) continue;
                var c = new WalkCrossing { Site = site, Group = line.Group };
                var proto = new WalkProto { P = new List<Vector3>(points), Kind = WalkKind.Crossing, Class = RoadClass.Path, Width = 3f, Crossing = c };
                protos.Add(proto);
                litLines.Add((points[0], points[^1]));
            }

        // ---- the zebras without lights: each a yellow paint of bars 3 m along the road, 0.5 m across it, side by side
        foreach (var tile in tileList)
            foreach (var paint in tile.Paint)
            {
                if (paint.Type != PaintType.YellowSolid || paint.Shape != PaintShape.Triangles || paint.Vertices.Length < 36 || paint.Vertices.Length % 12 != 0) continue;
                if (Zebra(paint, tile, origin) is not var (a, b)) continue;
                var mid = (a + b) * 0.5f;
                if (!Near(mid)) continue;
                bool lit = false;
                foreach (var (la, lb) in litLines)
                    if (DistanceToSegment(mid, la, lb) < 3f) { lit = true; break; }
                if (lit) continue;
                protos.Add(new WalkProto { P = [a, b], Kind = WalkKind.Crossing, Class = RoadClass.Path, Width = 3f, Crossing = new WalkCrossing() });
            }

        // ---- what a link may not cross: the carriageways (the cars' edges, their junction paths and connectors)
        var roadGrid = new SegmentGrid(16f);
        foreach (var e in roads.Edges)
            if (Near(e.Points[0]) || Near(e.Points[^1]))
                for (int i = 0; i + 1 < e.Points.Length; i++) roadGrid.Add(e.Points[i], e.Points[i + 1], e, i);
        bool CrossesRoad(Vector3 a, Vector3 b)
        {
            bool hit = false;
            roadGrid.Visit(a, b, 0f, (p, q, _, _) =>
            {
                if (!hit && SegmentsCross(a, b, p, q, 0.2f)) hit = true;
            });
            return hit;
        }

        // ---- every walkway segment, for the nearest point a crossing or a stray end ties to
        var walkGrid = new SegmentGrid(16f);
        for (int k = 0; k < walkways; k++)
            for (int i = 0; i + 1 < protos[k].P.Count; i++) walkGrid.Add(protos[k].P[i], protos[k].P[i + 1], protos[k], i);
        (WalkProto Proto, float Arc, Vector3 At, float Dist)? NearestWalkway(Vector3 p, float within, Func<WalkProto, bool> ok, Func<Vector3, bool>? clear = null)
        {
            (WalkProto, float, Vector3, float)? best = null;
            float bestD = within;
            walkGrid.Visit(p, p, within, (a, b, owner, i) =>
            {
                var proto = (WalkProto)owner;
                if (!ok(proto)) return;
                var q = ClosestOnSegment(p, a, b);
                float d = MathX.FlatDistance(p, q);
                if (d >= bestD || clear is not null && !clear(q)) return;
                bestD = d;
                best = (proto, ArcAt(proto.P, i) + MathX.FlatDistance(a, q), q, d);
            });
            return best;
        }
        var links = new List<WalkProto>();
        void Tie(WalkProto proto, float arc, Vector3 at, Vector3 to)
        {
            proto.Splits.Add(arc);
            if (MathX.FlatDistance(at, to) > 0.05f || Mathf.Abs(at.Y - to.Y) > 0.05f)
                links.Add(new WalkProto { P = [at, to], Kind = WalkKind.Link, Class = RoadClass.Path, Width = 1.5f });
        }

        // ---- a crossing's ends: a two-stage crossing's halves meet across their island; every other end ties to the nearest sidewalk
        // (a crossing whose ends find no walkway is dropped: its walkers would only turn back in the road)
        var ends = new List<(WalkProto Proto, bool AtEnd)>();
        for (int k = walkways; k < protos.Count; k++) { ends.Add((protos[k], false)); ends.Add((protos[k], true)); }
        var islandOf = new Dictionary<(WalkProto, bool), (WalkProto, bool)>();
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                var (pa, ea) = ends[i];
                var (pb, eb) = ends[j];
                if (pa == pb || pa.Crossing!.Site is null || pa.Crossing.Site != pb.Crossing!.Site || pa.Crossing.Group == pb.Crossing.Group) continue;
                var a = ea ? pa.P[^1] : pa.P[0];
                var b = eb ? pb.P[^1] : pb.P[0];
                if (MathX.FlatDistance(a, b) > IslandReach || islandOf.ContainsKey((pa, ea)) || islandOf.ContainsKey((pb, eb))) continue;
                // the halves of one crossing run on in a line: not two crossings of neighbouring arms meeting at a corner
                var da = (ea ? pa.P[^1] - pa.P[^2] : pa.P[0] - pa.P[1]).Normalized();
                var db = (eb ? pb.P[^2] - pb.P[^1] : pb.P[1] - pb.P[0]).Normalized();
                if (MathX.Flat(da).Normalized().Dot(MathX.Flat(db).Normalized()) < 0.8f) continue;
                islandOf[(pa, ea)] = (pb, eb);
                islandOf[(pb, eb)] = (pa, ea);
            }
        // ---- unmarked crossings (the user's choice, #1033): at a junction with neither lights nor a zebra, straight over each side
        // road a little back from its mouth, sidewalk to sidewalk; the walkers cross on a gap, the cars do not yield
        var marked = new List<Vector3>();
        foreach (var site in roads.Signals)
            foreach (var (line, points) in site.Lines)
                if (points.Length > 0) marked.Add(points[0]);
        for (int k = walkways; k < protos.Count; k++) marked.Add((protos[k].P[0] + protos[k].P[^1]) * 0.5f);
        bool Clear(Vector3 p)
        {
            foreach (var m in marked)
                if (MathX.FlatDistance(m, p) < UnmarkedClear) return false;
            return true;
        }
        foreach (var e in roads.Edges)
        {
            if (e.Connector || e.Length < 2f * UnmarkedBack + 2f || (e.Flags & RoadFlags.Divided) != 0 || !Near(e.Points[0])) continue;
            foreach (bool atEnd in (ReadOnlySpan<bool>)[false, true])
            {
                long key = atEnd ? e.KeyEnd : e.KeyStart;
                // a junction's mouth: three ends meet, or a junction path or connector takes the road on
                bool mouth = roads.Degree(key) >= 3 || roads.Incident(key).Any(o => o.Edge != e && o.Edge.Connector);
                if (!mouth || !(e.Class >= RoadClass.Minor || roads.GivesWay(key, e))) continue;
                var (at, tangent) = e.Sample(atEnd ? e.Length - UnmarkedBack : UnmarkedBack);
                if (!Clear(at)) continue;
                var t = MathX.Flat(tangent).Normalized();
                var n = new Vector3(-t.Z, 0f, t.X);
                float half = e.Width * 0.5f;
                var right = NearestWalkway(at + n * half, CrossingReach, p => p.Kind is WalkKind.Sidewalk or WalkKind.Path, q => MathX.Flat(q - at).Dot(n) > half * 0.5f);
                var left = NearestWalkway(at - n * half, CrossingReach, p => p.Kind is WalkKind.Sidewalk or WalkKind.Path, q => MathX.Flat(q - at).Dot(n) < -half * 0.5f);
                if (right is not { } r || left is not { } l || MathX.FlatDistance(r.At, l.At) > UnmarkedSpan) continue;
                // over this road only: no other carriageway in the way
                bool other = false;
                roadGrid.Visit(l.At, r.At, 0f, (p, q, owner, _) =>
                {
                    if (!other && owner != e && SegmentsCross(l.At, r.At, p, q, 0.2f)) other = true;
                });
                if (other) continue;
                protos.Add(new WalkProto { P = [l.At, r.At], Kind = WalkKind.Crossing, Class = RoadClass.Path, Width = 2f, Crossing = new WalkCrossing { Unmarked = true } });
                marked.Add(at);
            }
        }

        // ---- a lit crossing with no walkway at a kerb (a junction out of town): a walkway on the verge from there, out along the arm,
        // so walkers come to it (and round the corner to the next arm's, by the stray ends below)
        var stubs = new List<WalkProto>();
        var centreOf = new Dictionary<SignalSite, Vector3>();
        foreach (var site in roads.Signals)
        {
            var sum = Vector3.Zero;
            int count = 0;
            foreach (var (line, points) in site.Lines)
                if (line.Mover == ConflictMover.Walker && points.Length >= 2) { sum += (points[0] + points[^1]) * 0.5f; count++; }
            if (count > 0) centreOf[site] = sum / count;
        }
        for (int k = walkways; k < protos.Count; k++)
        {
            var c = protos[k];
            if (c.Crossing!.Site is not { } site || !centreOf.TryGetValue(site, out var centre)) continue;
            var across = MathX.Flat(c.P[^1] - c.P[0]).Normalized();
            var away = new Vector3(-across.Z, 0f, across.X);
            if (MathX.Flat((c.P[0] + c.P[^1]) * 0.5f - centre).Dot(away) < 0f) away = -away;
            foreach (bool atEnd in (ReadOnlySpan<bool>)[false, true])
            {
                if (islandOf.ContainsKey((c, atEnd))) continue;
                var end = atEnd ? c.P[^1] : c.P[0];
                if (NearestWalkway(end, CrossingReach, p => p.Kind is WalkKind.Sidewalk or WalkKind.Path) is not null) continue;
                var start = end + (atEnd ? across : -across) * 1f;
                float length = StubLength;
                while (length > 5f && CrossesRoad(start, start + away * length)) length -= 5f;
                if (length <= 5f) continue;
                var stub = new WalkProto { P = [start, start + away * length], Kind = WalkKind.Path, Class = RoadClass.Path, Width = 1.5f };
                stubs.Add(stub);
                walkGrid.Add(stub.P[0], stub.P[1], stub, 0);
            }
        }

        // (a walker line may run on past the sidewalk, over the paths beside it: each end is cut back to where the line passes
        // nearest a walkway within its outer CrossingReach m, and tied there)
        var crossingTies = new List<(WalkProto Crossing, bool AtEnd, WalkProto Proto, float Arc, Vector3 At)>();
        for (int k = walkways; k < protos.Count; k++)
        {
            var c = protos[k];
            var found = new List<(bool AtEnd, float Cut, WalkProto Proto, float Arc, Vector3 At)>(2);
            float length = 0f;
            for (int i = 1; i < c.P.Count; i++) length += MathX.FlatDistance(c.P[i - 1], c.P[i]);
            foreach (bool atEnd in (ReadOnlySpan<bool>)[false, true])
            {
                if (islandOf.ContainsKey((c, atEnd))) continue;
                (float Cut, WalkProto Proto, float Arc, Vector3 At, float Dist)? best = null;
                for (float back = 0f; back <= Mathf.Min(CrossingReach, length * 0.4f); back += 0.5f)
                {
                    var at = PointAt(c.P, atEnd ? length - back : back);
                    if (NearestWalkway(at, CrossingReach, p => p.Kind == WalkKind.Sidewalk || p.Kind == WalkKind.Path,
                            q => !CrossesRoad(at, q)) is not { } hit) continue;
                    // nearest wins; a tie, the outer one (the kerb)
                    if (best is null || hit.Dist < best.Value.Dist - 0.25f) best = (back, hit.Proto, hit.Arc, hit.At, hit.Dist);
                }
                if (best is { } h) found.Add((atEnd, h.Cut, h.Proto, h.Arc, h.At));
            }
            int wanted = 2 - (islandOf.ContainsKey((c, false)) ? 1 : 0) - (islandOf.ContainsKey((c, true)) ? 1 : 0);
            if (found.Count < wanted) { c.Dead = true; continue; }
            // cut the line back (the end first, so the start's arc still holds)
            foreach (var f in found)
                if (f.AtEnd && f.Cut > 0.01f) c.P = Cut(c.P, 0f, length - f.Cut);
            foreach (var f in found)
                if (!f.AtEnd && f.Cut > 0.01f) c.P = Cut(c.P, f.Cut, float.MaxValue);
            foreach (var f in found) crossingTies.Add((c, f.AtEnd, f.Proto, f.Arc, f.At));
        }
        // (an island's half whose other half died is dead too)
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ((p, _), (q, _)) in islandOf)
                if (p.Dead && !q.Dead) { q.Dead = true; changed = true; }
        }
        foreach (var (c, atEnd, proto, arc, at) in crossingTies)
            if (!c.Dead) Tie(proto, arc, at, atEnd ? c.P[^1] : c.P[0]);
        var islands = new List<WalkProto>();
        var islandDone = new HashSet<(WalkProto, bool)>();
        foreach (var ((p, ep), (q, eq)) in islandOf)
            if (!p.Dead && !islandDone.Contains((q, eq)) && islandDone.Add((p, ep)))
                islands.Add(new WalkProto
                {
                    P = [ep ? p.P[^1] : p.P[0], eq ? q.P[^1] : q.P[0]], Kind = WalkKind.Link, Class = RoadClass.Path, Width = 2f,
                    Crossing = new WalkCrossing { Island = true },
                });

        // ---- stray ends: a sidewalk that stops at a junction's corner or a tile seam, a path that ends beside a street. Each ties
        // to the nearest walkway within reach that it reaches without crossing a carriageway (not the other side of its own street)
        var endCount = new Dictionary<long, int>();
        long Cell(Vector3 p) { var (e, nn) = origin.ToLv95(p); return Key(e, nn); }
        int Meeting(Vector3 p)
        {
            long key = Cell(p);
            int x = (int)(key >> 32), z = (int)(uint)key, count = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    count += endCount.GetValueOrDefault(((long)(x + dx) << 32) ^ (uint)(z + dz));
            return count;
        }
        var walkList = protos.Take(walkways).Concat(stubs).ToList();
        foreach (var w in walkList)
            foreach (var p in (ReadOnlySpan<Vector3>)[w.P[0], w.P[^1]])
                endCount[Cell(p)] = endCount.GetValueOrDefault(Cell(p)) + 1;
        var strays = new List<(WalkProto Proto, float Arc, Vector3 At, Vector3 From)>();
        foreach (var w in walkList)
        {
            foreach (bool atEnd in (ReadOnlySpan<bool>)[false, true])
            {
                var end = atEnd ? w.P[^1] : w.P[0];
                if (Meeting(end) > 1) continue;
                float within = w.Kind == WalkKind.Sidewalk ? CornerReach : PathReach;
                var hit = NearestWalkway(end, within,
                    p => p != w && (w.Segment < 0 || p.Segment != w.Segment) && (w.Kind == WalkKind.Sidewalk || p.Kind == WalkKind.Sidewalk),
                    q => !CrossesRoad(end, q));
                if (hit is { } h) strays.Add((h.Proto, h.Arc, h.At, end));
            }
        }
        // (two stray ends that found each other tie once)
        var tied = new HashSet<(long, long)>();
        foreach (var (proto, arc, at, from) in strays)
        {
            long ka = Cell(at), kb = Cell(from);
            if (tied.Add((Math.Min(ka, kb), Math.Max(ka, kb)))) Tie(proto, arc, at, from);
        }

        // ---- cut into edges at every tie; each piece's ends keyed by their LV95 cell, so ties, seams and rebuilds meet
        var g = new LaneGraph(origin);
        void Emit(List<Vector3> pts, WalkProto from)
        {
            if (pts.Count < 2) return;
            var cumulative = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++) cumulative[i] = cumulative[i - 1] + pts[i].DistanceTo(pts[i - 1]);
            if (cumulative[^1] < 0.05f) return;
            var edge = new LaneEdge
            {
                Points = pts.ToArray(), Cumulative = cumulative, Class = from.Class, Width = from.Width, Kind = MoverKind.Walker,
                KeyStart = Cell(pts[0]), KeyEnd = Cell(pts[^1]), Connector = from.Kind is WalkKind.Link or WalkKind.Crossing,
            };
            g.Add(edge);
            if (from.Crossing is { } c) CrossingOfEdge.AddOrUpdate(edge, c);
        }
        protos.AddRange(stubs);
        foreach (var w in protos)
        {
            if (w.Dead) { g.WalkDropped++; continue; }
            switch (w.Kind)
            {
                case WalkKind.Sidewalk: g.WalkSidewalks++; break;
                case WalkKind.Path: g.WalkPaths++; break;
                default:
                    if (w.Crossing!.Lit) g.WalkLit++;
                    else if (w.Crossing.Unmarked) g.WalkUnmarked++;
                    else g.WalkZebras++;
                    break;
            }
            if (w.Splits.Count == 0) { Emit(w.P, w); continue; }
            w.Splits.Sort();
            var piece = new List<Vector3> { w.P[0] };
            float s = 0f;
            int next = 0;
            for (int i = 1; i < w.P.Count; i++)
            {
                float len = MathX.FlatDistance(w.P[i - 1], w.P[i]);
                while (next < w.Splits.Count && w.Splits[next] <= s + len)
                {
                    float t = len > 1e-4f ? Mathf.Clamp((w.Splits[next] - s) / len, 0f, 1f) : 0f;
                    var cut = w.P[i - 1].Lerp(w.P[i], t);
                    next++;
                    if (MathX.FlatDistance(piece[^1], cut) < 0.3f && piece.Count == 1) continue;
                    piece.Add(cut);
                    Emit(piece, w);
                    piece = [cut];
                }
                if (MathX.FlatDistance(piece[^1], w.P[i]) > 1e-3f || piece.Count == 1 && i == w.P.Count - 1) piece.Add(w.P[i]);
                s += len;
            }
            Emit(piece, w);
        }
        foreach (var l in links) { Emit(l.P, l); g.WalkLinks++; }
        foreach (var l in islands) { Emit(l.P, l); g.WalkIslands++; }

        // ---- where the zebras cross the cars' lanes, for the cars to stop at
        var hits = new Dictionary<LaneEdge, List<(float, WalkCrossing)>>();
        foreach (var w in protos)
        {
            if (w.Dead || w.Kind != WalkKind.Crossing || !w.Crossing!.Zebra) continue;
            var a = w.P[0];
            var b = w.P[^1];
            var seen = new HashSet<LaneEdge>();
            roadGrid.Visit(a, b, 0f, (p, q, owner, i) =>
            {
                var e = (LaneEdge)owner;
                if (!SegmentsCross(a, b, p, q, 0f) || !seen.Add(e)) return;
                float along = Intersection(p, q, a, b);
                if (!hits.TryGetValue(e, out var list)) hits[e] = list = new();
                list.Add((e.Cumulative[i] + along * (e.Cumulative[i + 1] - e.Cumulative[i]), w.Crossing));
            });
        }
        g.ZebraHits = hits.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        return g;
    }

    /// <summary>A zebra's line across the road (world): through its bars' middles, from the kerb on one side to the other's.</summary>
    private static (Vector3 A, Vector3 B)? Zebra(RoadPaint paint, RoadTile tile, OriginFrame origin)
    {
        var v = paint.Vertices;
        int bars = v.Length / 12;
        Vector3 At(int k) => new(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
        var across = Vector3.Zero;
        var centres = new Vector3[bars];
        for (int b = 0; b < bars; b++)
        {
            Vector3 p0 = At(b * 4), p1 = At(b * 4 + 1), p2 = At(b * 4 + 2), p3 = At(b * 4 + 3);
            float along = MathX.FlatDistance(p0, p1), wide = MathX.FlatDistance(p1, p2);
            // a bar: about 3 m along the road and half a metre across (EmitCrossing), else not a zebra
            if (along < 2f || along > 4f || wide < 0.3f || wide > 0.7f) return null;
            centres[b] = (p0 + p1 + p2 + p3) * 0.25f;
            if (b == 0) across = MathX.Flat(p2 - p1).Normalized();
        }
        float lo = float.MaxValue, hi = float.MinValue;
        int ilo = 0, ihi = 0;
        for (int b = 0; b < bars; b++)
        {
            float d = (centres[b] - centres[0]).Dot(across);
            if (d < lo) (lo, ilo) = (d, b);
            if (d > hi) (hi, ihi) = (d, b);
        }
        if (hi - lo < 2f) return null;
        // half a bar and a step on past the outer bars: onto the kerb
        var a = centres[ilo] - across * 0.6f;
        var bEnd = centres[ihi] + across * 0.6f;
        Vector3 World(Vector3 p) => origin.ToWorld(tile.Id.MinE + p.X, tile.Id.MaxN - p.Z, p.Y);
        return (World(a), World(bEnd));
    }

    /// <summary>A polyline's unit normal at a point, to the right of its direction (flat), mitred at a bend (at most 2x).</summary>
    private static Vector3 Normal(Vector3[] line, int i)
    {
        static Vector3 Right(Vector3 a, Vector3 b)
        {
            var d = MathX.Flat(b - a);
            return d.LengthSquared() < 1e-8f ? Vector3.Zero : new Vector3(-d.Z, 0f, d.X).Normalized();
        }
        var n0 = i > 0 ? Right(line[i - 1], line[i]) : Vector3.Zero;
        var n1 = i + 1 < line.Length ? Right(line[i], line[i + 1]) : Vector3.Zero;
        if (n0 == Vector3.Zero) return n1;
        if (n1 == Vector3.Zero) return n0;
        var m = (n0 + n1);
        if (m.LengthSquared() < 1e-6f) return n1;
        m = m.Normalized();
        float cos = Mathf.Max(0.5f, m.Dot(n1));
        return m / cos;
    }

    /// <summary>The point <paramref name="s"/> m (flat) along a polyline.</summary>
    private static Vector3 PointAt(List<Vector3> p, float s)
    {
        for (int i = 1; i < p.Count; i++)
        {
            float len = MathX.FlatDistance(p[i - 1], p[i]);
            if (s <= len) return p[i - 1].Lerp(p[i], len > 1e-4f ? s / len : 0f);
            s -= len;
        }
        return p[^1];
    }

    /// <summary>The part of a polyline from <paramref name="from"/> to <paramref name="to"/> m along it (flat).</summary>
    private static List<Vector3> Cut(List<Vector3> p, float from, float to)
    {
        var r = new List<Vector3> { PointAt(p, from) };
        float s = 0f;
        for (int i = 1; i < p.Count; i++)
        {
            s += MathX.FlatDistance(p[i - 1], p[i]);
            if (s > from + 1e-3f && s < to - 1e-3f) r.Add(p[i]);
        }
        r.Add(PointAt(p, Mathf.Min(to, s)));
        return r;
    }

    private static float ArcAt(List<Vector3> p, int i)
    {
        float s = 0f;
        for (int k = 1; k <= i; k++) s += MathX.FlatDistance(p[k - 1], p[k]);
        return s;
    }

    private static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = MathX.Flat(b - a);
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-8f ? 0f : Mathf.Clamp(MathX.Flat(p - a).Dot(ab) / len2, 0f, 1f);
        return a.Lerp(b, t);
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b) => MathX.FlatDistance(p, ClosestOnSegment(p, a, b));

    private static float Cross2(Vector3 a, Vector3 b) => a.X * b.Z - a.Z * b.X;

    /// <summary>Whether two flat segments cross, each at least <paramref name="margin"/> m in from the first's ends.</summary>
    private static bool SegmentsCross(Vector3 a, Vector3 b, Vector3 p, Vector3 q, float margin)
    {
        var r = MathX.Flat(b - a);
        var s = MathX.Flat(q - p);
        float den = Cross2(r, s);
        if (Mathf.Abs(den) < 1e-8f) return false;
        var ap = MathX.Flat(p - a);
        float t = Cross2(ap, s) / den, u = Cross2(ap, r) / den;
        float m = r.Length() > 1e-4f ? margin / r.Length() : 0f;
        return t > m && t < 1f - m && u >= 0f && u <= 1f;
    }

    /// <summary>Where along p→q (0..1) the line a→b crosses it.</summary>
    private static float Intersection(Vector3 p, Vector3 q, Vector3 a, Vector3 b)
    {
        var r = MathX.Flat(q - p);
        var s = MathX.Flat(b - a);
        float den = Cross2(r, s);
        return Mathf.Abs(den) < 1e-8f ? 0f : Mathf.Clamp(Cross2(MathX.Flat(a - p), s) / den, 0f, 1f);
    }

    /// <summary>Flat segments in square cells, for the walk graph's nearest-point and crossing tests.</summary>
    private sealed class SegmentGrid(float cell)
    {
        private readonly Dictionary<long, List<(Vector3 A, Vector3 B, object Owner, int Index)>> _cells = new();

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        public void Add(Vector3 a, Vector3 b, object owner, int index)
        {
            int x0 = Mathf.FloorToInt(Mathf.Min(a.X, b.X) / cell), x1 = Mathf.FloorToInt(Mathf.Max(a.X, b.X) / cell);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, b.Z) / cell), z1 = Mathf.FloorToInt(Mathf.Max(a.Z, b.Z) / cell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue(CellKey(x, z), out var list)) _cells[CellKey(x, z)] = list = new();
                    list.Add((a, b, owner, index));
                }
        }

        /// <summary>Every segment in the cells the box of a→b, grown by <paramref name="pad"/>, touches (one may come more than once).</summary>
        public void Visit(Vector3 a, Vector3 b, float pad, Action<Vector3, Vector3, object, int> visit)
        {
            int x0 = Mathf.FloorToInt((Mathf.Min(a.X, b.X) - pad) / cell), x1 = Mathf.FloorToInt((Mathf.Max(a.X, b.X) + pad) / cell);
            int z0 = Mathf.FloorToInt((Mathf.Min(a.Z, b.Z) - pad) / cell), z1 = Mathf.FloorToInt((Mathf.Max(a.Z, b.Z) + pad) / cell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (_cells.TryGetValue(CellKey(x, z), out var list))
                        foreach (var (p, q, owner, i) in list) visit(p, q, owner, i);
        }
    }
}
