// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

using UnitSport.Occasions.Festivals.Desalpe;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string DesalpeId = DesalpeRules.FestivalId;

    static void RegisterDesalpe() => Register(Desalpe());

    /// <summary>
    /// The Désalpe (Alpabzug), 20 September to 5 October: Charmey's village fair in the Val-de-Charmey, then Urnäsch (AR), then the
    /// largest town of the region (the relocation rule). Decorating the herd opens on day 1, the drive on day 3, the fair's vote on day 4.
    /// </summary>
    public static FestivalDef Desalpe() => new(
        DesalpeId, "Désalpe",
        [
            new FestivalSite("Val-de-Charmey", 2578750, 1163750, 160),
            new FestivalSite("Urnäsch", 2741200, 1243500, 160),
        ],
        [
            new FestivalGame(DesalpeRules.DressGame, "Dress the herd", 60),
            new FestivalGame(DesalpeRules.DriveGame, "Drive the herd", 120),
            new FestivalGame(DesalpeRules.VoteGame, "Vote for the finest herd", 40),
        ],
        [
            new FestivalDay(1, [DesalpeRules.DressGame], "The herd stands on the alp: dress the cows in flowers, the Leitkuh wears the big bell."),
            new FestivalDay(3, [DesalpeRules.DriveGame], "The Alpabzug: start at the alp sign and drive the herd down to the fair. Keep it together, fetch the strays."),
            new FestivalDay(4, [DesalpeRules.VoteGame], "The fair's ring: every herd that came down is on show. Vote for the finest one (not your own)."),
        ],
        ClosingShow.None,
        [("09-20", "10-05")],
        [new FestivalMilestone(600, 20, "First herd down"), new FestivalMilestone(1600, 40, "Flowers and bells"), new FestivalMilestone(3200, 80, "Whole alp at the fair")]);
}
