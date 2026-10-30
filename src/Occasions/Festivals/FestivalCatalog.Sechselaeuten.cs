// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-sechselaeuten.md).

using UnitSport.Occasions.Festivals.Sechselaeuten;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string SechselaeutenId = SechselaeutenRules.FestivalId;

    static void RegisterSechselaeuten() => Register(Sechselaeuten());

    /// <summary>
    /// The Sechseläuten, the third Monday of April, one day (<c>04-mon#3 .. +0</c>): Zürich's Sechseläutenplatz; a region without Zürich moves it to its largest
    /// town (the relocation rule). The Zunft's purse and the bet on the Böögg both open on day 1; one day, so the token cap is 200.
    /// </summary>
    public static FestivalDef Sechselaeuten() => new(
        SechselaeutenRules.FestivalId, "Sechseläuten",
        [new FestivalSite("Sechseläutenplatz, Zürich", 2683617, 1246744, 120)],
        [
            new FestivalGame(SechselaeutenRules.PurseGame, "The Zunft's purse", SechselaeutenRules.PurseDayCap),
            new FestivalGame(SechselaeutenRules.BetGame, "The bet on the Böögg", SechselaeutenRules.BetDayCap),
        ],
        [
            new FestivalDay(1, [SechselaeutenRules.PurseGame, SechselaeutenRules.BetGame],
                "The Zunft's Weibel hands out a purse. Bet on the minute the Böögg's head bursts before the pyre is lit at 18:00; the pool is shared by closeness."),
        ],
        ClosingShow.None,
        [("04-mon#3", "+0")],
        [new FestivalMilestone(100, 5, "A round of Zunft wine"), new FestivalMilestone(300, 10, "The guilds ride by"), new FestivalMilestone(700, 20, "Summer is sure")],
        TokenCap: 200);
}
