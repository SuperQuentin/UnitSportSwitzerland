using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// The <c>crag</c> fixture course (#1225): faces to climb and slopes that lift, for <c>--climbcheck</c>,
/// the thermal flychecks and <c>tools/climbnetcheck.sh</c>. Metres from the start (the spawn), X east,
/// Y north, on flat open ground at <see cref="FixtureCourse.FlatHeight"/>. Every face rises toward the
/// east (it faces west, -X) across a band of Y, so walking east from the start meets it:
/// <list type="bullet">
/// <item>the rock wall (Y ±<see cref="BandHalf"/>): from x = <see cref="WallX"/>, 80° up to a 3 m ledge at
/// <see cref="LedgeH"/>, 80° on to <see cref="WallH"/>, a plateau, then a gentle way down east;</item>
/// <item>the scree slope (Y round <see cref="ScreeY"/>): <see cref="ScreeH"/> m at 55°, loose scree;</item>
/// <item>the glacier wall (Y round <see cref="GlacierY"/>): <see cref="GlacierH"/> m at 80°, ice (refused);</item>
/// <item>a flat-roofed block <see cref="BlockH"/> m tall at (<see cref="BlockX"/>, 0);</item>
/// <item>a <see cref="SouthSlopeLength"/> m 35° south-facing slope north of the start (rock, the thermal);</item>
/// <item>a west-facing ridge south-west (open grass, the ridge lift).</item>
/// </list>
/// </summary>
public static class Crag
{
    public const double Flat = FixtureCourse.FlatHeight;
    /// <summary>Each climbing feature is this wide either side of its centre line.</summary>
    public const double BandHalf = 12;
    public const double WallX = 20, LedgeH = 14, LedgeDepth = 3, WallH = 30;
    public const double ScreeY = 60, ScreeH = 60;
    public const double GlacierY = -55, GlacierH = 20;
    public const double BlockX = -25, BlockSize = 8;
    public const float BlockH = 12;
    /// <summary>The south-facing slope: from y = <see cref="SouthSlopeY"/>, 35° up north for this long along the ground.</summary>
    public const double SouthSlopeY = 150, SouthSlopeLength = 300, SouthSlopeHalf = 150;
    /// <summary>The ridge: from x = <see cref="RidgeX"/> it rises 30° east to <see cref="RidgeH"/>, then falls as steeply.</summary>
    public const double RidgeX = -500, RidgeH = 60, RidgeYFrom = -320, RidgeYTo = -140;

    private static readonly double Tan80 = Math.Tan(80 * Math.PI / 180), Tan55 = Math.Tan(55 * Math.PI / 180),
        Tan35 = Math.Tan(35 * Math.PI / 180), Tan30 = Math.Tan(30 * Math.PI / 180), Tan20 = Math.Tan(20 * Math.PI / 180);

    /// <summary>The rock wall's foot-to-ledge run, the ledge's and the upper wall's run (m).</summary>
    public static readonly double LowerRun = LedgeH / Tan80, UpperRun = (WallH - LedgeH) / Tan80;
    /// <summary>Where the wall's top edge is (x).</summary>
    public static readonly double WallTopX = WallX + LowerRun + LedgeDepth + UpperRun;
    /// <summary>The ledge's middle (x).</summary>
    public static readonly double LedgeX = WallX + LowerRun + LedgeDepth / 2;

    public static FixtureCourse Create()
    {
        var c = new FixtureCourse
        {
            Name = "crag",
            Terrain = Ground,
            Cover = Cover,
            Extent = (-620, -340, 260, 560),
        };
        c.Blocks.Add(new FixtureCourse.Block(BlockX, 0, (float)BlockSize, (float)BlockSize, BlockH));
        return c;
    }

    /// <summary>The ground over the flat at x of a face rising at <paramref name="tan"/> to <paramref name="h"/>, a plateau, then down at 20°.</summary>
    private static double Face(double x, double from, double h, double tan, double plateau)
    {
        if (x <= from) return 0;
        double run = h / tan;
        if (x < from + run) return (x - from) * tan;
        double top = from + run + plateau;
        return x < top ? h : Math.Max(0, h - (x - top) * Tan20);
    }

    public static double Ground(double x, double y)
    {
        double h = 0;
        if (Math.Abs(y) <= BandHalf)
        {
            // the rock wall with its ledge
            double lowerTop = WallX + LowerRun, ledgeEnd = lowerTop + LedgeDepth;
            h = x <= WallX ? 0
                : x < lowerTop ? (x - WallX) * Tan80
                : x < ledgeEnd ? LedgeH
                : x < WallTopX ? LedgeH + (x - ledgeEnd) * Tan80
                : x < WallTopX + 25 ? WallH
                : Math.Max(0, WallH - (x - WallTopX - 25) * Tan20);
        }
        else if (Math.Abs(y - ScreeY) <= BandHalf) h = Face(x, WallX, ScreeH, Tan55, 20);
        else if (Math.Abs(y - GlacierY) <= BandHalf) h = Face(x, WallX, GlacierH, Tan80, 20);
        if (y >= SouthSlopeY && Math.Abs(x) <= SouthSlopeHalf)
        {
            // rising north: the face looks south. Its length along the ground, then a plateau
            double run = SouthSlopeLength * Math.Cos(35 * Math.PI / 180);
            h = Math.Max(h, Math.Min(y - SouthSlopeY, run) * Tan35);
        }
        if (y >= RidgeYFrom && y <= RidgeYTo && x >= RidgeX)
        {
            double run = RidgeH / Tan30;
            double r = x < RidgeX + run ? (x - RidgeX) * Tan30 : Math.Max(0, RidgeH - (x - RidgeX - run) * Tan30);
            h = Math.Max(h, r);
        }
        return Flat + h;
    }

    public static CoverClass Cover(double x, double y)
    {
        bool risen = Ground(x, y) > Flat + 0.05;
        if (Math.Abs(y) <= BandHalf && x > WallX - 1) return CoverClass.Rock;
        if (Math.Abs(y - ScreeY) <= BandHalf && x > WallX - 1) return CoverClass.Scree;
        if (Math.Abs(y - GlacierY) <= BandHalf && x > WallX - 1) return CoverClass.Glacier;
        if (y >= SouthSlopeY && Math.Abs(x) <= SouthSlopeHalf && risen) return CoverClass.Rock;
        return CoverClass.Open;
    }
}
