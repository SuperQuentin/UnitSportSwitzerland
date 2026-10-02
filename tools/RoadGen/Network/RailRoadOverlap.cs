namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Rails embedded in the road (#124): level crossings and street running.
///
/// <para>
/// Where a ground-level railway runs inside a carriageway (a road of class Lane or wider, or a
/// Platz), that stretch of the rail segment is cut out as its own piece flagged
/// <see cref="RoadAttrFlags.Embedded"/>: the game draws no ballast and no raised rails for it,
/// and the tile gets <see cref="PaintType.RailGroove"/> paint at the rails' gauge and track
/// offsets instead. The embedded piece sits at the road's height, and the rail blends back to its
/// own height over <see cref="Blend"/> metres outside the road, so neither the visual ribbon nor
/// the collision blend (both read the segment heights) leaves a step. The road's own paint is
/// cleared from the track zone. Train routes are unchanged: the pieces still join end to end.
/// </para>
///
/// <para>
/// Lines TLM flags <c>auf_strasse</c> (<see cref="RoadAttrFlags.OnStreet"/>) get a wider
/// tolerance, since a street track and its road are drawn as two separate centrelines. A run of
/// more than <see cref="StreetRunMin"/> metres, or any run on an <c>auf_strasse</c> line, counts
/// as street running; shorter runs are level crossings.
/// </para>
/// </summary>
public sealed class RailRoadOverlap
{
    /// <summary>Polished rail head, sRGB: a dark groove vanished into the asphalt at 0.35x.</summary>
    public const uint GrooveRgba = 0x9A9893FF;

    /// <summary>Rail head plus groove (~0.12 m real) drawn a little wider, like the road lines.</summary>
    public const float GrooveWidth = 0.2f;

    private const double Step = 0.5;          // sampling along the rail
    private const double MinRun = 1.0;        // shorter covered runs are ignored (a road edge grazed)
    private const double MergeGap = 3.0;      // two runs this close are one (a narrow island)
    private const double Extend = 1.0;        // embed at least this far past the centreline's exit: the outer rail is still inside
    private const double MaxExtend = 15.0;    // ... and on until the ballast's collision core clears the road, at most this
    private const double Blend = 8.0;         // back to the rail's own height over this, outside the road
    private const double StreetMargin = 1.5;  // auf_strasse lines: track and road centrelines are apart
    private const double StreetRunMin = 25.0;
    private const double ZoneMargin = 0.5;    // road paint kept this far outside the outer rail
    private const double Cell = 20.0;
    private const float TileSize = 1000f;

    /// <summary>
    /// The raised rails' height over the line (RoadMeshBuilder 0.18) less the paint lift (0.02).
    /// Outside the road the line starts this far below the road, so the raised rails meet the
    /// grooves flush; inside it the file keeps the road's height (the collision blend reads it)
    /// and <c>LaneGraph</c> sinks it by this for the trains.
    /// </summary>
    public const float RailTop = 0.16f;

    private sealed record Road(Vec2[] Plan, float[] Height, double Half, bool Bridge);

    private readonly List<Road> _roads = new();
    private readonly Dictionary<(long, long), List<(int Road, int Seg)>> _grid = new();

    /// <summary>Embedded track centrelines (LV95) with their half width, for clearing road paint.</summary>
    private readonly List<(Vec2[] Line, double Half)> _zones = new();

    public readonly Tally Stats;

    public sealed class Tally
    {
        public int Crossings, StreetRuns, Grooves, SeamBlends, PaintCut;
        public double CrossingM, StreetM, OnStreetM, OnStreetEmbeddedM;

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    rail/road level crossings {Crossings:N0} ({CrossingM:F0} m embedded), street running {StreetRuns:N0} runs {StreetM:F0} m; "
            + $"auf_strasse {OnStreetM:F0} m, {OnStreetEmbeddedM:F0} m of it in a carriageway; {Grooves:N0} groove lines, "
            + $"{PaintCut:N0} road paint lines cut at a track; {SeamBlends} height blends cut by a tile seam");
    }

    /// <summary>
    /// A carriageway a rail can be embedded in: a car road or a Platz, at ground level or on a
    /// bridge (a tram crossing a river on the road bridge: its ballast lay on the deck at the deck's
    /// height and fought it). A rail is matched with carriageways of its own level only.
    /// </summary>
    public static bool IsCarriageway(RoadSegment s) =>
        (s.Class <= RoadClass.Lane || s.Class == RoadClass.Square)
        && (s.Flags & (RoadFlags.Tunnel | RoadFlags.Stairs)) == 0;

    /// <summary>A rail that can be embedded: at ground level or on a bridge, not a funicular.</summary>
    public static bool IsEmbeddable(RoadSegment s) =>
        s.Class == RoadClass.Railway && (s.Flags & (RoadFlags.Tunnel | RoadFlags.Funicular)) == 0;

    /// <summary>Mirror of <c>RoadMeshBuilder.BridgeLift</c>: a deck and its paint are drawn this far above the line.</summary>
    private const float BridgeLift = 0.15f;

    private static bool IsBridge(RoadSegment s) => (s.Flags & RoadFlags.Bridge) != 0;

    /// <summary>
    /// Indexes every carriageway of the block and its halo (untrimmed: junction areas included),
    /// at the planned width and on the shifted plan of #117, so a rail meets the road where it is drawn.
    /// </summary>
    public RailRoadOverlap(IEnumerable<CrossSectionPlanner.Line> lines, Tally stats)
    {
        Stats = stats;
        foreach (var line in lines.OrderBy(l => l.Tile.E).ThenBy(l => l.Tile.N))
        {
            var seg = line.Segment;
            if (!IsCarriageway(seg) || seg.PointCount < 2) continue;
            var plan = line.Shifted;
            float width = line.Width > 0.1f ? line.Width : seg.Width;
            var height = HeightsAlong(plan, line.Plan, seg);
            int r = _roads.Count;
            _roads.Add(new Road(plan, height, width * 0.5, IsBridge(seg)));
            for (int i = 1; i < plan.Length; i++)
            {
                double pad = width * 0.5 + StreetMargin + 5.0;   // every margin Covered is asked with
                long x0 = (long)Math.Floor((Math.Min(plan[i - 1].X, plan[i].X) - pad) / Cell);
                long x1 = (long)Math.Floor((Math.Max(plan[i - 1].X, plan[i].X) + pad) / Cell);
                long y0 = (long)Math.Floor((Math.Min(plan[i - 1].Y, plan[i].Y) - pad) / Cell);
                long y1 = (long)Math.Floor((Math.Max(plan[i - 1].Y, plan[i].Y) + pad) / Cell);
                for (long cx = x0; cx <= x1; cx++)
                for (long cy = y0; cy <= y1; cy++)
                {
                    if (!_grid.TryGetValue((cx, cy), out var list)) _grid[(cx, cy)] = list = new();
                    list.Add((r, i));
                }
            }
        }
    }

    /// <summary>
    /// The segment's heights at each point of <paramref name="plan"/>. A roundabout ring rebuilt as
    /// an arc (#122) has other points than its segment, so there they are read by the share of the
    /// length along the original plan (a tram near a Geneva roundabout ran off the end).
    /// </summary>
    private static float[] HeightsAlong(Vec2[] plan, Vec2[] original, RoadSegment seg)
    {
        var height = new float[plan.Length];
        if (plan.Length == seg.PointCount)
        {
            for (int i = 0; i < height.Length; i++) height[i] = seg.Points[i * 3 + 1];
            return height;
        }
        int n = Math.Min(original.Length, seg.PointCount);
        var from = Polyline.ArcLengths(original.Length == n ? original : original[..n]);
        var to = Polyline.ArcLengths(plan);
        for (int i = 0, j = 0; i < plan.Length; i++)
        {
            double s = to[^1] > 0 ? to[i] / to[^1] * from[^1] : 0;
            while (j < n - 2 && from[j + 1] < s) j++;
            double span = from[j + 1] - from[j];
            double t = span > 1e-9 ? Math.Clamp((s - from[j]) / span, 0, 1) : 0;
            height[i] = (float)(seg.Points[j * 3 + 1] + t * (seg.Points[(j + 1) * 3 + 1] - seg.Points[j * 3 + 1]));
        }
        return height;
    }

    /// <summary>
    /// The carriageway covering <paramref name="p"/> (centreline within half width + margin), the
    /// one it is deepest inside, and the road's height at the foot of the perpendicular.
    /// </summary>
    private bool Covered(Vec2 p, double margin, out float height, bool bridge)
    {
        height = 0;
        if (!_grid.TryGetValue(((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell)), out var list)) return false;
        double best = double.MaxValue;
        foreach (var (r, i) in list)
        {
            var road = _roads[r];
            if (road.Bridge != bridge) continue;
            var a = road.Plan[i - 1];
            var ab = road.Plan[i] - a;
            double len2 = ab.LengthSquared;
            double t = len2 < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
            double depth = p.DistanceTo(a + ab * t) - road.Half;
            if (depth > margin || depth >= best) continue;
            best = depth;
            height = (float)(road.Height[i - 1] + t * (road.Height[i] - road.Height[i - 1]));
        }
        return best < double.MaxValue;
    }

    public sealed record Piece(List<Vec2> Plan, float[] Height, bool Embedded);

    /// <summary>
    /// Splits one finished rail line (LV95) where it runs inside a carriageway. Null when it never
    /// does: the caller writes it exactly as before. <paramref name="count"/> is false for halo
    /// lines, which still clear road paint at the tile seam but are another block's to count.
    /// </summary>
    public List<Piece>? Split(IReadOnlyList<Vec2> plan, Func<Vec2, float> height, RoadSegment rail, bool count)
    {
        if (!IsEmbeddable(rail) || plan.Count < 2) return null;
        bool onStreet = rail.Attributes.Has(RoadAttrFlags.OnStreet);
        bool bridge = IsBridge(rail);
        double margin = onStreet ? StreetMargin : 0;
        var arc = Polyline.ArcLengths(plan);
        double total = arc[^1];
        if (count && onStreet) Stats.OnStreetM += total;

        // covered runs along the line, then merged, filtered and widened
        var runs = new List<(double A, double B)>();
        int samples = Math.Max(1, (int)Math.Ceiling(total / Step));
        double runStart = -1;
        for (int k = 0; k <= samples; k++)
        {
            double s = total * k / samples;
            bool inside = Covered(Polyline.PointAt(plan, arc, s), margin, out _, bridge);
            if (inside && runStart < 0) runStart = s;
            if ((!inside || k == samples) && runStart >= 0)
            {
                runs.Add((runStart, inside ? s : total * (k - 1) / samples));
                runStart = -1;
            }
        }
        var merged = new List<(double A, double B)>();
        foreach (var r in runs)
            if (merged.Count > 0 && r.A - merged[^1].B < MergeGap) merged[^1] = (merged[^1].A, r.B);
            else merged.Add(r);
        merged.RemoveAll(r => r.B - r.A < MinRun);
        if (merged.Count == 0) return null;
        for (int i = 0; i < merged.Count; i++)
        {
            // On until the ballast ribbon's core (its half width, what the collision road blend
            // flattens to the rail's line) no longer reaches into any carriageway: at an oblique
            // crossing it would otherwise pull the road edge down to the ballast line.
            double clear = margin + Math.Max(rail.Width * 0.5, 1.0) + 0.3;
            double a = Math.Max(0, merged[i].A - Extend), b = Math.Min(total, merged[i].B + Extend);
            for (double lim = a - MaxExtend; a > 0 && a > lim && Covered(Polyline.PointAt(plan, arc, a), clear, out _, bridge);) a = Math.Max(0, a - Step);
            for (double lim = b + MaxExtend; b < total && b < lim && Covered(Polyline.PointAt(plan, arc, b), clear, out _, bridge);) b = Math.Min(total, b + Step);
            merged[i] = (a, b);
        }
        for (int i = merged.Count - 1; i > 0; i--)
            if (merged[i].A <= merged[i - 1].B) { merged[i - 1] = (merged[i - 1].A, Math.Max(merged[i].B, merged[i - 1].B)); merged.RemoveAt(i); }

        // stations: the original vertices, every metre around each run, and the cuts themselves
        var stations = new SortedSet<double>(arc);
        foreach (var (a, b) in merged)
        {
            for (double s = Math.Max(0, a - Blend); s <= Math.Min(total, b + Blend); s += 1.0) stations.Add(s);
            stations.Add(a); stations.Add(b);
            stations.Add(Math.Max(0, a - Blend)); stations.Add(Math.Min(total, b + Blend));
        }
        var at = new List<double>();
        foreach (double s in stations) if (at.Count == 0 || s - at[^1] > 1e-3) at.Add(s);
        if (total - at[^1] > 1e-9) at.Add(total); else at[^1] = total;

        float RoadHeight(Vec2 p, float fallback) => Covered(p, 3.0 + margin, out float h, bridge) ? h : fallback;
        var points = at.Select(s => Polyline.PointAt(plan, arc, s)).ToList();
        var heights = new float[at.Count];
        var edgeHeight = merged.Select(r => (
            RoadHeight(Polyline.PointAt(plan, arc, r.A), height(Polyline.PointAt(plan, arc, r.A))) - RailTop,
            RoadHeight(Polyline.PointAt(plan, arc, r.B), height(Polyline.PointAt(plan, arc, r.B))) - RailTop)).ToList();
        for (int k = 0; k < at.Count; k++)
        {
            double s = at[k];
            float own = height(points[k]);
            // nearest run decides: inside it the road's height, outside a smoothstep back to the rail's
            double bestD = double.MaxValue; float h = own;
            for (int r = 0; r < merged.Count; r++)
            {
                var (a, b) = merged[r];
                // strictly inside: the boundary vertex belongs to the blend piece's height
                if (s > a + 1e-6 && s < b - 1e-6) { h = RoadHeight(points[k], own); bestD = 0; break; }
                double d = s <= a ? a - s : s - b;
                if (d >= Blend || d >= bestD) continue;
                bestD = d;
                float edge = s <= a ? edgeHeight[r].Item1 : edgeHeight[r].Item2;
                double w = d / Blend;
                w = w * w * (3 - 2 * w);
                h = (float)(edge + (own - edge) * w);
            }
            heights[k] = h;
        }

        // pieces, cut at every run boundary
        var pieces = new List<Piece>();
        int from = 0;
        bool Inside(double s) => merged.Any(r => s > r.A + 1e-6 && s < r.B - 1e-6);
        for (int k = 1; k < at.Count; k++)
        {
            bool boundary = k == at.Count - 1 || merged.Any(r => Math.Abs(at[k] - r.A) < 1e-6 || Math.Abs(at[k] - r.B) < 1e-6);
            if (!boundary) continue;
            if (k > from)
            {
                var h = heights[from..(k + 1)];
                bool embedded = Inside(0.5 * (at[from] + at[k]));
                // an embedded piece's ends are on the road too; the blend pieces end RailTop lower
                if (embedded) { h[0] += RailTop; h[^1] += RailTop; }
                pieces.Add(new Piece(points.GetRange(from, k - from + 1), h, embedded));
            }
            from = k;
        }

        double halfZone = RoadFormat.RailGauge(rail.Flags) * 0.5 + RoadFormat.TrackOffset(rail.Flags) + ZoneMargin;
        foreach (var p in pieces.Where(p => p.Embedded)) _zones.Add((p.Plan.ToArray(), halfZone));

        if (count)
        {
            foreach (var (a, b) in merged)
            {
                double m = b - a;
                if (onStreet) Stats.OnStreetEmbeddedM += m;
                if (onStreet || m > StreetRunMin) { Stats.StreetRuns++; Stats.StreetM += m; }
                else { Stats.Crossings++; Stats.CrossingM += m; }
                // ponytail: the neighbour tile's piece of this line does not see this run, so a
                // blend reaching past the seam ends in a step there; counted, rare (a run within 8 m of a seam)
                if ((a > 1e-6 && a < Blend && OnSeam(plan[0])) || (b < total - 1e-6 && total - b < Blend && OnSeam(plan[^1])))
                    Stats.SeamBlends++;
            }
        }
        return pieces;
    }

    private static bool OnSeam(Vec2 p)
    {
        double fx = p.X / TileSize - Math.Round(p.X / TileSize), fy = p.Y / TileSize - Math.Round(p.Y / TileSize);
        return Math.Abs(fx) * TileSize < 0.01 || Math.Abs(fy) * TileSize < 0.01;
    }

    /// <summary>
    /// <see cref="PaintType.RailGroove"/> lines for one embedded piece, one per rail, tile-local,
    /// at the road's height under each rail (the road ribbon is flat across, the rail centreline
    /// crosses it at an angle).
    /// </summary>
    public void EmitGrooves(Piece piece, RoadSegment rail, TileId tile, List<RoadPaint> into, bool count = true, bool ownHeight = false)
    {
        float gauge = RoadFormat.RailGauge(rail.Flags), track = RoadFormat.TrackOffset(rail.Flags);
        var centres = track > 0 ? new[] { -track, track } : new[] { 0f };
        var plan = piece.Plan;
        int n = plan.Count;
        bool bridge = IsBridge(rail);
        float lift = bridge ? BridgeLift : 0f;   // on a deck the paint rides at the deck's lift
        foreach (float c in centres)
        foreach (int side in new[] { -1, 1 })
        {
            double offset = c + side * gauge * 0.5;
            var v = new List<float>(n * 3);
            for (int i = 0; i < n; i++)
            {
                var f = plan[Math.Min(n - 1, i + 1)] - plan[Math.Max(0, i - 1)];
                f = f.LengthSquared < 1e-12 ? new Vec2(0, 1) : f.Normalized();
                var q = plan[i] + f.Perp * offset;
                float y = (!ownHeight && Covered(q, 3.0, out float h, bridge) ? h : piece.Height[i]) + lift;
                v.Add((float)(q.X - tile.MinE)); v.Add(y); v.Add((float)(tile.MaxN - q.Y));
            }
            into.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.RailGroove, Rgba = GrooveRgba,
                Width = GrooveWidth, Vertices = v.ToArray(),
            });
            if (count) Stats.Grooves++;
        }
    }

    /// <summary>
    /// Cuts the road's own paint (every polyline but the grooves) out of the track zone of every
    /// embedded piece seen so far, halo included. A dashed line resumes on its next dash so the
    /// pattern keeps its phase (it starts with a dash at the polyline's first vertex).
    /// </summary>
    public void ClearTrackZones(List<RoadPaint> paint, TileId tile)
    {
        if (_zones.Count == 0 || paint.Count == 0) return;
        var near = _zones.Where(z => z.Line.Any(p =>
            p.X > tile.MinE - 50 && p.X < tile.MinE + TileSize + 50 && p.Y > tile.MinN - 50 && p.Y < tile.MaxN + 50)).ToList();
        if (near.Count == 0) return;

        bool InZone(float x, float z)
        {
            var p = new Vec2(tile.MinE + x, tile.MaxN - z);
            foreach (var (line, half) in near)
                for (int i = 1; i < line.Length; i++)
                    if (Polyline.PointSegmentDistance(p, line[i - 1], line[i]) < half) return true;
            return false;
        }

        for (int pi = paint.Count - 1; pi >= 0; pi--)
        {
            var paintLine = paint[pi];
            if (paintLine.Shape != PaintShape.Polyline || paintLine.Type == PaintType.RailGroove) continue;
            // a line along its segment is cut on its unsimplified line, and stays a reference
            var v = paintLine.Segment is { } along
                ? RoadPaintGeometry.Cut(RoadPaintGeometry.Offset(along, paintLine.Offset), paintLine.From, paintLine.To)
                : paintLine.Vertices;
            double length = Length(v);
            int samples = Math.Max(1, (int)Math.Ceiling(length / 0.25));
            var keep = new List<(double A, double B)>();
            double start = 0;
            bool inside = false, any = false;
            double step = length / samples;
            for (int k = 0; k <= samples; k++)
            {
                double s = length * k / samples;
                var p = PointAt(v, s);
                bool now = InZone(p.X, p.Z);
                // kept pieces end and start on samples outside the zone
                if (now && !inside) { if (s - step - start > 1e-3) keep.Add((start, s - step)); any = true; }
                if (!now && inside) start = s;
                inside = now;
            }
            if (!any) continue;
            if (!inside && length - start > 1e-3) keep.Add((start, length));

            paint.RemoveAt(pi);
            Stats.PaintCut++;
            double period = paintLine.Dash + paintLine.Gap;
            foreach (var (a0, b) in keep)
            {
                double a = a0;
                // a dashed piece after a cut starts on its next dash, keeping the phase
                if (paintLine.Dash > 0 && a > 0) a = Math.Ceiling(a / period - 1e-9) * period;
                if (b - a < 0.3) continue;
                if (paintLine.Segment is { } seg)
                {
                    paint.Insert(pi, RoadPaint.AlongSegment(seg, paintLine.Type, paintLine.Rgba, paintLine.Width,
                        paintLine.Dash, paintLine.Gap, paintLine.Offset, paintLine.From + a,
                        b >= length - 1e-3 && float.IsPositiveInfinity(paintLine.To) ? double.PositiveInfinity : paintLine.From + b,
                        paintLine.Variant));
                    continue;
                }
                paint.Insert(pi, new RoadPaint
                {
                    Shape = paintLine.Shape, Type = paintLine.Type, Variant = paintLine.Variant, Rgba = paintLine.Rgba,
                    Width = paintLine.Width, Dash = paintLine.Dash, Gap = paintLine.Gap, Vertices = Cut(v, a, b),
                });
            }
        }
    }

    private static double Length(float[] v)
    {
        double s = 0;
        for (int i = 3; i < v.Length; i += 3) s += Math.Sqrt(Sq(v[i] - v[i - 3]) + Sq(v[i + 2] - v[i - 1]));
        return s;
    }

    private static double Sq(double x) => x * x;

    private static (float X, float Z) PointAt(float[] v, double at)
    {
        double s = 0;
        for (int i = 3; i < v.Length; i += 3)
        {
            double len = Math.Sqrt(Sq(v[i] - v[i - 3]) + Sq(v[i + 2] - v[i - 1]));
            if (s + len >= at && len > 1e-9)
            {
                double t = (at - s) / len;
                return ((float)(v[i - 3] + (v[i] - v[i - 3]) * t), (float)(v[i - 1] + (v[i + 2] - v[i - 1]) * t));
            }
            s += len;
        }
        return (v[^3], v[^1]);
    }

    /// <summary>The piece of a polyline between two horizontal distances along it (xyz kept).</summary>
    private static float[] Cut(float[] v, double from, double to)
    {
        var result = new List<float>();
        double s = 0;
        void Lerp(int a, double t) { for (int k = 0; k < 3; k++) result.Add((float)(v[a + k] + (v[a + 3 + k] - v[a + k]) * t)); }
        for (int i = 3; i < v.Length; i += 3)
        {
            double len = Math.Sqrt(Sq(v[i] - v[i - 3]) + Sq(v[i + 2] - v[i - 1]));
            double s1 = s + len;
            if (s1 >= from && s <= to && len > 1e-9)
            {
                if (result.Count == 0) Lerp(i - 3, Math.Max(0, (from - s) / len));
                if (s1 < to) { result.Add(v[i]); result.Add(v[i + 1]); result.Add(v[i + 2]); }
                else { Lerp(i - 3, (to - s) / len); break; }
            }
            s = s1;
        }
        return result.ToArray();
    }
}
