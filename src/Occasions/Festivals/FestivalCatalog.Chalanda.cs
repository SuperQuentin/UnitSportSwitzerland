// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-chalandamarz.md).

using UnitSport.Occasions.Festivals.Chalanda;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string ChalandaId = ChalandaRules.FestivalId;

    static void RegisterChalanda() => Register(Chalanda());

    /// <summary>
    /// The Chalandamarz, 1 March (<c>03-01 .. 03-01</c>): the children's procession in an Engadin village, Scuol, then Zuoz, then Guarda; a region
    /// without them moves it to its largest town (the relocation rule). One day, one game, so the token cap is 100.
    /// </summary>
    public static FestivalDef Chalanda() => new(
        ChalandaRules.FestivalId, "Chalandamarz",
        [new FestivalSite("Scuol", 2818706, 1186865, 100), new FestivalSite("Zuoz", 2793090, 1164206, 100), new FestivalSite("Guarda", 2806464, 1183894, 100)],
        [new FestivalGame(ChalandaRules.UmzugGame, "The procession", ChalandaRules.UmzugDayCap)],
        [new FestivalDay(1, [ChalandaRules.UmzugGame], "Take the Treichel at the stand and walk the village loop, ringing on the march beat. The party scores the rings that are in step.")],
        ClosingShow.None,
        [("03-01", "03-01")],
        [new FestivalMilestone(300, 5, "Winter wavers"), new FestivalMilestone(700, 10, "Winter flees"), new FestivalMilestone(1200, 10, "Cows out of the stable")],
        TokenCap: 100);
}
