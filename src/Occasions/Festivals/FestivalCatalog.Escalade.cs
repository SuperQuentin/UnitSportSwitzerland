// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-escalade.md).

using UnitSport.Occasions.Festivals.Escalade;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string EscaladeId = EscaladeRules.FestivalId;

    static void RegisterEscalade() => Register(Escalade());

    /// <summary>
    /// The Escalade, the second Saturday of December and the Sunday after it (<c>12-sat#2 .. +1</c>): the Place du Bourg-de-Four in Geneva's old town;
    /// a region without Geneva moves it to its largest town (the relocation rule). Two days only, so the token cap is 300 (not 600). The marmite, the Course and the soupe all open on day 1.
    /// </summary>
    public static FestivalDef Escalade() => new(
        EscaladeRules.FestivalId, "Escalade",
        [new FestivalSite("Place du Bourg-de-Four, Genève", 2500040, 1117610, 100)],
        [
            new FestivalGame(EscaladeRules.MarmiteGame, "The Marmite", EscaladeRules.MarmiteDayCap),
            new FestivalGame(EscaladeRules.CourseGame, "Course de l'Escalade", EscaladeRules.CourseDayCap),
            new FestivalGame(EscaladeRules.SoupeGame, "Soupe de l'Escalade", EscaladeRules.SoupeDayCap),
        ],
        [
            new FestivalDay(1, [EscaladeRules.MarmiteGame, EscaladeRules.CourseGame, EscaladeRules.SoupeGame],
                "The Marmite stands in the square: the party's youngest and oldest smash it together. Run the Course through the old town; ladle the soupe for the party."),
        ],
        ClosingShow.None,
        [("12-sat#2", "+1")],
        [new FestivalMilestone(250, 10, "Chocolate for all"), new FestivalMilestone(600, 20, "Over the walls"), new FestivalMilestone(1100, 40, "Republic saved")],
        TokenCap: 300);
}
