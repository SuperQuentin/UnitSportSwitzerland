using Godot;
using UnitSport.Core;
using UnitSport.Occasions.Festivals;
using UnitSport.Terrain.Fixture;

namespace UnitSport.World.Fun;

/// <summary>
/// The fun world's rules (#1396, <c>docs/notes/world/fun-world.md</c>), on the server and every client: the sky held at
/// dusk and graded purple, the arena's stage always playing, the Älplerfest on the festival field all year with every
/// game open, and weapons that hurt only in the PvP block, where crates of weapons stand. The ground and what stands
/// on it are the course's (<see cref="FunCourse"/>); the look is <see cref="FunBackdrop"/>'s.
/// </summary>
public static class FunWorld
{
    /// <summary>The hour the sky is held at: the sun just down in the west, the blue hour, the retro sun on the horizon there.</summary>
    public const double DuskHour = 19.6;

    /// <summary>
    /// Where the course is laid on the Earth (#1648): its metres on the LV95 grid from the default spawn (the flat world,
    /// and every server: a course online stays flat), or on the plane tangent there (the earth world's leaves, offline).
    /// The course's own source sets the same one (<see cref="CoursePlace.Active"/>); this is decided as the world is
    /// entered, before the source exists, for the festival field.
    /// </summary>
    public static CoursePlace Place => CoursePlace.Active ?? _place ?? CoursePlace.Flat(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    private static CoursePlace? _place;

    /// <summary>Where the course starts, on its ground: the default spawn on every peer (a fixture course is built round it).</summary>
    public static GlobalPos Start => At(0, 0, 0);

    /// <summary>A course point (metres east and north of the start) <paramref name="lift"/> metres over the course's ground, on the Earth.</summary>
    public static GlobalPos At(double x, double y, double lift) => Place.Lv95(x, y) is var (e, n)
        ? GlobalPos.FromLv95(e, n, FunCourse.Ground(x, y) + lift) : default;

    private static bool _entered;

    /// <summary>
    /// The world boots in the fun world (both sides, as <see cref="WorldChoice.Active"/> is set, before the occasions,
    /// the festivals and the concerts are made): what those read is set here.
    /// </summary>
    /// <param name="onLeaves">The course is played on its leaves (under <c>--frame earth</c>, on every peer: #1683).</param>
    public static void Enter(bool onLeaves = false)
    {
        _entered = true;
        _place = onLeaves
            ? CoursePlace.OnEarth(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N, FunCourse.Ground(0, 0))
            : CoursePlace.Flat(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        Concert.ConcertDirector.WorldSeed = FunCourse.ArenaSeed;
        var (x, y, r) = FunCourse.Festival;
        var (fe, fn) = Place.Lv95(x, y);
        var alp = FestivalCatalog.Aelplerfest();
        FestivalCatalog.Register(alp with
        {
            Sites = [new FestivalSite("the neon festival field", fe, fn, r)],
            Days = [new FestivalDay(1, alp.Games.Select(g => g.Id).ToArray(),
                "Every game is open on the neon festival field: the flags, the sawdust ring, the alphorns and the stone.")],
        });
        Occasions.OccasionManager.WorldStarted = [FestivalCatalog.AelplerfestId];
        DayNight.WorldGrade = Grade;
        Combat.PvpRules.Zone = (shooter, victim) => InPvp(shooter) && InPvp(victim) ? true : null;
    }

    /// <summary>The world is left: everything <see cref="Enter"/> set goes back.</summary>
    public static void Leave()
    {
        if (!_entered) return;
        _entered = false;
        Concert.ConcertDirector.WorldSeed = null;
        FestivalCatalog.Register(FestivalCatalog.Aelplerfest());
        Occasions.OccasionManager.WorldStarted = [];
        DayNight.WorldGrade = null;
        Combat.PvpRules.Zone = null;
        Players = null;
        _place = null;
    }

    /// <summary>
    /// The dusk's colours whatever the hour says: a warm pink light and a violet haze, which the fog and every lit
    /// material take (<see cref="DayNight"/>'s globals), so the ground fades into the synthwave sky.
    /// </summary>
    private static (Color Tint, Color Sky) Grade(float sunElevationDeg, Color tint, Color sky) =>
        (new Color(0.62f, 0.42f, 0.7f), new Color(0.24f, 0.07f, 0.34f));

    // ---- the PvP block (server) -------------------------------------------------------------------

    /// <summary>Server: where a peer stands, or null when it has no body here.</summary>
    public static Func<long, GlobalPos?>? Players { get; set; }

    /// <summary>Whether a place is in the PvP block: the box in course metres, the place read back onto the course as it is laid.</summary>
    public static bool InPvp(GlobalPos at)
    {
        var (x0, y0, x1, y1) = FunCourse.PvpBox;
        var (e, n, _) = at.Lv95;
        var (x, y) = Place.Course(e, n);
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    private static bool InPvp(long peer) => Players?.Invoke(peer) is { } p && InPvp(p);
}
