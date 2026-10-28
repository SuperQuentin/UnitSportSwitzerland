// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-alphorn.md).

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string AelplerfestId = "aelplerfest";

    // The Älplerfest (#1269, docs/plans/adventure-festivals.md): 14 to 24 August, Interlaken's Höhematte by Ruine Unspunnen, then Schwägalp (AR)
    // and Schwarzsee (FR); a region that holds none moves it to its largest town. The alphorn and yodel game registers here; the other games
    // of the day table (Steinstossen, Fahnenschwingen, Schwingen) add theirs in their own parts.
    static FestivalCatalog() => Register(Aelplerfest());

    public static FestivalDef Aelplerfest() => new(
        AelplerfestId, "Älplerfest",
        [
            new FestivalSite("Interlaken, Höhematte", 2631883, 1170538, 150),
            new FestivalSite("Schwägalp", 2742170, 1234780, 150),
            new FestivalSite("Schwarzsee", 2588305, 1169231, 150),
        ],
        [new FestivalGame(Horn.AlphornRules.GameId, "Alphorn and yodel", Horn.AlphornRules.DayCap)],
        [new FestivalDay(1, [Horn.AlphornRules.GameId], "The alphorns are out: play the song with others and the valley answers.")],
        ClosingShow.Fireworks,
        [("08-14", "08-24")],
        [new FestivalMilestone(600, 30, "First songs"), new FestivalMilestone(2000, 50, "Whole meadow"), new FestivalMilestone(5000, 70, "Echo of the valley")]);
}
