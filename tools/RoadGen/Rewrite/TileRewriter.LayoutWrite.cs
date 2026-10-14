namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The junction planner's output written into the tiles (#818 phase 3, under <c>--junctions new</c>; the user's decision, Oct 13
/// 2026): at every planned single-node junction whose checks pass the core is its cap, the arms' streets are trimmed (or run on)
/// to the core's mouths, and the pieces each transition covers carry <see cref="RoadAttrFlags.InTransition"/> with their
/// <c>TRNS</c> records and the transition's paint; the street's own paint is laid again on what is left of it. The old pocket
/// layout and the exterior (stop lines, crosswalks, poles, corners, signs) are not built there: phase 4 lays them from the core.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>What the planner wrote, for the region report.</summary>
    public sealed class LayoutWriteStats
    {
        public int Junctions, Skipped, Transitions, Pieces, Lines, Marks, Trimmed, Extended, Orphaned, CoreVertices, CapVertices, Bars, Refuges, Signs, Ramps, Signals, SignalsInvalid, Poles;
        /// <summary>Written junctions with a transition (LV95), for the seam check and pictures.</summary>
        public readonly List<Vec2> WithTransitions = new();
        public readonly List<Vec2> OrphanedAt = new();
        public readonly SortedDictionary<string, int> Why = new(StringComparer.Ordinal);

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    junction planner written (#818 phase 3): {Junctions:N0} junctions as cores, {Skipped:N0} left to the old layout ({string.Join(", ", Why.Select(kv => $"{kv.Key} {kv.Value}"))}); ")
            + string.Create(CultureInfo.InvariantCulture, $"{Transitions:N0} transitions over {Pieces:N0} pieces, {Lines:N0} lines and {Marks:N0} marks; arms trimmed to their mouth {Trimmed:N0}, run on to it {Extended:N0}; pieces lost by a later cut {Orphaned:N0}{(OrphanedAt.Count > 0 ? " (at " + string.Join("; ", OrphanedAt.Take(6).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:F0},{v.Y:F0}"))) + ")" : "")}; cap vertices {CapVertices:N0} of the cores' {CoreVertices:N0}; exterior: {Bars:N0} bars, {Refuges:N0} refuges, {Signs:N0} signs, {Ramps:N0} crosswalks with lowered kerbs; lights {Signals:N0} ({SignalsInvalid:N0} plans invalid), {Poles:N0} poles")
            + (WithTransitions.Count > 0 ? "\n      with transitions at " + string.Join("; ", WithTransitions.Take(12).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:F0},{v.Y:F0}"))) : "");
    }

    /// <summary>One piece of a street from a junction outward: its link, tile, whole segment and piece, and whether it is drawn toward the junction.</summary>
    private sealed record StreetPiece(int Link, TileId Tile, RoadSegment Whole, RoadSegment Piece, bool TowardJunction)
    {
        public double Length => RoadPaintGeometry.Length(Piece.Points);
    }

    /// <summary>
    /// The junctions the planner may write: a core with its checks passed (kerbs inside the mouths, connectors inside the
    /// outline, an outline not crossing itself), every arm a car road of the block with no internal street (a multi-node core is
    /// phase 5), every street a transition runs on in the block and at grade, and the far junction of every transition shared
    /// with one written too.
    /// </summary>
    private static HashSet<int> WritableLayouts(List<JunctionPlan> plans, RoadGenResult result, Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf,
        LayoutWriteStats stats)
    {
        var junctions = result.Junctions.ToDictionary(j => j.NodeId);
        var why = new Dictionary<int, string>();
        foreach (var jp in plans)
        {
            string? no = null;
            if (jp.Core is not { } core) no = "no core";
            else if (!core.Simple || core.KerbsNotTangent > 0 || core.ConnectorsOutside > 0) no = "a check failed";
            else if (jp.Arms.Any(a => a.Internal)) no = "multi-node";
            else if (!junctions.TryGetValue(jp.Node, out var j) || j.Arms.Count != jp.Arms.Count) no = "a path or track joins";
            else if (jp.Arms.Any(a => a.Axis is null || !core.Mouths.Any(m => m.Arm == a.Arm))) no = "an arm without a mouth";
            else
                foreach (var a in jp.Arms)
                    foreach (int link in a.StreetOn.Select(x => x.Link).Prepend(a.LinkId))
                        if (!segmentOf.TryGetValue(link, out var so)) no ??= "a street beyond the block";
                        else if ((so.Segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) no ??= "a bridge or tunnel";
            if (no is not null) why[jp.Node] = no;
        }
        var ok = plans.Where(p => !why.ContainsKey(p.Node)).Select(p => p.Node).ToHashSet();
        var planOf = plans.SelectMany(p => p.Arms.Select(a => (a, p))).ToDictionary(x => x.a, x => x.p);
        // a transition shared with the junction beyond is written by both or neither
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var a in plans.SelectMany(p => p.Arms))
                if (a.Transition is { Far: { } far } t && planOf.TryGetValue(t.Arm, out var pa) && planOf.TryGetValue(far, out var pb)
                    && ok.Contains(pa.Node) != ok.Contains(pb.Node))
                {
                    int drop = ok.Contains(pa.Node) ? pa.Node : pb.Node;
                    ok.Remove(drop);
                    why[drop] = "the junction beyond is not written";
                    changed = true;
                }
        }
        foreach (var w in why.Values) stats.Why[w] = stats.Why.GetValueOrDefault(w) + 1;
        stats.Skipped += why.Count;
        return ok;
    }

    /// <summary>A tile's transitions whose segment it still holds (a check: any other is counted and left out, its piece drawn plain).</summary>
    private static List<RoadTransition> Placed(TileId id, List<RoadTransition>? transitions, List<RoadSegment> segments, LayoutWriteStats stats)
    {
        if (transitions is null || transitions.Count == 0) return [];
        var held = new HashSet<RoadSegment>(segments, ReferenceEqualityComparer.Instance);
        var placed = transitions.Where(t => held.Contains(t.Segment)).ToList();
        stats.Orphaned += transitions.Count - placed.Count;
        foreach (var t in transitions.Where(t => !held.Contains(t.Segment)))
            stats.OrphanedAt.Add(new Vec2(id.MinE + t.Segment.Points[0], id.MaxN - t.Segment.Points[2]));
        return placed;
    }

    /// <summary>The priority plans without the junctions the planner writes: what the old passes lay out.</summary>
    private static PriorityResult WithoutWritten(PriorityResult priority, HashSet<int> written)
    {
        if (written.Count == 0) return priority;
        var rest = new PriorityResult();
        rest.Plans.AddRange(priority.Plans.Where(p => !written.Contains(p.Junction.NodeId)));
        foreach (var (k, v) in priority.Yield) rest.Yield[k] = v;
        rest.SignalsFromData.UnionWith(priority.SignalsFromData);
        return rest;
    }

    /// <summary>
    /// Writes the planner's junctions (<see cref="WritableLayouts"/>): caps, trims, transitions and their paint. Runs once the
    /// street pieces are final and before the bike lanes are painted on them (they skip a transition's pieces: its paint has them).
    /// </summary>
    private static void WriteLayouts(List<JunctionPlan> plans, HashSet<int> written, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<RoadSegment, List<RoadSegment>> finalPieces,
        Dictionary<TileId, List<RoadSegment>> output, Dictionary<int, (TileId Home, RoadJunction Cap)> capOf, Dictionary<TileId, List<RoadJunction>> caps,
        HashSet<RoadJunction> writtenCaps, Dictionary<TileId, List<RoadPaint>> paint, Dictionary<int, (double Station, bool Start, bool End)> paintStation,
        Dictionary<TileId, List<RoadTransition>> transitionsOf, Dictionary<TileId, List<RoadAreaProp>> areas, Dictionary<TileId, List<RoadPointProp>> signs,
        Dictionary<TileId, List<RoadSignal>> signals, Dictionary<TileId, List<RoadApproach>> approaches, Cantons? cantons, SignalStats signalStats, LayoutWriteStats stats)
    {
        var net = result.Network;
        var mine = plans.Where(p => written.Contains(p.Node)).ToList();
        var touched = new HashSet<int>();
        List<RoadSegment> PiecesOf(RoadSegment whole) => finalPieces.TryGetValue(whole, out var p) && p.Count > 0 ? p : [whole];
        // the street from an arm's junction outward, piece by piece
        List<StreetPiece> Street(ArmPlan a)
        {
            var list = new List<StreetPiece>();
            foreach (var (link, atEnd) in a.StreetOn.Prepend((a.LinkId, a.AtEnd)))
            {
                var so = segmentOf[link];
                var pieces = PiecesOf(so.Segment);
                // junction at the link's last point: drawn toward it, so the pieces run backwards from it
                foreach (var piece in atEnd ? Enumerable.Reverse(pieces) : pieces) list.Add(new StreetPiece(link, so.Tile, so.Segment, piece, atEnd));
            }
            return list;
        }
        // replaces a whole's pieces in its tile and in finalPieces
        void Replace(RoadSegment whole, TileId tile, List<RoadSegment> pieces)
        {
            var old = PiecesOf(whole);
            var list = output[tile];
            int first = list.FindIndex(x => ReferenceEquals(x, old[0]));
            if (first < 0) return;
            list.RemoveRange(first, old.Count);
            list.InsertRange(first, pieces);
            finalPieces[whole] = pieces;
        }
        static RoadSegment Copy(RoadSegment s, float[] points, RoadAttributes? attributes = null) => new()
        {
            Class = s.Class, Surface = s.Surface, Flags = s.Flags, Width = s.Width, Points = points, Attributes = attributes ?? s.Attributes,
        };

        // 1. the cores as caps
        foreach (var jp in mine)
        {
            var core = jp.Core!;
            if (!capOf.TryGetValue(jp.Node, out var old) || core.Outline.Count < 3 || core.Outline.Count > ushort.MaxValue || core.Triangles.Count < 3) continue;
            var home = old.Home;
            // the outline as the cap keeps it: its kerbs sampled every half metre simplified to the tolerance of the sides (the
            // mouths' corners kept, where the ribbons meet it), triangulated again
            var keep = SimplifiedRing(core.Outline, core.Heights, core.Mouths.SelectMany(m => new[] { m.Left, m.Right }).ToList());
            var ring = keep.Select(i => core.Outline[i]).ToList();
            var triangles = keep.Count == core.Outline.Count ? core.Triangles : Junctions.EarClip.Triangulate(ring);
            if (triangles.Count < 3) (keep, ring, triangles) = (Enumerable.Range(0, core.Outline.Count).ToList(), core.Outline, core.Triangles);
            var vertices = new float[ring.Count * 3];
            for (int i = 0; i < ring.Count; i++)
            {
                vertices[i * 3] = (float)(ring[i].X - home.MinE);
                vertices[i * 3 + 1] = (float)core.Heights[keep[i]];
                vertices[i * 3 + 2] = (float)(home.MaxN - ring[i].Y);
            }
            stats.CoreVertices += core.Outline.Count;
            stats.CapVertices += ring.Count;
            var cap = new RoadJunction { Class = old.Cap.Class, Layer = old.Cap.Layer, Vertices = vertices, Indices = triangles.Select(i => (ushort)i).ToArray() };
            var list = caps[home];
            list[list.IndexOf(old.Cap)] = cap;
            writtenCaps.Add(cap);
            stats.Junctions++;
        }

        // 2. every arm's street trimmed to its mouth, or run on to it along the axis
        foreach (var jp in mine)
            foreach (var a in jp.Arms)
            {
                double mouth = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
                var street = Street(a);
                if (street.Count == 0) continue;
                var first = street[0];
                var p = first.Piece.Points;
                int endAt = first.TowardJunction ? p.Length - 3 : 0;
                var end = new Vec2(first.Tile.MinE + p[endAt], first.Tile.MaxN - p[endAt + 2]);
                double delta = mouth - a.Axis!.StationOf(end);
                touched.Add(a.LinkId);
                if (delta > 0.01)
                {
                    // cut off the junction end, piece by piece
                    double left = delta;
                    var byWhole = new Dictionary<RoadSegment, List<RoadSegment>>(ReferenceEqualityComparer.Instance);
                    foreach (var sp in street)
                    {
                        if (left <= 1e-6) break;
                        var pieces = byWhole.TryGetValue(sp.Whole, out var got) ? got : byWhole[sp.Whole] = PiecesOf(sp.Whole).ToList();
                        int at = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
                        double len = sp.Length;
                        touched.Add(sp.Link);
                        if (len <= left + 0.05) { pieces.RemoveAt(at); left -= len; continue; }
                        var points = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, 0, len - left) : StreetPlanner.Slice(sp.Piece, left, double.PositiveInfinity);
                        pieces[at] = Copy(sp.Piece, points);
                        left = 0;
                    }
                    foreach (var (whole, pieces) in byWhole)
                        if (pieces.Count > 0) Replace(whole, segmentOf[street.First(x => ReferenceEquals(x.Whole, whole)).Link].Tile, pieces);
                    stats.Trimmed++;
                }
                else if (delta < -0.01 && net.Links[a.LinkId].Tag is Source source)
                {
                    // run on toward the node along the axis, a point a metre, its heights from the line's own
                    var extra = new List<float>();
                    double s0 = mouth - delta;
                    for (double s = mouth; s < s0 - 0.05; s += 1)
                    {
                        var q = a.Axis.PointAt(s);
                        extra.Add((float)(q.X - first.Tile.MinE)); extra.Add(source.SampleHeight(q)); extra.Add((float)(first.Tile.MaxN - q.Y));
                    }
                    // the junction end first, outward: drawn toward the junction they go after the piece's points, reversed
                    float[] points;
                    if (first.TowardJunction)
                    {
                        var back = new List<float>(p);
                        for (int k = extra.Count - 3; k >= 0; k -= 3) back.AddRange(extra.Skip(k).Take(3));
                        points = back.ToArray();
                    }
                    else points = [.. extra, .. p];
                    var pieces = PiecesOf(first.Whole).ToList();
                    pieces[pieces.FindIndex(x => ReferenceEquals(x, first.Piece))] = Copy(first.Piece, points);
                    Replace(first.Whole, first.Tile, pieces);
                    stats.Extended++;
                }
            }

        // a street's piece split at <at> m from its junction end: the junction side returned, the yield flags kept on their ends
        RoadSegment? Split(StreetPiece sp, double at)
        {
            double len = sp.Length;
            if (at < 0.05 || at > len - 0.05) return null;
            var near = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, len - at, double.PositiveInfinity) : StreetPlanner.Slice(sp.Piece, 0, at);
            var far = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, 0, len - at) : StreetPlanner.Slice(sp.Piece, at, double.PositiveInfinity);
            if (near.Length < 6 || far.Length < 6) return null;
            var a = sp.Piece.Attributes;
            var nearPiece = Copy(sp.Piece, near, a with { Flags = a.Flags & ~(sp.TowardJunction ? RoadAttrFlags.YieldAtStart : RoadAttrFlags.YieldAtEnd) });
            var farPiece = Copy(sp.Piece, far, a with { Flags = a.Flags & ~(sp.TowardJunction ? RoadAttrFlags.YieldAtEnd : RoadAttrFlags.YieldAtStart) });
            var pieces = PiecesOf(sp.Whole).ToList();
            int i = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
            pieces.RemoveAt(i);
            pieces.InsertRange(i, sp.TowardJunction ? [farPiece, nearPiece] : [nearPiece, farPiece]);
            Replace(sp.Whole, sp.Tile, pieces);
            return nearPiece;
        }
        // the street split at <at> m out from the arm's mouth
        void SplitStreetAt(ArmPlan a, double at)
        {
            double d = 0;
            foreach (var sp in Street(a))
            {
                double len = sp.Length;
                if (at > d + 0.05 && at < d + len - 0.05) { Split(sp, at - d); return; }
                d += len;
                if (d > at) return;
            }
        }

        // 2b. the kerbs lowered where a crosswalk crosses the sides (#711): its stretch and 0.6 m either way, its own pieces
        foreach (var jp in mine)
            foreach (var ex in jp.Exterior)
            {
                if (ex.Crosswalk is not { } cw) continue;
                double from = Math.Max(0, cw.From - RampMargin), to = cw.To + RampMargin;
                SplitStreetAt(ex.Arm, from);
                SplitStreetAt(ex.Arm, to);
                double d = 0;
                var lower = new List<StreetPiece>();
                foreach (var sp in Street(ex.Arm))
                {
                    double len = sp.Length;
                    if (d >= from - 0.06 && d + len <= to + 0.06) lower.Add(sp);
                    d += len;
                    if (d > to) break;
                }
                foreach (var sp in lower)
                {
                    var pieces = PiecesOf(sp.Whole).ToList();
                    int i = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
                    if (i < 0) continue;
                    // where the walkers cross, the path takes the grass strips' width and its sloped kerb meets the road (as Lower, #711)
                    static RoadSide Fold(RoadSide s) => s.HasTrack ? s with { BikeDm = (byte)(s.BikeDm + s.VergeDm + s.BufferDm), VergeDm = 0, BufferDm = 0 } : s;
                    var at = sp.Piece.Attributes;
                    pieces[i] = Copy(sp.Piece, sp.Piece.Points, at with { Flags = at.Flags | RoadAttrFlags.LoweredKerbs, Left = Fold(at.Left), Right = Fold(at.Right) });
                    Replace(sp.Whole, sp.Tile, pieces);
                    touched.Add(sp.Link);
                }
                stats.Ramps++;
            }

        // 3. the transitions: every one cut at its end first (so no cut splits a piece another one flagged), then the pieces each
        // covers flagged, each with its TRNS record and the paint on it
        var all =mine.SelectMany(p => p.Arms).Select(a => a.Transition).OfType<Transition>().Distinct().ToList();
        List<(StreetPiece Piece, double From, double To)> Cover(Transition t, bool cut)
        {
            var street = Street(t.Arm);
            double d = 0, length = t.Length;
            var covered = new List<(StreetPiece Piece, double From, double To)>();
            for (int k = 0; k < street.Count && d < length - 0.01; k++)
            {
                var sp = street[k];
                double len = sp.Length;
                // the far mouth of a shared transition: the last piece covers to its end
                bool last = t.Far is not null && (k == street.Count - 1 || d + len >= length - 0.5);
                // cut where the transition ends: the junction side is the transition's
                if (cut && !last && d + len > length + 0.01 && Split(sp, length - d) is { } near)
                {
                    sp = sp with { Piece = near };
                    len = sp.Length;
                }
                // a piece another transition already holds (the two overlap there: a street too short for both) stays with it
                if (!sp.Piece.Attributes.Has(RoadAttrFlags.InTransition)) covered.Add((sp, d, Math.Min(length, d + len)));
                touched.Add(sp.Link);
                d += len;
            }
            return covered;
        }
        foreach (var t in all) Cover(t, cut: true);
        foreach (var t in all)
        {
            var covered = Cover(t, cut: false);
            foreach (var (sp, from, to) in covered)
            {
                var flagged = Copy(sp.Piece, sp.Piece.Points, sp.Piece.Attributes with { Flags = sp.Piece.Attributes.Flags | RoadAttrFlags.InTransition });
                var pieces = PiecesOf(sp.Whole).ToList();
                pieces[pieces.FindIndex(x => ReferenceEquals(x, sp.Piece))] = flagged;
                Replace(sp.Whole, sp.Tile, pieces);
                var rt = PieceTransition(t, flagged, from, to, sp.TowardJunction);
                Get(transitionsOf, sp.Tile).Add(rt);
                stats.Pieces++;
                var into = Get(paint, sp.Tile);
                foreach (var l in t.Markings?.Lines ?? [])
                {
                    double f = Math.Max(l.From, from), e = Math.Min(l.To, to);
                    if (e - f < 0.05) continue;
                    var (pf, pe) = sp.TowardJunction ? (to - e, to - f) : (f - from, e - from);
                    int n = t.Strips.Count;
                    into.Add(RoadPaint.AlongTransition(rt, sp.TowardJunction ? l.Boundary : n - l.Boundary, l.Type, l.Rgba, l.Width, l.Dash, l.Gap,
                        sp.TowardJunction ? l.Offset : -l.Offset, pf, pe));
                    stats.Lines++;
                }
            }
            Marks(t, covered, paint, stats);
            if (mine.First(p => p.Arms.Contains(t.Arm)) is { } owner && !stats.WithTransitions.Contains(owner.Centre)) stats.WithTransitions.Add(owner.Centre);
            stats.Transitions++;
        }

        // 4. the streets' own paint laid again on what the transitions leave of them
        foreach (int link in touched)
        {
            if (!segmentOf.TryGetValue(link, out var so) || !paintStation.TryGetValue(link, out var ps)) continue;
            var into = Get(paint, so.Tile);
            into.RemoveAll(p => ReferenceEquals(p.Segment, so.Painted));
            var pieces = PiecesOf(so.Segment);
            double s = 0;
            for (int i = 0; i < pieces.Count;)
            {
                if (pieces[i].Attributes.Has(RoadAttrFlags.InTransition)) { s += RoadPaintGeometry.Length(pieces[i].Points); i++; continue; }
                int j = i;
                var points = new List<float>();
                double runFrom = s;
                while (j < pieces.Count && !pieces[j].Attributes.Has(RoadAttrFlags.InTransition))
                {
                    var pp = pieces[j].Points;
                    points.AddRange(points.Count == 0 ? pp : pp.Skip(3));
                    s += RoadPaintGeometry.Length(pp);
                    j++;
                }
                if (points.Count >= 6)
                    PaintEmitter.Emit(Copy(so.Painted, points.ToArray()), ps.Station + runFrom, into, i == 0 && ps.Start, j == pieces.Count && ps.End, bikeLanes: false);
                i = j;
            }
        }

        // 5. the exterior (phase 4): every arm's bars, refuge and signs
        foreach (var jp in mine)
        {
            WriteExterior(jp, result, segmentOf, finalPieces, paint, areas, signs, signalStats, stats);
            if (jp.Lights) WriteSignals(jp, result, signals, approaches, cantons, signalStats, stats);
        }
    }

    /// <summary>
    /// A transition's record over one piece it covers, from station <paramref name="from"/> to <paramref name="to"/> (from its
    /// mouth): its stations there along the piece's drawing, its strips seen along the drawing (mirrored where the piece is drawn
    /// away from the junction), every number rounded as the file keeps it.
    /// </summary>
    private static RoadTransition PieceTransition(Transition t, RoadSegment piece, double from, double to, bool towardJunction)
    {
        float pieceLength = SegmentSections.Stations(piece)[^1];
        var knots = t.Stations.Where(s => s > from + 0.01 && s < to - 0.01).Prepend(from).Append(to).ToList();
        // the piece's stations, ascending along its drawing, scaled onto its own length
        double span = Math.Max(1e-6, to - from);
        var order = knots.Select(s => (Piece: (towardJunction ? to - s : s - from) / span * pieceLength, At: Math.Min(s, t.Length))).OrderBy(x => x.Piece).ToList();
        var stations = order.Select(x => RoadTransition.Cm(x.Piece)).ToArray();
        stations[0] = 0;
        stations[^1] = RoadTransition.Cm(pieceLength);
        int n = t.Strips.Count;
        int first = t.Strips.FindIndex(x => x.Strip.InCarriageway), last = t.Strips.FindLastIndex(x => x.Strip.InCarriageway);
        var strips = new List<RoadTransitionStrip>(n);
        for (int j = 0; j < n; j++)
        {
            int k = towardJunction ? j : n - 1 - j;
            var x = t.Strips[k].Strip;
            sbyte travel = x.Way == StripWay.None ? (sbyte)0 : (x.Way == StripWay.In) == towardJunction ? (sbyte)1 : (sbyte)-1;
            // a band is the piece's own (the street planner's: a gap for a driveway, a narrower sidewalk) changed as the transition
            // changes it from the normal section: a transition changes widths, the street decides the rest
            double? own = null;
            if (k < first || k > last)
            {
                // the side it lies on, along the piece's drawing
                bool rightOfDrawing = (k > last) == towardJunction;
                own = BandWidth(rightOfDrawing ? piece.Attributes.Right : piece.Attributes.Left, x.Kind);
            }
            double normal = 0;
            foreach (var y in t.Arm.Normal.Strips) if (y.Id == x.Id) normal = y.Width;
            strips.Add(new RoadTransitionStrip
            {
                Kind = x.Kind, Id = x.Id, Travel = travel, Moves = x.Moves, Flags = x.SharedWithBikes ? TransitionStripFlags.SharedWithBikes : 0,
                Widths = order.Select(o => RoadTransition.Cm(own is { } w ? Math.Max(0, w + t.WidthAt(k, o.At) - normal) : t.WidthAt(k, o.At))).ToArray(),
            });
        }
        return new RoadTransition
        {
            Segment = piece, Stations = stations, Strips = strips, Anchor = towardJunction ? t.Anchor : n - t.Anchor,
            Centre = order.Select(o => RoadTransition.Cm(towardJunction ? t.CentreAt(o.At) : -t.CentreAt(o.At))).ToArray(),
        };
    }

    /// <summary>A cap's outline may leave its kerb by this much in plan and in height (as the sides' vertices, <c>RoadStreetBuilder</c>).</summary>
    /// <summary>A crosswalk lowers the kerbs either side this far past its bars each way (#711).</summary>
    private const double RampMargin = 0.6;

    private const double CapPlanTolerance = 0.015, CapHeightTolerance = 0.01;

    /// <summary>
    /// The indices of a ring's points worth keeping (Douglas-Peucker in plan and height on each run between two points that stay):
    /// every point of <paramref name="fixedAt"/> stays.
    /// </summary>
    private static List<int> SimplifiedRing(List<Vec2> ring, List<double> heights, List<Vec2> fixedAt)
    {
        int n = ring.Count;
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = fixedAt.Any(f => f.DistanceTo(ring[i]) < 1e-6);
        if (!keep.Any(k => k)) keep[0] = true;
        void Run(int a, int b)
        {
            // points strictly between a and b round the ring
            int len = (b - a + n) % n;
            if (len < 2) return;
            double worst = 0;
            int at = -1;
            var pa = ring[a];
            var ab = ring[b] - pa;
            double l2 = ab.LengthSquared;
            for (int s = 1; s < len; s++)
            {
                int i = (a + s) % n;
                double t = l2 < 1e-12 ? 0 : Math.Clamp((ring[i] - pa).Dot(ab) / l2, 0, 1);
                double plan = ring[i].DistanceTo(pa + ab * t) / CapPlanTolerance;
                double up = Math.Abs(heights[a] + (heights[b] - heights[a]) * t - heights[i]) / CapHeightTolerance;
                double off = Math.Max(plan, up);
                if (off > 1 && off > worst) { worst = off; at = i; }
            }
            if (at < 0) return;
            keep[at] = true;
            Run(a, at);
            Run(at, b);
        }
        var fixedIdx = Enumerable.Range(0, n).Where(i => keep[i]).ToList();
        for (int k = 0; k < fixedIdx.Count; k++) Run(fixedIdx[k], fixedIdx[(k + 1) % fixedIdx.Count]);
        return Enumerable.Range(0, n).Where(i => keep[i]).ToList();
    }

    /// <summary>A band's width on a side as the street planner laid it (0 where the side has none).</summary>
    private static double BandWidth(RoadSide side, StripKind kind) => kind switch
    {
        StripKind.Sidewalk => side.SidewalkDm / 10.0,
        StripKind.Path => side.HasTrack ? side.BikeDm / 10.0 : 0,
        StripKind.Verge => side.HasTrack ? side.VergeDm / 10.0 : 0,
        StripKind.Buffer => side.HasTrack ? side.BufferDm / 10.0 : 0,
        _ => 0,
    };

    /// <summary>
    /// A transition's hatch stripes, arrows and bike symbols as geometry on the pieces it covers: each point (station, offset)
    /// found on its piece, on the road's height there.
    /// </summary>
    private static void Marks(Transition t, List<(StreetPiece Piece, double From, double To)> covered, Dictionary<TileId, List<RoadPaint>> paint, LayoutWriteStats stats)
    {
        if (t.Markings is not { } m || covered.Count == 0) return;
        // a point of the transition on its piece: tile-local position, and the drawing's forward direction there
        (TileId Tile, float X, float Y, float Z, float Fx, float Fz, bool Toward)? At(double s, double o)
        {
            var hit = covered.FirstOrDefault(c => s >= c.From - 1e-6 && s <= c.To + 1e-6);
            if (hit.Piece is null) return null;
            var sp = hit.Piece;
            var p = sp.Piece.Points;
            double len = sp.Length, span = Math.Max(1e-6, hit.To - hit.From);
            double along = (sp.TowardJunction ? hit.To - s : s - hit.From) / span * len;
            double travelled = 0;
            for (int i = 0; i + 1 < sp.Piece.PointCount; i++)
            {
                float ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
                double seg = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                if (travelled + seg < along - 1e-6 && i + 2 < sp.Piece.PointCount) { travelled += seg; continue; }
                double u = seg < 1e-9 ? 0 : Math.Clamp((along - travelled) / seg, 0, 1);
                float fx = (float)((bx - ax) / Math.Max(seg, 1e-9)), fz = (float)((bz - az) / Math.Max(seg, 1e-9));
                // right of the drawing (x east, z south): (-fz, fx); a piece drawn away from the junction sees offsets mirrored
                double off = sp.TowardJunction ? o : -o;
                float x = (float)(ax + (bx - ax) * u - fz * off), z = (float)(az + (bz - az) * u + fx * off);
                float y = (float)(p[i * 3 + 1] + (p[i * 3 + 4] - p[i * 3 + 1]) * u);
                return (sp.Tile, x, y, z, fx, fz, sp.TowardJunction);
            }
            return null;
        }
        foreach (var quad in m.Stripes)
        {
            var pts = quad.Select(q => At(q.S, q.O)).ToList();
            if (pts.Any(q => q is null) || pts.Select(q => q!.Value.Tile).Distinct().Count() > 1) continue;
            Get(paint, pts[0]!.Value.Tile).Add(new RoadPaint
            {
                Shape = PaintShape.Triangles, Type = PaintType.Hatch, Rgba = PaintEmitter.White,
                Vertices = pts.SelectMany(q => new[] { q!.Value.X, q.Value.Y, q.Value.Z }).ToArray(), Indices = [0, 1, 2, 0, 2, 3],
            });
            stats.Marks++;
        }
        foreach (var mark in m.Marks)
        {
            var (f0, f1) = t.SpanAt(mark.Strip, mark.Station);
            double o = (f0 + f1) * 0.5;
            if (mark.Bike)
            {
                // the symbol a metre along the lane, toward the mouth (the riders arriving) or away from it
                double s0 = mark.Station - BikePlanner.SymbolSize, s1 = mark.Station;
                if (At(s0, o) is not { } a || At(s1, o) is not { } b || a.Tile != b.Tile) continue;
                // the riders go toward the mouth (an arriving lane, s falling) or away from it; the glyph reads along the drawing
                bool ridersAlongDrawing = mark.TowardFar ? !a.Toward : a.Toward;
                var (p0, p1) = a.Toward ? (b, a) : (a, b);
                Get(paint, a.Tile).Add(new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.BikeSymbol, Rgba = PaintEmitter.Yellow, Width = BikePlanner.SymbolSize,
                    Variant = ridersAlongDrawing ? (byte)0 : RoadPaintGeometry.BikeReversed, Vertices = [p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z],
                });
                stats.Marks++;
                continue;
            }
            // an arrow from its tail toward the mouth
            double tipAt = mark.Station - PaintEmitter.ArrowLength;
            if (At(mark.Station, o) is not { } tail || At(tipAt, o) is not { } tip || tail.Tile != tip.Tile) continue;
            float dx = tip.X - tail.X, dz = tip.Z - tail.Z;
            Get(paint, tail.Tile).Add(PaintEmitter.Arrow(tail.X, tail.Y, tail.Z, dx, dz, mark.Arrow, tip.Y));
            stats.Marks++;
        }
    }
}
