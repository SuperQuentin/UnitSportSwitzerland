// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fasnacht.md).

using UnitSport.Occasions.Festivals.Fasnacht;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string FasnachtId = FasnachtRules.FestivalId, BaslerFasnachtId = FasnachtRules.BaselId;

    static void RegisterFasnacht()
    {
        Register(Fasnacht());
        Register(BaslerFasnacht());
    }

    /// <summary>
    /// Luzern's Fasnacht, <c>easter-52 .. +5</c>: the six days from the Schmutziger Donnerstag to the Tuesday (Güdisdienstag) at the Weinmarkt. The Larven stall,
    /// the Guggenmusik stage and the Räppli all open on day 1. A region without Luzern gets it in its largest town.
    /// </summary>
    public static FestivalDef Fasnacht() => new(
        FasnachtRules.FestivalId, "Fasnacht",
        [new FestivalSite("Weinmarkt, Luzern", 2665850, 1211700, 90)],
        [
            new FestivalGame(FasnachtRules.LarvenGame, "Larven", FasnachtRules.StallDayCap),
            new FestivalGame(FasnachtRules.GuggenGame, "Guggenmusik", GuggenRules.DayCap),
            new FestivalGame(FasnachtRules.RaeppliGame, "Räppli", FasnachtRules.RaeppliDayCap),
        ],
        [new FestivalDay(1, [FasnachtRules.LarvenGame, FasnachtRules.GuggenGame, FasnachtRules.RaeppliGame],
            "Schmutziger Donnerstag: take a Larve at the stall, play with the Guggenmusik on the stage and throw Räppli.")],
        ClosingShow.None,
        [("easter-52", "+5")],
        [new FestivalMilestone(300, 20, "Die Larve sitzt"), new FestivalMilestone(800, 40, "Gugge im Takt"), new FestivalMilestone(1500, 80, "Güdisdienstag")]);

    /// <summary>
    /// Basel's Fasnacht, <c>easter-41 .. +2</c>: the three days from the Monday after Ash Wednesday, the Morgestraich at 04:00 on day 1 (the street lights go out,
    /// the lanterns are lit). Three days cannot earn the default 600 tokens, so its cap is 300.
    /// </summary>
    public static FestivalDef BaslerFasnacht() => new(
        FasnachtRules.BaselId, "Basler Fasnacht",
        [new FestivalSite("Marktplatz, Basel", 2611900, 1267700, 90)],
        [
            new FestivalGame(FasnachtRules.MorgestraichGame, "Morgestraich", FasnachtRules.MorgestraichDayCap),
            new FestivalGame(FasnachtRules.LarvenGame, "Larven", FasnachtRules.StallDayCap),
            new FestivalGame(FasnachtRules.GuggenGame, "Guggenmusik", GuggenRules.DayCap),
            new FestivalGame(FasnachtRules.RaeppliGame, "Räppli", FasnachtRules.RaeppliDayCap),
        ],
        [new FestivalDay(1, [FasnachtRules.MorgestraichGame, FasnachtRules.LarvenGame, FasnachtRules.GuggenGame, FasnachtRules.RaeppliGame],
            "Morgestraich at four: the street lights go out and the lanterns are lit. Then the Larven stall, the Guggenmusik and the Räppli.")],
        ClosingShow.None,
        [("easter-41", "+2")],
        [new FestivalMilestone(250, 10, "Vorwärts, marsch"), new FestivalMilestone(600, 20, "Drei scheenschti Dääg"), new FestivalMilestone(1000, 40, "Endstreich")],
        TokenCap: 300);
}
