namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// Retaining walls for one tile's roads (#125), decided at build time with the full-resolution
/// terrain and written as <see cref="RoadLinearProp"/>s in the v3 LPRP section. The runtime blend
/// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>) shapes cut and fill slopes on its own; this only
/// says where a slope cannot reach the ground within <see cref="RoadEmbankment.RoadReachM"/>.
///
/// <para>
/// Every <see cref="Step"/> metres along a drivable at-grade road, on each side, the ground at
/// the slope's reach is compared with the slope: a fill slope still more than
/// <see cref="RoadEmbankment.MinWallDrop"/> above the ground there needs a supporting wall, a cut
/// slope that far below it a cut wall. A slope that runs into another road, rail line or stream
/// first is judged against that line's height instead, and only the upper road gets the wall
/// between two stacked roads. Consecutive samples become runs, one-sample gaps are closed, runs
/// shorter than <see cref="RoadEmbankment.MinWallRunM"/> are dropped. Where a surveyed TLM wall
/// already lines the edge, the run is kept only to free the ground (variant TLM) and the TLM
/// segment stays the wall that is drawn.
/// </para>
/// </summary>
public static class EmbankmentPlanner
{
    /// <summary>Spacing of the samples along a road, and of the wall's points.</summary>
    public const double Step = 2.0;

    /// <summary>Region numbers, printed with the stage report.</summary>
    public sealed class Stats
    {
        public int Fill, Cut, Tlm, Tiles, RailFill, RailCut, Points, Sampled;
        public double FillM, CutM, TlmM, FaceM2, MaxHeight, Ms, RailFillM, RailCutM, RoadKm, RailKm;

        /// <summary>LPRP bytes of the walls: 4 B count per tile with walls, 16 B per prop, 16 B per point.</summary>
        public long Bytes;

        public string Format() => string.Create(CultureInfo.InvariantCulture, $"""
              embankments (#125): {Fill:N0} fill walls ({FillM / 1000:F2} km), {Cut:N0} cut walls ({CutM / 1000:F2} km), {FaceM2:N0} m2 of face, highest {MaxHeight:F1} m
                of them on rail: {RailFill:N0} fill ({RailFillM / 1000:F2} km), {RailCut:N0} cut ({RailCutM / 1000:F2} km); walled per km of line: road {(FillM + CutM - RailFillM - RailCutM) / 1000 / Math.Max(RoadKm, 1e-9):F3}, rail {(RailFillM + RailCutM) / 1000 / Math.Max(RailKm, 1e-9):F3} (two sides)
                TLM walls kept instead {Tlm:N0} ({TlmM / 1000:F2} km); planning {Ms / Math.Max(1, Tiles):F1} ms/tile
                wall points {Points:N0} of {Sampled:N0} samples; LPRP {Bytes / 1024.0:F0} KB, {Bytes / Math.Max(1, Tiles) / 1024.0:F2} KB/tile
            """);
    }

    private enum Need : byte { None, Fill, Cut }

    /// <summary>
    /// The walls for one tile's final segments (tile-local, as written). <paramref name="ground"/>
    /// is the bare terrain at an LV95 point, NaN where no tile is loaded.
    /// </summary>
    /// <param name="owners">If given, gets the index in <paramref name="segments"/> of the line
    /// each returned wall stands beside, in the same order (the railing planner needs it).</param>
    public static List<RoadLinearProp> Plan(TileId id, IReadOnlyList<RoadSegment> segments,
        Func<double, double, double> ground, Stats stats, List<int>? owners = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var props = new List<RoadLinearProp>();
        double Ground(double x, double z) => ground(id.MinE + x, id.MaxN - z);
        var lines = new LineIndex(segments);

        for (int si = 0; si < segments.Count; si++)
        {
            var seg = segments[si];
            if (!RoadEmbankment.AllowsWall(seg) || seg.PointCount < 2) continue;
            if (seg.Class == RoadClass.Railway) stats.RailKm += Length(seg) / 1000;
            else stats.RoadKm += Length(seg) / 1000;

            var stations = Stations(seg);
            if (stations.Count < 2) continue;
            foreach (bool right in (ReadOnlySpan<bool>)[false, true])
            {
                double edge = RoadEmbankment.EdgeOffset(seg, right);
                var needs = new Need[stations.Count];
                for (int i = 0; i < stations.Count; i++)
                    needs[i] = Judge(stations[i], right, edge, si, lines, Ground);
                CloseGaps(needs);
                int before = props.Count;
                EmitRuns(seg, si, stations, needs, right, lines, Ground, props, stats);
                for (int k = before; k < props.Count; k++) owners?.Add(si);
            }
        }

        stats.Tiles++;
        if (props.Count > 0) stats.Bytes += 4 + props.Sum(p => 16L + 16L * p.PointCount);
        stats.Ms += clock.Elapsed.TotalMilliseconds;
        return props;
    }

    private static double Length(RoadSegment seg)
    {
        double len = 0;
        var p = seg.Points;
        for (int i = 0; i < seg.PointCount - 1; i++)
            len += Math.Sqrt((p[i * 3 + 3] - p[i * 3]) * (p[i * 3 + 3] - p[i * 3]) + (p[i * 3 + 5] - p[i * 3 + 2]) * (p[i * 3 + 5] - p[i * 3 + 2]));
        return len;
    }

    internal readonly record struct Station(double X, double Z, double Y, double Fx, double Fz)
    {
        /// <summary>Unit vector to one side, X east and Z south.</summary>
        public (double X, double Z) Side(bool right) => right ? (-Fz, Fx) : (Fz, -Fx);
    }

    /// <summary>Points every <see cref="Step"/> metres along the line, both ends included.</summary>
    internal static List<Station> Stations(RoadSegment seg)
    {
        var result = new List<Station>();
        var p = seg.Points;
        double next = 0, travelled = 0;
        for (int i = 0; i < seg.PointCount - 1; i++)
        {
            double ax = p[i * 3], ay = p[i * 3 + 1], az = p[i * 3 + 2];
            double bx = p[i * 3 + 3], by = p[i * 3 + 4], bz = p[i * 3 + 5];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (len < 1e-6) continue;
            double fx = (bx - ax) / len, fz = (bz - az) / len;
            for (; next <= travelled + len; next += Step)
            {
                double t = (next - travelled) / len;
                result.Add(new Station(ax + (bx - ax) * t, az + (bz - az) * t, ay + (by - ay) * t, fx, fz));
            }
            travelled += len;
            if (i == seg.PointCount - 2 && travelled - (next - Step) > 0.1)
                result.Add(new Station(bx, bz, by, fx, fz));
        }
        return result;
    }

    /// <summary>Whether one side of one station needs a wall, and which.</summary>
    private static Need Judge(Station st, bool right, double edge, int self, LineIndex lines,
        Func<double, double, double> ground)
    {
        var (sx, sz) = st.Side(right);
        double reach = RoadEmbankment.RoadReachM;
        // the slope runs into another line first: judged against it; only the upper road builds
        for (double d = 0.5; d <= reach; d += 0.5)
        {
            double x = st.X + sx * (edge + d), z = st.Z + sz * (edge + d);
            if (lines.Covering(x, z, self, st.X, st.Z, edge + 1.0) is not { } other) continue;
            double drop = st.Y - RoadEmbankment.FillSlope * d - other;
            return drop > RoadEmbankment.MinWallDrop ? Need.Fill : Need.None;
        }
        double g = ground(st.X + sx * (edge + reach), st.Z + sz * (edge + reach));
        if (double.IsNaN(g)) return Need.None;
        if (st.Y - RoadEmbankment.FillSlope * reach - g > RoadEmbankment.MinWallDrop) return Need.Fill;
        if (g - (st.Y + RoadEmbankment.CutSlope * reach) > RoadEmbankment.MinWallDrop) return Need.Cut;
        return Need.None;
    }

    /// <summary>A single sample of nothing between two of the same wall is noise: bridge it.</summary>
    private static void CloseGaps(Need[] needs)
    {
        for (int i = 1; i < needs.Length - 1; i++)
            if (needs[i] == Need.None && needs[i - 1] != Need.None && needs[i - 1] == needs[i + 1])
                needs[i] = needs[i - 1];
    }

    private static void EmitRuns(RoadSegment seg, int self, List<Station> stations, Need[] needs, bool right,
        LineIndex lines, Func<double, double, double> ground, List<RoadLinearProp> props, Stats stats)
    {
        int i = 0;
        while (i < needs.Length)
        {
            if (needs[i] == Need.None) { i++; continue; }
            int j = i;
            while (j + 1 < needs.Length && needs[j + 1] == needs[i]) j++;
            var type = needs[i] == Need.Fill ? LinearPropType.RetainingWallFill : LinearPropType.RetainingWallCut;
            if ((j - i) * Step >= RoadEmbankment.MinWallRunM - 1e-9)
                Emit(seg, self, stations, i, j, right, type, lines, ground, props, stats);
            i = j + 1;
        }
    }

    private static void Emit(RoadSegment seg, int self, List<Station> stations, int from, int to, bool right,
        LinearPropType type, LineIndex lines, Func<double, double, double> ground, List<RoadLinearProp> props, Stats stats)
    {
        bool fill = type == LinearPropType.RetainingWallFill;
        double face = RoadEmbankment.FaceOffset(seg, right, type);
        double edge = RoadEmbankment.EdgeOffset(seg, right);
        double cover = RoadEmbankment.CoverDepth;
        var pts = new List<(double X, double Z, double Foot, double Top)>();
        int tlm = 0;
        // the solid and its cover, face to back: toward the road for a fill wall, into the hill
        // for a cut wall
        double inward = fill ? -cover : cover;

        for (int k = from; k <= to; k++)
        {
            var st = stations[k];
            var (sx, sz) = st.Side(right);
            double x = st.X + sx * face, z = st.Z + sz * face;
            // on the inside of a tight bend the face line folds back on itself: skip those points
            if (pts.Count > 0 && (x - pts[^1].X) * st.Fx + (z - pts[^1].Z) * st.Fz <= 0.05) continue;
            // the solid must not stand on another line (a junction's other arm, the other leg of a
            // hairpin): the run stops there and starts again past it
            bool blocked = false;
            for (double d = 0; d <= 1.0 && !blocked; d += 1.0 / 3)
                blocked = lines.Covering(x + sx * inward * d, z + sz * inward * d, self, st.X, st.Z, edge + 1.0) is not null;
            if (blocked)
            {
                Write(seg, type, right, pts, tlm, props, stats);
                pts.Clear();
                tlm = 0;
                continue;
            }
            if (lines.TlmWallNear(st.X + sx * (edge + 1.0), st.Z + sz * (edge + 1.0), 2.5)) tlm++;

            double foot, top;
            if (fill)
            {
                // from the road down to the valley floor just past the face
                top = st.Y;
                foot = Math.Min(ground(x, z), ground(x + sx, z + sz));
            }
            else
            {
                // from the road up to the crown, 20 cm over the highest ground under the cover
                // (its level backfill, which then lies over every transition triangle)
                foot = st.Y;
                top = double.MinValue;
                for (double d = 0; d <= cover + 0.5; d += 0.5)
                    top = Math.Max(top, ground(x + sx * d, z + sz * d));
                top += RoadEmbankment.CutCrownOver;
            }
            if (double.IsNaN(foot) || double.IsNaN(top)) continue;
            if (top - foot < RoadEmbankment.MinWallDrop)
            {
                if (fill) foot = top - RoadEmbankment.MinWallDrop;
                else top = foot + RoadEmbankment.MinWallDrop;
            }
            pts.Add((x, z, foot, top));
        }
        Write(seg, type, right, pts, tlm, props, stats);
    }

    /// <summary>
    /// Drops the points a straight chord carries within tolerance: the plan line within 5 cm, the
    /// road-side height (a fill wall's top, a cut wall's foot) within 3 cm, the other one within
    /// 15 cm and never lower than a dropped point's (a cut wall's crown over its backfill) or more
    /// than 0.5 m over it (a fill wall's foot, sunk 1 m at runtime). A wall along a straight
    /// stretch then costs a point every few tens of metres instead of every 2 m.
    /// </summary>
    private static List<(double X, double Z, double Foot, double Top)> Simplify(
        List<(double X, double Z, double Foot, double Top)> pts, bool fill)
    {
        var kept = new List<(double X, double Z, double Foot, double Top)> { pts[0] };
        int a = 0;
        while (a < pts.Count - 1)
        {
            int b = a + 1;
            while (b + 1 < pts.Count && Carries(pts, a, b + 1, fill)) b++;
            kept.Add(pts[b]);
            a = b;
        }
        return kept;
    }

    private static bool Carries(List<(double X, double Z, double Foot, double Top)> pts, int a, int b, bool fill)
    {
        var (ax, az, af, at) = pts[a];
        var (bx, bz, bf, bt) = pts[b];
        double dx = bx - ax, dz = bz - az, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6) return false;
        for (int k = a + 1; k < b; k++)
        {
            var (x, z, f, t) = pts[k];
            double along = ((x - ax) * dx + (z - az) * dz) / len;
            if (along <= 0 || along >= len) return false;
            if (Math.Abs((x - ax) * dz - (z - az) * dx) / len > 0.05) return false;
            double u = along / len;
            double lf = af + (bf - af) * u, lt = at + (bt - at) * u;
            if (fill)
            {
                if (Math.Abs(lt - t) > 0.03 || lf - f > 0.5 || f - lf > 1.0) return false;
            }
            else if (Math.Abs(lf - f) > 0.03 || lt < t || lt - t > 0.15) return false;
        }
        return true;
    }

    /// <summary>One run of consecutive wall points as a prop, if it is long enough.</summary>
    private static void Write(RoadSegment seg, LinearPropType type, bool right,
        List<(double X, double Z, double Foot, double Top)> pts, int tlm, List<RoadLinearProp> props, Stats stats)
    {
        if (pts.Count < 2) return;
        bool fill = type == LinearPropType.RetainingWallFill;
        bool rail = seg.Class == RoadClass.Railway;
        int sampled = pts.Count;

        pts = new List<(double X, double Z, double Foot, double Top)>(pts);

        // the solid lies on the left of the point order: a fill wall's road, a cut wall's hill
        if (fill != right) pts.Reverse();
        double length = 0, area = 0, highest = 0;
        for (int k = 0; k < pts.Count; k++)
        {
            var (x, z, foot, top) = pts[k];
            highest = Math.Max(highest, top - foot);
            if (k > 0)
            {
                double seg2 = Math.Sqrt((x - pts[k - 1].X) * (x - pts[k - 1].X) + (z - pts[k - 1].Z) * (z - pts[k - 1].Z));
                length += seg2;
                area += seg2 * ((top - foot) + (pts[k - 1].Top - pts[k - 1].Foot)) / 2;
            }
        }
        if (length < RoadEmbankment.MinWallRunM - 1e-9) return;

        // length and face area above are from every sample; the prop keeps only the points needed
        var simple = Simplify(pts, fill);
        var flat = new float[simple.Count * 4];
        for (int k = 0; k < simple.Count; k++)
        {
            var (x, z, foot, top) = simple[k];
            flat[k * 4] = (float)x;
            flat[k * 4 + 1] = (float)foot;
            flat[k * 4 + 2] = (float)z;
            flat[k * 4 + 3] = (float)(top - foot);
        }
        stats.Points += simple.Count;
        stats.Sampled += sampled;

        bool surveyed = tlm * 2 >= pts.Count;
        props.Add(new RoadLinearProp
        {
            Type = type,
            Variant = surveyed ? RoadEmbankment.VariantTlmWall : RoadEmbankment.VariantGenerated,
            Flags = surveyed ? PropFlags.None : PropFlags.Solid,
            Thickness = RoadEmbankment.WallCrown,
            Points = flat,
        });

        if (surveyed) { stats.Tlm++; stats.TlmM += length; return; }
        if (fill) { stats.Fill++; stats.FillM += length; }
        else { stats.Cut++; stats.CutM += length; }
        if (rail && fill) { stats.RailFill++; stats.RailFillM += length; }
        else if (rail) { stats.RailCut++; stats.RailCutM += length; }
        stats.FaceM2 += area;
        stats.MaxHeight = Math.Max(stats.MaxHeight, highest);
    }

    /// <summary>
    /// The tile's lines in 16 m buckets: which other ground-level line covers a point (its height
    /// there), and whether a surveyed TLM wall runs near one.
    /// </summary>
    internal sealed class LineIndex
    {
        private const double Cell = 16;
        private readonly Dictionary<(int, int), List<int>> _buckets = new();
        private readonly List<(double Ax, double Az, double Ay, double Bx, double Bz, double By, double Half, int Seg, bool Wall)> _pieces = new();

        public LineIndex(IReadOnlyList<RoadSegment> segments)
        {
            for (int s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                bool wall = seg.Class == RoadClass.Wall;
                // the ground-level lines a slope must not bury, and the walls that make one needless
                if (!wall && !(RoadEmbankment.IsAtGrade(seg) || (RoadFormat.IsWatercourse(seg.Class)
                        && (seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0))) continue;
                double half = wall ? 0 : seg.Width * 0.5 + 0.3;
                var p = seg.Points;
                for (int i = 0; i < seg.PointCount - 1; i++)
                {
                    var piece = (p[i * 3], p[i * 3 + 2], (double)p[i * 3 + 1], p[i * 3 + 3], p[i * 3 + 5], (double)p[i * 3 + 4], half, s, wall);
                    int k = _pieces.Count;
                    _pieces.Add(piece);
                    double pad = half + 3;
                    int c0 = (int)Math.Floor((Math.Min(piece.Item1, piece.Item4) - pad) / Cell), c1 = (int)Math.Floor((Math.Max(piece.Item1, piece.Item4) + pad) / Cell);
                    int r0 = (int)Math.Floor((Math.Min(piece.Item2, piece.Item5) - pad) / Cell), r1 = (int)Math.Floor((Math.Max(piece.Item2, piece.Item5) + pad) / Cell);
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                        {
                            if (!_buckets.TryGetValue((c, r), out var list)) _buckets[(c, r)] = list = new List<int>();
                            list.Add(k);
                        }
                }
            }
        }

        /// <summary>
        /// Height of the nearest other line whose width covers (x, z), or null. The asking segment
        /// counts as another line only away from where it is asking from (its station at sx, sz):
        /// the other leg of a hairpin is, the piece the station sits on is not.
        /// </summary>
        public double? Covering(double x, double z, int self, double sx, double sz, double near) =>
            Cover(x, z, self, sx, sz, near) is { } c ? c.Height : null;

        /// <summary>
        /// The same, with which line it is and its drawing direction there (a unit vector, X east
        /// and Z south).
        /// </summary>
        public (int Seg, double Height, double Fx, double Fz)? Cover(double x, double z, int self, double sx, double sz, double near)
        {
            if (!_buckets.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var list)) return null;
            double best = double.MaxValue;
            (int, double, double, double)? found = null;
            foreach (int k in list)
            {
                var pc = _pieces[k];
                if (pc.Wall) continue;
                if (pc.Seg == self && Distance(pc.Ax, pc.Az, pc.Bx, pc.Bz, sx, sz, out _) < near) continue;
                double d = Distance(pc.Ax, pc.Az, pc.Bx, pc.Bz, x, z, out double t);
                if (d > pc.Half || d >= best) continue;
                best = d;
                double len = Math.Max(1e-9, Math.Sqrt((pc.Bx - pc.Ax) * (pc.Bx - pc.Ax) + (pc.Bz - pc.Az) * (pc.Bz - pc.Az)));
                found = (pc.Seg, pc.Ay + (pc.By - pc.Ay) * t, (pc.Bx - pc.Ax) / len, (pc.Bz - pc.Az) / len);
            }
            return found;
        }

        public bool TlmWallNear(double x, double z, double radius)
        {
            if (!_buckets.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var list)) return false;
            foreach (int k in list)
            {
                var pc = _pieces[k];
                if (pc.Wall && Distance(pc.Ax, pc.Az, pc.Bx, pc.Bz, x, z, out _) <= radius) return true;
            }
            return false;
        }

        private static double Distance(double ax, double az, double bx, double bz, double x, double z, out double t)
        {
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
            double px = ax + dx * t - x, pz = az + dz * t - z;
            return Math.Sqrt(px * px + pz * pz);
        }
    }
}
