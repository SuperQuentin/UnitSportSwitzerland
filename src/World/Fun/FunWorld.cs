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
    /// <summary>The hour the sky is held at: the sun a few degrees up in the west, the retro sun on the horizon.</summary>
    public const double DuskHour = 19.6;

    /// <summary>Where the course starts, LV95: the default spawn on every peer (a fixture course is built round it).</summary>
    public static (double E, double N) Start => (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    private static bool _entered;

    /// <summary>
    /// The world boots in the fun world (both sides, as <see cref="WorldChoice.Active"/> is set, before the occasions,
    /// the festivals and the concerts are made): what those read is set here.
    /// </summary>
    public static void Enter()
    {
        _entered = true;
        Concert.ConcertDirector.WorldSeed = FunCourse.ArenaSeed;
        var (x, y, r) = FunCourse.Festival;
        var alp = FestivalCatalog.Aelplerfest();
        FestivalCatalog.Register(alp with
        {
            Sites = [new FestivalSite("the neon festival field", Start.E + x, Start.N + y, r)],
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
    }

    /// <summary>
    /// The dusk's colours whatever the hour says: a warm pink light and a violet haze, which the fog and every lit
    /// material take (<see cref="DayNight"/>'s globals), so the ground fades into the synthwave sky.
    /// </summary>
    private static (Color Tint, Color Sky) Grade(float sunElevationDeg, Color tint, Color sky) =>
        (new Color(0.62f, 0.42f, 0.7f), new Color(0.24f, 0.07f, 0.34f));

    // ---- the PvP block (server) -------------------------------------------------------------------

    /// <summary>Server: where a peer stands, LV95, or null when it has no body here.</summary>
    public static Func<long, GlobalPos?>? Players { get; set; }

    /// <summary>Whether an LV95 point is in the PvP block.</summary>
    public static bool InPvp(double e, double n)
    {
        var (x0, y0, x1, y1) = FunCourse.PvpBox;
        double x = e - Start.E, y = n - Start.N;
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    private static bool InPvp(long peer) => Players?.Invoke(peer) is { } p && InPvp(p.E, p.N);
}
