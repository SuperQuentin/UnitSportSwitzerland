namespace UnitSport.Tools.RoadGen.Meshing;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Road paint for the <c>.road</c> v3 <c>PANT</c> layer (#116), from one finished segment of the
/// network stage: already trimmed at its junctions (so nothing is painted inside one), clipped to
/// its tile, carrying its v3 attributes.
///
/// <para>
/// Lines are offset from the segment's own tile-local points exactly as <c>RoadMeshBuilder</c>
/// offsets the ribbon edges (per-vertex bisector, no miter), so each line lies on the ribbon it is
/// painted on. Swiss rules (SSV Art. 73, 76, 90; widths and dash lengths in
/// docs/notes/tools/road-markings.md), following the lane count and one-way data in ATTR
/// (0 = class default): a centre Leitlinie on two-way roads wide enough for it, edge lines and
/// lane dividers on motorways, expressways, ramps and divided carriageways, on the lanes
/// <see cref="RoadCrossSection"/> lays out.
/// </para>
///
/// <para>
/// Dashes keep their phase across tile seams: a dashed polyline starts on a dash boundary of the
/// TLM line's own along-line metre (the raw input's key), and the dash cut by the seam is emitted
/// as a short solid lead-in. Toward a junction or a dead end, no stub shorter than 40 % of a dash.
/// </para>
/// </summary>
public static class PaintEmitter
{
    /// <summary>RGBA of today's marking colour (<c>ps1_road.gdshader</c> marking_color, sRGB).</summary>
    public const uint White = 0xE0DED1FF;

    /// <summary>
    /// Yellow of bike markings (SSV Art. 74a: Radstreifen 6.09, Velo symbol), toned like
    /// <see cref="White"/> (the Commons diagrams use #FCD213).
    /// </summary>
    public const uint Yellow = 0xE6BE33FF;

    /// <summary>Red surface of a bike lane across a junction (RAL 3020 Verkehrsrot, Stadt Bern C 2.10.10), toned down.</summary>
    public const uint Red = 0xB8392CFF;

    /// <summary>
    /// Leitlinie, Sicherheitslinie and Randlinie of ordinary roads: 15 cm (SN 640 850a, as the
    /// cantonal marking directives quote it; table in docs/notes/tools/road-markings.md). The
    /// shader's legibility dither takes a line out below ~1.5 px.
    /// </summary>
    public const float LineWidth = 0.15f;

    /// <summary>
    /// Randlinie of a motorway/expressway carriageway: 0.20 m (0.20-0.25 m, ASTRA 15002 (2023)
    /// Abb. 5.4/6.2 after SN 640 854a; see the note).
    /// </summary>
    public const float HighSpeedEdgeWidth = 0.20f;

    /// <summary>
    /// Leitlinie dash and gap: 3 m / 6 m, the SN 640 850a "Regelfall" in and out of built-up areas
    /// (BE Handbuch Markierung 1, Stadt Bern Normalien C 2.10.8; LU's 3 m / 3 m in town is a
    /// cantonal variant); motorways and expressways 6 m / 12 m (ASTRA 15002 Abb. 5.4).
    /// </summary>
    public static (float Dash, float Gap) Leitlinie(RoadSegment seg) =>
        RoadCrossSection.IsHighSpeed(seg.Class) ? (6f, 12f) : (3f, 6f);

    /// <summary>
    /// A rural Randlinie's axis lies this far in from the carriageway edge: 0.15 m clear, then the
    /// 0.15 m line (BE Handbuch Markierung 1 ch. 21).
    /// </summary>
    public const float EdgeLineInset = 0.225f;

    /// <summary>
    /// Two-way roads narrower than this get no centre line: 5.5 m in built-up areas, 6 m outside
    /// (SN 640 862, BE handbook ch. 16; FR 906 F: Leitlinie from 5.50 m).
    /// </summary>
    public static float MinCentreLineWidth(bool urban) => urban ? 5.5f : 6.0f;

    /// <summary>Edge lines at this fraction of the half width, where the shader drew them.</summary>
    private const float EdgeFraction = 0.87f;

    private const float TileSize = 1000f;

    /// <summary>Region numbers: primitives and what the game will draw from them.</summary>
    public sealed class Tally
    {
        public int Primitives, Vertices, TilesWithPaint;
        public long Triangles;
        public int MaxTriangles;
        public string MaxTile = "";

        public void Add(RoadTile tile)
        {
            if (tile.Paint.Count == 0) return;
            TilesWithPaint++;
            int tris = 0;
            foreach (var p in tile.Paint)
            {
                Primitives++;
                Vertices += p.Vertices.Length / 3;
                tris += RoadPaintGeometry.TriangleCount(p);
            }
            Triangles += tris;
            if (tris > MaxTriangles) { MaxTriangles = tris; MaxTile = $"{tile.Id.E}_{tile.Id.N}"; }
        }

        public string Format(int tiles) => string.Create(CultureInfo.InvariantCulture,
            $"    paint     {Primitives:N0} primitives, {Vertices:N0} vertices on {TilesWithPaint}/{tiles} tiles; "
            + $"game triangles {(double)Triangles / Math.Max(1, tiles):F0}/tile, max {MaxTriangles:N0} ({MaxTile})");
    }

    /// <param name="station">Along-line metre of the segment's first point on its TLM line (0 unknown).</param>
    /// <param name="startsAtJunction">The segment's first point is a junction's mouth or a dead end
    /// (a bike lane starts or ends there: its symbol, #120); same for <paramref name="endsAtJunction"/>.</param>
    /// <param name="bikeLanes">Paint the bike lanes too; the network stage paints them later, on the
    /// street's final pieces (<see cref="BikeLanes"/>), which may be shifted off a turn lane.</param>
    public static void Emit(RoadSegment seg, double station, List<RoadPaint> into,
        bool startsAtJunction = false, bool endsAtJunction = false, bool bikeLanes = true)
    {
        if (seg.Surface != RoadSurface.Paved || seg.Class > RoadClass.Minor || seg.PointCount < 2) return;
        if ((seg.Flags & RoadFlags.Stairs) != 0) return;

        var a = seg.Attributes;
        float half = seg.Width * 0.5f;
        bool divided = (seg.Flags & RoadFlags.Divided) != 0;
        bool motorway = seg.Class is RoadClass.Motorway or RoadClass.Expressway;
        bool oneDirection = a.OneWay != 0 || divided || motorway || seg.Class == RoadClass.Ramp;

        // the only solid lines are edge lines (Randlinien): broad on motorways and expressways
        float solid = RoadCrossSection.IsHighSpeed(seg.Class) ? HighSpeedEdgeWidth : LineWidth;
        void Line(float offset, bool dashed)
        {
            if (dashed) AddDashed(seg, offset, station, into);
            else Add(into, RoadPaint.AlongSegment(seg, PaintType.WhiteSolid, White, solid, 0, 0, offset));
        }

        if (CrossSectionLines(seg, Line)) return;

        // bike lanes (#120) take their width off the carriageway's edges; the car lanes share the rest
        float leftBike = a.Left.HasLane ? a.Left.BikeDm / 10f : 0f, rightBike = a.Right.HasLane ? a.Right.BikeDm / 10f : 0f;
        if (oneDirection)
        {
            int lanes = Math.Max(a.LanesForward, a.LanesBackward);
            if (lanes == 0) lanes = motorway ? 2 : 1;
            bool edges = motorway || divided || seg.Class == RoadClass.Ramp;
            float e = edges ? half * EdgeFraction : half;
            // no Randlinie where a bike lane runs: its yellow line is the edge (Stadt Bern C 2.10.2 §6)
            if (edges && leftBike == 0) Line(-e, false);
            if (edges && rightBike == 0) Line(e, false);
            float lo = leftBike > 0 ? -half + leftBike : -e, hi = rightBike > 0 ? half - rightBike : e;
            for (int k = 1; k < lanes; k++) Line(lo + k * (hi - lo) / lanes, true);
        }
        else if (!BikePlanner.IsKernfahrbahn(seg.Width, a.Left, a.Right)   // a Kernfahrbahn has no centre line
                 && seg.Width >= MinCentreLineWidth(a.Has(RoadAttrFlags.Urban)))   // nor a road too narrow to pass on its halves
        {
            // traffic keeps right: the lanes against the drawing are on its left
            int back = Math.Max(1, (int)a.LanesBackward), fwd = Math.Max(1, (int)a.LanesForward);
            float lo = -half + leftBike, w = (2 * half - leftBike - rightBike) / (back + fwd);
            for (int k = 1; k < back + fwd; k++) Line(lo + k * w, true);   // k == back is the centre
            // Randlinien outside built-up areas, on roads wide enough for a centre line too (BE
            // Handbuch Markierung 1 p. 17, Stadt Bern C 2.10.11; inside a town only exceptionally);
            // none beside a bike lane
            if (!a.Has(RoadAttrFlags.Urban))
            {
                if (leftBike == 0) Line(-half + EdgeLineInset, false);
                if (rightBike == 0) Line(half - EdgeLineInset, false);
            }
        }

        if (bikeLanes) BikeLanes(seg, station, into, startsAtJunction, endsAtJunction);
    }

    /// <summary>
    /// The bike lanes of a segment or street piece (#120). A lane beside a turn lane's widening
    /// (#123: <see cref="RoadSide.ShiftStartCm"/>/<see cref="RoadSide.ShiftEndCm"/>) moves out with
    /// the carriageway's edge, through traffic taking its old place: along a steady shift its line
    /// is offset by it, along a taper drawn as its own geometry, with no symbol there.
    /// </summary>
    public static void BikeLanes(RoadSegment seg, double station, List<RoadPaint> into, bool startsAtJunction, bool endsAtJunction,
        bool skipLeft = false, bool skipRight = false)
    {
        if (seg.Surface != RoadSurface.Paved || seg.Class > RoadClass.Minor || seg.PointCount < 2) return;
        var a = seg.Attributes;
        foreach (bool right in (ReadOnlySpan<bool>)[false, true])
        {
            var side = right ? a.Right : a.Left;
            if (!side.HasLane || (right ? skipRight : skipLeft)) continue;
            if (side.ShiftStartCm == side.ShiftEndCm) BikeLane(seg, right, station, into, startsAtJunction, endsAtJunction);
            else TaperLane(seg, right, into);
        }
    }

    /// <summary>A bike lane's line along a turn lane's taper: its offset follows the shift vertex by vertex.</summary>
    private static void TaperLane(RoadSegment seg, bool right, List<RoadPaint> into)
    {
        var side = right ? seg.Attributes.Right : seg.Attributes.Left;
        float sign = right ? 1f : -1f, half = seg.Width * 0.5f, lane = side.BikeDm / 10f;
        var along = RoadStreetSection.Fractions(seg);
        var p = seg.Points;
        int n = seg.PointCount;
        float lift = (seg.Flags & RoadFlags.Bridge) != 0 ? RoadPaintGeometry.BridgeLift : 0f;
        var v = new List<float>(n * 3);
        for (int i = 0; i < n; i++)
        {
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(n - 1, i + 1);
            float fx = p[i1 * 3] - p[i0 * 3], fz = p[i1 * 3 + 2] - p[i0 * 3 + 2], fl = MathF.Sqrt(fx * fx + fz * fz);
            if (fl < 1e-4f) continue;
            fx /= fl; fz /= fl;
            float o = sign * (half + side.ShiftAt(along[i]) - lane);
            v.Add(p[i * 3] - fz * o); v.Add(p[i * 3 + 1] + lift); v.Add(p[i * 3 + 2] + fx * o);
        }
        if (v.Count < 6) return;
        Add(into, new RoadPaint
        {
            Shape = PaintShape.Polyline, Type = PaintType.YellowDashed, Rgba = Yellow, Width = BikePlanner.LineWidth,
            Dash = BikePlanner.Dash, Gap = BikePlanner.Gap, Vertices = RoadPaintGeometry.Simplify(v.ToArray()),
        });
    }

    /// <summary>
    /// A Radstreifen (SSV 6.09, #120): its yellow dashed line (3 m / 3 m, 0.15 m) at the lane's
    /// width in from the carriageway edge, phased like the Leitlinien, and a Velo symbol near each
    /// end that lies at a junction or a dead end (Stadt Bern C 2.10.2 §5: at the start and end of
    /// every Radstreifen), facing the lane's traffic (right side with the drawing, left against).
    /// </summary>
    private static void BikeLane(RoadSegment seg, bool right, double station, List<RoadPaint> into,
        bool startsAtJunction, bool endsAtJunction)
    {
        var side = right ? seg.Attributes.Right : seg.Attributes.Left;
        float sign = right ? 1f : -1f, half = seg.Width * 0.5f + side.ShiftStartCm / 100f, lane = side.BikeDm / 10f;
        AddDashed(seg, sign * (half - lane), station, into, PaintType.YellowDashed, Yellow,
            BikePlanner.LineWidth, BikePlanner.Dash, BikePlanner.Gap);
        Symbols(seg, sign * (half - lane * 0.5f), right, into, startsAtJunction, endsAtJunction);
    }

    /// <summary>
    /// The Velo symbol of a bike lane or path at <paramref name="offset"/>: one,
    /// <see cref="BikePlanner.SymbolFromEnd"/> past the end where its riders come in, when that end
    /// is flagged a junction (Stadt Bern C 2.10.2 §5 asks for one at the start and one at the end
    /// of every Radstreifen; the end's is left out: a town's short pieces between junctions would
    /// carry four per piece). The right side's riders come in at the start, the left side's at the
    /// end. None on a piece shorter than <see cref="BikePlanner.SymbolMinLength"/>.
    /// </summary>
    public static int Symbols(RoadSegment seg, float offset, bool right, List<RoadPaint> into,
        bool atStart, bool atEnd)
    {
        if (right ? !atStart : !atEnd) return 0;
        double length = RoadPaintGeometry.Length(RoadPaintGeometry.Offset(seg, RoadPaint.FileOffset(offset)));
        float size = BikePlanner.SymbolSize, gap = BikePlanner.SymbolFromEnd;
        if (length < BikePlanner.SymbolMinLength) return 0;
        double from = right ? gap : length - gap - size;
        Add(into, RoadPaint.AlongSegment(seg, PaintType.BikeSymbol, Yellow, size, 0, 0, offset, from, from + size,
            right ? (byte)0 : RoadPaintGeometry.BikeReversed));
        return 1;
    }

    /// <summary>
    /// Motorway, expressway and ramp lines on the lanes <see cref="RoadCrossSection"/> lays out
    /// (#117), so paint, traffic and the carriageway agree: on a one-way carriageway, in its
    /// direction of travel, the left edge line between the inner margin and the fast lane, the
    /// right one between the slow lane and the hard shoulder, dashes between the lanes. An
    /// undivided one (a two-way bore) has a margin on both edges. False, to fall back on the
    /// proportional layout, when the direction is unknown or the lanes do not fit the width
    /// (v1/v2 widths).
    /// </summary>
    private static bool CrossSectionLines(RoadSegment seg, Action<float, bool> line)
    {
        var c = seg.Class;
        if (!RoadCrossSection.IsHighSpeed(c)) return false;
        var a = seg.Attributes;
        float half = seg.Width * 0.5f, lane = RoadCrossSection.LaneWidth(c);

        if (a.OneWay != 0)
        {
            int lanes = Math.Max(a.LanesForward, a.LanesBackward);
            if (lanes <= 0) lanes = RoadCrossSection.DefaultLanes(c, true);
            float block = lanes * lane;
            if (block > seg.Width + 0.01f) return false;
            float left = -half + Math.Min(RoadCrossSection.InnerMargin(c), seg.Width - block);
            float s = a.OneWay;   // travel frame to drawing frame: right of travel is left of the drawing when against it
            // a Randlinie is not part of the lane (ASTRA 11001): it lies just outside it
            line(s * (left - Math.Min(HighSpeedEdgeWidth * 0.5f, left + half)), false);
            line(s * (left + block + Math.Min(HighSpeedEdgeWidth * 0.5f, half - left - block)), false);
            for (int k = 1; k < lanes; k++) line(s * (left + k * lane), true);
            return true;
        }

        if ((seg.Flags & RoadFlags.Divided) != 0) return false;
        int back = Math.Max(1, (int)a.LanesBackward), fwd = Math.Max(1, (int)a.LanesForward);
        float all = (back + fwd) * lane;
        if (all > seg.Width + 0.01f) return false;
        float edge = -all * 0.5f;   // the margins share what the lanes leave
        float outside = Math.Min(HighSpeedEdgeWidth * 0.5f, half + edge);
        line(edge - outside, false);
        line(-edge + outside, false);
        for (int k = 1; k < back + fwd; k++) line(edge + k * lane, true);
        return true;
    }

    private static void AddDashed(RoadSegment seg, float offset, double station, List<RoadPaint> into)
    {
        var (dash, gap) = Leitlinie(seg);
        AddDashed(seg, offset, station, into, PaintType.WhiteDashed, White, LineWidth, dash, gap);
    }

    /// <summary>A dashed line along the segment, phased on its TLM line's metre so dashes run on across seams.</summary>
    public static void AddDashed(RoadSegment seg, float offset, double station, List<RoadPaint> into,
        PaintType type, uint rgba, float width, float dash, float gap)
    {
        var line = RoadPaintGeometry.Offset(seg, RoadPaint.FileOffset(offset));
        if (line.Length < 6) return;
        double length = RoadPaintGeometry.Length(line);
        double period = dash + gap;
        double phase = station - Math.Floor(station / period) * period;
        bool seamStart = OnSeam(seg.Points, 0), seamEnd = OnSeam(seg.Points, seg.PointCount - 1);

        // the dash already running at the first point: the seam cut it, or it is long enough to keep
        double first = phase < 1e-6 ? 0 : period - phase;
        if (phase >= 1e-6 && phase < dash)
        {
            double lead = Math.Min(dash - phase, length);
            if (seamStart || lead >= 0.4 * dash)
                Add(into, RoadPaint.AlongSegment(seg, type, rgba, width, 0, 0, offset, 0,
                    lead >= length ? double.PositiveInfinity : lead));
        }

        if (first >= length) return;
        double end = length;
        if (!seamEnd)
        {
            double lastStart = first + Math.Floor((length - first) / period) * period;
            if (length - lastStart < 0.4 * dash) end = lastStart - gap;
        }
        if (end - first > 1e-3)
            Add(into, RoadPaint.AlongSegment(seg, type, rgba, width, dash, gap, offset, first,
                end >= length ? double.PositiveInfinity : end));
    }

    private static void Add(List<RoadPaint> into, RoadPaint paint)
    {
        if (paint.Vertices.Length >= 6) into.Add(paint);
    }

    private static bool OnSeam(float[] p, int i)
    {
        const float eps = 0.01f;
        float x = p[i * 3], z = p[i * 3 + 2];
        return MathF.Abs(x) < eps || MathF.Abs(z) < eps || MathF.Abs(x - TileSize) < eps || MathF.Abs(z - TileSize) < eps;
    }
}
