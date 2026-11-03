// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-christmas.md).

using UnitSport.Occasions.Festivals.Christmas;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string ChristmasMarketId = ChristmasRules.FestivalId;

    static void RegisterChristmas() => Register(ChristmasMarket());

    /// <summary>
    /// The Christmas markets, 25 November to 23 December (inside the Christmas season, whose sky and lights they keep): Basel's Barfüsserplatz,
    /// Zürich's Werdmühleplatz, Bern's Münsterplatz, Luzern's Franziskanerplatz, then Montreux's Grand-Rue and Genève's Plainpalais; the region holds
    /// the first of these it has (the western one holds Montreux). The stall-hop is open from day 1, the Samichlaus on 6 December (day 12).
    /// </summary>
    public static FestivalDef ChristmasMarket() => new(
        ChristmasRules.FestivalId, "Christmas market",
        [
            new FestivalSite("Barfüsserplatz, Basel", 2611750, 1267550, 90),
            new FestivalSite("Werdmühleplatz, Zürich", 2683100, 1247300, 90),
            new FestivalSite("Münsterplatz, Bern", 2600700, 1199600, 90),
            new FestivalSite("Franziskanerplatz, Luzern", 2665850, 1211600, 90),
            new FestivalSite("Grand-Rue, Montreux", 2559396, 1142356, 90),
            new FestivalSite("Plainpalais, Genève", 2499450, 1117400, 90),
        ],
        [
            new FestivalGame(ChristmasRules.StallGame, "Stall-hop", ChristmasRules.DayMax),
            new FestivalGame(ChristmasRules.SamichlausGame, "The Samichlaus", 40),
        ],
        [
            new FestivalDay(1, [ChristmasRules.StallGame], "The stalls are open: taste at each one for a few Batzen, then buy a Glühwein, a raclette, a Grittibänz or a bauble."),
            new FestivalDay(ChristmasRules.SamichlausDay, [ChristmasRules.SamichlausGame], "6 December: the Samichlaus is at the market with his bag."),
        ],
        ClosingShow.None,
        [("11-25", "12-23")],
        [new FestivalMilestone(400, 20, "Warm hands"), new FestivalMilestone(1000, 40, "Lights on"), new FestivalMilestone(2000, 80, "Whole market")]);
}
