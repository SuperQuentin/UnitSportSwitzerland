// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-august1.md).

using UnitSport.Occasions.Festivals.August1;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string August1Id = August1Rules.FestivalId;

    static void RegisterAugust1() => Register(August1());

    /// <summary>
    /// The 1 August, 31 July and 1 August (<c>07-31 .. 08-01</c>): the festival ground of the first of the seven lake towns the region holds (Genève,
    /// Lausanne-Ouchy, Montreux, Neuchâtel, Zürich, Luzern, Lugano), else the largest town. The Höhenfeuer on the summits and the lake fireworks need no site
    /// (they are the same everywhere); the ground has the lampion stand, the fireworks stand and the lampion walk's post. Two days: token cap 180 (what the two games can pay in two days).
    /// </summary>
    public static FestivalDef August1() => new(
        August1Rules.FestivalId, "1 August",
        August1Rules.Lakes.Select(l => { var (e, n) = l.Site; return new FestivalSite(l.Name, e, n, 120); }).ToArray(),
        [
            new FestivalGame(August1Rules.LampionGame, "Lampion walk", August1Rules.WalkDayCap),
            new FestivalGame(August1Rules.ShowGame, "Lake fireworks", August1Rules.ShowDayCap),
        ],
        [
            new FestivalDay(1, [August1Rules.LampionGame, August1Rules.ShowGame],
                "The Höhenfeuer burn on the summits from dusk. Take a lampion at the stand and walk the village loop with two friends; watch the fireworks at 22:00."),
        ],
        ClosingShow.Bonfires,
        [("07-31", "08-01")],
        [new FestivalMilestone(120, 10, "A lantern lit"), new FestivalMilestone(300, 15, "A night out"), new FestivalMilestone(500, 20, "Fête nationale")],
        TokenCap: 180);
}
