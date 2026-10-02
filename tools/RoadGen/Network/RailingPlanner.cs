using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Plans road railings (#126) into the v3 <c>LPRP</c> layer, after <see cref="EmbankmentPlanner"/>
/// placed the retaining walls. Three kinds, conventions in <see cref="RoadRailing"/>:
/// <list type="bullet">
/// <item>on the crown of every generated fill wall: a guardrail beside the main roads, a fence
/// (railing) beside smaller ones and in towns;</item>
/// <item>above a drop with no wall: where the ground <see cref="RoadRailing.DropReach"/> out from
/// the edge lies more than <see cref="RoadRailing.MinDrop"/> below the road, a guardrail
/// <see cref="RoadRailing.EdgeClearance"/> out from the edge;</item>
/// <item>in a motorway or expressway median: where the other carriageway's paved edge is at most
/// <see cref="RoadRailing.MaxMedian"/> away, each carriageway writes its own beam of the double
/// guardrail, <see cref="RoadRailing.MedianHalfGap"/> off the median's middle, so no pairing
/// decision has to agree between the two.</item>
/// </list>
/// A run stops wherever another line comes under it (a junction arm, a driveway), and runs shorter
/// than <see cref="RoadRailing.MinRunM"/> are dropped. Bridges keep their parapets.
/// </summary>
public static class RailingPlanner
{
    public sealed class Stats
    {
        public int Tiles, Wall, Drop, Median, Fence;
        public double WallM, DropM, MedianM, FenceM, Ms;
        public long Points, Bytes;

        public string Format() => string.Create(CultureInfo.InvariantCulture, $"""
              railings (#126): guardrail on walls {Wall:N0} ({WallM / 1000:F2} km), above drops {Drop:N0} ({DropM / 1000:F2} km), median beams {Median:N0} ({MedianM / 1000:F2} km), fences {Fence:N0} ({FenceM / 1000:F2} km)
                points {Points:N0}; LPRP {Bytes / 1024.0:F0} KB, {Bytes / Math.Max(1, Tiles) / 1024.0:F2} KB/tile; planning {Ms / Math.Max(1, Tiles):F1} ms/tile
            """);
    }

    private enum Kind : byte { None, Drop, Median }

    private const double Step = EmbankmentPlanner.Step;

    public static List<RoadLinearProp> Plan(TileId id, IReadOnlyList<RoadSegment> segments,
        IReadOnlyList<RoadLinearProp> walls, IReadOnlyList<int> wallOwners,
        Func<double, double, double> ground, Stats stats)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var props = new List<RoadLinearProp>();
        double Ground(double x, double z) => ground(id.MinE + x, id.MaxN - z);
        var lines = new EmbankmentPlanner.LineIndex(segments);

        for (int w = 0; w < walls.Count; w++)
            if (walls[w].Type == LinearPropType.RetainingWallFill && walls[w].Variant == RoadEmbankment.VariantGenerated)
                OnWall(segments[wallOwners[w]], walls[w], props, stats);

        for (int si = 0; si < segments.Count; si++)
        {
            var seg = segments[si];
            if (!RoadRailing.GetsGuardrail(seg.Class) || !RoadEmbankment.IsAtGrade(seg)
                || (seg.Flags & (RoadFlags.Ford | RoadFlags.Stairs)) != 0 || seg.PointCount < 2) continue;
            var stations = EmbankmentPlanner.Stations(seg);
            if (stations.Count < 2) continue;
            var own = new List<RoadLinearProp>();
            for (int w = 0; w < walls.Count; w++)
                if (wallOwners[w] == si) own.Add(walls[w]);

            foreach (bool right in (ReadOnlySpan<bool>)[false, true])
            {
                double edge = RoadEmbankment.EdgeOffset(seg, right);
                var kinds = new Kind[stations.Count];
                var offsets = new double[stations.Count];
                for (int i = 0; i < stations.Count; i++)
                    kinds[i] = Judge(segments, si, stations[i], right, edge, own, lines, Ground, out offsets[i]);
                for (int i = 1; i < kinds.Length - 1; i++)
                    if (kinds[i] == Kind.None && kinds[i - 1] != Kind.None && kinds[i - 1] == kinds[i + 1])
                    {
                        kinds[i] = kinds[i - 1];
                        offsets[i] = (offsets[i - 1] + offsets[i + 1]) * 0.5;
                    }
                EmitRuns(seg, si, stations, kinds, offsets, right, lines, props, stats);
            }
        }

        stats.Tiles++;
        foreach (var p in props) stats.Points += p.PointCount;
        if (props.Count > 0) stats.Bytes += props.Sum(p => 16L + 16L * p.PointCount);
        stats.Ms += clock.Elapsed.TotalMilliseconds;
        return props;
    }

    /// <summary>What one side of one station needs, and the rail face's offset from the centreline.</summary>
    private static Kind Judge(IReadOnlyList<RoadSegment> segments, int self, EmbankmentPlanner.Station st, bool right,
        double edge, List<RoadLinearProp> walls, EmbankmentPlanner.LineIndex lines, Func<double, double, double> ground,
        out double offset)
    {
        offset = 0;
        var seg = segments[self];
        var (sx, sz) = st.Side(right);

        // a wall on this side carries its own railing (fill) or rises over the road (cut)
        double face = RoadEmbankment.FaceOffset(seg, right, LinearPropType.RetainingWallFill);
        foreach (var w in walls)
            if (NearLine(w, st.X + sx * face, st.Z + sz * face, 1.5)) return Kind.None;

        // the median: this carriageway's inner side, the other one close and running the other way
        sbyte oneWay = seg.Attributes.OneWay;
        if (seg.Class is RoadClass.Motorway or RoadClass.Expressway && oneWay != 0 && right == oneWay < 0)
        {
            double tx = st.Fx * oneWay, tz = st.Fz * oneWay;
            for (double d = edge + 0.1; d <= edge + RoadRailing.MaxMedian + 0.3; d += 0.1)
            {
                if (lines.Cover(st.X + sx * d, st.Z + sz * d, self, st.X, st.Z, edge + 1.0) is not { } c) continue;
                if (Partner(c, tx, tz, d, out offset)) return Kind.Median;
                break;
            }
        }

        // a drop: the ground a few metres out far below the road, and no other line in between
        for (double d = 0.5; d <= RoadRailing.DropReach; d += 0.5)
            if (lines.Covering(st.X + sx * (edge + d), st.Z + sz * (edge + d), self, st.X, st.Z, edge + 1.0) is not null)
                return Kind.None;
        double g = ground(st.X + sx * (edge + RoadRailing.DropReach), st.Z + sz * (edge + RoadRailing.DropReach));
        if (double.IsNaN(g) || st.Y - g <= RoadRailing.MinDrop) return Kind.None;
        offset = edge + RoadRailing.EdgeClearance;
        return Kind.Drop;

        bool Partner((int Seg, double Height, double Fx, double Fz) c, double tx, double tz, double hit, out double off)
        {
            off = 0;
            var o = segments[c.Seg];
            if (o.Class is not (RoadClass.Motorway or RoadClass.Expressway) || o.Attributes.OneWay == 0) return false;
            // the other carriageway's traffic runs against this one's
            if ((c.Fx * tx + c.Fz * tz) * o.Attributes.OneWay > -0.7) return false;
            // a line's cover starts 0.3 m short of its paved edge (LineIndex)
            double other = hit + 0.3;
            if (other - edge > RoadRailing.MaxMedian) return false;
            off = (edge + other) * 0.5 - RoadRailing.MedianHalfGap;
            return off > edge + 0.05;
        }
    }

    private static bool NearLine(RoadLinearProp w, double x, double z, double radius)
    {
        var p = w.Points;
        for (int i = 0; i < w.PointCount - 1; i++)
        {
            double ax = p[i * 4], az = p[i * 4 + 2], bx = p[i * 4 + 4], bz = p[i * 4 + 6];
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
            double px = ax + dx * t - x, pz = az + dz * t - z;
            if (px * px + pz * pz <= radius * radius) return true;
        }
        return false;
    }

    private static void EmitRuns(RoadSegment seg, int self, List<EmbankmentPlanner.Station> stations, Kind[] kinds,
        double[] offsets, bool right, EmbankmentPlanner.LineIndex lines, List<RoadLinearProp> props, Stats stats)
    {
        int i = 0;
        while (i < kinds.Length)
        {
            if (kinds[i] == Kind.None) { i++; continue; }
            int j = i;
            while (j + 1 < kinds.Length && kinds[j + 1] == kinds[i]) j++;
            var pts = new List<(double X, double Z, double Foot)>();
            for (int k = i; k <= j; k++)
            {
                var st = stations[k];
                var (sx, sz) = st.Side(right);
                double x = st.X + sx * offsets[k], z = st.Z + sz * offsets[k];
                // the inside of a tight bend folds the offset line back on itself
                if (pts.Count > 0 && (x - pts[^1].X) * st.Fx + (z - pts[^1].Z) * st.Fz <= 0.05) continue;
                // another line under the rail (a driveway, a junction arm): a gap
                if (lines.Covering(x, z, self, st.X, st.Z, offsets[k] + 1.0) is not null)
                {
                    Write(seg, kinds[i], right, pts, props, stats);
                    pts.Clear();
                    continue;
                }
                pts.Add((x, z, st.Y));
            }
            Write(seg, kinds[i], right, pts, props, stats);
            i = j + 1;
        }
    }

    private static void Write(RoadSegment seg, Kind kind, bool right, List<(double X, double Z, double Foot)> pts,
        List<RoadLinearProp> props, Stats stats)
    {
        if (pts.Count < 2) return;
        double length = 0;
        for (int k = 1; k < pts.Count; k++) length += Math.Sqrt(Sq(pts[k].X - pts[k - 1].X) + Sq(pts[k].Z - pts[k - 1].Z));
        if (length < RoadRailing.MinRunM - 1e-9) return;
        // the road on the right of the point order: a right-side rail runs against the road
        var ordered = new List<(double X, double Z, double Foot)>(pts);
        if (right) ordered.Reverse();

        bool fence = kind == Kind.Drop && seg.Attributes.Has(RoadAttrFlags.Urban);
        var type = kind == Kind.Median ? LinearPropType.MedianDouble : fence ? LinearPropType.Fence : LinearPropType.Guardrail;
        float top = fence ? RoadRailing.FenceTop : RoadRailing.GuardrailTop;
        props.Add(Prop(type, Simplify(ordered), top));
        if (kind == Kind.Median) { stats.Median++; stats.MedianM += length; }
        else if (fence) { stats.Fence++; stats.FenceM += length; }
        else { stats.Drop++; stats.DropM += length; }
    }

    /// <summary>
    /// A railing on a fill wall's crown, the posts in its middle and the face toward the road. The
    /// wall's points run with the road on their left; the railing's with the road on their right.
    /// </summary>
    private static void OnWall(RoadSegment seg, RoadLinearProp wall, List<RoadLinearProp> props, Stats stats)
    {
        if (!RoadRailing.GetsRailing(seg.Class) || wall.PointCount < 2) return;
        bool guard = RoadRailing.GetsGuardrail(seg.Class) && !seg.Attributes.Has(RoadAttrFlags.Urban);
        double into = wall.Thickness * 0.5 + RoadRailing.PostSetback;
        var p = wall.Points;
        int n = wall.PointCount;
        var pts = new List<(double X, double Z, double Foot)>(n);
        double length = 0;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double dx = p[b * 4] - p[a * 4], dz = p[b * 4 + 2] - p[a * 4 + 2], l = Math.Sqrt(dx * dx + dz * dz);
            if (l < 1e-9) continue;
            // left of the wall's points (X east, Z south): toward the road
            double lx = dz / l, lz = -dx / l;
            double top = p[i * 4 + 1] + p[i * 4 + 3] + RoadEmbankment.FillCrownLift;
            pts.Add((p[i * 4] + lx * into, p[i * 4 + 2] + lz * into, top));
            if (pts.Count > 1) length += Math.Sqrt(Sq(pts[^1].X - pts[^2].X) + Sq(pts[^1].Z - pts[^2].Z));
        }
        if (pts.Count < 2) return;
        pts.Reverse();
        var type = guard ? LinearPropType.Guardrail : LinearPropType.Fence;
        props.Add(Prop(type, pts, guard ? RoadRailing.GuardrailTop : RoadRailing.FenceTop));
        if (guard) { stats.Wall++; stats.WallM += length; }
        else { stats.Fence++; stats.FenceM += length; }
    }

    private static RoadLinearProp Prop(LinearPropType type, List<(double X, double Z, double Foot)> pts, float top)
    {
        var flat = new float[pts.Count * 4];
        for (int k = 0; k < pts.Count; k++)
        {
            flat[k * 4] = (float)pts[k].X;
            flat[k * 4 + 1] = (float)pts[k].Foot;
            flat[k * 4 + 2] = (float)pts[k].Z;
            flat[k * 4 + 3] = top;
        }
        return new RoadLinearProp
        {
            Type = type,
            Variant = 0,
            Flags = PropFlags.Solid,
            Thickness = RoadRailing.PostSetback,
            Param = type == LinearPropType.Fence ? RoadRailing.FencePostSpacing : RoadRailing.GuardrailPostSpacing,
            Points = flat,
        };
    }

    /// <summary>Keeps the points a straight chord cannot carry: plan within 5 cm, foot within 3 cm.</summary>
    private static List<(double X, double Z, double Foot)> Simplify(List<(double X, double Z, double Foot)> pts)
    {
        var kept = new List<(double X, double Z, double Foot)> { pts[0] };
        int a = 0;
        while (a < pts.Count - 1)
        {
            int b = a + 1;
            while (b + 1 < pts.Count && Carries(pts, a, b + 1)) b++;
            kept.Add(pts[b]);
            a = b;
        }
        return kept;
    }

    private static bool Carries(List<(double X, double Z, double Foot)> pts, int a, int b)
    {
        var (ax, az, af) = pts[a];
        var (bx, bz, bf) = pts[b];
        double dx = bx - ax, dz = bz - az, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6) return false;
        for (int k = a + 1; k < b; k++)
        {
            var (x, z, f) = pts[k];
            double along = ((x - ax) * dx + (z - az) * dz) / len;
            if (along <= 0 || along >= len) return false;
            if (Math.Abs((x - ax) * dz - (z - az) * dx) / len > 0.05) return false;
            if (Math.Abs(af + (bf - af) * along / len - f) > 0.03) return false;
        }
        return true;
    }

    private static double Sq(double v) => v * v;
}
