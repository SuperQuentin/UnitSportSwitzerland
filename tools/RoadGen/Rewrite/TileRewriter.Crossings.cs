namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Pedestrian crossings at traffic lights (#682, #292): the yellow bars of the Fussgängerstreifen
/// (SSV 6.17) across each signalised arm just behind the junction side of its stop line, over the
/// carriageway and on over the verge and separated path beside it, with a yellow stop line across
/// the approach's path before them.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>Bars 0.50 m wide at 0.50 m gaps, along the arm 3 m deep, a stop line's width clear of the cars' line.</summary>
    private const double ZebraBar = 0.5, ZebraGap = 0.5, ZebraDepth = 3.0, ZebraClear = 0.3;

    /// <summary>How far an island reaches into the junction past the crosswalk; a left turn starts and ends this much (and a little) beyond it.</summary>
    private const double IslandInsideM = 3.0;

    /// <summary>
    /// The crossing of one arm. <paramref name="mid"/> is the middle of its mouth, <paramref name="u"/> its outward direction,
    /// <paramref name="right"/> the approaching driver's right; <paramref name="stopAt"/> the cars' stop line's distance out
    /// from the mouth; the carriageway runs from <paramref name="lo"/> to <paramref name="hi"/> across it (negative to the left).
    /// </summary>
    private static void EmitCrossing(Dictionary<TileId, List<RoadPaint>> paint, Source source, Vec2 mid, Vec2 u, Vec2 right,
        double stopAt, double lo, double hi, RoadSide rightSide, RoadSide leftSide, Dictionary<TileId, List<RoadAreaProp>> areas, SignalStats stats)
    {
        static double Strip(RoadSide s) => s.HasTrack ? (s.VergeDm + s.BikeDm + s.BufferDm) / 10.0 : 0;
        double pathR = Strip(rightSide), pathL = Strip(leftSide);
        double s1 = stopAt - ZebraClear, s0 = s1 - ZebraDepth;
        if (s0 < 0.2) return;
        var verts = new List<float>();
        var index = new List<ushort>();
        void Quad(double a0, double a1, double l0, double l1, float lift)
        {
            Vec2 P(double s, double l) => mid + u * s + right * l;
            var corners = Local(source.Tile, [P(a0, l0), P(a1, l0), P(a1, l1), P(a0, l1)], source.SampleHeight, lift);
            ushort first = (ushort)(verts.Count / 3);
            verts.AddRange(corners);
            foreach (int k in (ReadOnlySpan<int>)[0, 1, 2, 0, 2, 3]) index.Add((ushort)(first + k));
        }
        // bars from the left end to the right end; one on a path or verge stands at its height
        double start = lo - pathL, end = hi + pathR;
        for (double l = start; l + ZebraBar <= end + 1e-6; l += ZebraBar + ZebraGap)
        {
            double centre = l + ZebraBar * 0.5;
            float lift = centre > hi ? RoadStreetSection.HeightAt(rightSide, (float)(centre - hi))
                : centre < lo ? RoadStreetSection.HeightAt(leftSide, (float)(lo - centre)) : 0f;
            Quad(s0, s1, l, l + ZebraBar, lift);
        }
        if (verts.Count == 0) return;
        Get(paint, source.Tile).Add(new RoadPaint
        {
            Shape = PaintShape.Triangles, Type = PaintType.YellowSolid, Rgba = PaintEmitter.Yellow, Vertices = verts.ToArray(), Indices = index.ToArray(),
        });
        stats.Crossings++;
        // the crosswalk cuts through the green strips: path surface over the verge and the buffer from the mouth to past the bars (the pole stands on it)
        void Cut(RoadSide side, double edge, double sign)
        {
            if (!side.HasTrack) return;
            float h = RoadStreetSection.TrackHeight(side);
            double verge = side.VergeDm / 10.0, path = side.BikeDm / 10.0, buffer = side.BufferDm / 10.0;
            foreach (var (d0, d1) in new[] { (0.0, verge), (verge + path, verge + path + buffer) })
            {
                if (d1 - d0 < 0.05) continue;
                Vec2 P(double along, double d) => mid + u * along + right * (edge + sign * d);
                Get(areas, source.Tile).Add(new RoadAreaProp
                {
                    Type = AreaPropType.BikePath, Flags = h > 0 ? PropFlags.Solid : PropFlags.None, Height = h,
                    Vertices = Local(source.Tile, [P(-0.2, d0), P(s1 + 0.25, d0), P(s1 + 0.25, d1), P(-0.2, d1)], source.SampleHeight, 0f),
                    Indices = [0, 1, 2, 0, 2, 3],
                });
            }
        }
        Cut(rightSide, hi, 1);
        Cut(leftSide, lo, -1);
        // the approach's path stops before the bars: a yellow line across it, the cars' line's distance out
        if (rightSide.HasTrack && pathR > 0)
        {
            double along = stopAt + BikeStopLine * 0.5;
            double from = hi + rightSide.VergeDm / 10.0, to = hi + (rightSide.VergeDm + rightSide.BikeDm) / 10.0;
            float lift = RoadStreetSection.HeightAt(rightSide, (float)(rightSide.VergeDm / 10.0 + 0.1));
            Get(paint, source.Tile).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.Yellow, Width = BikeStopLine,
                Vertices = Local(source.Tile, [mid + u * along + right * (from + 0.1), mid + u * along + right * (to - 0.1)], source.SampleHeight, lift),
            });
            stats.PathStopLines++;
        }
    }
}

public static partial class TileRewriter
{
    /// <summary>
    /// The left turn of arm <paramref name="arm"/> through the junction (#682): one dashed white line (SSV guide line, 0.15 m,
    /// 1 m / 1 m), the inner edge of its lane, from the pocket's left edge at the mouth round to the exit lane of the arm on its
    /// left, ending where that lane starts (past the island, <paramref name="islandArms"/> gives the hatch's width there) and
    /// arriving along the arm, so the car is led to the right of the island, not into it. Drawn only where the exit has an island.
    /// </summary>
    private static void EmitLeftGuides(Dictionary<TileId, List<RoadPaint>> paint, TileId home, Junction junction, int arm,
        ApproachLayout? layout, List<(Vec2 At, float Height)> anchors, Dictionary<int, IslandExit> islandArms)
    {
        if (layout?.LeftPocketLane is not { } lane) return;
        var from = junction.Arms[arm];
        var u = Vec2.FromHeading(from.OutwardHeading);
        // the arm on the approaching driver's left (they drive along -u, their right is u.Perp)
        int to = -1;
        double best = 0.5;
        for (int k = 0; k < junction.Arms.Count; k++)
        {
            if (k == arm) continue;
            double dot = Vec2.FromHeading(junction.Arms[k].OutwardHeading).Dot(-u.Perp);
            if (dot > best) { best = dot; to = k; }
        }
        if (to < 0 || !islandArms.TryGetValue(to, out var exit)) return;
        double lead = exit.Hatch;
        var target = junction.Arms[to];
        var ut = Vec2.FromHeading(target.OutwardHeading);
        var mid = (target.Left + target.Right) * 0.5;
        // the lane's left edge at the mouth, and the exit lane's inner edge at the target's mouth (the departing side is -ut.Perp)
        // the turn starts past the island of its own arm, and ends where the island at its exit ends, to the right of it
        double beyondOwn = islandArms.ContainsKey(arm) ? IslandInsideM + 0.3 : 0, beyondExit = IslandInsideM + 0.3;
        // the inner edge of the turn, and where a bike lane runs at the entry (the left-turn bike lane) and at the exit, its outer edge too
        void Guide(double entryAt, double exitAt)
        {
            Vec2 start = (from.Left + from.Right) * 0.5 + u.Perp * entryAt - u * beyondOwn;
            Vec2 end = mid - ut.Perp * exitAt - ut * beyondExit;
            // tangent to the way in (along -u) and to the way out (along ut): the control is where their lines meet
            double den = (-u).Cross(ut);
            Vec2 control = Math.Abs(den) < 1e-6 ? junction.Centre : start + (-u) * ((end - start).Cross(ut) / den);
            var line = new List<Vec2>();
            // straight from the crosswalk's junction edge to where the curve starts
            double crosswalkEdge = MouthSkew(junction, from) + SignalStopSetback - ZebraClear - ZebraDepth;
            line.Add((from.Left + from.Right) * 0.5 + u.Perp * entryAt + u * crosswalkEdge);
            for (int k = 0; k <= 16; k++)
            {
                double t = k / 16.0, mt = 1 - t;
                line.Add(start * (mt * mt) + control * (2 * mt * t) + end * (t * t));
            }
            Get(paint, home).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                Dash = 1f, Gap = 1f, Vertices = Local(home, line, p => HeightAt(anchors, p), 0f),
            });
        }
        Guide(lane.From + 0.1, lead);
        if (layout.LeftBikeLane is { } bikeLane && exit.Bike) Guide(bikeLane.From, lead + exit.Lane);   // between the car turn lane and the bike lane
    }
}

public static partial class TileRewriter
{
    /// <summary>What a left turn needs of its exit arm (#682): the hatch's width at the mouth, where the lane after the island starts; the car lane's width; whether it has a bike lane or path beside it.</summary>
    private readonly record struct IslandExit(double Hatch, double Lane, bool Bike);
}
