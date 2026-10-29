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

    // ---- shops and PAUSA vending machines (#273, docs/notes/loot/shops.md): never crafted ----
    /// <summary>Only from a PAUSA vending machine, like the three after it.</summary>
    IceTea = 160,
    Crisps = 161,
    GummyBears = 162,
    IsotonicDrink = 163,
    /// <summary>Sold at hardware, sport and gun shops; carried, a tree gives one more log (<c>Loot.Gathering</c>).</summary>
    SwissArmyKnife = 164,

    // ---- gadgets (#275, docs/notes/build/gadgets.md): placed, to get up high or to hide ----
    Zipline = 180, RopeLadder = 181, Trampoline = 182, LaunchPad = 183, CamoNet = 184, HayHideout = 185,

    // ---- Swiss match items (#478, Items/SwissItems): found in Battle Royale loot ----
    /// <summary>Blown: heard far, it shows the blower who is near and everyone else where it was blown.</summary>
    Alphorn = 190,
    /// <summary>Shared: everyone standing round it is fed.</summary>
    FonduePot = 191,
    /// <summary>Thrown: a cloud that hides who is inside from the radar.</summary>
    SmokeCanister = 192,

    /// <summary>A fallen squad player's tag (#480): used at a Postauto stop, it brings them back (<c>BrManager.Recall</c>).</summary>
    Dogtag = 193,

    // ---- fishing (#493, docs/notes/items/fishing.md, Items/Fishing) ----
    /// <summary>Use (hold) casts, Use strikes a bite, hold Use reels in; Aim winds the line back in.</summary>
    FishingRod = 194,
    /// <summary>Bread dough on the hook: the peaceful fish take it; one goes with each fish that takes it.</summary>
    DoughBait = 195,
    /// <summary>A spinning lure: the hunters take it; lost only when the line snaps.</summary>
    Spinner = 196,
    /// <summary>Cooked at a fire from two perch (#493).</summary>
    PerchFillets = 197,
    GrilledFish = 198,
    FishSoup = 199,
    /// <summary>Caught (never crafted): one item per species that may be kept, <c>FishCatalog</c>.</summary>
    BrownTrout = 200, LakeTrout = 201, RainbowTrout = 202, BrookTrout = 203, ArcticChar = 204, Namaycush = 205,
    Grayling = 206, Whitefish = 207, Perch = 208, Pike = 209, Zander = 210, Wels = 211, Burbot = 212,
    LargemouthBass = 213, Carp = 214, Tench = 215, Roach = 216, Rudd = 217, Bream = 218, Chub = 219,
    Barbel = 220, Agone = 221, RoundGoby = 222,

    // ---- toys (#501) ----
    /// <summary>The plush shark, out of a bin at any of the nine IKEA stores (<c>Interiors.Landmarks</c>).</summary>
    Blahaj = 223,

    // ---- farming (#494, docs/notes/farming/): seeds, harvests, what is made of them ----
    /// <summary>Seeds: sown by hand on a ploughed cell or by a seed drill (<c>Farming.FarmTables.SeedFor</c>).</summary>
    WheatSeed = 300, BarleySeed = 301, MaizeSeed = 302, SeedPotato = 303, RapeSeed = 304,
    SunflowerSeed = 305, SugarBeetSeed = 306, VegetableSeeds = 307, PeaSeed = 308,
    /// <summary>Harvests: a ripe cell by hand, or a combine / tractor's tank unloaded (<c>Farming.FarmTables.YieldOf</c>).</summary>
    Wheat = 310, Barley = 311, Maize = 312, Potato = 313, Rapeseed = 314,
    SunflowerSeeds = 315, SugarBeet = 316, Carrot = 317, HayBale = 318, Peas = 319,
    /// <summary>Made from the harvests (<c>Crafting.Recipes</c>).</summary>
    Flour = 320, RapeseedOil = 321, Sugar = 322, MaizeMeal = 323,
    BakedPotato = 324, Roesti = 325, Polenta = 326, Popcorn = 327, VegetableSoup = 328, Raclette = 329,
    /// <summary>Held: Use tills the cell ahead (plough by hand).</summary>
    Hoe = 335,
    /// <summary>Used on a sown cell (and the cells round it): the crop ripens sooner.</summary>
    Fertiliser = 336,
    /// <summary>Placed: a self-service farm stand with an honesty box (<c>PlacedKind.FarmStand</c>, <c>Farming.FarmStands</c>).</summary>
    FarmStand = 340,

    // ---- the barracks (#716, docs/notes/items/barracks-items.md): a night in the army, and the recruit's uniform ----
    /// <summary>Held, a fanned hand of five; nothing to use, it just looks right (shops: kiosk).</summary>
    PlayingCards = 350,
    /// <summary>Held, a short stack of coloured chips.</summary>
    PokerChips = 351,
    /// <summary>A 33 cl brown bottle: drunk, it plays the mouth pose like the other drinks.</summary>
    BeerBottle = 352,
    /// <summary>The army's three-part mess tin.</summary>
    Gamelle = 353,
    /// <summary>Garments (Avatar.Garments): the Swiss camouflage TAZ 90, and an olive army T-shirt.</summary>
    TazJacket = 354, TazTrousers = 355, ArmyTee = 356,
    /// <summary>Garments (#822): a building site's workwear, found in its office: a hard hat and a hi-vis vest.</summary>
    HardHat = 357, HiVisVest = 358,
    /// <summary>Garments (#880, Avatar.Aliens): antennae on a headband (green, silver, a tall pair) and a silver suit.</summary>
    AlienAntennae = 359, SilverAntennae = 360, LongAntennae = 361, SilverSuit = 362,

    /// <summary>An admin's traffic tool (#971): places, edits and removes traffic generators (<see cref="ItemUse.TrafficTool"/>); from the debug menu only.</summary>
    TrafficTool = 380,

    // ---- the Adventure rewards (#1227, docs/notes/adventure/rewards.md); 400-419 are this plan's, navigation took 420-421 ----
    /// <summary>Rock crystal: the Adventure crafting material, from chests and duplicates (<c>Adventure.Rewards.ChestLoot</c>).</summary>
    Bergkristall = 400,
    /// <summary>Held, it beeps faster nearer the closest unopened chest or unpicked edelweiss within 250 m; Use pings a direction (<see cref="ItemUse.Detector"/>).</summary>
    TreasureDetector = 401,
    /// <summary>Planted, a waypoint of one's own (<c>PlacedKind.TravelFlag</c>); what travel to it means is the navigation plan's.</summary>
    TravelFlag = 402,
    /// <summary>Thrown, an updraft column where it lands for 20 s (<see cref="ItemUse.Gust"/>, <c>ItemEventKind.Gust</c>).</summary>
    BottledGust = 403,
    /// <summary>Garments (Avatar.Garments): the Alpine set, found in chests only.</summary>
    Sennenkaeppi = 404, EdelweissShirt = 405, GuideHat = 406, GuideBreeches = 407, CrossBandana = 408, AlpineSocks = 409, AlpineGloves = 410,
    // ---- playing together (#1266, docs/notes/adventure/together.md); 480-499 are this plan's, 486-488 the alcohol plan's (#1271) ----
    /// <summary>A bottle of Fendant (Valais white wine), a glass a drink: the shared pot's <c>Warm</c> (<see cref="ItemDef.DrinkMl"/>, <see cref="ItemDef.Abv"/>).</summary>
    WhiteWine = 480,
    /// <summary>Kirsch, a shot a drink: the shared pot's <c>Hearty</c>.</summary>
    Kirsch = 481,
    // ---- alcohol (#1271, docs/notes/adventure/alcohol.md) ----
    /// <summary>An electronic breathalyzer, reusable: blow for 5 s and its screen reads the per mille and mg/L (<see cref="ItemUse.Breath"/>); held out to a player, it tests them if they agree.</summary>
    Breathalyzer = 486,
    /// <summary>A single-use crystal-tube test: one blow, green (under 0.5 per mille) or amber (over), then used up (<see cref="ItemUse.Breath"/>).</summary>
    BreathTestSingle = 487,
    /// <summary>An alcohol-free beer (0.5 %), the driver's choice: drunk like a beer.</summary>
    BeerAlcoholFree = 488,
    /// <summary>Garments (Avatar.Garments, <c>GarmentStyle.Together</c>): a mentored newcomer's first summit, glacier, ride and build.</summary>
    ErstbesteigungCap = 482, GletscherScarf = 483, MitfahrerGloves = 484, ZimmermannCap = 485,
    /// <summary>Garments: the mentor's own for each milestone, and the tenth punch card's felt hat.</summary>
    BergfuehrerCap = 489, SeilschaftScarf = 490, FahrlehrerGloves = 491, PolierHat = 492, StammgastHat = 493,
    // ---- the travel network (#1226, docs/notes/adventure/fares.md); 400-419 belong to the rewards plan ----
    /// <summary>The GA travelcard: rail, bus and ship trips free, cable cars half, travel from anywhere. The server trusts <c>TravelBook.HasGa</c>; the item is its picture.</summary>
    GaCard = 420,
    /// <summary>The half-fare travelcard: every fare halved (<c>TravelBook.HasHalfFare</c>).</summary>
    HalfFare = 421,

    // ---- Adventure errands (#1267, docs/notes/adventure/errands.md); 430-459 are this plan's (adventure-phase3) ----
    /// <summary>Cargo a Postkarte card hands out (never sold): the PostBus mail sack, the ferry crate, the hut supplies, a blank postcard.</summary>
    MailSack = 430, FerryCrate = 431, SupplyPack = 432, Postcard = 433,
    /// <summary>Cantonal specialties (<c>Adventure.Errands.Specialties</c>): food, eaten or sold; the cards' trade goods (Gruyère is <see cref="Cheese"/>).</summary>
    RacletteCheese = 434, BaslerLaeckerli = 435, EngadinerNusstorte = 436, Marroni = 437, ZugerKirschtorte = 438, Appenzeller = 439,
    TeteDeMoine = 440, StGallerBratwurst = 441, SaucissonVaudois = 442, ZuercherTirggel = 443, Sbrinz = 444,

    // ---- Versteckis (#1268, docs/notes/adventure/versteckis.md); 460-479 are this plan's (adventure-phase3) ----
    /// <summary>The round kit (<see cref="ItemUse.Round"/>), lent for a game only: the seeker's tag ("Gfunde!") and sniff ("Schnüffle").</summary>
    VsTag = 460, VsSniff = 461,
    /// <summary>The hiders' kit: a decoy ("Lockvogel"), the sprint burst ("Gump"), the prop's voice ("Muuh"), and the Disguise radial.</summary>
    VsDecoy = 462, VsSprint = 463, VsMoo = 464, VsDisguise = 465,
    /// <summary>Garments (Avatar.Garments), earned by playing: the gnome's Zipfelmütze (10 games) and the Treichel cowbell (30 rounds survived).</summary>
    Zipfelmuetze = 466, Treichel = 467,

    // ---- bowling (#1323, docs/notes/terrain/bowling-play.md) ----
    /// <summary>Hire shoes (Avatar.Garments): worn while signed in at a bowling centre's shoe counter, never owned.</summary>
    BowlingShoes = 540,

    // ---- Festivals (#1269, docs/notes/occasions/festival-framework.md); 500-539 are this plan's (adventure-phase3) ----
    /// <summary>The wrestler's jute over-trousers (#1269 Schwingen): worn in the Bottom slot, from the Älplerfest's chests and stalls.</summary>
    Schwingerhosen = 500,
    // ---- the Christmas markets (#1348, docs/notes/occasions/festival-christmas.md) ----
    /// <summary>The market's mulled wine in a mug, bought with Batzen: 2 dl, 10 % (<see cref="ItemDef.DrinkMl"/>, <see cref="ItemDef.Abv"/>), warm for 4 minutes. The found <see cref="Gluehwein"/> stays an alcohol-free treat.</summary>
    GluehweinMug = 501,
    /// <summary>A glass Christmas bauble, bought at the ornaments stall: worth nothing but the look in the hand.</summary>
    ChristmasBauble = 502,
    // ---- the Escalade (#1351, docs/notes/occasions/festival-escalade.md) ----
    /// <summary>A shard of the Marmite's chocolate shell, handed round when it is smashed: sweet food.</summary>
    MarmiteChocolate = 503,
    /// <summary>A bowl of the Escalade's vegetable soup from the pot that feeds the party: warm for 3 minutes.</summary>
    SoupeEscalade = 504,
    // ---- the Chalandamarz (#1356, docs/notes/occasions/festival-chalandamarz.md): 515-519 (the procession's Nusstorte is the existing 436) ----
    /// <summary>A child's hand Treichel from the Chalandamarz, the first march of the day: worth nothing but the sound (the worn Treichel is <see cref="Treichel"/>).</summary>
    ChalandaTreichel = 515,
    // Reserved, nothing uses them yet: the festival stall's wares (flower crown, Edelweiss shirts, Sennenkaeppi variants, Treichel for
    // the wall, fireworks), masks and the Schwingerhosen come with the parts that build their games.
}

/// <summary>What an item is for, independent of what Use does: drives loot pools and, later, trade.</summary>
/// <remarks>Append-only. <c>Produce</c> (#494): seeds and raw harvests, bought and sold at a farm co-op.</remarks>
public enum ItemCategory { Gear, Food, Water, Money, Medical, Scrap, Mineral, Part, Cosmetic, Clothing, Produce }

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
    /// <summary>Use blows it, a few seconds (<see cref="ItemId.Alphorn"/>, #478); it is not used up.</summary>
    Horn,
    /// <summary>Use sets it out for everyone near, after a few seconds (<see cref="ItemId.FonduePot"/>, #478).</summary>
    Share,
    /// <summary>Use throws it ahead, where it smokes (<see cref="ItemId.SmokeCanister"/>, #478).</summary>
    Smoke,
    /// <summary>Use, at a Postauto stop, recalls a fallen team-mate (<see cref="ItemId.Dogtag"/>, #480).</summary>
    Recall,
    /// <summary>Hold Use to cast, Use on a bite strikes, hold Use reels in, Aim winds in (<see cref="ItemId.FishingRod"/>, #493).</summary>
    Fish,
    /// <summary>Use works the field cell ahead (#494, <c>Farming.HandFarming</c>): a hoe tills, a seed sows, fertiliser feeds a sown crop.</summary>
    Farm,
    /// <summary>Use places a traffic generator on the road aimed at, or opens the one aimed at (<see cref="TrafficTool"/>, #971).</summary>
    TrafficTool,
    /// <summary>Held, it beeps nearer the closest reward; Use pings a direction (<see cref="ItemId.TreasureDetector"/>, #1227).</summary>
    Detector,
    /// <summary>Use throws it ahead: an updraft where it lands (<see cref="ItemId.BottledGust"/>, #1227).</summary>
    Gust,
    /// <summary>A Versteckis round tool (#1268): Use asks the game's server for it (tag, sniff, decoy, sprint, moo, the Disguise radial).</summary>
    Round,
    /// <summary>Use blows into it for 5 s (#1271): the electronic breathalyzer is kept, the single-use test is used up.</summary>
    Breath,
}
