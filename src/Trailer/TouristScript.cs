using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// "The Alien Tourist" (#865): a small saucer crash-lands in a field at night; its pilot needs a
/// day to mend it before the mothership comes back, and the four friends of the story film give it
/// the tour of Switzerland. The script is <c>docs/trailer/tourist.md</c>; every feature of the game
/// is something the alien tries, badly at first, then brilliantly. Cartoon throughout but for the
/// style gag; real terrain only.
/// </summary>
public static class TouristScript
{
    /// <summary>
    /// "Mighty Like Us", Kevin MacLeod (incompetech.com), CC BY 4.0: 90 BPM (incompetech's figure, the
    /// comb fits it to the end), the first downbeat at 0.624 s, 88 bars of music to 235.3 s; the film
    /// runs two bars more, to 240.62 s, the end card over the silence.
    /// </summary>
    internal static readonly Song MightyLikeUs = new("Mighty Like Us", 90.0, 0.624, 0.624 + 90 * 240.0 / 90.0,
        "\"Mighty Like Us\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Mighty%20Like%20Us.mp3");

    private static readonly Song S = MightyLikeUs;

    // ---- the cast: the story film's four friends, and the tourist ------------------------------

    /// <summary>
    /// The alien, until a figure of its own exists (see the script): bald, pale, a galaxy hoodie,
    /// neon gloves and star glasses, a headset for antennae. The skin palette has no green.
    /// </summary>
    internal static readonly Character Blip = new("BLIP", new Color("9cff57"),
        new Appearance(BodyBuild.Lean, 9, 2, 0, HairStyle.None, 0),
        [ItemId.GalaxyHoodie, ItemId.CargoPants, ItemId.NeonGloves, ItemId.StarGlasses, ItemId.NeonHeadset, ItemId.DiscoPlatforms]);

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        new()
        {
            Number = 1, Name = "Placeholder", FromBar = 1, Bars = 90, Song = S, Style = "cartoon", Hour = 12,
            Keys = [new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 3980), 50)],
        },
    };
}
