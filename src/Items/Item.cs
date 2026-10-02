using Godot;
using UnitSport.Avatar;

namespace UnitSport.Items;

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
    int PackSlots = 0,
    /// <summary>The body slot a <see cref="ItemUse.Wear"/> item goes in (hats: the head).</summary>
    WearSlot Slot = WearSlot.None);

public static class ItemDefs
{
    /// <summary>Every item: the authored rows, then one per look in the wardrobe (<see cref="Garments.All"/>).</summary>
    public static readonly ItemDef[] All = Authored().Concat(Garments.All.Select(Cloth)).ToArray();

    private static ItemDef[] Authored() => new[]
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
        Eat(ItemId.CaramelApple, "Caramel apple", 5, "#b8581e", "CP", 15, ItemCategory.Food, 4),
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

        // fire and placeables (#272): cooked at a fire, worth more than what went in
        Eat(ItemId.Fondue, "Fondue", 3, "#f0c850", "FO", 90, ItemCategory.Food, 30),
        Eat(ItemId.HotChocolate, "Hot chocolate", 5, "#7a4628", "HC", 30, ItemCategory.Water, 8),
        Eat(ItemId.ToastedBread, "Toasted bread", 5, "#a8642a", "TB", 30, ItemCategory.Food, 4),
        new(ItemId.Campfire, "Campfire", "{use_item} lays it where you look and lights it: it burns 20 minutes, a fire to cook at. Out, anyone may clear the ashes; yours, {use_item} with an empty hand puts it out.",
            ItemUse.Place, 3, new Color(0.85f, 0.42f, 0.12f), "CF", 0, ItemCategory.Gear, 7f),
        new(ItemId.Torch, "Torch", "A light in your hand: it burns while you hold it, and everyone sees it.",
            ItemUse.Readout, 5, new Color(0.95f, 0.55f, 0.15f), "TO", 0, ItemCategory.Gear, 6f),
        new(ItemId.FieldWorkbench, "Field workbench", "{use_item} sets it up where you look: a workbench to craft at, anywhere. {use_item} on it with an empty hand packs it up again.",
            ItemUse.Place, 1, new Color(0.62f, 0.44f, 0.24f), "WB", 0, ItemCategory.Gear, 15f),
    };

    private static ItemDef Bag(ItemId id, string name, string tint, string glyph, int slots, float value) =>
        new(id, name, $"Wear it in the bag slot for {slots} more pack slots. {{use_item}} or a click on the bag slot puts it on.",
            ItemUse.Bag, 1, new Color(tint), glyph, 0, ItemCategory.Gear, value, slots);

    private static ItemDef Eat(ItemId id, string name, int stack, string tint, string glyph, float heal,
        ItemCategory category, float value) =>
        new(id, name, $"{{use_item}} to {(category == ItemCategory.Water ? "drink" : category == ItemCategory.Medical ? "apply" : "eat")}. Restores {heal:F0} health.",
            ItemUse.Consume, stack, new Color(tint), glyph, heal, category, value);

    private static ItemDef Hat(ItemId id, string name, string tint, string glyph) =>
        new(id, name, "{use_item} puts it on your head, in place of what was there. Others see you wearing it.",
            ItemUse.Wear, 1, new Color(tint), glyph, 0, ItemCategory.Cosmetic, 10, Slot: WearSlot.Head);

    /// <summary>A look from the wardrobe as an item: one to a slot, worn in its body slot.</summary>
    private static ItemDef Cloth(Garment g)
    {
        string look = g.Style switch
        {
            GarmentStyle.Gothic => " Gothic.",
            GarmentStyle.Kawaii => " Kawaii.",
            GarmentStyle.Special => $" Rare: a {Garments.FinishName(g.Finish)} finish that moves.",
            _ => "",
        };
        string covers = g.CoversBottom ? " One piece: it takes the bottom slot too." : "";
        string glyph = string.Concat(g.Name.Split(' ', '-').Where(w => w.Length > 0).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        float value = g.Style switch { GarmentStyle.Special => 300f, GarmentStyle.Basic => 20f, _ => 45f };
        // a finish's tint is what it looks like at a glance, not its plain base colour
        var tint = g.Finish switch
        {
            Finish.Rainbow => new Color("ff4fa8"), Finish.Disco => new Color("d8dce8"), Finish.Galaxy => new Color("5a2a9a"),
            Finish.Holo => new Color("a8f0f8"), Finish.Glitch => new Color("30f0c8"), Finish.Lava => new Color("f05a10"),
            Finish.Neon => new Color("30f0ff"), _ => g.A,
        };
        return new(g.Item, g.Name, $"{{use_item}} puts it on ({Garments.SlotName(g.Slot)}), in place of what was there.{look}{covers}",
            ItemUse.Wear, 1, tint, glyph, 0, ItemCategory.Clothing, value, Slot: g.Slot);
    }

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
            case ItemId.Torch:
            {
                // upright like the GPS: a stick, a wrap of cloth soaked in pitch, a flame on top
                s.Tube(new Vector3(0, -0.12f, 0), new Vector3(0, 0.30f, 0), 0.016f, 0.022f, new Color(0.45f, 0.29f, 0.15f), 6);
                s.Tube(new Vector3(0, 0.22f, 0), new Vector3(0, 0.33f, 0), 0.034f, new Color(0.30f, 0.22f, 0.16f), 8);
                s.Box(new Vector3(0, 0.38f, 0), new Vector3(0.06f, 0.09f, 0.06f), new Color(1f, 0.55f, 0.12f));
                s.Box(new Vector3(0, 0.43f, 0), new Vector3(0.035f, 0.07f, 0.035f), new Color(1f, 0.88f, 0.35f));
                break;
            }
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
