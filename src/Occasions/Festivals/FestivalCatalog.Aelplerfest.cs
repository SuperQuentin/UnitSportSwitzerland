// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fahnenschwingen.md).

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string AelplerfestId = "aelplerfest";

    /// <summary>The Fahnenschwingen game of the Älplerfest (#1269 part 5); the other games add themselves to the def in their own parts.</summary>
    public const string FahneGame = "fahne";

    /// <summary>The flag ground: 12 m east of the site's middle (the mortar rack is 14 m west), and how far from it a body still counts as on it.</summary>
    public const double FahneGroundRadius = 16, FahneGroundOffset = 12;

    public static (double E, double N) FahneGround(ResolvedSite site) => (site.E + FahneGroundOffset, site.N);

    static FestivalCatalog() => Register(Aelplerfest());

    /// <summary>
    /// The Älplerfest, 14 to 24 August: Interlaken's Höhematte, then Schwägalp and Schwarzsee when the region lacks it. The flag swingers
    /// open on day 3 (the plan's day table); the stone, the horns and the wrestling come with their parts.
    /// </summary>
    public static FestivalDef Aelplerfest() => new(
        AelplerfestId, "Älplerfest",
        [
            new FestivalSite("Höhematte, Interlaken", 2631800, 1169800, 140),
            new FestivalSite("Schwägalp", 2740500, 1232900, 140),
            new FestivalSite("Schwarzsee", 2588600, 1169400, 140),
        ],
        [new FestivalGame(FahneGame, "Fahnenschwingen", 100)],
        [
            new FestivalDay(1, [], "The flags go up on the meadow."),
            new FestivalDay(3, [FahneGame], "The flag swingers take the meadow: swing the Fahne on the march."),
        ],
        ClosingShow.Fireworks,
        [("08-14", "08-24")],
        [new FestivalMilestone(1500, 20, "Fahnen up"), new FestivalMilestone(4000, 40, "Bunting"), new FestivalMilestone(8000, 80, "Whole Alp")]);
}
