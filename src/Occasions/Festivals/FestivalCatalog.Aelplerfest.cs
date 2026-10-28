// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-steinstossen.md).

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string AelplerfestId = "aelplerfest";

    static FestivalCatalog() => Register(Aelplerfest());

    /// <summary>
    /// The Älplerfest (#1269): 14 to 24 August in Interlaken's Höhematte, then Schwägalp and Schwarzsee, else the region's largest town.
    /// Each part of the plan adds its game and its row of the day table here; so far the stone put, open from day 1.
    /// </summary>
    public static FestivalDef Aelplerfest() => new(
        AelplerfestId, "Älplerfest",
        [
            new FestivalSite("the Höhematte, Interlaken", 2632427, 1170452, 120),
            new FestivalSite("Schwägalp", 2741776, 1234534, 120),
            new FestivalSite("Schwarzsee", 2588394, 1167530, 120),
        ],
        [new FestivalGame(SteinRules.GameId, "Steinstossen", 100)],
        [new FestivalDay(1, [SteinRules.GameId], "The Unspunnen stone lies on the lane: three puts a day.")],
        ClosingShow.Fireworks,
        [("08-14", "08-24")],
        [new FestivalMilestone(300, 25, "Warm-up"), new FestivalMilestone(800, 45, "Crowd"), new FestivalMilestone(1600, 80, "Whole village")]);
}
