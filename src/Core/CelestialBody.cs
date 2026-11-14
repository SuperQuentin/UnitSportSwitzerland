using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// The bodies of the world (#1847, docs/plans/planets.md section 1). A byte: it travels with a position
/// (<see cref="GlobalPos.Body"/>). <b>The Earth is 0</b>, so a default position, every key, file, record and wire
/// value of today means the Earth. Phobos and Deimos come with Mars (H7e).
/// </summary>
public enum BodyId : byte
{
    Earth = 0, Moon, Mars, Sun, Mercury, Venus, Jupiter, Saturn, Uranus, Neptune, Io, Europa, Ganymede, Callisto, Titan,
}

/// <summary>
/// A body the world can be on or near (#1847, docs/plans/planets.md section 1; it was <c>Planet</c>, Earth-only, of
/// #1430 H2): its shape, how hard it pulls and how fast it spins, whose sphere of influence it sits in, and the
/// ground game's numbers there. One record per body, the same code for all of them: the Earth's values are what the
/// game had.
///
/// <para>
/// <see cref="Spin"/> is the body's real sidereal rate: physics inside its sphere of influence runs on sim time
/// with it (the orbits' integration frame, <see cref="Orbit.RotationAngle"/>). Where the body is and how it is
/// turned in the sky come from <see cref="Frames"/> at a <see cref="SkyMoment"/> (section 2 of the plan).
/// </para>
/// </summary>
public sealed class CelestialBody
{
    private const double Au = Ephemeris.AuMetres;

    /// <summary>A rate in degrees a day (the IAU's W rates) in radians a second.</summary>
    private static double DegPerDay(double d) => d * System.Math.PI / 180 / 86400;

    /// <summary>WGS84: GM and the rotation rate of NIMA TR8350.2. The ground game keeps its tuned 9.81 m/s².</summary>
    public static readonly CelestialBody Earth = new(BodyId.Earth, "Earth", null, Ellipsoid.Wgs84, 3.986004418e14,
        7.292115e-5, BodyId.Sun, 1.00000261 * Au, landable: true, spaceEnterM: 60_000, spaceExitM: 50_000, leafLevel: 14,
        groundGravity: 9.81, air: BodyAir.Earth);

    public static readonly CelestialBody Sun = new(BodyId.Sun, "Sun", SkyBody.Sun, Ellipsoid.Sphere(695_700_000),
        1.32712440018e20, DegPerDay(14.1844), null, 0, landable: false);

    public static readonly CelestialBody Moon = new(BodyId.Moon, "Moon", SkyBody.Moon, Ellipsoid.Moon,
        4.902800066e12, DegPerDay(13.17635815), BodyId.Earth, 384_400_000, landable: true, spaceEnterM: 10_000,
        spaceExitM: 8_000, craterDensity: 1);

    public static readonly CelestialBody Mars = new(BodyId.Mars, "Mars", SkyBody.Mars,
        Ellipsoid.Mars, 4.282837e13, DegPerDay(350.89198226), BodyId.Sun, 1.52371034 * Au,
        landable: true, spaceEnterM: 40_000, spaceExitM: 30_000, craterDensity: 0.15, air: BodyAir.Mars);

    public static readonly CelestialBody Mercury = new(BodyId.Mercury, "Mercury", SkyBody.Mercury,
        Ellipsoid.Mercury, 2.2032e13, DegPerDay(6.1385108), BodyId.Sun, 0.38709927 * Au,
        landable: true, spaceEnterM: 10_000, spaceExitM: 8_000, craterDensity: 1);

    public static readonly CelestialBody Venus = new(BodyId.Venus, "Venus", SkyBody.Venus, Ellipsoid.Venus,
        3.24859e14, DegPerDay(-1.4813688), BodyId.Sun, 0.72333566 * Au, landable: true, spaceEnterM: 120_000,
        spaceExitM: 100_000, air: BodyAir.Venus);

    public static readonly CelestialBody Jupiter = new(BodyId.Jupiter, "Jupiter", SkyBody.Jupiter,
        Ellipsoid.FromRadii(71_492_000, 66_854_000), 1.26686534e17, DegPerDay(870.536), BodyId.Sun, 5.20288700 * Au,
        landable: false,
        spaceEnterM: 1_000_000, spaceExitM: 800_000, air: BodyAir.Jupiter);

    public static readonly CelestialBody Saturn = new(BodyId.Saturn, "Saturn", SkyBody.Saturn,
        Ellipsoid.FromRadii(60_268_000, 54_364_000), 3.7931187e16, DegPerDay(810.7939024), BodyId.Sun, 9.53667594 * Au,
        landable: false,
        spaceEnterM: 1_000_000, spaceExitM: 800_000, air: BodyAir.Saturn);

    public static readonly CelestialBody Uranus = new(BodyId.Uranus, "Uranus", SkyBody.Uranus,
        Ellipsoid.FromRadii(25_559_000, 24_973_000), 5.793939e15, DegPerDay(-501.1600928), BodyId.Sun, 19.18916464 * Au,
        landable: false,
        spaceEnterM: 1_000_000, spaceExitM: 800_000, air: BodyAir.Uranus);

    public static readonly CelestialBody Neptune = new(BodyId.Neptune, "Neptune", SkyBody.Neptune,
        Ellipsoid.FromRadii(24_764_000, 24_341_000), 6.836529e15, DegPerDay(541.1397757), BodyId.Sun, 30.06992276 * Au,
        landable: false,
        spaceEnterM: 1_000_000, spaceExitM: 800_000, air: BodyAir.Neptune);

    // the big moons turn once an orbit (they keep a face to their planet)
    public static readonly CelestialBody Io = new(BodyId.Io, "Io", SkyBody.Io, Ellipsoid.Sphere(1_821_600), 5.959916e12,
        DegPerDay(360 / 1.769137786), BodyId.Jupiter, 421_700_000, landable: true, spaceEnterM: 10_000, spaceExitM: 8_000);

    public static readonly CelestialBody Europa = new(BodyId.Europa, "Europa", SkyBody.Europa, Ellipsoid.Sphere(1_560_800),
        3.202739e12, DegPerDay(360 / 3.551181041), BodyId.Jupiter, 671_034_000, landable: true, spaceEnterM: 10_000,
        spaceExitM: 8_000, craterDensity: 0.03);

    public static readonly CelestialBody Ganymede = new(BodyId.Ganymede, "Ganymede", SkyBody.Ganymede,
        Ellipsoid.Sphere(2_634_100), 9.887834e12, DegPerDay(360 / 7.15455296), BodyId.Jupiter, 1_070_412_000,
        landable: true, spaceEnterM: 10_000, spaceExitM: 8_000, craterDensity: 0.5);

    public static readonly CelestialBody Callisto = new(BodyId.Callisto, "Callisto", SkyBody.Callisto,
        Ellipsoid.Sphere(2_410_300), 7.179289e12, DegPerDay(360 / 16.6890184), BodyId.Jupiter, 1_882_709_000,
        landable: true, spaceEnterM: 10_000, spaceExitM: 8_000, craterDensity: 1);

    public static readonly CelestialBody Titan = new(BodyId.Titan, "Titan", SkyBody.Titan, Ellipsoid.Sphere(2_574_730),
        8.978138e12, DegPerDay(360 / 15.945421), BodyId.Saturn, 1_221_870_000, landable: true, spaceEnterM: 200_000,
        spaceExitM: 180_000, craterDensity: 0.02, air: BodyAir.Titan);

    private static readonly CelestialBody[] ById =
        [Earth, Moon, Mars, Sun, Mercury, Venus, Jupiter, Saturn, Uranus, Neptune, Io, Europa, Ganymede, Callisto, Titan];

    /// <summary>Every body, in <see cref="BodyId"/> order.</summary>
    public static System.ReadOnlySpan<CelestialBody> All => ById;

    /// <summary>The body with this id.</summary>
    public static CelestialBody Of(BodyId id) => ById[(int)id];

    /// <summary>The body the sky draws as <paramref name="sky"/>.</summary>
    public static CelestialBody Of(SkyBody sky)
    {
        foreach (var b in ById)
            if (b.Sky == sky) return b;
        throw new System.ArgumentOutOfRangeException(nameof(sky));
    }

    public BodyId Id { get; }

    public string Name { get; }

    /// <summary>What the ephemeris and the sky call it; none for the Earth (the sky is drawn from it).</summary>
    public SkyBody? Sky { get; }

    /// <summary>Its reference ellipsoid (a sphere for the Moon): latitudes, longitudes and heights are on it.</summary>
    public Ellipsoid Shape { get; }

    /// <summary>
    /// Its cube tiling (#1848): the Earth's (face 0 on Bern, twisted) or, for any other body, face 0 on its
    /// 0° N 0° E with its poles at the centres of faces 4 and 5.
    /// </summary>
    public CubeShape Cube => _cube ??= Id switch
    {
        BodyId.Earth => CubeShape.Earth,
        BodyId.Moon => CubeShape.Moon,
        BodyId.Mars => CubeShape.Mars,
        BodyId.Mercury => CubeShape.Mercury,
        BodyId.Venus => CubeShape.Venus,
        BodyId.Io => CubeShape.Io,
        BodyId.Europa => CubeShape.Europa,
        BodyId.Ganymede => CubeShape.Ganymede,
        BodyId.Callisto => CubeShape.Callisto,
        BodyId.Titan => CubeShape.Titan,
        BodyId.Jupiter => CubeShape.Jupiter,
        BodyId.Saturn => CubeShape.Saturn,
        BodyId.Uranus => CubeShape.Uranus,
        BodyId.Neptune => CubeShape.Neptune,
        _ => new CubeShape(Shape),
    };

    private CubeShape? _cube;

    /// <summary>The gravitational parameter, m³/s².</summary>
    public double GM { get; }

    /// <summary>Its real sidereal spin about its pole, rad/s (negative: retrograde, Venus and Uranus).</summary>
    public double Spin { get; }

    /// <summary>The body whose sphere of influence it sits in; none for the Sun.</summary>
    public BodyId? Parent { get; }

    /// <summary>Its mean distance from <see cref="Parent"/>, metres.</summary>
    public double OrbitRadius { get; }

    /// <summary>
    /// Its sphere of influence, metres from its centre: Laplace's a·(m/M)^(2/5) (66,100 km for the Moon, 924,600 km
    /// for the Earth), never under its radius plus 20 km. Infinite for the Sun.
    /// </summary>
    public double SphereOfInfluence { get; }

    /// <summary>Whether it has ground to stand on (not the giants, not the Sun).</summary>
    public bool Landable { get; }

    /// <summary>The Space regime's handover heights over it, metres (in climbing, out descending): the Earth's 60 and 50 km; low on an airless body.</summary>
    public double SpaceEnterM { get; }

    public double SpaceExitM { get; }

    /// <summary>
    /// The cube level of its leaves: the one whose leaves are nearest the Earth's ~611 m (Earth 14, Moon 12, Mars 13),
    /// so every radius counted in leaves holds on every body.
    /// </summary>
    public int LeafLevel { get; }

    /// <summary>
    /// The ground game's gravity there, m/s²: the Earth's tuned 9.81 scaled by the true ratio of surface gravities
    /// (1.63 on the Moon, 3.72 on Mars).
    /// </summary>
    public double GroundGravity { get; }

    /// <summary>
    /// How cratered its ground is under its data (#1850, the cratered profile of docs/plans/planets.md section 5): a
    /// share of the lunar maria's equilibrium (<c>CraterField</c>): 1 on the Moon, Mercury and Callisto, 0.5 on
    /// Ganymede, 0.15 on Mars (wind and dust have filled most of its small craters), 0.03 on young Europa, 0.02 on Titan
    /// (its rain and dunes), 0 where nothing is generated (the Earth's own fill, volcanic Io, Venus, the giants).
    /// </summary>
    public double CraterDensity { get; }

    private CelestialBody(BodyId id, string name, SkyBody? sky, Ellipsoid shape, double gm, double spin, BodyId? parent,
        double orbitRadius, bool landable, double spaceEnterM = 0, double spaceExitM = 0, int leafLevel = -1,
        double groundGravity = -1, double craterDensity = 0, BodyAir? air = null)
    {
        Id = id;
        CraterDensity = craterDensity;
        Air = air;
        Name = name;
        Sky = sky;
        Shape = shape;
        GM = gm;
        Spin = spin;
        Parent = parent;
        OrbitRadius = orbitRadius;
        Landable = landable;
        SpaceEnterM = spaceEnterM;
        SpaceExitM = spaceExitM;
        LeafLevel = leafLevel >= 0 ? leafLevel : LeafLevelFor(shape.A);
        GroundGravity = groundGravity >= 0 ? groundGravity : 9.81 * (gm / (shape.A * shape.A)) / EarthSurfacePull;
        SphereOfInfluence = parent is { } p
            ? System.Math.Max(orbitRadius * System.Math.Pow(gm / ParentGM(p), 0.4), shape.A + 20_000)
            : double.PositiveInfinity;
    }

    // the Earth's true pull at its equator, what its tuned 9.81 stands for
    private const double EarthSurfacePull = 3.986004418e14 / (6_378_137.0 * 6_378_137.0);

    // the parents' GM, by id (the static fields are not all built yet while a child's is)
    private static double ParentGM(BodyId p) => p switch
    {
        BodyId.Sun => 1.32712440018e20,
        BodyId.Earth => 3.986004418e14,
        BodyId.Jupiter => 1.26686534e17,
        BodyId.Saturn => 3.7931187e16,
        _ => throw new System.ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>The leaf level for a body of equatorial radius <paramref name="a"/>: its cube face's edge (a·π/2) over 2^level nearest 611 m.</summary>
    public static int LeafLevelFor(double a) =>
        System.Math.Clamp((int)System.Math.Round(System.Math.Log2(a * System.Math.PI / 2 / 611.0)), 0, 20);

    /// <summary>
    /// Whether a craft at <paramref name="altitude"/> over this body is in the Space regime (#1851), with its hysteresis:
    /// entered above <see cref="SpaceEnterM"/>, left below <see cref="SpaceExitM"/> (the Earth's 60 and 50 km,
    /// <c>Curvature.SpaceRegime</c>).
    /// </summary>
    public bool SpaceRegime(bool inSpace, double altitude) => inSpace ? altitude >= SpaceExitM : altitude > SpaceEnterM;

    /// <summary>
    /// How close a camera may come to a body one cannot stand on (#1854), metres over its reference: a giant with air is
    /// flown into (<see cref="GasFloorM"/>, deep under its 1-bar level), the Sun stops the camera at 0.2 radii (139,000 km
    /// over the photosphere); 0 for a body with ground.
    /// </summary>
    public double FloorM => Landable ? 0 : Id == BodyId.Sun ? 0.2 * Shape.A : Air != null ? GasFloorM : 50_000;

    /// <summary>
    /// How deep a camera may dive into a giant (#1854, the spectator): 500 km under the 1-bar level, past where Galileo's
    /// probe went silent (150 km, 23 bar), where it is already black and the gas hundreds of bars; deeper is no more to
    /// see. Negative: under the reference.
    /// </summary>
    public const double GasFloorM = -500_000;

    /// <summary>Its air (#1853): what drags a craft and what its sky scatters; null where there is none (the Moon).</summary>
    public BodyAir? Air { get; }

    /// <summary>Whether it has air a craft feels (#1851): the Earth's, Mars' (#1853).</summary>
    public bool HasAir => Air != null;

    /// <summary>
    /// The air's density at a height over the reference, kg/m³: the Earth's 1976 standard atmosphere
    /// (<see cref="Atmosphere.Density"/>), another body's exponential (<see cref="BodyAir.Density"/>), 0 without air.
    /// </summary>
    public double AirDensity(double altitude) => Id == BodyId.Earth ? Atmosphere.Density(altitude) : Air?.Density(altitude) ?? 0;

    /// <summary>Point-mass gravity at a distance from the centre, m/s²: 8.68 at 400 km above the Earth.</summary>
    public double Gravity(double r) => GM / (r * r);

    /// <summary>The speed of a circular orbit at a distance from the centre, m/s: 7,669 at 400 km.</summary>
    public double CircularSpeed(double r) => System.Math.Sqrt(GM / r);

    public override string ToString() => Name;
}
