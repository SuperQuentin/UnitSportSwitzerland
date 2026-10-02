using Godot;
using UnitSport.Avatar;

namespace UnitSport.Items;

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
}

/// <summary>What an item is for, independent of what Use does: drives loot pools and, later, trade.</summary>
public enum ItemCategory { Gear, Food, Water, Money, Medical, Scrap, Mineral, Part, Cosmetic }

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
    /// <summary>Use puts it on, or takes it off (a hat — <see cref="Inventory.Worn"/>).</summary>
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
}

/// <summary>
/// One kind of item: a row of data plus how it looks in the hand. Behaviour lives in
/// <see cref="ItemController"/>, keyed on <see cref="Use"/>, so adding an item of an existing kind
/// is a line in <see cref="ItemDefs.All"/> and a mesh.
/// </summary>
public sealed record ItemDef(
    ItemId Id,
    string Name,
    string Blurb,
    ItemUse Use,
    int MaxStack,
    Color Tint,
    string Glyph,
    float Heal = 0f,
    ItemCategory Category = ItemCategory.Gear,
    /// <summary>Worth in Swiss francs, for trade later on.</summary>
    float Value = 0f,
    /// <summary>A bag's extra pack slots while it is worn (<see cref="ItemUse.Bag"/>).</summary>
    int PackSlots = 0);

public static class ItemDefs
{
    public static readonly ItemDef[] All =
    {
        new(ItemId.Binoculars, "Binoculars", "Hold {aim_item} to look through them. 8x.",
            ItemUse.Optic, 1, new Color(0.30f, 0.38f, 0.26f), "BN"),
        new(ItemId.SmartBinoculars, "Smart binoculars", "Hold them at a building's door, or inside: they read out what its containers can hold and the chance of finding each item.",
            ItemUse.Readout, 1, new Color(0.20f, 0.42f, 0.50f), "SB", 0, ItemCategory.Gear, 250f),
        new(ItemId.Camera, "Camera", "A Polaroid. Hold {aim_item} to frame, {use_item} to take a photo: it prints, develops, and goes in your pack.",
            ItemUse.Photo, 1, new Color(0.18f, 0.18f, 0.20f), "CM"),
        new(ItemId.Gps, "GPS", "Shows your LV95 coordinates, altitude and heading while held.",
            ItemUse.Readout, 1, new Color(0.95f, 0.78f, 0.12f), "GP"),
        new(ItemId.SwissFlag, "Swiss flag", "{use_item} plants it where you look; {use_item} on a planted flag picks it back up.",
            ItemUse.Place, 5, new Color(0.85f, 0.08f, 0.10f), "FL"),
        new(ItemId.EnergyBar, "Energy bar", "{use_item} to eat. Restores 35 health.",
            ItemUse.Consume, 10, new Color(0.95f, 0.50f, 0.10f), "EB", Heal: 35f, ItemCategory.Food, 3f),
        new(ItemId.WaterBottle, "Water bottle", "{use_item} to drink. Restores 15 health.",
            ItemUse.Consume, 5, new Color(0.25f, 0.55f, 0.95f), "WB", Heal: 15f, ItemCategory.Water, 2f),

        Eat(ItemId.Bread, "Bread", 5, "#c8914a", "BR", 20, ItemCategory.Food, 3),
        Eat(ItemId.CannedFood, "Canned food", 10, "#9aa3ab", "CN", 30, ItemCategory.Food, 4),
        Eat(ItemId.Apple, "Apple", 10, "#c8282a", "AP", 10, ItemCategory.Food, 1),
        Eat(ItemId.Cheese, "Gruyère", 5, "#f0d060", "GR", 35, ItemCategory.Food, 8),
        Eat(ItemId.Chocolate, "Chocolate", 10, "#5a3220", "CH", 15, ItemCategory.Food, 3),
        Eat(ItemId.MineralWater, "Mineral water", 5, "#6ec8e8", "MW", 25, ItemCategory.Water, 3),
        new(ItemId.Francs, "Swiss francs", "Money. Never takes a slot: it is counted as cash, and deposited to your account at a bank counter.",
            ItemUse.Material, 9999, new Color(0.80f, 0.70f, 0.35f), "CHF", 0, ItemCategory.Money, 1f),
        Eat(ItemId.Bandage, "Bandage", 10, "#f2eee6", "BD", 25, ItemCategory.Medical, 5),
        Eat(ItemId.FirstAidKit, "First-aid kit", 3, "#d02828", "+", 100, ItemCategory.Medical, 30),

        Mat(ItemId.ScrapMetal, "Scrap metal", 50, "#7c8088", "SM", ItemCategory.Scrap, 1),
        Mat(ItemId.Plastic, "Plastic", 50, "#e0e4e8", "PL", ItemCategory.Scrap, 0.5f),
        Mat(ItemId.WoodPlanks, "Wood planks", 30, "#a0703c", "WP", ItemCategory.Scrap, 1),
        Mat(ItemId.Cloth, "Cloth", 30, "#8a6aa8", "CL", ItemCategory.Scrap, 1),
        Mat(ItemId.Glass, "Glass", 30, "#a8d8d0", "GL", ItemCategory.Scrap, 1),
        Mat(ItemId.CopperWire, "Copper wire", 30, "#c87838", "CW", ItemCategory.Scrap, 4),
        Mat(ItemId.Screws, "Screws & bolts", 99, "#58606a", "SB", ItemCategory.Scrap, 0.2f),
        Mat(ItemId.Rubber, "Rubber", 30, "#262628", "RB", ItemCategory.Scrap, 2),
        Mat(ItemId.DuctTape, "Duct tape", 10, "#8c9098", "DT", ItemCategory.Scrap, 3),
        Mat(ItemId.Rope, "Rope", 10, "#c8b078", "RP", ItemCategory.Scrap, 4),

        Mat(ItemId.Stone, "Stone", 50, "#8a8a82", "ST", ItemCategory.Mineral, 0.2f),
        Mat(ItemId.SandBag, "Gravel bag", 20, "#c8b48a", "GB", ItemCategory.Mineral, 1),
        Mat(ItemId.Firewood, "Firewood", 30, "#6e4a28", "FW", ItemCategory.Mineral, 1),
        Mat(ItemId.RockSalt, "Rock salt", 20, "#e8e0e8", "SA", ItemCategory.Mineral, 2),
        Mat(ItemId.Coal, "Coal", 30, "#1c1c1e", "CO", ItemCategory.Mineral, 2),

        Mat(ItemId.BikeChain, "Bike chain", 5, "#4a4e54", "BC", ItemCategory.Part, 15),
        Mat(ItemId.Tyre, "Tyre", 4, "#202022", "TY", ItemCategory.Part, 20),
        Mat(ItemId.CarBattery, "Car battery", 2, "#2a4a8a", "BA", ItemCategory.Part, 40),
        Mat(ItemId.Electronics, "Electronics", 20, "#2a7a3a", "EL", ItemCategory.Scrap, 6),
        Mat(ItemId.FuelCan, "Fuel can", 3, "#c82a1e", "FC", ItemCategory.Part, 25),
        Mat(ItemId.EnginePart, "Engine part", 3, "#6a5a4a", "EP", ItemCategory.Part, 60),

        new(ItemId.Shotgun, "Shotgun", "{aim_item} to shoulder it, {use_item} to fire one shell. Game birds only, and only in season. {bird_journal} opens the field journal.",
            ItemUse.Shoot, 1, new Color(0.40f, 0.27f, 0.16f), "SG", 0, ItemCategory.Gear, 400f),
        new(ItemId.Shells, "Shotgun shells", "Ammunition for the shotgun.",
            ItemUse.Material, 50, new Color(0.70f, 0.16f, 0.12f), "SH", 0, ItemCategory.Gear, 1f),

        // occasions (#18): treats found in loot and the hunt while one runs, and the hats
        Eat(ItemId.Candy, "Candy", 20, "#e8702a", "SW", 5, ItemCategory.Food, 1),
        Eat(ItemId.Pumpkin, "Pumpkin", 5, "#e07818", "PU", 20, ItemCategory.Food, 3),
        Eat(ItemId.CaramelApple, "Caramel apple", 5, "#b8581e", "CP", 15, ItemCategory.Food, 2),
        Eat(ItemId.Biberli, "Biberli", 10, "#8a5a2a", "BI", 15, ItemCategory.Food, 2),
        Eat(ItemId.Mandarin, "Mandarin", 10, "#f08a18", "MA", 8, ItemCategory.Food, 1),
        Eat(ItemId.Grittibaenz, "Grittibänz", 5, "#d8a060", "GZ", 25, ItemCategory.Food, 4),
        Eat(ItemId.Gluehwein, "Glühwein", 5, "#8a1a2a", "GW", 20, ItemCategory.Water, 4),
        Hat(ItemId.WitchHat, "Witch hat", "#3a2250", "WH"),
        Hat(ItemId.PumpkinHead, "Pumpkin head", "#e07818", "PH"),
        Hat(ItemId.SantaHat, "Santa hat", "#c81e24", "SH"),
        Hat(ItemId.ReindeerAntlers, "Reindeer antlers", "#7a5230", "RA"),

        new(ItemId.Photo, "Photo", "A Polaroid you took. {use_item} to look at it; {aim_item} + {use_item} sticks it on a wall or the ground, {use_item} on it again takes it back.",
            ItemUse.Print, 1, new Color(0.96f, 0.95f, 0.90f), "PH"),
        // radio (#104): thrown into the world, plays burned CDs for whoever stands near
        new(ItemId.Radio, "Radio", "{use_item} opens it in your hand; put away, it rides on your back and keeps playing. {aim_item} + {use_item} throws it (it hurts whoever it hits). Lying in the world: point at it, {use_item} takes it in hand, {interact_mount} opens it to play a CD.",
            ItemUse.Throw, 1, new Color(0.16f, 0.17f, 0.19f), "RD", 0, ItemCategory.Gear, 80f),

        // weapons (#178): they hurt players only while the server allows it (/pvp, a Battle Royale match)
        new(ItemId.Pistol, "Pistol", "{aim_item} to raise it, {use_item} to fire. 9 mm.",
            ItemUse.Shoot, 1, new Color(0.18f, 0.18f, 0.20f), "PI", 0, ItemCategory.Gear, 300f),
        new(ItemId.Rifle, "Assault rifle", "{aim_item} to shoulder it, {use_item} to fire. 7.5 mm, the army's Stgw.",
            ItemUse.Shoot, 1, new Color(0.24f, 0.28f, 0.20f), "AR", 0, ItemCategory.Gear, 900f),
        new(ItemId.HuntingRifle, "Hunting rifle", "{aim_item} looks through the scope, {use_item} fires. 7.5 mm, one shot at a time.",
            ItemUse.Shoot, 1, new Color(0.42f, 0.28f, 0.16f), "HR", 0, ItemCategory.Gear, 1200f),
        new(ItemId.Knife, "Army knife", "{use_item} to stab whoever stands in front of you.",
            ItemUse.Melee, 1, new Color(0.80f, 0.10f, 0.12f), "KN", 0, ItemCategory.Gear, 40f),
        new(ItemId.Ammo9mm, "9 mm rounds", "Ammunition for the pistol.",
            ItemUse.Material, 90, new Color(0.80f, 0.66f, 0.30f), "9M", 0, ItemCategory.Gear, 0.5f),
        new(ItemId.Ammo75, "7.5 mm rounds", "Ammunition for the assault rifle and the hunting rifle.",
            ItemUse.Material, 90, new Color(0.70f, 0.52f, 0.22f), "75", 0, ItemCategory.Gear, 1f),
        new(ItemId.ArmorVest, "Armour vest", "{use_item} to put it on: it takes half of every hit until it has soaked up 50.",
            ItemUse.Armor, 1, new Color(0.30f, 0.34f, 0.24f), "AV", 0, ItemCategory.Gear, 200f),
        new(ItemId.FlareGun, "Flare gun", "{use_item} fires its one flare into the sky: in a Battle Royale, a supply drop comes down where you stand. Everyone sees the flare.",
            ItemUse.Signal, 1, new Color(0.95f, 0.45f, 0.10f), "FG", 0, ItemCategory.Gear, 60f),
        // bags (#208): found in houses, worn in the bag slot, one row of the pack per 9 slots
        Bag(ItemId.BeltPouch, "Belt pouch", "#6a5a3a", "BP", 9, 15),
        Bag(ItemId.Handbag, "Handbag", "#8a2a3a", "HB", 18, 40),
        Bag(ItemId.Backpack, "Backpack", "#2a5a8a", "BK", 27, 70),
        Bag(ItemId.HikingPack, "Hiking backpack", "#c8602a", "HK", 36, 150),
    };

    private static ItemDef Bag(ItemId id, string name, string tint, string glyph, int slots, float value) =>
        new(id, name, $"Wear it in the bag slot for {slots} more pack slots. {{use_item}} or a click on the bag slot puts it on.",
            ItemUse.Bag, 1, new Color(tint), glyph, 0, ItemCategory.Gear, value, slots);

    private static ItemDef Eat(ItemId id, string name, int stack, string tint, string glyph, float heal,
        ItemCategory category, float value) =>
        new(id, name, $"{{use_item}} to {(category == ItemCategory.Water ? "drink" : category == ItemCategory.Medical ? "apply" : "eat")}. Restores {heal:F0} health.",
            ItemUse.Consume, stack, new Color(tint), glyph, heal, category, value);

    private static ItemDef Hat(ItemId id, string name, string tint, string glyph) =>
        new(id, name, "{use_item} to put it on, or take it off. Others see you wearing it.",
            ItemUse.Wear, 1, new Color(tint), glyph, 0, ItemCategory.Cosmetic, 10);

    private static ItemDef Mat(ItemId id, string name, int stack, string tint, string glyph,
        ItemCategory category, float value) =>
        new(id, name, category == ItemCategory.Part
                ? "A vehicle part. Keep it for building or trading."
                : "A material. Keep it for building or trading.",
            ItemUse.Material, stack, new Color(tint), glyph, 0, category, value);

    private static readonly Dictionary<ItemId, ItemDef> ById = All.ToDictionary(d => d.Id);

    public static ItemDef? Get(ItemId id) => ById.GetValueOrDefault(id);

    /// <summary>
    /// Aim + Use throws it (<see cref="ThrowAim"/>): anything whose Aim means nothing else. Optics,
    /// cameras and guns aim, a print aims where it sticks, a flag where it is planted, and a GPS or
    /// money is not something to lob at a hillside.
    /// </summary>
    public static bool Throwable(ItemDef? def) =>
        def != null && def.Id != ItemId.Francs && def.Use is ItemUse.Throw or ItemUse.Consume or ItemUse.Material or ItemUse.Wear;

    // ------------------------------------------------------------------------------------
    // meshes
    // ------------------------------------------------------------------------------------

    private static readonly Dictionary<ItemId, ArrayMesh> HandMeshes = new();
    private static ArrayMesh? _foreEnd;

    /// <summary>The shotgun's slide handle, origin where it sits at rest (the viewmodel and the hand slide it along Z to pump).</summary>
    public static ArrayMesh ShotgunForeEnd()
    {
        if (_foreEnd != null) return _foreEnd;
        var s = new MeshScratch();
        s.Box(new Vector3(0, -0.01f, 0.24f), new Vector3(0.04f, 0.035f, 0.26f), new Color(0.40f, 0.26f, 0.15f));
        return _foreEnd = s.Build();
    }

    private static ArrayMesh? _plantedFlag;
    private static StandardMaterial3D? _material;

    /// <summary>The same shaded vertex-colour material the figures use, so an item is lit like the hand holding it.</summary>
    public static StandardMaterial3D Material => _material ??= HumanMeshBuilder.Material();

    /// <summary>
    /// The material a held item needs instead of the shared vertex-colour <see cref="Material"/>, or
    /// null for that one: items drawn from a texture, which may depend on the stack's
    /// <see cref="ItemStack.Data"/> (a photo shows its own print).
    /// </summary>
    public static Material? HandMaterial(ItemId id, string? data) =>
        id == ItemId.Photo ? PhotoVisuals.Material(data) : null;

    /// <summary>
    /// The item as held: origin at the grip, pointing forward (−Z once built, like every
    /// <see cref="MeshScratch"/> mesh). Real sizes — a 0.12 m camera, not a prop.
    /// </summary>
    public static ArrayMesh? HandMesh(ItemId id)
    {
        if (id == ItemId.None) return null;
        if (HandMeshes.TryGetValue(id, out var cached)) return cached;

        // a textured card (its material: HandMaterial), not a vertex-coloured MeshScratch
        if (id == ItemId.Photo) return HandMeshes[id] = PhotoVisuals.HeldCard;

        var s = new MeshScratch();
        switch (id)
        {
            case ItemId.Binoculars:
            {
                var body = new Color(0.22f, 0.28f, 0.19f);
                var glass = new Color(0.10f, 0.12f, 0.16f);
                foreach (float x in new[] { -0.034f, 0.034f })
                {
                    s.Tube(new Vector3(x, 0.02f, -0.05f), new Vector3(x, 0.02f, 0.07f), 0.024f, 0.028f, body, 8);
                    s.Tube(new Vector3(x, 0.02f, 0.07f), new Vector3(x, 0.02f, 0.078f), 0.026f, glass, 8);
                }
                s.Box(new Vector3(0, 0.03f, 0.0f), new Vector3(0.05f, 0.018f, 0.05f), body);
                break;
            }
            case ItemId.SmartBinoculars:
            {
                var body = new Color(0.14f, 0.20f, 0.24f);
                var glass = new Color(0.10f, 0.12f, 0.16f);
                foreach (float x in new[] { -0.034f, 0.034f })
                {
                    s.Tube(new Vector3(x, 0.02f, -0.05f), new Vector3(x, 0.02f, 0.07f), 0.024f, 0.028f, body, 8);
                    s.Tube(new Vector3(x, 0.02f, 0.07f), new Vector3(x, 0.02f, 0.078f), 0.026f, glass, 8);
                }
                s.Box(new Vector3(0, 0.03f, 0.0f), new Vector3(0.05f, 0.018f, 0.05f), body);
                s.Box(new Vector3(0, 0.043f, -0.01f), new Vector3(0.04f, 0.006f, 0.03f), new Color(0.25f, 0.95f, 1.0f));   // the small screen
                break;
            }
            case ItemId.Camera:
            {
                var body = new Color(0.12f, 0.12f, 0.13f);
                s.Box(new Vector3(0, 0.04f, 0), new Vector3(0.13f, 0.085f, 0.06f), body);
                s.Box(new Vector3(-0.04f, 0.09f, -0.005f), new Vector3(0.03f, 0.015f, 0.03f), new Color(0.6f, 0.6f, 0.62f));
                s.Tube(new Vector3(0.01f, 0.04f, 0.03f), new Vector3(0.01f, 0.04f, 0.085f), 0.028f, 0.026f, new Color(0.30f, 0.30f, 0.32f), 10);
                s.Tube(new Vector3(0.01f, 0.04f, 0.085f), new Vector3(0.01f, 0.04f, 0.09f), 0.022f, new Color(0.15f, 0.25f, 0.35f), 10);
                break;
            }
            case ItemId.Gps:
            {
                // the screen faces +Z, toward the holder's camera (in first person a texture quad is drawn on top)
                var body = new Color(0.95f, 0.78f, 0.12f);
                s.Box(new Vector3(0, 0.07f, 0), new Vector3(0.085f, 0.15f, 0.028f), body);
                s.Box(new Vector3(0, 0.075f, 0.0145f), new Vector3(0.074f, 0.063f, 0.002f), new Color(0.35f, 0.55f, 0.40f));
                foreach (float x in new[] { -0.02f, 0.02f })
                    s.Box(new Vector3(x, 0.022f, 0.0145f), new Vector3(0.022f, 0.012f, 0.002f), new Color(0.15f, 0.15f, 0.15f));
                s.Tube(new Vector3(0.03f, 0.145f, 0), new Vector3(0.03f, 0.19f, 0), 0.007f, new Color(0.1f, 0.1f, 0.1f));
                break;
            }
            case ItemId.SwissFlag:
                AppendFlag(s, pole: 0.95f, cloth: 0.26f, bottom: -0.35f);
                break;
            case ItemId.EnergyBar:
                s.Box(new Vector3(0, 0.02f, 0.02f), new Vector3(0.035f, 0.018f, 0.13f), new Color(0.95f, 0.50f, 0.10f));
                s.Box(new Vector3(0, 0.02f, 0.02f), new Vector3(0.036f, 0.019f, 0.03f), new Color(0.35f, 0.15f, 0.08f));
                break;
            case ItemId.WaterBottle:
                s.Tube(new Vector3(0, -0.04f, 0), new Vector3(0, 0.15f, 0), 0.035f, new Color(0.30f, 0.60f, 0.95f), 10);
                s.Tube(new Vector3(0, 0.15f, 0), new Vector3(0, 0.19f, 0), 0.035f, 0.015f, new Color(0.30f, 0.60f, 0.95f), 10);
                s.Tube(new Vector3(0, 0.19f, 0), new Vector3(0, 0.21f, 0), 0.016f, new Color(0.9f, 0.9f, 0.92f), 8);
                break;
            case ItemId.Shotgun:
            {
                var wood = new Color(0.40f, 0.26f, 0.15f);
                var steel = new Color(0.22f, 0.23f, 0.25f);
                s.Box(new Vector3(0, -0.03f, -0.22f), new Vector3(0.04f, 0.09f, 0.34f), wood);      // stock
                s.Box(new Vector3(0, 0.01f, 0.02f), new Vector3(0.045f, 0.06f, 0.16f), steel);     // action
                // the fore-end is its own mesh (ShotgunForeEnd): it slides back and forth to pump
                foreach (float x in new[] { -0.011f, 0.011f })
                    s.Tube(new Vector3(x, 0.03f, 0.08f), new Vector3(x, 0.03f, 0.72f), 0.011f, steel, 6);
                s.Box(new Vector3(0, 0.048f, 0.40f), new Vector3(0.012f, 0.006f, 0.64f), new Color(0.55f, 0.56f, 0.6f));   // rib between the barrels
                s.Box(new Vector3(0, 0.056f, 0.70f), new Vector3(0.009f, 0.012f, 0.012f), new Color(1f, 0.85f, 0.25f));   // front bead
                break;
            }
            case ItemId.Pistol:
            {
                var steel = new Color(0.16f, 0.16f, 0.18f);
                s.Box(new Vector3(0, -0.045f, -0.01f), new Vector3(0.03f, 0.09f, 0.035f), new Color(0.10f, 0.10f, 0.11f));  // grip
                s.Box(new Vector3(0, 0.02f, 0.05f), new Vector3(0.03f, 0.035f, 0.18f), steel);                              // slide
                s.Box(new Vector3(0, 0.042f, 0.13f), new Vector3(0.006f, 0.008f, 0.006f), new Color(1f, 0.85f, 0.25f));     // front sight
                break;
            }
            case ItemId.Rifle:
            {
                var green = new Color(0.24f, 0.28f, 0.20f);
                var steel = new Color(0.18f, 0.19f, 0.20f);
                s.Box(new Vector3(0, -0.02f, -0.22f), new Vector3(0.04f, 0.08f, 0.28f), green);        // stock
                s.Box(new Vector3(0, 0.01f, 0.06f), new Vector3(0.05f, 0.07f, 0.30f), steel);         // receiver
                s.Box(new Vector3(0, -0.09f, 0.10f), new Vector3(0.03f, 0.13f, 0.05f), steel);        // magazine
                s.Box(new Vector3(0, 0.02f, 0.30f), new Vector3(0.045f, 0.05f, 0.18f), green);        // hand guard
                s.Tube(new Vector3(0, 0.03f, 0.38f), new Vector3(0, 0.03f, 0.62f), 0.010f, steel, 6); // barrel
                s.Box(new Vector3(0, 0.056f, 0.56f), new Vector3(0.009f, 0.012f, 0.012f), new Color(1f, 0.85f, 0.25f));
                break;
            }
            case ItemId.HuntingRifle:
            {
                var wood = new Color(0.42f, 0.28f, 0.16f);
                var steel = new Color(0.20f, 0.21f, 0.23f);
                s.Box(new Vector3(0, -0.03f, -0.20f), new Vector3(0.04f, 0.09f, 0.34f), wood);
                s.Box(new Vector3(0, 0.0f, 0.18f), new Vector3(0.045f, 0.05f, 0.42f), wood);
                s.Tube(new Vector3(0, 0.03f, 0.05f), new Vector3(0, 0.03f, 0.80f), 0.010f, steel, 6);
                s.Tube(new Vector3(0, 0.085f, -0.02f), new Vector3(0, 0.085f, 0.26f), 0.018f, new Color(0.08f, 0.08f, 0.09f), 8);   // scope
                break;
            }
            case ItemId.Knife:
            {
                s.Box(new Vector3(0, 0.0f, 0.0f), new Vector3(0.022f, 0.028f, 0.10f), new Color(0.80f, 0.10f, 0.12f));   // the red handle
                s.Box(new Vector3(0, 0.003f, 0.10f), new Vector3(0.006f, 0.022f, 0.10f), new Color(0.80f, 0.82f, 0.86f)); // blade
                break;
            }
            case ItemId.FlareGun:
            {
                var orange = new Color(0.95f, 0.45f, 0.10f);
                s.Box(new Vector3(0, -0.045f, -0.01f), new Vector3(0.03f, 0.09f, 0.035f), orange);
                s.Tube(new Vector3(0, 0.02f, -0.03f), new Vector3(0, 0.02f, 0.16f), 0.022f, orange, 8);
                s.Tube(new Vector3(0, 0.02f, 0.16f), new Vector3(0, 0.02f, 0.17f), 0.018f, new Color(0.15f, 0.15f, 0.15f), 8);
                break;
            }
            case ItemId.ArmorVest:
            {
                var cloth = new Color(0.30f, 0.34f, 0.24f);
                s.Box(new Vector3(0, 0.12f, 0), new Vector3(0.36f, 0.42f, 0.06f), cloth);
                s.Box(new Vector3(0, 0.20f, 0.035f), new Vector3(0.22f, 0.12f, 0.02f), new Color(0.22f, 0.25f, 0.18f));  // plate pocket
                break;
            }
            case ItemId.WitchHat or ItemId.PumpkinHead or ItemId.SantaHat or ItemId.ReindeerAntlers:
                // the real hat, the one a figure wears
                HumanMeshBuilder.AppendHat(s, UnitSport.Occasions.OccasionHats.ForItem(id), new Vector3(0, -0.02f, 0), Vector3.Up * 0.2f);
                break;
            case ItemId.Radio:
                // the grip is the handle: the box hangs from the hand at its real 0.46 m
                AppendRadio(s, new Vector3(0, -0.16f, 0));
                break;
            default:
                // no bespoke mesh: a thin card of the item's icon, one box per run of same-coloured pixels
                AppendIconCard(s, id);
                break;
        }

        var mesh = s.Build();
        HandMeshes[id] = mesh;
        return mesh;
    }

    /// <summary>
    /// The boombox, shifted by <paramref name="offset"/>: a copy of <see cref="RadioBody.Mesh"/>'s
    /// boxes (which is centred and cached for the thing lying in the world), so the held one can
    /// hang from its handle instead.
    /// </summary>
    public static void AppendRadio(MeshScratch s, Vector3 offset)
    {
        const float w = 0.46f, h = 0.22f, d = 0.16f;
        var shell = new Color(0.16f, 0.17f, 0.19f);
        var grille = new Color(0.08f, 0.08f, 0.09f);
        var chrome = new Color(0.72f, 0.74f, 0.76f);
        var red = new Color(0.80f, 0.12f, 0.10f);
        Vector3 P(float x, float y, float z) => new Vector3(x, y, z) + offset;
        s.Box(P(0, 0, 0), new Vector3(w, h, d), shell);
        foreach (float x in new[] { -0.14f, 0.14f })
        {
            s.Box(P(x, -0.01f, d * 0.5f + 0.004f), new Vector3(0.13f, 0.13f, 0.008f), grille);
            s.Ring(P(x, -0.01f, d * 0.5f + 0.009f), Vector3.Back, 0.03f, 0.06f, 0.004f, chrome, 12);
        }
        s.Box(P(0, -0.03f, d * 0.5f + 0.004f), new Vector3(0.11f, 0.06f, 0.008f), grille);
        for (int i = 0; i < 4; i++)
            s.Box(P(-0.045f + i * 0.03f, 0.06f, d * 0.5f + 0.006f), new Vector3(0.02f, 0.015f, 0.012f), i == 3 ? red : chrome);
        s.Tube(P(-0.15f, h * 0.5f, 0), P(-0.15f, h * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(P(0.15f, h * 0.5f, 0), P(0.15f, h * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(P(-0.15f, h * 0.5f + 0.05f, 0), P(0.15f, h * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(P(0.20f, h * 0.5f, -0.04f), P(0.28f, h * 0.5f + 0.30f, -0.06f), 0.004f, chrome);
    }

    /// <summary>
    /// The icon as a 10 cm card facing the holder. The hand material is vertex-coloured (it ignores
    /// textures), so the pixels are baked into the mesh as boxes, merged per horizontal run.
    /// </summary>
    private static void AppendIconCard(MeshScratch s, ItemId id)
    {
        var img = ItemIcons.GetImage(id);
        if (img == null) return;
        const float px = 0.0065f, depth = 0.006f;
        int n = ItemIcons.Size;
        for (int y = 0; y < n; y++)
        {
            int x = 0;
            while (x < n)
            {
                var c = img.GetPixel(x, y);
                if (c.A < 0.5f) { x++; continue; }
                int x1 = x;
                while (x1 + 1 < n && img.GetPixel(x1 + 1, y) == c) x1++;
                float cx = ((x + x1 + 1) * 0.5f - n * 0.5f) * px;
                float cy = 0.06f + (n * 0.5f - y - 0.5f) * px;
                s.Box(new Vector3(cx, cy, 0.02f), new Vector3((x1 - x + 1) * px, px, depth), c);
                x = x1 + 1;
            }
        }
    }

    /// <summary>A flag planted in the ground: origin at the foot of the pole.</summary>
    public static ArrayMesh PlantedFlagMesh()
    {
        if (_plantedFlag != null) return _plantedFlag;
        var s = new MeshScratch();
        AppendFlag(s, pole: 1.9f, cloth: 0.55f, bottom: 0f);
        return _plantedFlag = s.Build();
    }

    /// <summary>
    /// A pole with a square red cloth and a white cross — square, because the Swiss flag is one
    /// of the two square national flags, and a rectangle would be a different country's.
    /// </summary>
    private static void AppendFlag(MeshScratch s, float pole, float cloth, float bottom)
    {
        var red = new Color(0.85f, 0.08f, 0.10f);
        var white = new Color(0.97f, 0.97f, 0.97f);
        s.Tube(new Vector3(0, bottom, 0), new Vector3(0, bottom + pole, 0), 0.011f, new Color(0.75f, 0.72f, 0.68f));

        // the cloth hangs to the right of the pole, seen from the front
        float top = bottom + pole - 0.02f;
        var centre = new Vector3(-(cloth * 0.5f + 0.012f), top - cloth * 0.5f, 0);
        s.Box(centre, new Vector3(cloth, cloth, 0.006f), red);

        // the cross stands ~1.2 cm proud of each face: at 2 mm it z-fought with the cloth and
        // flickered away at distance (24-bit depth resolves ~4 mm at 60 m)
        const float CrossDepth = 0.03f;
        // official proportions: on a flag 32 units square the cross spans 20, its arms 6 wide
        float arm = cloth * 6f / 32f, span = cloth * 20f / 32f;
        s.Box(centre, new Vector3(span, arm, CrossDepth), white);
        s.Box(centre, new Vector3(arm, span, CrossDepth), white);
    }
}
