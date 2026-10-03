using Godot;
using UnitSport.Items;

namespace UnitSport.Avatar;

/// <summary>
/// Where on the body a worn thing goes (docs/notes/avatar/clothing.md). One item per slot; the
/// numbers are the slot's place in <see cref="Outfit"/>'s bits, so <b>append only</b>.
/// </summary>
public enum WearSlot { None = 0, Head, Eyes, Face, Ears, Neck, Top, Bottom, Legs, Feet, Hands }

/// <summary>
/// A shader effect on a garment's surfaces, carried in the vertex colour's alpha
/// (<see cref="Garments.Fx"/>, decoded by <c>shaders/avatar.gdshader</c>). The numbers are what the
/// shader reads: <b>append only</b>.
/// </summary>
public enum Finish : byte
{
    None = 0,
    Rainbow = 1,
    Disco = 2,
    Galaxy = 3,
    Holo = 4,
    Glitch = 5,
    Lava = 6,
    Neon = 7,
    // patterns rather than specials: drawn the same way, found in the ordinary pools
    Tartan = 8,
    Fishnet = 9,
    Lace = 10,
    /// <summary>Not a cloth: the pixel face's band (#394, <see cref="FaceAtlas"/>); listed so no pattern takes its id.</summary>
    Face = 11,
    // #394: the references' cloth patterns
    Checker = 12,
    Stripes = 13,
    Studs = 14,
}

/// <summary>How a garment is built out of tubes and boxes (<c>HumanMeshBuilder.Clothing.cs</c>).</summary>
public enum GarmentShape
{
    // head
    CatEars, CatHeadset, Headset, BunnyEars, Horns, Bow, LaceHeadband, Beanie,
    // eyes
    RoundGlasses, RoundShades, HeartShades, Shades, StarGlasses,
    // face
    Mask,
    // ears
    Studs, Hoops, Industrial, Spikes, StarStuds,
    // neck
    SpikedChoker, HeartChoker, Chain, BellCollar,
    // top
    TShirt, PrintTee, Polo, Marcel, Corset, CropTop, Longsleeve, Hoodie, CroppedJacket, Robe, Dress,
    // bottom
    Shorts, Pants, Cargo, HighLowSkirt, SlitMaxi, RuffleMini, PleatedSkirt, LongPleated,
    // legs
    ThighHigh, StripedThighHigh, KneeSock, Fishnets,
    // feet
    PlatformBoots, CombatBoots, Sneakers, MaryJanes,
    // hands
    ArmWarmers, StripedWarmers, Fingerless, Paws, Gloves,
}

/// <summary>The pixel face printed on a <see cref="GarmentShape.Mask"/>.</summary>
public enum MaskFace { Plain, Uwu, SwirlA, SwirlW, Fang, Tongue, Skull, CatMouth }

/// <summary>Which look a garment belongs to, for the blurb and the loot pools.</summary>
public enum GarmentStyle { Basic, Gothic, Kawaii, Special }

/// <summary>
/// One wearable look: the item it is, where it goes, how it is drawn and in which colours.
/// <see cref="Code"/> is its number within its <see cref="Slot"/>, 1..63, which is what
/// <see cref="Outfit"/> packs: <b>never reuse or renumber one</b>.
/// </summary>
public sealed record Garment(
    ItemId Item, string Name, WearSlot Slot, int Code, GarmentShape Shape, GarmentStyle Style,
    Color A, Color B, Color C, Finish Finish = Finish.None, MaskFace Face = MaskFace.Plain)
{
    /// <summary>A robe or dress: one piece from shoulders to hem, so nothing goes in the bottom slot with it.</summary>
    public bool CoversBottom => Shape is GarmentShape.Robe or GarmentShape.Dress;

    /// <summary>A rare one with a shader finish (patterns like tartan are not).</summary>
    public bool IsSpecial => Style == GarmentStyle.Special;
}

/// <summary>
/// Everything worn, packed into one <see cref="long"/>: six bits per <see cref="WearSlot"/>, the
/// garment's <see cref="Garment.Code"/>, 0 for nothing. Replicated as
/// <see cref="Player.FootPlayer.OutfitBits"/>, so ten slots fit in 60 bits.
/// </summary>
public readonly record struct Outfit(long Bits)
{
    public const int SlotCount = 10;
    private const int Width = 6;
    private const long Mask = (1L << Width) - 1;

    public static readonly Outfit Empty = new(0);

    public bool IsEmpty => Bits == 0;

    public int CodeIn(WearSlot slot) => slot == WearSlot.None ? 0 : (int)((Bits >> (Width * ((int)slot - 1))) & Mask);

    public Garment? this[WearSlot slot] => Garments.ByCode(slot, CodeIn(slot));

    public Outfit With(WearSlot slot, int code)
    {
        if (slot == WearSlot.None) return this;
        int shift = Width * ((int)slot - 1);
        return new((Bits & ~(Mask << shift)) | ((long)(code & (int)Mask) << shift));
    }

    /// <summary>The outfit of these items, each in its own slot (a later one in the same slot wins).</summary>
    public static Outfit Of(IEnumerable<ItemId> items)
    {
        var o = Empty;
        foreach (var id in items)
            if (Garments.Get(id) is { } g) o = o.With(g.Slot, g.Code);
        return o;
    }
}

/// <summary>
/// The wardrobe: every look that can be found and worn, gothic, kawaii and plain, plus the rare
/// ones with a shader finish. Pure data; the item rows come from here
/// (<see cref="ItemDefs"/>), so a new look is one line plus its <see cref="ItemId"/>.
/// </summary>
public static class Garments
{
    // the two palettes the looks are picked from
    private static readonly Color Black = new("141216"), Ink = new("0c0c0e"), Charcoal = new("1c1c20");
    private static readonly Color Blood = new("a01020"), Wine = new("7a0f1a"), Plum = new("3a1d4a");
    private static readonly Color Silver = new("b8bcc4"), Bone = new("e8e6e2"), White = new("f4f4f0"), Gold = new("e0b848");
    private static readonly Color Pink = new("ff8fc8"), Blush = new("ffb0d4"), Candy = new("ff5fa8"), Rose = new("ffc2dc");
    private static readonly Color Lilac = new("c8a8f0"), Mint = new("a8e8d0"), Sky = new("a8d0f8"), Lemon = new("ffe070");

    public static readonly Garment[] All =
    {
        // ---- head ----
        new(ItemId.CatEarsBlack, "Black cat ears", WearSlot.Head, 1, GarmentShape.CatEars, GarmentStyle.Gothic, Black, Pink, Black),
        new(ItemId.CatEarsPink, "Pink cat ears", WearSlot.Head, 2, GarmentShape.CatEars, GarmentStyle.Kawaii, Blush, new("fff0f6"), Blush),
        new(ItemId.CatHeadset, "Cat-ear headset", WearSlot.Head, 3, GarmentShape.CatHeadset, GarmentStyle.Kawaii, Rose, White, Candy),
        new(ItemId.GamerHeadset, "Black headset", WearSlot.Head, 4, GarmentShape.Headset, GarmentStyle.Basic, new("1a1a1e"), new("3a3a42"), new("3ad0ff")),
        new(ItemId.BunnyEars, "Bunny ears", WearSlot.Head, 5, GarmentShape.BunnyEars, GarmentStyle.Kawaii, new("f4f0f2"), Blush, White),
        new(ItemId.DevilHorns, "Devil horns", WearSlot.Head, 6, GarmentShape.Horns, GarmentStyle.Gothic, Wine, new("2a0a0e"), Black),
        new(ItemId.PinkBow, "Big pink bow", WearSlot.Head, 7, GarmentShape.Bow, GarmentStyle.Kawaii, new("ff7ab8"), White, Candy),
        new(ItemId.LaceHeadband, "Lace headband", WearSlot.Head, 8, GarmentShape.LaceHeadband, GarmentStyle.Gothic, Black, Bone, Black),
        new(ItemId.BlackBeanie, "Black beanie", WearSlot.Head, 9, GarmentShape.Beanie, GarmentStyle.Basic, Charcoal, new("2c2c32"), Charcoal),
        new(ItemId.RainbowCatEars, "Rainbow cat ears", WearSlot.Head, 10, GarmentShape.CatEars, GarmentStyle.Special, White, White, White, Finish.Rainbow),
        new(ItemId.NeonHeadset, "Neon headset", WearSlot.Head, 11, GarmentShape.Headset, GarmentStyle.Special, new("ff3fc0"), new("30f0ff"), new("ff3fc0"), Finish.Neon),

        // ---- eyes ----
        new(ItemId.RoundGlasses, "Round glasses", WearSlot.Eyes, 1, GarmentShape.RoundGlasses, GarmentStyle.Basic, new("c8a04a"), new("c8e0f0"), Black),
        new(ItemId.HeartShades, "Heart shades", WearSlot.Eyes, 2, GarmentShape.HeartShades, GarmentStyle.Kawaii, Candy, new("b0204a"), Candy),
        new(ItemId.BlackShades, "Black shades", WearSlot.Eyes, 3, GarmentShape.Shades, GarmentStyle.Basic, new("141416"), new("0a0a0c"), Black),
        new(ItemId.GothShades, "Gothic round shades", WearSlot.Eyes, 4, GarmentShape.RoundShades, GarmentStyle.Gothic, Silver, new("120c10"), Black),
        new(ItemId.StarGlasses, "Star glasses", WearSlot.Eyes, 5, GarmentShape.StarGlasses, GarmentStyle.Kawaii, Lemon, new("ff9fd0"), Lemon),
        new(ItemId.DiscoShades, "Disco shades", WearSlot.Eyes, 6, GarmentShape.Shades, GarmentStyle.Special, Silver, Silver, Silver, Finish.Disco),

        // ---- face: the five kawaii masks, and a few more ----
        new(ItemId.MaskUwu, "UwU mask", WearSlot.Face, 1, GarmentShape.Mask, GarmentStyle.Kawaii, new("121214"), White, Candy, Face: MaskFace.Uwu),
        new(ItemId.MaskSwirlA, "Swirly A mask", WearSlot.Face, 2, GarmentShape.Mask, GarmentStyle.Kawaii, new("121214"), White, Candy, Face: MaskFace.SwirlA),
        new(ItemId.MaskSwirlW, "Swirly OwO mask", WearSlot.Face, 3, GarmentShape.Mask, GarmentStyle.Kawaii, new("121214"), White, Candy, Face: MaskFace.SwirlW),
        new(ItemId.MaskFang, "Fang mask", WearSlot.Face, 4, GarmentShape.Mask, GarmentStyle.Gothic, new("121214"), White, Candy, Face: MaskFace.Fang),
        new(ItemId.MaskTongue, "Tongue-out mask", WearSlot.Face, 5, GarmentShape.Mask, GarmentStyle.Kawaii, new("121214"), White, Candy, Face: MaskFace.Tongue),
        new(ItemId.MaskBlack, "Black face mask", WearSlot.Face, 6, GarmentShape.Mask, GarmentStyle.Basic, new("121214"), White, White),
        new(ItemId.MaskSkull, "Skull mask", WearSlot.Face, 7, GarmentShape.Mask, GarmentStyle.Gothic, new("121214"), Bone, Bone, Face: MaskFace.Skull),
        new(ItemId.MaskCat, "Pink cat mask", WearSlot.Face, 8, GarmentShape.Mask, GarmentStyle.Kawaii, Rose, new("6a2a40"), Candy, Face: MaskFace.CatMouth),
        new(ItemId.HoloMask, "Holo mask", WearSlot.Face, 9, GarmentShape.Mask, GarmentStyle.Special, White, White, White, Finish.Holo, MaskFace.Uwu),

        // ---- ears: piercings ----
        new(ItemId.SilverStuds, "Silver studs", WearSlot.Ears, 1, GarmentShape.Studs, GarmentStyle.Basic, Silver, Silver, Silver),
        new(ItemId.SilverHoops, "Silver hoops", WearSlot.Ears, 2, GarmentShape.Hoops, GarmentStyle.Basic, Silver, Silver, Silver),
        new(ItemId.IndustrialSet, "Industrial bar and nose ring", WearSlot.Ears, 3, GarmentShape.Industrial, GarmentStyle.Gothic, Silver, Silver, Silver),
        new(ItemId.SpikePiercings, "Spike piercings", WearSlot.Ears, 4, GarmentShape.Spikes, GarmentStyle.Gothic, Silver, Black, Silver),
        new(ItemId.StarStuds, "Star studs", WearSlot.Ears, 5, GarmentShape.StarStuds, GarmentStyle.Kawaii, Lemon, Pink, Lemon),

        // ---- neck ----
        new(ItemId.SpikedChoker, "Spiked choker", WearSlot.Neck, 1, GarmentShape.SpikedChoker, GarmentStyle.Gothic, Black, Silver, Black),
        new(ItemId.HeartChoker, "Heart choker", WearSlot.Neck, 2, GarmentShape.HeartChoker, GarmentStyle.Kawaii, Blush, new("ff3f8f"), Blush),
        new(ItemId.ChainNecklace, "Chain necklace", WearSlot.Neck, 3, GarmentShape.Chain, GarmentStyle.Gothic, Silver, Silver, Silver),
        new(ItemId.BellCollar, "Bell collar", WearSlot.Neck, 4, GarmentShape.BellCollar, GarmentStyle.Kawaii, Candy, Gold, Candy),

        // ---- top ----
        new(ItemId.WhiteTee, "White T-shirt", WearSlot.Top, 1, GarmentShape.TShirt, GarmentStyle.Basic, new("f2f2ee"), new("f2f2ee"), new("f2f2ee")),
        new(ItemId.BandTee, "Black band tee", WearSlot.Top, 2, GarmentShape.PrintTee, GarmentStyle.Gothic, new("141416"), Bone, Blood),
        new(ItemId.PastelTee, "Pastel heart tee", WearSlot.Top, 3, GarmentShape.PrintTee, GarmentStyle.Kawaii, Lilac, Pink, White),
        new(ItemId.GreenPolo, "Green polo", WearSlot.Top, 4, GarmentShape.Polo, GarmentStyle.Basic, new("2a7a3a"), new("f0f0e8"), new("1e5a2a")),
        new(ItemId.NavyPolo, "Navy polo", WearSlot.Top, 5, GarmentShape.Polo, GarmentStyle.Basic, new("1e2a4a"), Bone, new("141c34")),
        new(ItemId.WhiteMarcel, "White marcel", WearSlot.Top, 6, GarmentShape.Marcel, GarmentStyle.Basic, White, White, White),
        new(ItemId.BlackMarcel, "Black marcel", WearSlot.Top, 7, GarmentShape.Marcel, GarmentStyle.Gothic, new("18181a"), new("18181a"), new("18181a")),
        new(ItemId.BuckleCorset, "Buckle corset", WearSlot.Top, 8, GarmentShape.Corset, GarmentStyle.Gothic, Black, Silver, Black),
        new(ItemId.ChainCropTop, "Chain crop top", WearSlot.Top, 9, GarmentShape.CropTop, GarmentStyle.Gothic, Black, Silver, Black),
        new(ItemId.PinkCropTop, "Pink crop top", WearSlot.Top, 10, GarmentShape.CropTop, GarmentStyle.Kawaii, new("ff9fd0"), White, Candy),
        new(ItemId.StripedLongsleeve, "Striped long-sleeve", WearSlot.Top, 11, GarmentShape.Longsleeve, GarmentStyle.Gothic, Black, Bone, Black),
        new(ItemId.PastelHoodie, "Pastel hoodie", WearSlot.Top, 12, GarmentShape.Hoodie, GarmentStyle.Kawaii, Rose, White, Sky),
        new(ItemId.PostalJacket, "Postal-doll jacket", WearSlot.Top, 13, GarmentShape.CroppedJacket, GarmentStyle.Kawaii, new("2a4a8a"), new("f4f0e6"), new("2a9a5a")),
        new(ItemId.GothicRobe, "Black gothic robe", WearSlot.Top, 14, GarmentShape.Robe, GarmentStyle.Gothic, Black, Wine, Silver),
        new(ItemId.LolitaDress, "Pink lolita dress", WearSlot.Top, 15, GarmentShape.Dress, GarmentStyle.Kawaii, Blush, White, Candy),
        new(ItemId.WitchRobe, "Witch robe", WearSlot.Top, 16, GarmentShape.Robe, GarmentStyle.Gothic, new("2a1a3a"), new("6a3a8a"), Gold),
        new(ItemId.RainbowTee, "Rainbow tee", WearSlot.Top, 17, GarmentShape.TShirt, GarmentStyle.Special, White, White, White, Finish.Rainbow),
        new(ItemId.DiscoTop, "Disco-ball top", WearSlot.Top, 18, GarmentShape.Marcel, GarmentStyle.Special, Silver, Silver, Silver, Finish.Disco),
        new(ItemId.GalaxyHoodie, "Galaxy hoodie", WearSlot.Top, 19, GarmentShape.Hoodie, GarmentStyle.Special, Plum, Plum, Plum, Finish.Galaxy),
        new(ItemId.LavaTee, "Lava tee", WearSlot.Top, 20, GarmentShape.TShirt, GarmentStyle.Special, new("2a1410"), new("2a1410"), new("2a1410"), Finish.Lava),
        new(ItemId.GalaxyDress, "Galaxy dress", WearSlot.Top, 21, GarmentShape.Dress, GarmentStyle.Special, Plum, Plum, Plum, Finish.Galaxy),
        new(ItemId.GlitchTee, "Glitch tee", WearSlot.Top, 22, GarmentShape.TShirt, GarmentStyle.Special, White, White, White, Finish.Glitch),

        // ---- bottom ----
        new(ItemId.JoggingShorts, "Grey jogging shorts", WearSlot.Bottom, 1, GarmentShape.Shorts, GarmentStyle.Basic, new("8a8a8e"), White, new("8a8a8e")),
        new(ItemId.BlackShorts, "Black shorts", WearSlot.Bottom, 2, GarmentShape.Shorts, GarmentStyle.Basic, new("1a1a1c"), new("1a1a1c"), new("1a1a1c")),
        new(ItemId.TartanSkirt, "Red tartan skirt", WearSlot.Bottom, 3, GarmentShape.HighLowSkirt, GarmentStyle.Gothic, Blood, Silver, Black, Finish.Tartan),
        new(ItemId.SlitMaxiSkirt, "Black slit maxi skirt", WearSlot.Bottom, 4, GarmentShape.SlitMaxi, GarmentStyle.Gothic, Black, Silver, Black),
        new(ItemId.RuffledMini, "Ruffled black mini", WearSlot.Bottom, 5, GarmentShape.RuffleMini, GarmentStyle.Gothic, Black, new("2a2a2e"), Black),
        new(ItemId.PinkPleated, "Pink pleated skirt", WearSlot.Bottom, 6, GarmentShape.PleatedSkirt, GarmentStyle.Kawaii, Blush, White, Blush),
        new(ItemId.PostalSkirt, "Postal-doll skirt", WearSlot.Bottom, 7, GarmentShape.LongPleated, GarmentStyle.Kawaii, new("f4f0e6"), new("5a3a22"), new("f4f0e6")),
        new(ItemId.Jeans, "Jeans", WearSlot.Bottom, 8, GarmentShape.Pants, GarmentStyle.Basic, new("3a5a8a"), new("c8a060"), new("2e4a72")),
        new(ItemId.CargoPants, "Black cargo pants", WearSlot.Bottom, 9, GarmentShape.Cargo, GarmentStyle.Gothic, Charcoal, new("2a2a30"), Silver),
        new(ItemId.HoloSkirt, "Holo skirt", WearSlot.Bottom, 10, GarmentShape.PleatedSkirt, GarmentStyle.Special, White, White, White, Finish.Holo),

        // ---- legs ----
        new(ItemId.GothStockings, "Black-white thigh-highs", WearSlot.Legs, 1, GarmentShape.StripedThighHigh, GarmentStyle.Gothic, Black, White, Black),
        new(ItemId.BeeStockings, "Bee stockings", WearSlot.Legs, 2, GarmentShape.StripedThighHigh, GarmentStyle.Kawaii, Black, new("f0b818"), Black),
        new(ItemId.PinkStockings, "Pink-white thigh-highs", WearSlot.Legs, 3, GarmentShape.StripedThighHigh, GarmentStyle.Kawaii, new("ff9fd0"), White, new("ff9fd0")),
        new(ItemId.BlackStockings, "Black thigh-highs", WearSlot.Legs, 4, GarmentShape.ThighHigh, GarmentStyle.Basic, Black, Black, Black),
        new(ItemId.Fishnets, "Fishnets", WearSlot.Legs, 5, GarmentShape.Fishnets, GarmentStyle.Gothic, Ink, Ink, Ink, Finish.Fishnet),
        new(ItemId.KneeSocks, "Kawaii knee socks", WearSlot.Legs, 6, GarmentShape.KneeSock, GarmentStyle.Kawaii, White, new("ff9fd0"), White),
        new(ItemId.RainbowStockings, "Rainbow stockings", WearSlot.Legs, 7, GarmentShape.ThighHigh, GarmentStyle.Special, White, White, White, Finish.Rainbow),

        // ---- feet ----
        new(ItemId.PlatformBoots, "Platform buckle boots", WearSlot.Feet, 1, GarmentShape.PlatformBoots, GarmentStyle.Gothic, Black, Silver, Black),
        new(ItemId.CombatBoots, "Combat boots", WearSlot.Feet, 2, GarmentShape.CombatBoots, GarmentStyle.Gothic, new("1c1a18"), new("3a3630"), new("1c1a18")),
        new(ItemId.PinkSneakers, "Pink sneakers", WearSlot.Feet, 3, GarmentShape.Sneakers, GarmentStyle.Kawaii, new("ff9fd0"), White, new("ff9fd0")),
        new(ItemId.WhiteSneakers, "White sneakers", WearSlot.Feet, 4, GarmentShape.Sneakers, GarmentStyle.Basic, new("f2f2ee"), new("d0d0d4"), new("f2f2ee")),
        new(ItemId.MaryJanes, "Mary Janes", WearSlot.Feet, 5, GarmentShape.MaryJanes, GarmentStyle.Kawaii, Black, White, Black),
        new(ItemId.DiscoPlatforms, "Disco platforms", WearSlot.Feet, 6, GarmentShape.PlatformBoots, GarmentStyle.Special, Silver, Silver, Silver, Finish.Disco),

        // ---- hands ----
        new(ItemId.LaceArmWarmers, "Lace arm warmers", WearSlot.Hands, 1, GarmentShape.ArmWarmers, GarmentStyle.Gothic, Ink, Ink, Ink, Finish.Lace),
        new(ItemId.FingerlessGloves, "Fingerless gloves", WearSlot.Hands, 2, GarmentShape.Fingerless, GarmentStyle.Gothic, new("18181a"), Silver, new("18181a")),
        new(ItemId.PawGloves, "Pink paw gloves", WearSlot.Hands, 3, GarmentShape.Paws, GarmentStyle.Kawaii, Rose, new("ff7ab8"), Rose),
        new(ItemId.StripedArmWarmers, "Striped arm warmers", WearSlot.Hands, 4, GarmentShape.StripedWarmers, GarmentStyle.Kawaii, new("ff9fd0"), White, new("ff9fd0")),
        new(ItemId.NeonGloves, "Neon gloves", WearSlot.Hands, 5, GarmentShape.Gloves, GarmentStyle.Special, new("30f0ff"), new("30f0ff"), new("30f0ff"), Finish.Neon),
    };

    private static readonly Dictionary<ItemId, Garment> ById = All.ToDictionary(g => g.Item);
    private static readonly Garment?[][] BySlot = BuildSlots();

    private static Garment?[][] BuildSlots()
    {
        var table = new Garment?[Outfit.SlotCount + 1][];
        for (int s = 0; s < table.Length; s++) table[s] = new Garment?[64];
        foreach (var g in All) table[(int)g.Slot][g.Code] = g;
        return table;
    }

    public static Garment? Get(ItemId id) => ById.GetValueOrDefault(id);

    public static Garment? ByCode(WearSlot slot, int code) =>
        slot == WearSlot.None || code <= 0 || code > 63 ? null : BySlot[(int)slot][code];

    /// <summary>
    /// <paramref name="c"/> carrying <paramref name="finish"/> in its alpha, 1 − id/255, for
    /// <c>shaders/avatar.gdshader</c>. Alpha 1 (no finish) is every other colour in the game.
    /// </summary>
    public static Color Fx(Color c, Finish finish) =>
        finish == Finish.None ? c : new Color(c.R, c.G, c.B, 1f - (int)finish / 255f);

    public static string SlotName(WearSlot slot) => slot switch
    {
        WearSlot.Head => "head",
        WearSlot.Eyes => "eyes",
        WearSlot.Face => "face",
        WearSlot.Ears => "ears",
        WearSlot.Neck => "neck",
        WearSlot.Top => "top",
        WearSlot.Bottom => "bottom",
        WearSlot.Legs => "legs",
        WearSlot.Feet => "feet",
        WearSlot.Hands => "hands",
        _ => "",
    };

    public static string FinishName(Finish f) => f switch
    {
        Finish.Rainbow => "rainbow",
        Finish.Disco => "disco ball",
        Finish.Galaxy => "galaxy",
        Finish.Holo => "holographic",
        Finish.Glitch => "glitch",
        Finish.Lava => "lava",
        Finish.Neon => "neon",
        _ => "",
    };

    /// <summary>Duplicate codes, codes out of range, or items with no row: what <c>--outfitcheck</c> fails on.</summary>
    public static List<string> Validate()
    {
        var bad = new List<string>();
        foreach (var group in All.GroupBy(g => (g.Slot, g.Code)).Where(x => x.Count() > 1))
            bad.Add($"{group.Key.Slot} code {group.Key.Code} used by {string.Join(", ", group.Select(g => g.Item))}");
        foreach (var g in All)
        {
            if (g.Code is < 1 or > 63) bad.Add($"{g.Item}: code {g.Code} out of 1..63");
            if (g.Slot == WearSlot.None) bad.Add($"{g.Item}: no slot");
            if (ItemDefs.Get(g.Item) is not { } def) bad.Add($"{g.Item}: no item row");
            else if (def.Slot != g.Slot) bad.Add($"{g.Item}: item slot {def.Slot} is not {g.Slot}");
            if (Outfit.Empty.With(g.Slot, g.Code)[g.Slot] != g) bad.Add($"{g.Item}: does not round-trip through Outfit");
        }
        return bad;
    }
}
