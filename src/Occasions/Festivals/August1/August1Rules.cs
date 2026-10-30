// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-august1.md).

using UnitSport.Terrain.Format;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>A lake town that holds a 1 August fireworks: where it is (WGS84) and the compass bearing, in degrees, from the town towards the water.</summary>
public sealed record Lake(string Name, double Lat, double Lon, double BearingDeg)
{
    /// <summary>The quay: the shore point the lat/lon names (LV95).</summary>
    public (double E, double N) Quay => SwissProjection.ToLv95(Lat, Lon);

    public double Bearing => BearingDeg * Math.PI / 180;

    /// <summary>The festival ground, <see cref="August1Rules.Inland"/> m back from the quay (the village loop must be on land).</summary>
    public (double E, double N) Site
    {
        get
        {
            var (e, n) = Quay;
            return (e - Math.Sin(Bearing) * August1Rules.Inland, n - Math.Cos(Bearing) * August1Rules.Inland);
        }
    }

    /// <summary>The barge the shells leave from: <see cref="August1Rules.BargeOut"/> m out on the water from the quay.</summary>
    public (double E, double N) Barge
    {
        get
        {
            var (e, n) = Quay;
            return (e + Math.Sin(Bearing) * August1Rules.BargeOut, n + Math.Cos(Bearing) * August1Rules.BargeOut);
        }
    }
}

/// <summary>Where a show's shells leave from, and the bearing (radians, clockwise from north) its fan is turned to.</summary>
public readonly record struct Launch(double E, double N, double Bearing, string Name);

/// <summary>
/// The 1 August's rules (#1355, part of #1269): <b>Höhenfeuer</b> on one summit in four under 2 500 m, lit from dusk (drawn on the client as flickering billboards),
/// <b>lake fireworks</b> at 22:00 game time over the seven lakes that hold them, the <b>lampion</b> (a paper lantern, a held light) and the <b>Lampionumzug</b>
/// (a village loop at night with three walkers: the party, and the village children when it is short), the <b>fireworks stand</b> (a box to light on one's own).
/// Everything about the ground is a function of the site alone (the server, every client and the props agree with nothing sent). Pure.
/// </summary>
public static class August1Rules
{
    public const string FestivalId = "august1";

    /// <summary>The games: the lampion walk, and watching the lake fireworks.</summary>
    public const string LampionGame = "lampion", ShowGame = "lakes";

    // ---- the lakes ----------------------------------------------------------------------------------------------

    /// <summary>The ground is this far inland from the quay, the barge this far out on the water, m.</summary>
    public const double Inland = 130, BargeOut = 150;

    /// <summary>
    /// Genève, Lausanne-Ouchy, Montreux, Neuchâtel, Zürich, Luzern, Lugano: an approximate quay and the bearing to open water, read off the map by eye (a
    /// human checks the fan lies over the water: <c>tests/TO-TEST-BY-HUMAN.md</c>). The order is the festival's site order (the first the region holds).
    /// </summary>
    public static readonly Lake[] Lakes =
    [
        new("Genève, Jardin Anglais", 46.2058, 6.1517, 60),
        new("Lausanne-Ouchy", 46.5066, 6.6268, 170),
        new("Montreux", 46.4318, 6.9100, 240),
        new("Neuchâtel, le Port", 46.9934, 6.9360, 150),
        new("Zürich, Bürkliplatz", 47.3669, 8.5417, 175),
        new("Luzern, Schwanenplatz", 47.0510, 8.3105, 110),
        new("Lugano, Riva", 46.0020, 8.9520, 165),
    ];

    /// <summary>
    /// Where the show leaves from for a viewer at (<paramref name="e"/>, <paramref name="n"/>): at a lake town (the site is one of the lakes' grounds) the
    /// barge of the lake nearest the viewer; anywhere else (a relocated festival, a check's fixture) a point 200 m north of the site's middle, its fan to the north.
    /// </summary>
    public static Launch LaunchFor(ResolvedSite site, double e, double n)
    {
        bool atLake = false;
        foreach (var lake in Lakes)
        {
            var (le, ln) = lake.Site;
            if (site.DistanceTo(le, ln) < 50) { atLake = true; break; }
        }
        if (!atLake) return new Launch(site.E, site.N + 200, 0, site.Name);
        Launch best = default;
        double bestD = double.MaxValue;
        foreach (var lake in Lakes)
        {
            var (be, bn) = lake.Barge;
            double d = (be - e) * (be - e) + (bn - n) * (bn - n);
            if (d < bestD) { bestD = d; best = new Launch(be, bn, lake.Bearing, lake.Name); }
        }
        return best;
    }

    /// <summary>Game hour the lake shows start at, on every game evening of the festival.</summary>
    public const double ShowHour = 22;

    /// <summary>Whether an evening show starts now: the sky crossed <see cref="ShowHour"/> since the last look (or this is the first look, the evening under way), not after midnight.</summary>
    public static bool ShowStarts(double? lastHour, double hour) => hour >= ShowHour && hour < 24 && (lastHour is null || lastHour < ShowHour);

    /// <summary>Watching the show for 20 s within 1.5 km of the site pays this once a festival (the running list's watch rule).</summary>
    public const int ShowBatzen = 30, ShowPoints = 60, ShowDayCap = 30;

    // ---- the Höhenfeuer ---------------------------------------------------------------------------------------------

    /// <summary>A summit lower than this holds a bonfire (one in four of them).</summary>
    public const float FireCeiling = 2500f;

    /// <summary>Fires are drawn within this many metres of the camera (the shows' draw range), at most <see cref="MaxFires"/> of the nearest.</summary>
    public const double DrawRange = 15000;

    public const int MaxFires = 192;

    /// <summary>The fires are lit from 21:00 to 05:00 game time.</summary>
    public static bool Burning(double hour) => hour >= 21 || hour < 5;

    /// <summary>Whether the summit with this POI hash and elevation holds a Höhenfeuer: 1 in 4 under 2 500 m, by a hash (the same on every peer and every year). <paramref name="salt"/> is a check's.</summary>
    public static bool Lit(ulong hash, float elevation, int salt = 0)
    {
        if (elevation >= FireCeiling) return false;
        ulong z = hash + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z & 3) == 0;
    }

    /// <summary>A burning Höhenfeuer lifts a glider like a campfire does (<c>World/AirLiftField</c>): this wide and this high, m/s.</summary>
    public const float LiftRadius = 14f, LiftTop = 160f, LiftSpeed = 3.5f;

    /// <summary>Fires this close to the camera lift gliders (the volumes are few and local), m.</summary>
    public const double LiftRange = 3000;

    // ---- the ground at the village ------------------------------------------------------------------------------------

    /// <summary>The lampion stand 10 m west and 6 m north of the site's middle, the fireworks stand 10 m east, the village fire at the middle.</summary>
    public static (double E, double N) LampionStandAt(ResolvedSite site) => (site.E - 10, site.N + 6);

    public static (double E, double N) FireworkStandAt(ResolvedSite site) => (site.E + 10, site.N + 6);

    public static (double E, double N) VillageFireAt(ResolvedSite site) => (site.E, site.N);

    /// <summary>How near a body must be to a stand, the post and a waypoint, m; the walk's members start within <see cref="JoinReach"/> of the post and must stay within <see cref="Together"/> of the leader.</summary>
    public const double StandReach = 4.5, PostReach = 5.0, WaypointReach = 8.0, JoinReach = 15.0, Together = 30.0;

    // ---- the lampion walk ----------------------------------------------------------------------------------------------

    /// <summary>The loop is a ring this far from the site's middle, m, with <see cref="Waypoints"/> points on it; the post stands on it, at its south.</summary>
    public const double RingRadius = 90;

    public const int Waypoints = 10;

    /// <summary>Three walk; the party's humans, and the village children make up the rest.</summary>
    public const int Needed = 3;

    public static (double E, double N) PostAt(ResolvedSite site) => (site.E, site.N - RingRadius);

    /// <summary>The loop's points in order, counter-clockwise from the post's side; index <see cref="Waypoints"/> is the post again (the finish).</summary>
    public static (double E, double N)[] Path(ResolvedSite site)
    {
        var path = new (double, double)[Waypoints + 1];
        for (int k = 0; k < Waypoints; k++)
        {
            double a = -Math.PI / 2 + (k + 1) * (2 * Math.PI / (Waypoints + 1));
            path[k] = (site.E + RingRadius * Math.Cos(a), site.N + RingRadius * Math.Sin(a));
        }
        path[Waypoints] = PostAt(site);
        return path;
    }

    public static double LoopLength(ResolvedSite site)
    {
        var path = Path(site);
        var (e, n) = PostAt(site);
        double len = 0;
        foreach (var (pe, pn) in path)
        {
            len += Dist(pe, pn, (e, n));
            (e, n) = (pe, pn);
        }
        return len;
    }

    /// <summary>A stroll with a lantern: 1.2 m/s, so the par is the loop's length over that, s.</summary>
    public const double StrollSpeed = 1.2;

    public static double ParSeconds(ResolvedSite site) => LoopLength(site) / StrollSpeed;

    /// <summary>The walk is closed after this long, s.</summary>
    public static double MaxSeconds(ResolvedSite site) => Math.Max(300, ParSeconds(site) * 3);

    /// <summary>A loop done faster than a brisk run with a lantern (4 m/s) is not a walk: it was a jump through the map.</summary>
    public const double RunSpeed = 4.0;

    public static bool Plausible(ResolvedSite site, double seconds, double metresPerSecond = RunSpeed) => seconds >= LoopLength(site) / metresPerSecond;

    /// <summary>The walk pays 25 Batzen to each human walker, 10 more for each human friend up to three, and 3 points a Batzen; one walk a day pays.</summary>
    public const int WalkBatzen = 25, FriendBatzen = 10, PointsPerBatzen = 3, WalkDayCap = 60;

    public static int WalkPay(int humans) => WalkBatzen + FriendBatzen * (Math.Clamp(humans, 1, Needed) - 1);

    /// <summary>Lanterns are carried from 20:30 to 05:30 game time: the walk starts only then.</summary>
    public static bool IsNight(double hour) => hour >= 20.5 || hour < 5.5;

    // ---- the stands ---------------------------------------------------------------------------------------------------------

    /// <summary>A box of fireworks costs this many Batzen, three a day at most; lighting one is allowed at night, once in this many seconds a player.</summary>
    public const int BoxPrice = 20, BoxesPerDay = 3;

    public const double OwnCooldown = 25;

    /// <summary>Where a player's own volley plays: shown to everyone within this many metres (the draw range).</summary>
    public const double OwnRange = 20000;

    /// <summary>A stable seed for a player's own volley.</summary>
    public static int OwnSeed(string instance, int number) => FestivalShows.SeedOf(instance + "/own", number);

    public static double Dist(double e, double n, (double E, double N) at) => Math.Sqrt((e - at.E) * (e - at.E) + (n - at.N) * (n - at.N));

    public static bool NearLampionStand(ResolvedSite site, double e, double n) => Dist(e, n, LampionStandAt(site)) <= StandReach;

    public static bool NearFireworkStand(ResolvedSite site, double e, double n) => Dist(e, n, FireworkStandAt(site)) <= StandReach;

    public static bool NearPost(ResolvedSite site, double e, double n) => Dist(e, n, PostAt(site)) <= PostReach;
}
