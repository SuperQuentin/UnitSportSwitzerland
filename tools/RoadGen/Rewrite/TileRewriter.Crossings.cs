namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
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

    /// <summary>
    /// The crossing of one arm. <paramref name="mid"/> is the middle of its mouth, <paramref name="u"/> its outward direction,
    /// <paramref name="right"/> the approaching driver's right; <paramref name="stopAt"/> the cars' stop line's distance out
    /// from the mouth; the carriageway runs from <paramref name="lo"/> to <paramref name="hi"/> across it (negative to the left).
    /// </summary>
    private static void EmitCrossing(Dictionary<TileId, List<RoadPaint>> paint, Source source, Vec2 mid, Vec2 u, Vec2 right,
        double stopAt, double lo, double hi, RoadSide rightSide, RoadSide leftSide, SignalStats stats)
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
