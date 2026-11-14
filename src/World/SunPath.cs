using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

// Godot's managed maths only (Vector3, Mathf): linked into the unit tests (docs/notes/general/testing.md).

/// <summary>Which sun lights the world: the real one, or the old fixed summer day.</summary>
public enum SunModel { Real, Fixed }

/// <summary>
/// Where the sun is by the hour (#1225): the model <see cref="DayNight"/> lights the world with,
/// pulled out so the thermals (<see cref="AirLift"/>) read the very same sky.
///
/// <para>
/// <b>The real sun</b> (#1430, phase H6): the ephemeris (<see cref="Ephemeris"/>) on the calendar's
/// date (<see cref="Calendar"/>) at the game's hour, read as Swiss civil time, from the place's real
/// latitude and longitude. Bern's day is 8 h 30 min in December and 15 h 55 min in June, and the
/// June noon sun stands 66.5° high. The moon is real too (<see cref="Toward"/>).
/// </para>
///
/// <para>
/// <b>The fixed day</b> (<c>--sun fixed</c>, and the default of checks, test and fixture worlds,
/// screenshot runs and trailer renders, so their results and pictures do not move with the date): up at <see cref="Sunrise"/>, due south and
/// <see cref="NoonElevation"/> high at noon, down at <see cref="Sunset"/>.
/// </para>
///
/// <para>
/// Axes are the world's: +X east, -Z north (LV95 grid north, not true north), +Y up. The vector
/// points from the ground to the sun.
/// </para>
/// </summary>
public static class SunPath
{
    public const float Sunrise = 6f, Sunset = 18f, NoonElevation = 62f;

    /// <summary>Where the sky is read when nothing says where: Bern.</summary>
    public const double BernE = 2_600_000, BernN = 1_200_000;

    /// <summary>The sun in force for this process.</summary>
    public static SunModel Model { get; set; } = DefaultModel();

    /// <summary>The date the real sun is read on.</summary>
    public static Func<DateOnly> Today { get; set; } = DefaultToday();

#if GODOT
    /// <summary>
    /// <c>--sun real|fixed</c>; otherwise fixed for a check (<c>--*check</c>), a narrowed or fixture
    /// world, a screenshot run and a trailer render, real in play.
    /// </summary>
    private static SunModel DefaultModel() => CmdArgs.Value("--sun") switch
    {
        "real" => SunModel.Real,
        "fixed" => SunModel.Fixed,
        _ => Systems.Narrowed
            || TerrainPaths.ParseChunkDirArg()?.StartsWith(Systems.FixturePrefix) == true
            || Array.Exists(CmdArgs.All, a => a.StartsWith("--") && a.EndsWith("check"))
            || CmdArgs.Has("--shot") || CmdArgs.Has("--shot-queue")
            || TrailerRequested()
            ? SunModel.Fixed : SunModel.Real,
    };

    // a build without the trailer studio (#1074) films nothing
#if TRAILER
    private static bool TrailerRequested() => Trailer.TrailerDirector.Requested;
#else
    private static bool TrailerRequested() => false;
#endif

    private static Func<DateOnly> DefaultToday() => () => Calendar.Today;
#else
    private static SunModel DefaultModel() => SunModel.Fixed;

    // the unit tests: a fixed day, never the wall clock (Calendar is the game's one reader of the date)
    private static Func<DateOnly> DefaultToday() => () => new DateOnly(2026, 6, 21);
#endif

    /// <summary>
    /// How far round the sun is: 0 at sunrise, 1 at sunset, 2 at the next sunrise. Day and night each
    /// take half the circle however long they last, so the default 6 / 18 reduces to (h - 6) / 12.
    /// </summary>
    public static float Phase(float hour, float rise = Sunrise, float set = Sunset)
    {
        float dayLength = set - rise;
        return hour >= rise && hour <= set
            ? (hour - rise) / dayLength
            : 1f + Mathf.PosMod(hour - set, 24f) / (24f - dayLength);
    }

    /// <summary>
    /// The fixed day's direction to the sun and its elevation in degrees (negative at night: the sun
    /// below the horizon). The numbers <c>DayNight</c> has always produced, to the last bit.
    /// </summary>
    public static Vector3 Direction(float hour, out float elevationDeg,
        float rise = Sunrise, float set = Sunset, float noonElevation = NoonElevation)
    {
        float phase = Phase(hour, rise, set);
        // azimuth measured clockwise from north; world +X is east and -Z is north
        float az = Mathf.DegToRad(90f + phase * 180f);
        elevationDeg = noonElevation * Mathf.Sin(phase * Mathf.Pi);
        float el = Mathf.DegToRad(elevationDeg);
        return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));
    }

    /// <summary><see cref="Direction(float, out float, float, float, float)"/> for the default fixed day.</summary>
    public static Vector3 Direction(double hour) => Direction((float)hour, out _);

    /// <summary>The sun over LV95 <paramref name="e"/> / <paramref name="n"/> at this hour, by the <see cref="Model"/> in force.</summary>
    public static Vector3 Direction(double hour, double e, double n, out float elevationDeg) =>
        Model == SunModel.Real ? Toward(SkyBody.Sun, Today(), hour, e, n, out elevationDeg) : Direction((float)hour, out elevationDeg);

    /// <summary>
    /// The real direction to <paramref name="body"/> from LV95 <paramref name="e"/> / <paramref name="n"/>
    /// at <paramref name="hour"/> (Swiss civil time) on <paramref name="date"/>, in world axes, and its
    /// elevation in degrees. Whatever the <see cref="Model"/>.
    /// </summary>
    public static Vector3 Toward(SkyBody body, DateOnly date, double hour, double e, double n, out float elevationDeg) =>
        Toward(body, date, hour, e, n, out elevationDeg, out _);

    /// <summary><see cref="Toward(SkyBody, DateOnly, double, double, double, out float)"/>, and how far the body is, metres.</summary>
    public static Vector3 Toward(SkyBody body, DateOnly date, double hour, double e, double n, out float elevationDeg, out double distance) =>
        Toward(body, date, hour, PlaceAt(e, n), out elevationDeg, out distance);

    /// <summary>
    /// The real direction to <paramref name="body"/> from <paramref name="at"/>, in world axes (#1845). In an anchor
    /// frame (the earth world, Space) it is read from that very point, its height included and no LV95 in it: from
    /// orbit the moon's parallax is the camera's (up to ~3° at 20,000 km), not the ground's under it. In the flat
    /// ground frame, as from <c>at</c>'s LV95 place (<see cref="Toward(SkyBody, DateOnly, double, double, double, out float, out double)"/>).
    /// </summary>
    public static Vector3 Toward(SkyBody body, DateOnly date, double hour, GlobalPos at, out float elevationDeg, out double distance) =>
        Toward(body, date, hour, PlaceOf(at), out elevationDeg, out distance);

    private static Vector3 Toward(SkyBody body, DateOnly date, double hour, Place place, out float elevationDeg, out double distance)
    {
        var h = Ephemeris.Topocentric(body, SwissTime.ToUtc(date, hour), place.Geo);
        elevationDeg = (float)h.ElevationDeg;
        distance = h.Distance;
        return ToWorld(h, place);
    }

    /// <summary>
    /// The moon's own axes in world axes (#1440): X its 0° N 0° E (the near side's centre), Y 90° E,
    /// Z its north pole. A point of its surface with world normal <c>n</c> is at longitude
    /// atan2(n·Y, n·X), latitude asin(n·Z): the moon's map, the right way round, with its libration.
    /// </summary>
    public static Basis MoonFace(DateOnly date, double hour, double e, double n) => Face(SkyBody.Moon, date, hour, e, n);

    /// <summary><see cref="MoonFace(DateOnly, double, double, double)"/> from <paramref name="at"/> (see <see cref="Toward(SkyBody, DateOnly, double, GlobalPos, out float, out double)"/>).</summary>
    public static Basis MoonFace(DateOnly date, double hour, GlobalPos at) => Face(SkyBody.Moon, date, hour, PlaceOf(at));

    /// <summary>Any body's own axes in world axes, as <see cref="MoonFace"/> (#1444): a planet's map and its rings' plane.</summary>
    public static Basis Face(SkyBody body, DateOnly date, double hour, double e, double n) => Face(body, date, hour, PlaceAt(e, n));

    /// <summary><see cref="Face(SkyBody, DateOnly, double, double, double)"/> from <paramref name="at"/>.</summary>
    public static Basis Face(SkyBody body, DateOnly date, double hour, GlobalPos at) => Face(body, date, hour, PlaceOf(at));

    private static Basis Face(SkyBody body, DateOnly date, double hour, Place place)
    {
        var (pole, prime, east) = Ephemeris.Axes(body, SwissTime.ToUtc(date, hour));
        Vector3 Axis(EcefPos v) => ToWorld(Ephemeris.Local(v, place.Geo), place);
        return new Basis(Axis(prime), Axis(east), Axis(pole));
    }

    /// <summary>
    /// The celestial sphere in world axes (#1444): columns are the ICRF's X (the equinox), Y and Z
    /// (the north celestial pole) as seen from LV95 <paramref name="e"/> / <paramref name="n"/>. A
    /// star's catalogue direction times it is where it shines.
    /// </summary>
    public static Basis SkyFrame(DateOnly date, double hour, double e, double n) => SkyFrame(date, hour, PlaceAt(e, n));

    /// <summary><see cref="SkyFrame(DateOnly, double, double, double)"/> from <paramref name="at"/>.</summary>
    public static Basis SkyFrame(DateOnly date, double hour, GlobalPos at) => SkyFrame(date, hour, PlaceOf(at));

    private static Basis SkyFrame(DateOnly date, double hour, Place place)
    {
        var (x, y, z) = Ephemeris.Celestial(SwissTime.ToUtc(date, hour));
        Vector3 Axis(EcefPos v) => ToWorld(Ephemeris.Local(v, place.Geo), place);
        return new Basis(Axis(x), Axis(y), Axis(z));
    }

    /// <summary>
    /// The Earth-fixed frame at LV95 <paramref name="e"/> / <paramref name="n"/> (#1444): the basis taking an
    /// Earth-fixed offset (metres) into world axes, and the place itself on the ellipsoid. A satellite's
    /// direction is <c>Basis * (its ECEF - Observer)</c>.
    /// </summary>
    public static (Basis ToWorld, EcefPos Observer) EarthFrame(double e, double n) => EarthFrame(PlaceAt(e, n));

    /// <summary>
    /// <see cref="EarthFrame(double, double)"/> from <paramref name="at"/>: in an anchor frame the observer is that
    /// point itself, its height included (a satellite seen from orbit, #1845).
    /// </summary>
    public static (Basis ToWorld, EcefPos Observer) EarthFrame(GlobalPos at) => EarthFrame(PlaceOf(at));

    private static (Basis ToWorld, EcefPos Observer) EarthFrame(Place place)
    {
        Vector3 Axis(double x, double y, double z)
        {
            var h = Ephemeris.Local(new EcefPos(x, y, z), place.Geo);
            return ToWorld(h, place);
        }
        return (new Basis(Axis(1, 0, 0), Axis(0, 1, 0), Axis(0, 0, 1)), Ellipsoid.Wgs84.ToEcef(place.Geo));
    }

    /// <summary>
    /// The world's frame when it is an anchor frame (#1430 H3), set by <see cref="DayNight"/> each frame:
    /// directions then go from the Earth into its axes, not into grid ones. Null: the ground frame.
    /// Per thread: the main thread's sky follows the camera's frame, a worker reading the sun (the
    /// server's thermals) keeps the ground one, and tests running side by side never see each other's.
    /// </summary>
    public static OriginFrame? Frame { get => _frame; set => _frame = value; }

    [ThreadStatic] private static OriginFrame? _frame;

    /// <summary>True east / north / up into world axes: turned by the meridian convergence to grid north.</summary>
    private static Vector3 ToWorld(Horizontal h, Place place)
    {
        if (Frame is { IsAnchor: true } frame)
        {
            // east / north / up at the place, as Earth-fixed directions, into the anchor's axes
            double lat = place.Geo.LatDeg * Math.PI / 180, lon = place.Geo.LonDeg * Math.PI / 180;
            double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
            var ecef = new EcefPos(
                -so * h.East - sl * co * h.North + cl * co * h.Up,
                co * h.East - sl * so * h.North + cl * so * h.Up,
                cl * h.North + sl * h.Up);
            return frame.DirectionOf(ecef);
        }
        double gridE = h.East * place.NorthN + h.North * place.NorthE;
        double gridN = -h.East * place.NorthE + h.North * place.NorthN;
        return new Vector3((float)gridE, (float)h.Up, (float)-gridN);
    }

    /// <summary>
    /// The hour (Swiss civil time) the sun rises or sets at Bern today, by the <see cref="Model"/>:
    /// the upper limb on the horizon with standard refraction (-0.833°), as an almanac gives it.
    /// </summary>
    public static double RiseOrSet(bool rise) =>
        Model == SunModel.Fixed ? (rise ? Sunrise : Sunset) : RiseOrSet(Today(), rise);

    /// <summary>The real sunrise or sunset at Bern on <paramref name="date"/>, hours of Swiss civil time.</summary>
    public static double RiseOrSet(DateOnly date, bool rise) =>
        RiseOrSet(date, rise, BernE, BernN) ?? (rise ? Sunrise : Sunset);

    /// <summary>Bern's sunrise or sunset, read through the sky's own (LV95-cell) place: the fixed day's hours.</summary>
    private static double? RiseOrSet(DateOnly date, bool rise, double e, double n) =>
        Crossing(rise, hour =>
        {
            Toward(SkyBody.Sun, date, hour, e, n, out float el);
            return el;
        });

    /// <summary>
    /// The real sunrise or sunset at <paramref name="at"/> on <paramref name="date"/>, hours of Swiss
    /// civil time (#1801: far away it may fall at any hour of the Swiss day). Null when the sun does not
    /// cross the horizon that way there: polar night or midnight sun.
    /// </summary>
    public static double? RiseOrSet(DateOnly date, bool rise, GlobalPos at)
    {
        var geo = at.Geodetic;
        return Crossing(rise, hour => (float)Ephemeris.Topocentric(SkyBody.Sun, SwissTime.ToUtc(date, hour), geo).ElevationDeg);
    }

    /// <summary>The sun's elevation at <paramref name="at"/>, degrees, at <paramref name="hour"/> (Swiss civil time) on <paramref name="date"/>.</summary>
    public static float ElevationAt(DateOnly date, double hour, GlobalPos at) =>
        (float)Ephemeris.Topocentric(SkyBody.Sun, SwissTime.ToUtc(date, hour), at.Geodetic).ElevationDeg;

    /// <summary>The hour the sun's upper limb crosses the horizon (refraction -0.833°), rising or setting; null when it never does.</summary>
    private static double? Crossing(bool rise, Func<double, float> elevation)
    {
        double El(double hour) => elevation(hour) + 0.833;
        // morning: the first climb through the horizon; evening: the last fall
        const double step = 0.25;
        for (double a = 0; a < 24; a += step)
        {
            double b = a + step, ea = El(a), eb = El(b);
            if (rise ? !(ea < 0 && eb >= 0) : !(ea >= 0 && eb < 0)) continue;
            for (int i = 0; i < 30; i++)
            {
                double mid = (a + b) / 2, em = El(mid);
                if ((em < 0) == (ea < 0)) { a = mid; ea = em; } else b = mid;
            }
            return (a + b) / 2;
        }
        return null;
    }

    /// <summary>A place on the Earth, and true north as a unit vector in LV95 grid east / north.</summary>
    private sealed record Place(long CellE, long CellN, Geodetic Geo, double NorthE, double NorthN);

    /// <summary>The last place asked for, per kilometre: swapped whole, so threads never see half of one.</summary>
    private static Place? _place;

    /// <summary>The last point asked for in an anchor frame, and its place: swapped whole, as <see cref="_place"/>.</summary>
    private sealed record PointPlace(GlobalPos At, bool Anchor, Place Place);

    private static PointPlace? _point;

    /// <summary>
    /// A point's place (#1845): in an anchor frame the point itself (its geodetic latitude, longitude and height; the
    /// grid's north is never used there), in the flat ground frame its LV95 kilometre cell as <see cref="PlaceAt"/>.
    /// </summary>
    private static Place PlaceOf(GlobalPos at)
    {
        bool anchor = Frame is { IsAnchor: true };
        if (_point is { } p && p.Anchor == anchor && p.At == at) return p.Place;
        Place place;
        if (anchor) place = new Place(long.MinValue, long.MinValue, at.Geodetic, 0, 1);
        else
        {
            var lv95 = at.Lv95;
            place = PlaceAt(lv95.E, lv95.N);
        }
        _point = new PointPlace(at, anchor, place);
        return place;
    }

    private static Place PlaceAt(double e, double n)
    {
        long ce = (long)Math.Floor(e / 1000), cn = (long)Math.Floor(n / 1000);
        if (_place is { } p && p.CellE == ce && p.CellN == cn) return p;
        double pe = (ce + 0.5) * 1000, pn = (cn + 0.5) * 1000;
        var geo = SwissGeodesy.ToWgs84(pe, pn, 500);
        // a step due north in latitude, measured on the grid
        var (ne, nn, _) = SwissGeodesy.FromWgs84(geo with { LatDeg = geo.LatDeg + 0.01 });
        double de = ne - pe, dn = nn - pn, len = GlobalPos.HorizontalDistance(pe, pn, ne, nn);
        var place = new Place(ce, cn, geo, de / len, dn / len);
        _place = place;
        return place;
    }
}
