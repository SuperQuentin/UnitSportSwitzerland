using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The logs on the empty colonies' terminals (#1944, docs/plans/moon-surface.md section 5): a few short, dated entries per
/// terminal, read in the colony's order, that hint at what happened without answering it. Original text; each page fits
/// the terminal's screen (six lines of about 40 characters).
/// </summary>
public static class ColonyLogs
{
    /// <summary>A terminal's log: where it stands (a module's room, or the sealed lock) and its pages.</summary>
    public sealed record Log(RoomKind Room, bool AtSealed, string Title, string[] Pages);

    public static Log[] For(ColonyTheme theme) => theme switch
    {
        ColonyTheme.Abandoned => MariusHills,
        ColonyTheme.ColdWar => Plaskett,
        _ => [],
    };

    private static readonly Log[] MariusHills =
    [
        new(RoomKind.Control, false, "MHS OPS LOG  1/5",
        [
            "2041-03-02  Tube survey back. Section C\nis dry and stable for 400 m, to the bend.\n\n2041-03-09  Sensors in C read a steady\n-11 C. Warmer than the rock around it.\nLogged for the geologists.",
            "2041-04-18  Draw on the lower tube line\nup 30 %. Nothing on it but the survey\nlights. Sent Okafor to look.\n\n2041-04-19  Okafor says the lights are\nfine. Wants to go past the bend. Denied.",
        ]),
        new(RoomKind.Lab, false, "LAB 1 SAMPLES  2/5",
        [
            "2041-05-02  Core 17, from past the bend.\nLayered glass, not basalt. Thin, even\nbands, as if poured more than once.\n\n2041-05-03  Earth asks us to resend.\nThey think the file is corrupt.",
            "2041-05-11  Core 17 warm again this\nmorning. The freezer log says it never\nleft the freezer.\n\n2041-05-12  Core 17 moved to the tube.\nCommander's order.",
        ]),
        new(RoomKind.Quarters, false, "D. OKAFOR  PERSONAL  3/5",
        [
            "Three nights now I hear the tube through\nthe floor plates. Not machinery. Slow,\nlike breathing out. Halvorsen hears it\ntoo, says it is the heat exchangers.\n\nWe switched those off on Tuesday.",
            "Asked Earth for a relief crew.\nAnswer: next window, 140 days.\n\nHalvorsen sleeps in the lab now,\nby the door to the lower lock.",
        ]),
        new(RoomKind.Galley, false, "GALLEY BOARD  4/5",
        [
            "ROTA FOR JUNE:\n\nWhoever keeps opening the inner door\nof the lower lock: STOP. It is not a\nshortcut and it is not funny.\n                                   - M.",
            "2041-06-01  Crew meeting. 4 to 2: we\nseal the lower lock and leave on the next\nlander. Halvorsen and Okafor against.\nTheir badges are locked out of section C\nuntil then.",
        ]),
        new(RoomKind.Airlock, true, "LOWER LOCK  5/5",
        [
            "OUTER DOOR    SEALED (MANUAL)\nINNER DOOR    OPEN\nLAST CYCLE    2041-06-14 03:12\nDIRECTION     OUT\nBADGES        HALVORSEN, OKAFOR",
            "2041-06-14 07:40  Lander away with four.\nTwo badges did not answer the call.\nStation to standby. Lights left on in C.\n\n[no entry after this one]",
        ]),
    ];

    private static readonly Log[] Plaskett =
    [
        new(RoomKind.Control, false, "STATION LOG  PLASKETT",
        [
            "06.00 SHIFT CHANGE. RADAR ON SWEEP 6.\n06.40 RETURN AT BEARING 112, NO CODE.\n06.45 RETURN LOST. INSTRUMENT ERROR.\n09.00 DOME: 14 OAK SAPLINGS PLANTED.\n12.00 FROM EARTH: HOLD POSITION.",
            "31.12.1979 23.59\nSTATION TO CARETAKER MODE.\nPOWER TO DOME AND RADAR ONLY.\nLIGHTS LEFT ON FOR WHOEVER COMES NEXT.\n\n-- END OF TAPE --",
        ]),
        new(RoomKind.Greenhouse, false, "DOME CARETAKER",
        [
            "WATER CYCLE      AUTO\nLAMPS            ON  14 H\nPANES CRACKED    3\nOAKS             14 OF 14\nLAST VISIT       1979",
            "OAKS             14 OF 14\nHEIGHT, TALLEST  11.6 M\n\nNOTE: CANOPY NEAR THE GLASS. TRIM?\nNO REPLY.",
        ]),
    ];
}
