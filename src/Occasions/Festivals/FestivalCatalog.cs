// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The festivals the game knows, by id. Part 1 of #1269 ships the framework and no real festival: the Älplerfest, the Désalpe
/// and the calendar festivals register theirs in the PRs that build their games. The one entry that exists is the
/// <see cref="TestId"/> festival, which <c>--festival test</c> adds for the checks and which is never part of the game.
/// </summary>
public static partial class FestivalCatalog
{
    public const string TestId = "test";

    private static readonly Dictionary<string, FestivalDef> Defs = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<FestivalDef> All => Defs.Values;

    public static FestivalDef? Get(string id) => Defs.GetValueOrDefault(id);

    /// <summary>Adds (or replaces) a festival; a later part registers its own at start-up.</summary>
    public static void Register(FestivalDef def) => Defs[def.Id] = def;

    public static void Unregister(string id) => Defs.Remove(id);

    /// <summary>
    /// The check-only festival: five days from 10 March, a bell from day 1 and a gong from day 3, each rung three times
    /// (<see cref="Rings"/>) for Batzen and points. Its site is the fixture course's start, <paramref name="e"/> / <paramref name="n"/>.
    /// </summary>
    public static FestivalDef Test(double e, double n) => new(
        TestId, "Test festival",
        [new FestivalSite("the test field", e, n, 400)],
        [new FestivalGame("bell", "Ring the bell", 100), new FestivalGame("gong", "Ring the gong", 100)],
        [new FestivalDay(1, ["bell"], "The bell is open."), new FestivalDay(3, ["gong"], "The gong joins in.")],
        ClosingShow.Fireworks,
        [("03-10", "03-14")],
        [new FestivalMilestone(150, 20, "Warm-up"), new FestivalMilestone(400, 40, "Crowd"), new FestivalMilestone(800, 80, "Whole village")]);

    /// <summary>Rings of a test game that make one play.</summary>
    public const int Rings = 3;

    /// <summary>What one play of a test game pays: (Batzen, points).</summary>
    public static (int Batzen, int Points) TestPay(string game) => game switch
    {
        "bell" => (40, 100),
        "gong" => (60, 150),
        _ => (0, 0),
    };
}
