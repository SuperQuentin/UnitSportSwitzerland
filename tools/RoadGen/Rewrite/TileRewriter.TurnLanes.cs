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
/// pocket and through traffic moves into the new lane. Past the junction the main road's other
/// arm takes that lane on, widened by the same lane at the mouth and tapering back, with a hatched
/// median between the centre line and the through lane's left edge that brings the lane back to
/// its place. The ribbon keeps one width per segment, so each widening is a flush
/// <see cref="AreaPropType.Pavement"/> strip along a segment's edge (the road blend holds the
/// ground under it at the road's height, so it is in the collision too). A pocket is placed only
/// when both its approach and its exit fit. No lane-level topology is stored: traffic still
/// drives the original lane.
/// </summary>
public static partial class TileRewriter
{
    public sealed class TurnLaneStats
    {
        public int Candidates, Placed, Short, Building, OtherLine, Ground, Seam, NoSegment, NoExit, Arrows, Stripes, StopBars, SignsMoved;

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    turn lanes (#123): {Candidates:N0} main-road approaches with a left turn, {Placed:N0} pockets placed with their exit taper, {Arrows:N0} arrows, {StopBars:N0} stop bars, {Stripes:N0} median stripes, {SignsMoved:N0} signs moved off the widening; " +
            $"rejected (approach or exit): too short {Short:N0}, building {Building:N0}, another line {OtherLine:N0}, ground off the road {Ground:N0}, tile seam {Seam:N0}, no segment {NoSegment:N0}, no main road out {NoExit:N0}\n");

        public void Reject(string why)
        {
            switch (why)
            {
                case "short": Short++; break;
                case "building": Building++; break;
                case "line": OtherLine++; break;
                case "ground": Ground++; break;
                default: Seam++; break;
            }
        }
    }

    // an urban-sized pocket: 20 m taper, 20 m storage, 5 m kept clear of whatever is at the other end
    private const double TurnTaper = 20, TurnStorage = 20, TurnLane = 3.0, TurnSolid = 10, TurnClear = 5;

    /// <summary>The stop bar across the end of the left-turn lane.</summary>
    private const float StopBar = 0.4f;

    /// <summary>Past the junction the through lane comes back to its place over this length.</summary>
    private const double TurnExit = 30;

    private const double TurnStation = 2.5, TurnGroundStep = 1.2, TileSizeM = 1000;
    private const double HatchStep = 2.5, HatchWidth = 0.3;

    private static double Sq(double v) => v * v;

    private static void EmitTurnLanes(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile)> segmentOf, Dictionary<TileId, List<RoadSegment>> output,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadAreaProp>> areas,
        Dictionary<TileId, List<RoadPointProp>> signs, TurnLaneStats stats)
    {
        var net = result.Network;
        var indexes = new Dictionary<TileId, EmbankmentPlanner.LineIndex>();
        EmbankmentPlanner.LineIndex Lines(TileId t) =>
            indexes.TryGetValue(t, out var l) ? l : indexes[t] = new EmbankmentPlanner.LineIndex(output[t]);

        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Kind != PriorityPlanner.Kind.Main) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var arm = plan.Arms[i];
                if (arm.Role != PriorityPlanner.Role.Main || !TurnLaneRoad(net.Links[arm.LinkId])) continue;

                // the approaching driver's way, and whether a car road leaves to their left / right
                var d = Vec2.FromHeading(junction.Arms[i].OutwardHeading) * -1;
                bool left = false, right = false;
                int exit = -1;
                for (int k = 0; k < junction.Arms.Count && k < plan.Arms.Count; k++)
                {
                    if (k == i) continue;
                    if (plan.Arms[k].Role == PriorityPlanner.Role.Main) { exit = k; continue; }
                    if (plan.Arms[k].Role != PriorityPlanner.Role.Yield) continue;
                    if (InfoOf(net.Links[plan.Arms[k].LinkId]) is not { } info || info.Class > RoadClass.Lane) continue;
                    var v = Vec2.FromHeading(junction.Arms[k].OutwardHeading);
                    if (d.X * v.X + d.Y * v.Y > 0.87) continue;   // carries on nearly straight
                    if (d.X * v.Y - d.Y * v.X > 0) left = true; else right = true;
                }
                if (!left) continue;
                stats.Candidates++;
                if (exit < 0 || !TurnLaneRoad(net.Links[plan.Arms[exit].LinkId])) { stats.NoExit++; continue; }
                if (!segmentOf.TryGetValue(arm.LinkId, out var inSeg) || !segmentOf.TryGetValue(plan.Arms[exit].LinkId, out var outSeg))
                { stats.NoSegment++; continue; }

                // approach: traffic drives toward the junction and keeps right
                bool inAtEnd = arm.End == LinkEnd.End;
                var approach = new Widening(inSeg.Segment, inSeg.Tile, output[inSeg.Tile].IndexOf(inSeg.Segment),
                    junctionAtEnd: inAtEnd, side: inAtEnd ? 1 : -1, length: TurnTaper + TurnStorage, taper: TurnTaper);
                // exit: traffic drives away from the junction and keeps right
                bool outAtEnd = plan.Arms[exit].End == LinkEnd.End;
                var departure = new Widening(outSeg.Segment, outSeg.Tile, output[outSeg.Tile].IndexOf(outSeg.Segment),
                    junctionAtEnd: outAtEnd, side: outAtEnd ? -1 : 1, length: TurnExit, taper: TurnExit);

                if ((approach.Check(Lines(inSeg.Tile), grids, buildings) ?? departure.Check(Lines(outSeg.Tile), grids, buildings)) is { } why)
                { stats.Reject(why); continue; }

                approach.Emit(Get(paint, inSeg.Tile), Get(areas, inSeg.Tile));
                approach.Pocket(Get(paint, inSeg.Tile), right, stats);
                departure.Emit(Get(paint, outSeg.Tile), Get(areas, outSeg.Tile));
                departure.Median(Get(paint, outSeg.Tile), stats);
                Across(approach, departure, inSeg.Tile, Get(areas, inSeg.Tile), Get(paint, home), home, priority.Guides, joined: right);
                // a sign beside the old edge (#121's 3.03) would now stand on the widening
                stats.SignsMoved += approach.PushOut(Get(signs, inSeg.Tile)) + departure.PushOut(Get(signs, outSeg.Tile));
                stats.Placed++;
            }
        }
    }

    /// <summary>
    /// The through lane across the junction: the junction polygon only covers the original road,
    /// so the lane's outer part, from the approach strip's mouth to the exit strip's, is paved as
    /// one more strip (in the approach's tile). The junction's guide line on that side (#121) runs
    /// along the old edge, now inside the lane: it moves out onto the strip's edge, dashed where a
    /// road joins on that side, solid where none does (and only if there was one).
    /// </summary>
    private static void Across(Widening approach, Widening exit, TileId tile, List<RoadAreaProp> areas,
        List<RoadPaint> homePaint, TileId home, HashSet<RoadPaint> guides, bool joined)
    {
        var (ai, ao) = approach.Mouth(tile);
        var (ei, eo) = exit.Mouth(tile);
        areas.Add(new RoadAreaProp
        {
            Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
            Vertices = [.. ai, .. ao, .. eo, .. ei], Indices = [0, 1, 2, 0, 2, 3],
        });

        var (corner, aEdge) = approach.Mouth(home, PaintEmitter.EdgeLineInset);
        var (_, eEdge) = exit.Mouth(home, PaintEmitter.EdgeLineInset);
        static double Gap(float[] v, int i, float[] p) => Math.Sqrt(Sq(v[i] - p[0]) + Sq(v[i + 2] - p[2]));
        var old = homePaint.FirstOrDefault(q => guides.Contains(q)
            && (Gap(q.Vertices, 0, corner) < 1.5 || Gap(q.Vertices, q.Vertices.Length - 3, corner) < 1.5));
        if (old is not null) homePaint.Remove(old);
        if (old is null && !joined) return;
        homePaint.Add(new RoadPaint
        {
            Shape = PaintShape.Polyline, Type = joined ? PaintType.WhiteDashed : PaintType.WhiteSolid,
            Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
            Dash = joined ? GuideDash : 0, Gap = joined ? GuideDash : 0,
            Vertices = [.. aEdge, .. eEdge],
        });
    }

    /// <summary>A main-road arm a pocket can be built on: two-way, paved, at grade, Major or Road.</summary>
    private static bool TurnLaneRoad(RoadLink link) =>
        link.Tag is Source { Segment: var s } source
        && s.Class is RoadClass.Major or RoadClass.Road && s.Surface == RoadSurface.Paved
        && (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel | RoadFlags.Divided)) == 0
        && CrossSectionPlanner.Attributes(source.Line).OneWay == 0;

    /// <summary>
    /// One lane's widening along the junction end of a segment, on one side: full lane width from
    /// the junction mouth out to <c>length - taper</c>, then tapering to nothing at
    /// <c>length</c>. Distances (<c>dist</c>) run from the mouth outward; <c>side</c> is +1 right
    /// of the drawing direction, -1 left of it.
    /// </summary>
    private sealed class Widening
    {
        private readonly RoadSegment _seg;
        private readonly TileId _tile;
        private readonly int _self, _side;
        private readonly bool _atEnd;
        private readonly double[] _along;
        private readonly double _half, _length, _taper, _total;
        private readonly List<double> _dists = new();

        public Widening(RoadSegment seg, TileId tile, int self, bool junctionAtEnd, int side, double length, double taper)
        {
            _seg = seg; _tile = tile; _self = self; _atEnd = junctionAtEnd; _side = side;
            _length = length; _taper = taper; _half = seg.Width * 0.5;
            int n = seg.PointCount;
            var p = seg.Points;
            _along = new double[n];
            for (int k = 1; k < n; k++)
                _along[k] = _along[k - 1] + Math.Sqrt(Sq(p[k * 3] - p[k * 3 - 3]) + Sq(p[k * 3 + 2] - p[k * 3 - 1]));
            _total = _along[^1];
            for (double dd = 0; dd < length - 1e-6; dd += TurnStation) _dists.Add(dd);
            if (!_dists.Any(x => Math.Abs(x - (length - taper)) < 1e-6)) _dists.Add(length - taper);
            _dists.Add(length);
            _dists.Sort();
        }

        private double Widen(double dist) => TurnLane * Math.Clamp((_length - dist) / _taper, 0, 1);

        /// <summary>The segment at a distance from the mouth: position, road height, unit vector to the widened side.</summary>
        private (double X, double Y, double Z, double Sx, double Sz) At(double dist)
        {
            double a = AlongOf(dist);
            int n = _seg.PointCount, k = 1;
            var p = _seg.Points;
            while (k < n - 1 && _along[k] < a) k++;
            double span = Math.Max(1e-9, _along[k] - _along[k - 1]), t = Math.Clamp((a - _along[k - 1]) / span, 0, 1);
            double x = p[k * 3 - 3] + (p[k * 3] - p[k * 3 - 3]) * t, z = p[k * 3 - 1] + (p[k * 3 + 2] - p[k * 3 - 1]) * t;
            double y = p[k * 3 - 2] + (p[k * 3 + 1] - p[k * 3 - 2]) * t;
            double fx = (p[k * 3] - p[k * 3 - 3]) / span, fz = (p[k * 3 + 2] - p[k * 3 - 1]) / span;
            return (x, y, z, -fz * _side, fx * _side);   // right of the drawing is (-fz, fx), X east and Z south
        }

        private float[] Point(double dist, double offset)
        {
            var (x, y, z, sx, sz) = At(dist);
            return [(float)(x + sx * offset), (float)y, (float)(z + sz * offset)];
        }

        /// <summary>
        /// The strip's inner and outer corners at the mouth (less <paramref name="inset"/> on the
        /// outer one), as points in <paramref name="frame"/>'s tile-local frame.
        /// </summary>
        public (float[] Inner, float[] Outer) Mouth(TileId frame, double inset = 0)
        {
            float[] In(float[] p) =>
                [(float)(p[0] + _tile.MinE - frame.MinE), p[1], (float)(p[2] + frame.MaxN - _tile.MaxN)];
            return (In(Point(0, _half)), In(Point(0, _half + TurnLane - inset)));
        }

        /// <summary>
        /// Moves the point props of the segment's tile that stand beside the old edge along the
        /// widening out by the widening there, so they keep their clearance from the new edge.
        /// Returns how many moved.
        /// </summary>
        public int PushOut(List<RoadPointProp> props)
        {
            int moved = 0;
            var p = _seg.Points;
            for (int i = 0; i < props.Count; i++)
            {
                var prop = props[i];
                // nearest point of the segment, and the prop's offset toward the widened side
                double best = double.MaxValue, at = 0, offset = 0;
                for (int k = 1; k < _seg.PointCount; k++)
                {
                    double ax = p[k * 3 - 3], az = p[k * 3 - 1], dx = p[k * 3] - ax, dz = p[k * 3 + 2] - az;
                    double l2 = dx * dx + dz * dz;
                    if (l2 < 1e-12) continue;
                    double t = Math.Clamp(((prop.X - ax) * dx + (prop.Z - az) * dz) / l2, 0, 1);
                    double px = ax + dx * t, pz = az + dz * t, d2 = Sq(prop.X - px) + Sq(prop.Z - pz);
                    if (d2 >= best) continue;
                    best = d2;
                    at = _along[k - 1] + Math.Sqrt(l2) * t;
                    double len = Math.Sqrt(l2);
                    offset = ((prop.X - px) * (-dz / len) + (prop.Z - pz) * (dx / len)) * _side;   // right of the drawing is (-dz, dx)
                }
                double dist = _atEnd ? _total - at : at;
                if (dist < -1 || dist > _length) continue;
                double w = Widen(Math.Clamp(dist, 0, _length));
                if (w < 0.05 || offset < _half - 0.3 || offset > _half + w + 2.0) continue;
                var (x, _, z, sx, sz) = At(Math.Clamp(dist, 0, _length));
                double o = offset + w;
                props[i] = prop with { X = (float)(x + sx * o), Z = (float)(z + sz * o) };
                moved++;
            }
            return moved;
        }

        /// <summary>Along-segment metres of a distance from the mouth.</summary>
        private double AlongOf(double dist) => _atEnd ? _total - dist : dist;

        /// <summary>Null when it fits; else why not.</summary>
        public string? Check(EmbankmentPlanner.LineIndex lines, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings)
        {
            if (_total < _length + TurnClear) return "short";
            foreach (double dist in _dists)
            {
                double w = Widen(dist);
                if (w < 0.3) continue;
                var (x, y, z, sx, sz) = At(dist);
                foreach (double o in (ReadOnlySpan<double>)[_half + w * 0.5, _half + w + 0.5])
                {
                    double px = x + sx * o, pz = z + sz * o;
                    if (px < 0 || pz < 0 || px > TileSizeM || pz > TileSizeM) return "seam";
                    var lv95 = new Vec2(_tile.MinE + px, _tile.MaxN - pz);
                    if (buildings.Contains(lv95)) return "building";
                    if (lines.Covering(px, pz, _self, x, z, _half + 1.0) is not null) return "line";
                    if (grids is not null && SampleGround(grids, lv95.X, lv95.Y) is var g && !double.IsNaN(g)
                        && Math.Abs(g - y) > TurnGroundStep) return "ground";
                }
            }
            return null;
        }

        /// <summary>The strip, and the edge line moved out onto it.</summary>
        public void Emit(List<RoadPaint> paint, List<RoadAreaProp> areas)
        {
            var v = new List<float>();
            var outer = new List<float>();
            foreach (double dist in _dists)
            {
                double w = Widen(dist);
                v.AddRange(Point(dist, _half));
                v.AddRange(Point(dist, _half + w));
                outer.AddRange(Point(dist, _half + w - PaintEmitter.EdgeLineInset));
            }
            var idx = new List<ushort>();
            for (int k = 0; k + 1 < _dists.Count; k++)
            {
                ushort i0 = (ushort)(k * 2), i1 = (ushort)(k * 2 + 1), i2 = (ushort)(k * 2 + 2), i3 = (ushort)(k * 2 + 3);
                idx.AddRange([i0, i2, i3, i0, i3, i1]);
            }
            areas.Add(new RoadAreaProp
            {
                Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
                Vertices = v.ToArray(), Indices = idx.ToArray(),
            });

            // the old edge line stops where the widening starts; a new one follows the strip
            var edge = paint.FirstOrDefault(q => q.Segment == _seg && q.Dash == 0 && Math.Sign(q.Offset) == _side && Math.Abs(q.Offset) > _half * 0.5);
            if (edge is null) return;
            Cut(paint, edge);
            paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = PaintEmitter.White, Width = edge.Width,
                Vertices = outer.ToArray(),
            });
        }

        /// <summary>Removes a line along the segment where the widening runs, keeping the rest.</summary>
        private void Cut(List<RoadPaint> paint, RoadPaint line)
        {
            paint.Remove(line);
            double far = AlongOf(_length);   // the widening's far end, in along-segment metres
            double from = _atEnd ? line.From : far, to = _atEnd ? far : line.To;
            if (to - from > 1)
                paint.Add(RoadPaint.AlongSegment(_seg, line.Type, line.Rgba, line.Width, line.Dash, line.Gap, line.Offset, from, to, line.Variant));
        }

        /// <summary>A line along the segment between two distances from the mouth.</summary>
        private RoadPaint Line(PaintType type, float dash, float gap, double offset, double d0, double d1)
        {
            double a = AlongOf(d0), b = AlongOf(d1);
            return RoadPaint.AlongSegment(_seg, type, PaintEmitter.White, PaintEmitter.LineWidth, dash, gap,
                offset, Math.Min(a, b), Math.Max(a, b));
        }

        /// <summary>
        /// The approach, from far to near. Over the taper the strip widens on the right while a
        /// hatched median opens between the centre line and the through lane, carrying the lane
        /// across by a lane width. Where the hatch closes, the left-turn lane appears beside the
        /// through lane, whose left edge carries on as a dashed line (taking the pocket is a lane
        /// change), solid for the last <see cref="TurnSolid"/> m. A stop bar closes the pocket at
        /// the mouth; arrows in both lanes; the centre line is solid along all of it.
        /// </summary>
        public void Pocket(List<RoadPaint> paint, bool rightTurn, TurnLaneStats stats)
        {
            double storage = _length - _taper;
            SolidCentre(paint);
            Hatch(paint, stats, storage, _length, d => _half * Math.Clamp((_length - d) / _taper, 0, 1));

            double offset = _side * _half;
            paint.Add(Line(PaintType.WhiteDashed, 3f, 3f, offset, TurnSolid, storage));
            paint.Add(Line(PaintType.WhiteSolid, 0, 0, offset, 0, TurnSolid));

            // across the pocket, just short of the mouth
            double bar = StopBar * 0.5 + 0.1;
            paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.White, Width = StopBar,
                Vertices = [.. Point(bar, 0.1), .. Point(bar, _half - 0.1)],
            });
            stats.StopBars++;

            // two per lane in the storage length, tips 5 m and 13 m from the stop bar: left in the
            // pocket, straight (and right) in the through lane
            foreach (double back in (ReadOnlySpan<double>)[5 + ArrowLength, 13 + ArrowLength])
            {
                var (x, y, z, sx, sz) = At(back);
                // the through lane lies on the driver's right (sx, sz): their forward is that turned a quarter left
                double fx = sz, fz = -sx;
                paint.Add(Arrow(x + sx * _half * 0.5, y, z + sz * _half * 0.5, fx, fz, PaintArrow.Left));
                paint.Add(Arrow(x + sx * (_half + TurnLane * 0.5), y, z + sz * (_half + TurnLane * 0.5), fx, fz,
                    rightTurn ? PaintArrow.Straight | PaintArrow.Right : PaintArrow.Straight));
                stats.Arrows += 2;
            }
        }

        /// <summary>
        /// The exit: the through lane comes out of the junction on the strip and eases back over
        /// the taper. Between the centre line and the lane's left edge, a hatched median a lane
        /// wide at the mouth, closing where the lane is back in place; the centre line is solid
        /// along it.
        /// </summary>
        public void Median(List<RoadPaint> paint, TurnLaneStats stats)
        {
            SolidCentre(paint);
            Hatch(paint, stats, 0, _length, d => _half * Math.Clamp(1 - d / _length, 0, 1));
        }

        /// <summary>The dashed centre line along the widening turned solid: no overtaking into the junction.</summary>
        private void SolidCentre(List<RoadPaint> paint)
        {
            var centre = paint.FirstOrDefault(q => q.Segment == _seg && q.Dash > 0 && Math.Abs(q.Offset) < 0.3);
            if (centre is null) return;
            Cut(paint, centre);
            paint.Add(Line(PaintType.WhiteSolid, 0, 0, centre.Offset, 0, _length));
        }

        /// <summary>
        /// A hatched median between the centre line and <paramref name="border"/> (an offset that
        /// closes to 0 at <paramref name="far"/>), from <paramref name="near"/> to
        /// <paramref name="far"/> metres from the mouth: the solid border, and stripes at 45
        /// degrees from the centre line outward and away from the mouth.
        /// </summary>
        private void Hatch(List<RoadPaint> paint, TurnLaneStats stats, double near, double far, Func<double, double> border)
        {
            var line = new List<float>();
            foreach (double dist in _dists.Where(x => x >= near - 1e-6 && x <= far + 1e-6))
                line.AddRange(Point(dist, border(dist)));
            paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                Vertices = line.ToArray(),
            });

            var v = new List<float>();
            var idx = new List<ushort>();
            double h = HatchWidth * Math.Sqrt(0.5);   // half the stripe's width, along the road
            for (double d0 = near + 0.6; d0 < far; d0 += HatchStep)
            {
                // where the stripe meets the border: border(d1) = d1 - d0, by bisection
                double lo = d0, hi = far;
                for (int k = 0; k < 40; k++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (border(mid) > mid - d0) lo = mid; else hi = mid;
                }
                double d1 = lo;
                if (d1 - d0 < 0.4) break;
                ushort b = (ushort)(v.Count / 3);
                v.AddRange(Point(d0 - h, 0)); v.AddRange(Point(d1 - h, border(d1 - h)));
                v.AddRange(Point(d1 + h, border(d1 + h))); v.AddRange(Point(d0 + h, 0));
                idx.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
                stats.Stripes++;
            }
            if (idx.Count > 0)
                paint.Add(new RoadPaint
                {
                    Shape = PaintShape.Triangles, Type = PaintType.Hatch, Rgba = PaintEmitter.White,
                    Vertices = v.ToArray(), Indices = idx.ToArray(),
                });
        }
    }

    /// <summary>Length of a straight lane arrow, tail to tip.</summary>
    private const double ArrowLength = 6.5;

    /// <summary>
    /// Lane arrows (Einspurpfeile, SSV 6.06), outlines traced from the Wikimedia Commons diagram
    /// <c>CH-Markierung-606-Einspurpfeile.svg</c> (path data in its units, y up = the driver's left;
    /// tail x, shaft centre y): straight, a dart head with notches where the barbs meet the shaft;
    /// left, the shaft jogging left near its end into an open corner head pointing 45 degrees
    /// forward-left; straight + right, the straight arrow with a short barb leaving its shaft to the
    /// right. Right and straight + left are their mirrors. Scaled so the straight one is
    /// <see cref="ArrowLength"/> long (the Stadt Bern Normalien's 6.50 m); the shaft comes out
    /// 0.175 m.
    /// </summary>
    private static readonly (double Tail, double Centre, double[] Xy) StraightOutline = (449.281, 390.959,
    [
        912.961, 362.883, 1026, 380.879, 449.281, 380.879, 449.281, 401.039, 1027.44, 401.039, 912.961, 420.48,
        912.961, 438.48, 1198.08, 391.68, 912.961, 344.16,
    ]);

    private static readonly (double Tail, double Centre, double[] Xy) LeftOutline = (449.281, 671.039,
    [
        1057.68, 654.48, 1110.24, 701.277, 939.602, 660.961, 449.281, 660.961, 449.281, 681.117, 927.359, 681.117,
        1066.32, 712.078, 912.961, 712.078, 913.684, 733.684, 1210.32, 733.684, 1091.52, 619.199,
    ]);

    private static readonly (double Tail, double Centre, double[] Xy) StraightRightOutline = (443.52, 141.838,
    [
        907.203, 113.762, 1020.24, 131.758, 616.316, 131.758, 699.121, 65.5195, 704.879, 113.762, 740.879, 110.16,
        731.52, 29.5195, 585.359, 47.5195, 586.801, 66.957, 668.16, 56.879, 570.961, 131.758, 443.52, 131.758,
        443.52, 151.918, 1021.68, 151.918, 907.203, 170.641, 907.203, 189.359, 1192.32, 141.84, 907.203, 95.0391,
    ]);

    private static readonly Dictionary<PaintArrow, (Vec2[] Points, int[] Triangles)> ArrowShapes = new()
    {
        [PaintArrow.Straight] = Shape(StraightOutline, mirror: false),
        [PaintArrow.Left] = Shape(LeftOutline, mirror: false),
        [PaintArrow.Right] = Shape(LeftOutline, mirror: true),
        [PaintArrow.Straight | PaintArrow.Right] = Shape(StraightRightOutline, mirror: false),
        [PaintArrow.Straight | PaintArrow.Left] = Shape(StraightRightOutline, mirror: true),
    };

    /// <summary>An outline in metres, (forward, left) from the tail, and its triangles.</summary>
    private static (Vec2[] Points, int[] Triangles) Shape((double Tail, double Centre, double[] Xy) o, bool mirror)
    {
        double scale = ArrowLength / (1198.08 - 449.281);
        var pts = new Vec2[o.Xy.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new Vec2((o.Xy[i * 2] - o.Tail) * scale, (o.Xy[i * 2 + 1] - o.Centre) * scale * (mirror ? -1 : 1));
        return (pts, Junctions.EarClip.Triangulate(pts).ToArray());
    }

    /// <summary>
    /// A lane arrow (#123) as paint triangles, its tail at (x, z) and pointing along (fx, fz)
    /// (tile-local, X east and Z south); shapes in <see cref="ArrowShapes"/>.
    /// </summary>
    private static RoadPaint Arrow(double x, double y, double z, double fx, double fz, PaintArrow kind)
    {
        double len = Math.Sqrt(fx * fx + fz * fz);
        fx /= len; fz /= len;
        // the driver's left, X east and Z south: forward (fx, fz) turned a quarter to the left
        double lx = fz, lz = -fx;
        var (pts, tris) = ArrowShapes[kind];
        var v = new float[pts.Length * 3];
        for (int i = 0; i < pts.Length; i++)
        {
            v[i * 3] = (float)(x + fx * pts[i].X + lx * pts[i].Y);
            v[i * 3 + 1] = (float)y;
            v[i * 3 + 2] = (float)(z + fz * pts[i].X + lz * pts[i].Y);
        }
        return new RoadPaint
        {
            Shape = PaintShape.Triangles, Type = PaintType.Arrow, Variant = (byte)kind, Rgba = PaintEmitter.White,
            Vertices = v, Indices = tris.Select(i => (ushort)i).ToArray(),
        };
    }
}
