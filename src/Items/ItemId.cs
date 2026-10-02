namespace UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md), with the crafting rules.

/// <summary>
/// Every item in the game. Replicated as an int (<see cref="Player.FootPlayer.HeldItemId"/>) and
/// saved by name, so <b>append only, never reorder</b> — the same rule as <c>RideKind</c>.
/// </summary>
public enum ItemId
{
    None = 0,
    Binoculars = 1,
    Camera = 2,
    Gps = 3,
    SwissFlag = 4,
    EnergyBar = 5,
    WaterBottle = 6,

    // ---- scavenged (src/Loot) ----
    Bread = 7,
    CannedFood = 8,
    Apple = 9,
    Cheese = 10,
    Chocolate = 11,
    MineralWater = 12,
    Francs = 13,
    Bandage = 14,
    FirstAidKit = 15,
    ScrapMetal = 16,
    Plastic = 17,
    WoodPlanks = 18,
    Cloth = 19,
    Glass = 20,
    CopperWire = 21,
    Screws = 22,
    Rubber = 23,
    DuctTape = 24,
    Rope = 25,
    Stone = 26,
    SandBag = 27,
    Firewood = 28,
    RockSalt = 29,
    Coal = 30,
    BikeChain = 31,
    Tyre = 32,
    CarBattery = 33,
    Electronics = 34,
    FuelCan = 35,
    EnginePart = 36,

    // ---- hunting (src/Birds) ----
    Shotgun = 37,
    Shells = 38,

    // ---- occasions (src/Occasions) ----
    Candy = 39,
    Pumpkin = 40,
    CaramelApple = 41,
    Biberli = 42,
    Mandarin = 43,
    Grittibaenz = 44,
    Gluehwein = 45,
    WitchHat = 46,
    PumpkinHead = 47,
    SantaHat = 48,
    ReindeerAntlers = 49,

    // ---- the Polaroid camera (docs/notes/items/polaroid.md) ----
    /// <summary>A printed photo; which one is <see cref="ItemStack.Data"/> (the photo id).</summary>
    Photo = 50,
    // ---- optics (src/Items/SmartBinocularsHud) ----
    SmartBinoculars = 51,

    // ---- radio (src/Items/Radio*, src/Audio/Cd) ----
    Radio = 52,

    // ---- weapons (src/Items/Weapons.cs; Battle Royale, #177) ----
    Pistol = 53,
    Rifle = 54,
    HuntingRifle = 55,
    Knife = 56,
    Ammo9mm = 57,
    Ammo75 = 58,
    ArmorVest = 59,
    /// <summary>One flare: fired, it calls a Battle Royale supply drop where you stand (#198).</summary>
    FlareGun = 60,

    // ---- bags (docs/notes/items/bags.md): worn in the bag slot, each adds pack slots ----
    BeltPouch = 61,
    Handbag = 62,
    Backpack = 63,
    HikingPack = 64,

    // ---- clothes (#251, docs/notes/avatar/clothing.md): worn in a body slot; the looks are Avatar.Garments ----
    CatEarsBlack = 65, CatEarsPink = 66, CatHeadset = 67, GamerHeadset = 68, BunnyEars = 69,
    DevilHorns = 70, PinkBow = 71, LaceHeadband = 72, BlackBeanie = 73, RainbowCatEars = 74, NeonHeadset = 75,
    RoundGlasses = 76, HeartShades = 77, BlackShades = 78, GothShades = 79, StarGlasses = 80, DiscoShades = 81,
    MaskUwu = 82, MaskSwirlA = 83, MaskSwirlW = 84, MaskFang = 85, MaskTongue = 86, MaskBlack = 87,
    MaskSkull = 88, MaskCat = 89, HoloMask = 90,
    SilverStuds = 91, SilverHoops = 92, IndustrialSet = 93, SpikePiercings = 94, StarStuds = 95,
    SpikedChoker = 96, HeartChoker = 97, ChainNecklace = 98, BellCollar = 99,
    WhiteTee = 100, BandTee = 101, PastelTee = 102, GreenPolo = 103, NavyPolo = 104, WhiteMarcel = 105,
    BlackMarcel = 106, BuckleCorset = 107, ChainCropTop = 108, PinkCropTop = 109, StripedLongsleeve = 110,
    PastelHoodie = 111, PostalJacket = 112, GothicRobe = 113, LolitaDress = 114, WitchRobe = 115,
    RainbowTee = 116, DiscoTop = 117, GalaxyHoodie = 118, LavaTee = 119, GalaxyDress = 120, GlitchTee = 121,
    JoggingShorts = 122, BlackShorts = 123, TartanSkirt = 124, SlitMaxiSkirt = 125, RuffledMini = 126,
    PinkPleated = 127, PostalSkirt = 128, Jeans = 129, CargoPants = 130, HoloSkirt = 131,
    GothStockings = 132, BeeStockings = 133, PinkStockings = 134, BlackStockings = 135, Fishnets = 136,
    KneeSocks = 137, RainbowStockings = 138,
    PlatformBoots = 139, CombatBoots = 140, PinkSneakers = 141, WhiteSneakers = 142, MaryJanes = 143, DiscoPlatforms = 144,
    LaceArmWarmers = 145, FingerlessGloves = 146, PawGloves = 147, StripedArmWarmers = 148, NeonGloves = 149,

    // ---- building (#274, docs/notes/build/building.md) ----
    /// <summary>Held, it builds structure pieces from the pack's materials (<see cref="ItemUse.Build"/>).</summary>
    Hammer = 150,

    // ---- fire and placeables (#272, docs/notes/crafting/campfire.md) ----
    /// <summary>Cooked at a fire (#272).</summary>
    Fondue = 151,
    HotChocolate = 152,
    ToastedBread = 153,
    /// <summary>Placed: a fire that burns 20 minutes, a cooking station (<c>PlacedKind.Campfire</c>).</summary>
    Campfire = 154,
    /// <summary>Held: a light in the hand, seen by everyone (it follows the replicated held item).</summary>
    Torch = 155,
    /// <summary>Placed: a workbench station anywhere (<c>PlacedKind.FieldWorkbench</c>).</summary>
    FieldWorkbench = 156,

    // 160-179 are taken by the shops / vending machines (#273)

    // ---- gadgets (#275, docs/notes/build/gadgets.md): placed, to get up high or to hide ----
    Zipline = 180, RopeLadder = 181, Trampoline = 182, LaunchPad = 183, CamoNet = 184, HayHideout = 185,
}

/// <summary>What an item is for, independent of what Use does: drives loot pools and, later, trade.</summary>
public enum ItemCategory { Gear, Food, Water, Money, Medical, Scrap, Mineral, Part, Cosmetic, Clothing }

/// <summary>What pressing Use does with the item in hand.</summary>
public enum ItemUse
{
    /// <summary>Held to the eye with Aim; Use does nothing.</summary>
    Optic,
    /// <summary>Aim frames, Use takes a picture.</summary>
    Photo,
    /// <summary>Shows a readout while held.</summary>
    Readout,
    /// <summary>Use plants one in the ground in front of you, or picks a planted one back up.</summary>
    Place,
    /// <summary>Use eats or drinks one, for health.</summary>
    Consume,
    /// <summary>A material: kept for trading and building; Use does nothing.</summary>
    Material,
    /// <summary>Aim shoulders it, Use fires one shell (<see cref="ItemController.Fire"/>).</summary>
    Shoot,
    /// <summary>Use puts it on in its body slot (<see cref="ItemDef.Slot"/>, <see cref="Inventory.Wear"/>), swapping with what was there.</summary>
    Wear,
    /// <summary>A printed photo: Use looks at it, Aim + Use (or the stick key) sticks it where you look.</summary>
    Print,
    /// <summary>Use throws it into the world, where it stays as a thing (<see cref="RadioManager"/>).</summary>
    Throw,
    /// <summary>Use swings it at whoever stands in front (<see cref="Weapons"/>).</summary>
    Melee,
    /// <summary>Use puts it on: body armour, used up as it absorbs hits (<see cref="Player.FootPlayer.Armor"/>).</summary>
    Armor,
    /// <summary>Use fires it into the sky: a flare that calls a supply drop (<see cref="ItemId.FlareGun"/>).</summary>
    Signal,
    /// <summary>Worn in the bag slot, it adds <see cref="ItemDef.PackSlots"/> to the pack (<see cref="Inventory.Bag"/>).</summary>
    Bag,
    /// <summary>Use builds the piece the ghost shows; Aim + Use takes your own piece back (<c>Build.BuildTool</c>).</summary>
    Build,
    /// <summary>Use sets the gadget down where the ghost shows; Aim + Use takes your own back (<c>Build.GadgetTool</c>).</summary>
    Gadget,
}
