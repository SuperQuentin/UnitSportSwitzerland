using Godot;
using UnitSport.Items;

namespace UnitSport.Avatar;

/// <summary>
/// An alien and what it wears (#880): a figure (<see cref="Appearance"/>) and its clothes, the way a
/// film's <c>Trailer.Character</c> takes them: <c>new Character("ZORP", tint, Aliens.Zorp.Looks, Aliens.Zorp.Wears)</c>.
/// </summary>
public sealed record AlienLook(string Name, Appearance Looks, ItemId[] Wears)
{
    /// <summary>The clothes packed as <c>FootPlayer.OutfitBits</c> carries them.</summary>
    public Outfit Outfit => Outfit.Of(Wears);
}

/// <summary>
/// The aliens (#880, docs/notes/avatar/aliens.md): everything a player can also pick in
/// Settings → Character (the <see cref="BodyBuild.Alien"/> build, the alien skin tones, the alien
/// faces) and find in a wardrobe (antennae, the silver suit), put together as named presets for the
/// UFO films (#864-#866), the UFO sightings' pilots (#863) and the odd bot.
/// </summary>
public static class Aliens
{
    // indices into Appearance.SkinTones and FaceGenome's presets (append only, saved and replicated)
    public const int Green = 8, Grey = 9, Violet = 10, Teal = 11;
    public const int VisitorFace = 16, GazerFace = 17, GigglerFace = 18;
    // indices into Appearance.EyeColours: what glints in the dark eyes
    private const int BlueEyes = 1, GreenEyes = 2, VioletEyes = 3, AmberEyes = 5;

    /// <summary>An alien figure: the alien build, bald; <paramref name="skin"/> and <paramref name="face"/> as above.</summary>
    public static Appearance Look(int skin = Green, int face = VisitorFace, int eyes = VioletEyes) =>
        new(BodyBuild.Alien, face, eyes, skin, HairStyle.None, 0);

    /// <summary>The plain one: green, big dark eyes, a small smile, green antennae and the silver suit.</summary>
    public static readonly AlienLook Visitor = new("Visitor", Look(), [ItemId.AlienAntennae, ItemId.SilverSuit]);

    /// <summary>The films' pilot: green, violet glints, a small smile.</summary>
    public static readonly AlienLook Zorp = new("Zorp", Look(Green, VisitorFace, VioletEyes), [ItemId.AlienAntennae, ItemId.SilverSuit]);

    /// <summary>The co-pilot: grey, round staring eyes and an "oh" of a mouth, silver antennae.</summary>
    public static readonly AlienLook Zib = new("Zib", Look(Grey, GazerFace, BlueEyes), [ItemId.SilverAntennae, ItemId.SilverSuit]);

    /// <summary>The tall one: violet, blushing and giggling, the tall antennae over everyone.</summary>
    public static readonly AlienLook Gleeb = new("Gleeb", Look(Violet, GigglerFace, GreenEyes), [ItemId.LongAntennae, ItemId.SilverSuit]);

    /// <summary>A teal one, without the suit: a tourist in a T-shirt and jeans.</summary>
    public static readonly AlienLook Blip = new("Blip", Look(Teal, VisitorFace, AmberEyes), [ItemId.SilverAntennae, ItemId.PastelTee, ItemId.Jeans, ItemId.WhiteSneakers]);

    /// <summary>The named ones, for the model viewer and anyone choosing.</summary>
    public static readonly AlienLook[] Crew = [Visitor, Zorp, Zib, Gleeb, Blip];

    private static readonly ItemId[] AntennaeKinds = [ItemId.AlienAntennae, ItemId.SilverAntennae, ItemId.LongAntennae];

    /// <summary>
    /// An alien made up from <paramref name="seed"/>, the same on every peer: a UFO sighting's pilot
    /// (#863), a bot (<see cref="IsAlienBot"/>). Skin, face, eye glint and antennae vary; the suit is always on.
    /// </summary>
    public static AlienLook ForSeed(int seed)
    {
        uint h = (uint)seed * 2246822519u ^ 0x9E3779B9u;
        int Next(int n) { h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15; return (int)(h % (uint)n); }
        ReadOnlySpan<int> eyes = [BlueEyes, GreenEyes, VioletEyes, AmberEyes];
        var look = Look(Green + Next(4), VisitorFace + Next(3), eyes[Next(eyes.Length)]);
        return new AlienLook("Visitor", look, [AntennaeKinds[Next(AntennaeKinds.Length)], ItemId.SilverSuit]);
    }

    /// <summary>
    /// A bot (#837) numbered <paramref name="n"/> is one of the rare aliens among them (about one in
    /// thirteen), by its number alone so every peer agrees; <see cref="ForSeed"/>(n) is its look.
    /// </summary>
    public static bool IsAlienBot(int n) => n % 13 == 7;
}
