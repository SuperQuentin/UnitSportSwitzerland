namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Left-turn pockets (#123) on main-road approaches. Where a two-way Major or Road approach of a
/// main road (#121) has a yielding car road to its left, the approach is widened by one lane on
/// its right over a taper and a storage length: the original approach lane becomes the left-turn
/// pocket and through traffic moves into the new lane. The ribbon keeps one width per segment, so
/// the widening is a flush <see cref="AreaPropType.Pavement"/> strip along the segment's edge (the
/// road blend holds the ground under it at the road's height, so it is in the collision too).
/// Paint: the old edge line stops where the taper starts, a new one runs along the strip's outer
/// edge, a lane divider (dashed, solid for the last <see cref="TurnSolid"/> m) along the old edge,
/// and arrows in both lanes. No lane-level topology is stored: traffic still drives the
/// original lane.
/// </summary>
public static partial class TileRewriter
{
    public sealed class TurnLaneStats
    {
        public int Candidates, Placed, Short, Building, OtherLine, Ground, Seam, NoSegment, Arrows;

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    turn lanes (#123): {Candidates:N0} main-road approaches with a left turn, {Placed:N0} pockets placed, {Arrows:N0} arrows; " +
            $"rejected: approach too short {Short:N0}, building {Building:N0}, another line {OtherLine:N0}, ground off the road {Ground:N0}, tile seam {Seam:N0}, no segment {NoSegment:N0}\n");
    }

    // an urban-sized pocket: 20 m taper, 20 m storage, 5 m kept clear of the junction at the other end
    private const double TurnTaper = 20, TurnStorage = 20, TurnLane = 3.0, TurnSolid = 15, TurnClear = 5;
    private const double TurnStation = 2.5, TurnGroundStep = 1.2, TileSizeM = 1000;

    private static double Sq(double v) => v * v;

    private static void EmitTurnLanes(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile)> segmentOf, Dictionary<TileId, List<RoadSegment>> output,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadAreaProp>> areas, TurnLaneStats stats)
    {
        var net = result.Network;
        var indexes = new Dictionary<TileId, EmbankmentPlanner.LineIndex>();
        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Kind != PriorityPlanner.Kind.Main) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var arm = plan.Arms[i];
                if (arm.Role != PriorityPlanner.Role.Main || net.Links[arm.LinkId].Tag is not Source source) continue;
                var seg0 = source.Segment;
                if (seg0.Class is not (RoadClass.Major or RoadClass.Road) || seg0.Surface != RoadSurface.Paved
                    || (seg0.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel | RoadFlags.Divided)) != 0
                    || CrossSectionPlanner.Attributes(source.Line).OneWay != 0) continue;

                // the approaching driver's way, and whether a car road leaves to their left / right
                var d = Vec2.FromHeading(junction.Arms[i].OutwardHeading) * -1;
                bool left = false, right = false;
                for (int k = 0; k < junction.Arms.Count && k < plan.Arms.Count; k++)
                {
                    if (k == i || plan.Arms[k].Role != PriorityPlanner.Role.Yield) continue;
                    if (InfoOf(net.Links[plan.Arms[k].LinkId]) is not { } info || info.Class > RoadClass.Lane) continue;
                    var v = Vec2.FromHeading(junction.Arms[k].OutwardHeading);
                    if (d.X * v.X + d.Y * v.Y > 0.87) continue;   // carries on nearly straight
                    if (d.X * v.Y - d.Y * v.X > 0) left = true; else right = true;
                }
                if (!left) continue;
                stats.Candidates++;
                if (!segmentOf.TryGetValue(arm.LinkId, out var so)) { stats.NoSegment++; continue; }
                if (!indexes.TryGetValue(so.Tile, out var lines))
                    indexes[so.Tile] = lines = new EmbankmentPlanner.LineIndex(output[so.Tile]);
                PlacePocket(so.Segment, so.Tile, output[so.Tile].IndexOf(so.Segment), arm.End == LinkEnd.End, right,
                    lines, grids, buildings, Get(paint, so.Tile), Get(areas, so.Tile), stats);
            }
        }
    }

    private static void PlacePocket(RoadSegment seg, TileId id, int self, bool atEnd, bool rightTurn,
        EmbankmentPlanner.LineIndex lines, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        List<RoadPaint> paint, List<RoadAreaProp> areas, TurnLaneStats stats)
    {
        // horizontal arc length at each point
        int n = seg.PointCount;
        var p = seg.Points;
        var along = new double[n];
        for (int k = 1; k < n; k++)
            along[k] = along[k - 1] + Math.Sqrt(Sq(p[k * 3] - p[k * 3 - 3]) + Sq(p[k * 3 + 2] - p[k * 3 - 1]));
        double length = along[^1];
        double pocket = TurnTaper + TurnStorage;
        if (length < pocket + TurnClear) { stats.Short++; return; }

        // traffic keeps right: towards the end it drives right of the drawing, towards the start left of it
        int s = atEnd ? 1 : -1;
        double half = seg.Width * 0.5;
        double a0 = atEnd ? length - pocket : 0, a1 = atEnd ? length : pocket;
        double Widen(double a) => TurnLane * Math.Clamp(atEnd ? (a - a0) / TurnTaper : (a1 - a) / TurnTaper, 0, 1);

        (double X, double Y, double Z, double Rx, double Rz) At(double a)
        {
            int k = 1;
            while (k < n - 1 && along[k] < a) k++;
            double span = Math.Max(1e-9, along[k] - along[k - 1]), t = Math.Clamp((a - along[k - 1]) / span, 0, 1);
            double x = p[k * 3 - 3] + (p[k * 3] - p[k * 3 - 3]) * t, z = p[k * 3 - 1] + (p[k * 3 + 2] - p[k * 3 - 1]) * t;
            double y = p[k * 3 - 2] + (p[k * 3 + 1] - p[k * 3 - 2]) * t;
            double fx = (p[k * 3] - p[k * 3 - 3]) / span, fz = (p[k * 3 + 2] - p[k * 3 - 1]) / span;
            return (x, y, z, -fz * s, fx * s);   // the side the strip widens to, X east and Z south
        }

        var stations = new List<double>();
        for (double a = a0; a < a1 - 1e-6; a += TurnStation) stations.Add(a);
        if (!stations.Contains(atEnd ? a0 + TurnTaper : a1 - TurnTaper)) stations.Add(atEnd ? a0 + TurnTaper : a1 - TurnTaper);
        stations.Add(a1);
        stations.Sort();

        // room for it: no building, no other line, the ground at the road's height, inside the tile
        foreach (double a in stations)
        {
            double w = Widen(a);
            if (w < 0.3) continue;
            var (x, y, z, rx, rz) = At(a);
            foreach (double o in (ReadOnlySpan<double>)[half + w * 0.5, half + w + 0.5])
            {
                double px = x + rx * o, pz = z + rz * o;
                if (px < 0 || pz < 0 || px > TileSizeM || pz > TileSizeM) { stats.Seam++; return; }
                var lv95 = new Vec2(id.MinE + px, id.MaxN - pz);
                if (buildings.Contains(lv95)) { stats.Building++; return; }
                if (lines.Covering(px, pz, self, x, z, half + 1.0) is not null) { stats.OtherLine++; return; }
                if (grids is not null && SampleGround(grids, lv95.X, lv95.Y) is var g && !double.IsNaN(g)
                    && Math.Abs(g - y) > TurnGroundStep) { stats.Ground++; return; }
            }
        }

        // the strip: inner edge on the ribbon's edge, outer edge widened
        var v = new List<float>();
        var outer = new List<float>();
        foreach (double a in stations)
        {
            double w = Widen(a);
            var (x, y, z, rx, rz) = At(a);
            v.AddRange([(float)(x + rx * half), (float)y, (float)(z + rz * half)]);
            v.AddRange([(float)(x + rx * (half + w)), (float)y, (float)(z + rz * (half + w))]);
            double e = half + w - PaintEmitter.EdgeLineInset;
            outer.AddRange([(float)(x + rx * e), (float)y, (float)(z + rz * e)]);
        }
        var idx = new List<ushort>();
        for (int k = 0; k + 1 < stations.Count; k++)
        {
            ushort i0 = (ushort)(k * 2), i1 = (ushort)(k * 2 + 1), i2 = (ushort)(k * 2 + 2), i3 = (ushort)(k * 2 + 3);
            idx.AddRange([i0, i2, i3, i0, i3, i1]);
        }
        areas.Add(new RoadAreaProp
        {
            Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
            Vertices = v.ToArray(), Indices = idx.ToArray(),
        });

        // the old edge line stops where the taper starts; a new one follows the strip
        double edgeOffset = s * half;
        var edge = paint.FirstOrDefault(q => q.Segment == seg && q.Dash == 0 && Math.Sign(q.Offset) == s && Math.Abs(q.Offset) > half * 0.5);
        if (edge is not null)
        {
            paint.Remove(edge);
            double from = atEnd ? edge.From : a1, to = atEnd ? a0 : edge.To;
            if (to - from > 1)
                paint.Add(RoadPaint.AlongSegment(seg, edge.Type, edge.Rgba, edge.Width, 0, 0, edge.Offset, from, to, edge.Variant));
            paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = PaintEmitter.White, Width = edge.Width,
                Vertices = outer.ToArray(),
            });
        }

        // the lane divider along the old edge: dashed, then solid near the junction
        double taperEnd = atEnd ? a0 + TurnTaper : a1 - TurnTaper;
        double solidFrom = atEnd ? a1 - TurnSolid : a0, solidTo = atEnd ? a1 : a0 + TurnSolid;
        double dashFrom = atEnd ? taperEnd : solidTo, dashTo = atEnd ? solidFrom : taperEnd;
        paint.Add(RoadPaint.AlongSegment(seg, PaintType.WhiteDashed, PaintEmitter.White, PaintEmitter.LineWidth, 3f, 3f, edgeOffset, dashFrom, dashTo));
        paint.Add(RoadPaint.AlongSegment(seg, PaintType.WhiteSolid, PaintEmitter.White, PaintEmitter.LineWidth, 0, 0, edgeOffset, solidFrom, solidTo));

        // arrows, two per lane: left in the pocket, straight (and right) in the new lane
        foreach (double back in (ReadOnlySpan<double>)[6, 21])
        {
            double a = atEnd ? a1 - back : a0 + back;
            var (x, y, z, rx, rz) = At(a);
            // the strip lies on the driver's right (rx, rz): their forward is that turned a quarter left
            double fx = rz, fz = -rx;
            paint.Add(Arrow(x + rx * half * 0.5, y, z + rz * half * 0.5, fx, fz, PaintArrow.Left));
            paint.Add(Arrow(x + rx * (half + TurnLane * 0.5), y, z + rz * (half + TurnLane * 0.5), fx, fz,
                rightTurn ? PaintArrow.Straight | PaintArrow.Right : PaintArrow.Straight));
            stats.Arrows += 2;
        }
        stats.Placed++;
    }

    /// <summary>
    /// A road arrow (#123) as paint triangles, 5 m long, its tail at (x, z) and pointing along
    /// (fx, fz) (tile-local, X east and Z south): a straight shaft with a head, a left or right
    /// branch bent off it at 45 degrees with its own head.
    /// </summary>
    private static RoadPaint Arrow(double x, double y, double z, double fx, double fz, PaintArrow kind)
    {
        double len = Math.Sqrt(fx * fx + fz * fz);
        fx /= len; fz /= len;
        // the driver's left, X east and Z south: forward (fx, fz) turned a quarter to the left
        double lx = fz, lz = -fx;
        var v = new List<float>();
        var idx = new List<ushort>();
        void Pt(double f, double l) => v.AddRange([(float)(x + fx * f + lx * l), (float)y, (float)(z + fz * f + lz * l)]);
        void Quad(double f0, double l0, double f1, double l1, double f2, double l2, double f3, double l3)
        {
            ushort b = (ushort)(v.Count / 3);
            Pt(f0, l0); Pt(f1, l1); Pt(f2, l2); Pt(f3, l3);
            idx.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
        }
        void Tri(double f0, double l0, double f1, double l1, double f2, double l2)
        {
            ushort b = (ushort)(v.Count / 3);
            Pt(f0, l0); Pt(f1, l1); Pt(f2, l2);
            idx.AddRange([b, (ushort)(b + 1), (ushort)(b + 2)]);
        }
        const double W = 0.09, Head = 1.4, HeadW = 0.35;
        bool straight = (kind & PaintArrow.Straight) != 0;
        double shaft = straight ? 5.0 - Head : 2.6;
        Quad(0, -W, shaft, -W, shaft, W, 0, W);
        if (straight) Tri(shaft, -HeadW, 5.0, 0, shaft, HeadW);
        foreach (var (bit, sign) in (ReadOnlySpan<(PaintArrow, int)>)[(PaintArrow.Left, 1), (PaintArrow.Right, -1)])
        {
            if ((kind & bit) == 0) continue;
            // a 45 degree branch from 2.6 m up the shaft, 1.4 m long, then its head; (bf, bl) its
            // way and (nf, nl) across it, in forward/left metres
            const double F0 = 2.6, Branch = 1.4, BranchHead = 1.2;
            double c = Math.Sqrt(0.5);
            double bf = c, bl = sign * c, nf = -sign * c, nl = c;
            double ef = F0 + bf * Branch, el = bl * Branch;
            Quad(F0 - nf * W, -nl * W, ef - nf * W, el - nl * W, ef + nf * W, el + nl * W, F0 + nf * W, nl * W);
            Tri(ef - nf * HeadW, el - nl * HeadW, ef + bf * BranchHead, el + bl * BranchHead, ef + nf * HeadW, el + nl * HeadW);
        }
        return new RoadPaint
        {
            Shape = PaintShape.Triangles, Type = PaintType.Arrow, Variant = (byte)kind, Rgba = PaintEmitter.White,
            Vertices = v.ToArray(), Indices = idx.ToArray(),
        };
    }
}
