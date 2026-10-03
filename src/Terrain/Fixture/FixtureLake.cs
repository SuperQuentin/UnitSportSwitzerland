using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// The <c>lake</c> fixture course (#299): water with a volume under it, for the wave field, the
/// translucent surface, the underwater look and vehicles that drive in. Metres from the start (the
/// spawn), X east, Y north:
/// <list type="bullet">
/// <item>the start is on a gravel beach 2 m above the water, facing it; the waterline is at
/// x = <see cref="ShoreX"/>;</item>
/// <item>an elliptical lake 2.6 x 2 km east of it, its still level <see cref="Level"/>; from the
/// waterline a 150 m shelf (gravel, down to 2.5 m: wading, then floating), a 50 m drop-off, then
/// 25 m of water;</item>
/// <item>a 16 m river from the west joining it at y = <see cref="RiverY"/>, 2 m deep, its level
/// falling 0.67 % to the lake's (small fetch: flat water);</item>
/// <item>a slipway road down to the waterline at y = <see cref="SlipwayY"/>, to drive a car in.</item>
/// </list>
/// </summary>
public static class Lake
{
    public const double Level = FixtureCourse.FlatHeight;
    public const double ShoreX = 40;
    public const double RiverY = 400;
    public const double SlipwayY = -150;
    public const double ShelfM = 150, DropM = 50, ShelfDepth = 2.5, DeepDepth = 25;
    public const double RiverHalfWidth = 8, RiverDepth = 2, RiverFall = 0.0067;

    /// <summary>The fixture's boat landing (#377): a stop on the shelf, its pier planned out to the steamer's water.</summary>
    public const string LandingName = "Fixture (lac)";
    public const double LandingX = ShoreX + 110, LandingY = 260;

    /// <summary>A harbour jetty (#377) straight out from the beach, for parking boats.</summary>
    public const double JettyY = -60, JettyLength = 90;

    /// <summary>The lake's fetch for the waves: a big lake (a 2 km crossing and more in the long axis).</summary>
    public const double LakeFetch = 3000;

    private const double A = 1300, B = 1000, Cx = ShoreX + A;

    public static FixtureCourse Create()
    {
        var c = new FixtureCourse
        {
            Name = "lake",
            Terrain = Ground,
            Water = Water,
            Cover = Cover,
            Extent = (-1000, -1100, 2700, 1100),
        };
        // the slipway: 300 m down to the waterline, then 40 m on into the shelf (a boat ramp)
        // a boat landing out on the shelf (#377): its pier is planned from the stop, out to water
        // that floats the steamer; and a jetty off the beach for the boats
        c.Stops.Add((LandingName, LandingX, LandingY));
        c.Jetties.Add(new() { (ShoreX - 8, JettyY, Level + 0.4), (ShoreX + JettyLength, JettyY, Level + 0.4) });
        var slip = new List<(double X, double Y, double Z)>();
        for (double x = -300; x <= ShoreX + 40; x += 2) slip.Add((x, SlipwayY, Ground(x, SlipwayY)));
        return c.Road(RoadClass.Road, slip);
    }

    /// <summary>Metres inside the lake's shore (negative outside), along the long axis near y = 0.</summary>
    private static double Inside(double x, double y)
    {
        double u = (x - Cx) / A, v = y / B;
        return (1 - Math.Sqrt(u * u + v * v)) * A;
    }

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Water depth at a distance inside the shore: shelf, drop-off, deep.</summary>
    private static double LakeDepth(double s) =>
        s < ShelfM ? ShelfDepth * s / ShelfM
        : s < ShelfM + DropM ? ShelfDepth + (DeepDepth - ShelfDepth) * Smooth((s - ShelfM) / DropM)
        : DeepDepth;

    /// <summary>The river's still level at x: the lake's where it joins, rising upstream (west).</summary>
    private static double RiverLevel(double x) => Level + Math.Max(0, RiverJoinX - x) * RiverFall;

    /// <summary>Where the river's centreline meets the lake's shore.</summary>
    private static readonly double RiverJoinX = Cx - A * Math.Sqrt(1 - RiverY / B * (RiverY / B));

    public static double Ground(double x, double y)
    {
        double s = Inside(x, y);
        double g = s >= 0
            ? Level - LakeDepth(s)
            // the beach rises 2.5 m over 50 m, then the land 1 %
            : Level + 2.5 * Smooth(-s / 50) + Math.Max(0, -s - 50) * 0.01;

        // the river's channel: a 12 m bed 2 m under its level, 1:1 banks, then 0.8 up to the land
        if (x < RiverJoinX + 30)
        {
            double w = Math.Abs(y - RiverY), level = RiverLevel(x);
            double channel = w < RiverHalfWidth - 2 ? level - RiverDepth
                : w < RiverHalfWidth ? level - RiverDepth + (w - (RiverHalfWidth - 2)) * (RiverDepth / 2)
                : level + (w - RiverHalfWidth) * 0.8;
            g = Math.Min(g, channel);
        }
        return g;
    }

    /// <summary>
    /// Still water: the lake's level inside its shore, the river's in its channel. Wet a little
    /// past the waterline (up to 1 m of ground above the level) so the surface runs on under the
    /// beach, which hides its edge, rather than stopping in 2 m steps short of it.
    /// </summary>
    public static (double Level, double Fetch) Water(double x, double y)
    {
        double g = Ground(x, y);
        if (Inside(x, y) > -60 && g < Level + 1.0) return (Level, LakeFetch);
        if (x < RiverJoinX + 30 && Math.Abs(y - RiverY) < RiverHalfWidth + 1)
        {
            double level = RiverLevel(x);
            if (g < level + 1.0) return (level, 2 * RiverHalfWidth);
        }
        return (double.NaN, 0);
    }

    /// <summary>Gravel on the beach, the shelf and the river bed; water cover in the deep; grass inland.</summary>
    public static CoverClass Cover(double x, double y)
    {
        double g = Ground(x, y);
        var (level, _) = Water(x, y);
        if (!double.IsNaN(level)) return level - g > ShelfDepth + 0.5 ? CoverClass.Water : CoverClass.LooseScree;
        return Inside(x, y) > -50 ? CoverClass.LooseScree : CoverClass.Open;
    }
}
