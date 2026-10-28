// Plain C#, no Godot: linked into the unit tests (docs/notes/combat/schwingen.md).

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string AelplerfestId = "aelplerfest";

    /// <summary>The Schwingen game of the Älplerfest (#1269): the sawdust ring's Anschwingen from day 5, the Schlussgang from day 9.</summary>
    public const string SchwingGame = "schwingen";

    static FestivalCatalog() => Register(Aelplerfest());

    /// <summary>
    /// The Älplerfest, 14 to 24 August: Interlaken's Höhematte, then Schwägalp and Schwarzsee when the region lacks it. The wrestlers
    /// open on day 5 (the plan's day table); the stone, the horns and the flags come with their parts and add their games here.
    /// </summary>
    public static FestivalDef Aelplerfest() => new(
        AelplerfestId, "Älplerfest",
        [
            new FestivalSite("Höhematte, Interlaken", 2631800, 1169800, 140),
            new FestivalSite("Schwägalp", 2740500, 1232900, 140),
            new FestivalSite("Schwarzsee", 2588600, 1169400, 140),
        ],
        [new FestivalGame(SchwingGame, "Schwingen", 120)],
        [
            new FestivalDay(1, [], "The flags go up on the meadow."),
            new FestivalDay(5, [SchwingGame], "The Anschwingen: step into the sawdust ring. From day 9 the bouts are the Schlussgang."),
        ],
        ClosingShow.Fireworks,
        [("08-14", "08-24")],
        [new FestivalMilestone(1500, 20, "Fahnen up"), new FestivalMilestone(4000, 40, "Bunting"), new FestivalMilestone(8000, 80, "Whole Alp")]);
}
