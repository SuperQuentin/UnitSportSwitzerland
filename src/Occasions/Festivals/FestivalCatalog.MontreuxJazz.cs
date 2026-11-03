// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-montreuxjazz.md).

using UnitSport.Occasions.Festivals.MontreuxJazz;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    public const string MontreuxJazzId = MontreuxJazzRules.FestivalId;

    static void RegisterMontreuxJazz() => Register(MontreuxJazz());

    /// <summary>
    /// The Montreux Jazz Festival (#1394), sixteen days from the first Friday of July (<c>07-fri#1 .. +15</c>): a small stage on the Place du Marché by the quay
    /// (the venue row <see cref="StadiumCatalog.MontreuxJazzOpenAir"/>, which stands only in this window). Listening (a song heard from the audience pays when it
    /// ends) and the Encore (the crowd claps the band back on the beat) are open from day 1; day 8 is the Jazz Night (both pay half as much again), day 16 the
    /// closing concert (a long set and a finale bonus). It needs its real stage: a region without Montreux does not hold it (no relocation). Token cap 400.
    /// </summary>
    public static FestivalDef MontreuxJazz() => new(
        MontreuxJazzRules.FestivalId, "Montreux Jazz",
        [new FestivalSite("Quai de Montreux", StadiumCatalog.MontreuxJazzOpenAir.E, StadiumCatalog.MontreuxJazzOpenAir.N, 120)],
        [
            new FestivalGame(MontreuxJazzRules.ListenGame, "Listening", MontreuxJazzRules.ListenDayCap),
            new FestivalGame(MontreuxJazzRules.EncoreGame, "Encore", MontreuxJazzRules.EncoreDayCap),
        ],
        [
            new FestivalDay(1, [MontreuxJazzRules.ListenGame, MontreuxJazzRules.EncoreGame],
                "A small stage stands on the quay. Press the interact key at the stage-front rail, stay for a song and it pays when it ends; when the set is over, clap the band back on the beat."),
            new FestivalDay(MontreuxJazzRules.JazzNight, [], "The Jazz Night: a longer set, and both games pay half as much again."),
            new FestivalDay(MontreuxJazzRules.ClosingNight, [], "The closing concert: the longest set, with a finale bonus for everyone in the audience at the last song."),
        ],
        ClosingShow.Concert,
        MontreuxJazzRules.Schedule,
        [new FestivalMilestone(300, 10, "Front row"), new FestivalMilestone(800, 30, "Late set"), new FestivalMilestone(1500, 60, "Standing ovation")],
        TokenCap: MontreuxJazzRules.TokenCap,
        Relocates: false);
}
