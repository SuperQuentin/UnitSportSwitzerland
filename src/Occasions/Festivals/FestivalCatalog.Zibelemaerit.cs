// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-zibelemaerit.md).

using UnitSport.Occasions.Festivals.Zibelemaerit;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string ZibelemaeritId = ZibeleRules.FestivalId;

    static void RegisterZibelemaerit() => Register(Zibelemaerit());

    /// <summary>
    /// The Zibelemärit, Bern's onion market on the fourth Monday of November (<c>11-mon#4</c>: 23 November 2026, 22 November 2027): the Bundesplatz; a region without
    /// Bern moves it to its largest town (the relocation rule). One day, so the token cap is the two games' day caps (52), not 600.
    /// </summary>
    public static FestivalDef Zibelemaerit() => new(
        ZibeleRules.FestivalId, "Zibelemärit",
        [new FestivalSite("Bundesplatz, Bern", 2600590, 1199700, 100)],
        [
            new FestivalGame(ZibeleRules.StallGame, "Onion stalls", ZibeleRules.DayMax),
            new FestivalGame(ZibeleRules.ConfettiGame, "Konfettischlacht", ZibeleRules.ConfettiDayCap),
        ],
        [
            new FestivalDay(1, [ZibeleRules.StallGame, ZibeleRules.ConfettiGame],
                "The onion market: taste at every stall, buy a Zwiebelzopf, a Zibelechueche or a Mehlsuppe, and throw confetti eggs over the square."),
        ],
        ClosingShow.None,
        [("11-mon#4", "+0")],
        [new FestivalMilestone(50, 3, "A braid for the door"), new FestivalMilestone(120, 4, "Tart for all"), new FestivalMilestone(250, 6, "Onion king")],
        TokenCap: ZibeleRules.TokenCap);
}
