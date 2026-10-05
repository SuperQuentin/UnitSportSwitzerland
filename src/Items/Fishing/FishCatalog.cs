namespace UnitSport.Items.Fishing;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>What kind of water the float landed in (<see cref="FishWaters.Classify"/>).</summary>
[Flags]
public enum WaterKind
{
    None = 0,
    /// <summary>One of the big named lakes (<see cref="FishWaters.Lakes"/>).</summary>
    LargeLake = 1,
    /// <summary>A pond or a small lake below the alpine zone.</summary>
    SmallLake = 2,
    /// <summary>Flowing water in the lowlands: the surface slopes, or a stream line below 1000 m.</summary>
    River = 4,
    /// <summary>A stream line above 1000 m.</summary>
    MountainStream = 8,
    /// <summary>Still water above 1500 m (Sils, the tarns).</summary>
    AlpineLake = 16,
    Any = LargeLake | SmallLake | River | MountainStream | AlpineLake,
}

/// <summary>The drainage basin, which decides which fish live there (<see cref="FishWaters.BasinAt"/>).</summary>
[Flags]
public enum Basin
{
    None = 0,
    /// <summary>Rhine: Aare, Reuss, Limmat, Thur and the lakes north of the Alps but Léman.</summary>
    Rhine = 1,
    /// <summary>Rhone: Valais and Lake Geneva.</summary>
    Rhone = 2,
    /// <summary>Ticino and the Po: Maggiore, Lugano, Misox, Bergell, Poschiavo.</summary>
    Ticino = 4,
    /// <summary>Inn and the Danube: the Engadin.</summary>
    Inn = 8,
    /// <summary>The Doubs, in the Jura.</summary>
    Doubs = 16,
    All = Rhine | Rhone | Ticino | Inn | Doubs,
}

/// <summary>What is on the hook: dough takes the peaceful fish, a spinner the hunters.</summary>
public enum Bait { None, Dough, Spinner }

/// <summary>Why a fish caught went back (or was not kept).</summary>
public enum Verdict
{
    Keep,
    /// <summary>Critically endangered or extinct in Switzerland: always released.</summary>
    Protected,
    /// <summary>Its closed season (spawning).</summary>
    ClosedSeason,
    /// <summary>Under the legal minimum length.</summary>
    Undersized,
    /// <summary>A lake's own ban (Lake Constance whitefish 2024-2026).</summary>
    Moratorium,
    /// <summary>An invasive fish that must be killed and is worth nothing (the stickleback).</summary>
    Culled,
}

/// <summary>What the cook makes of it at a fire (<c>Crafting.Recipes</c>).</summary>
public enum Dish { None, PerchFillets, GrilledFish, FishSoup }

public enum Rarity { Common, Uncommon, Rare, VeryRare, Legendary }

/// <summary>
/// One fish species of Swiss waters. Sources: the BAFU / info fauna Red List 2022 (status), the federal
/// fishing ordinance VBGF SR 923.01 art. 1-2 (closed seasons, minimum lengths, raised as most cantons do),
/// the Zug, Bern and Léman rules, the CSCF atlas (where each lives). Lengths and altitudes are rounded for play.
/// </summary>
/// <param name="Item">Its item, or <see cref="ItemId.None"/> for a fish that is never kept.</param>
/// <param name="Kind">Weight in grams = Kind × cm³ / 100 (Fulton's condition factor).</param>
/// <param name="ClosedMonths">Bit m (1-12) set: closed that month.</param>
/// <param name="Hunter">Takes a spinner; the others take dough.</param>
public sealed record FishSpecies(
    string Name, string German, string French, string Latin, ItemId Item,
    WaterKind Waters, Basin Basins, int AltMin, int AltMax,
    float CmMin, float CmMax, float Kind, float MinCm, int ClosedMonths,
    Rarity Rarity, bool Hunter, string RedList, string Fact)
{
    public bool Protected => Item == ItemId.None && !Culled;
    /// <summary>Must be killed, never put back alive (Léman's rule for unwanted introduced fish).</summary>
    public bool Culled { get; init; }
    /// <summary>Feeds at night: bites better in the dark.</summary>
    public bool Night { get; init; }
    /// <summary>Introduced by people, not native.</summary>
    public bool Introduced { get; init; }

    public bool ClosedIn(int month) => (ClosedMonths & (1 << month)) != 0;

    /// <summary>Weight in kg of a fish this long.</summary>
    public float KgAt(float cm) => Kind * cm * cm * cm / 100f / 1000f;

    /// <summary>Months, as "Oct–Feb", for the journal; "" when always open.</summary>
    public string ClosedText
    {
        get
        {
            if (ClosedMonths == 0) return "";
            var names = new[] { "", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            // the first closed month after an open one, round the year
            int start = Enumerable.Range(1, 12).First(m => ClosedIn(m) && !ClosedIn(m == 1 ? 12 : m - 1));
            int end = start;
            while (ClosedIn(end % 12 + 1) && end % 12 + 1 != start) end = end % 12 + 1;
            return start == end ? names[start] : $"{names[start]}–{names[end]}";
        }
    }
}

public static class FishCatalog
{
    private static int Months(params int[] m) => m.Aggregate(0, (bits, x) => bits | 1 << x);

    private const WaterKind LL = WaterKind.LargeLake, SL = WaterKind.SmallLake, R = WaterKind.River,
        MS = WaterKind.MountainStream, AL = WaterKind.AlpineLake;
    private const Basin Rh = Basin.Rhine, Ro = Basin.Rhone, Ti = Basin.Ticino, In = Basin.Inn, Do = Basin.Doubs,
        Every = Basin.All;

    /// <summary>Every fish, kept or not. The whitefish is one item; its local name comes from the lake.</summary>
    public static readonly FishSpecies[] All =
    {
        // ---- salmonids ----
        new("Brown trout", "Bachforelle", "Truite fario", "Salmo trutta fario", ItemId.BrownTrout,
            MS | R | AL, Every, 200, 2600, 18, 45, 1.0f, 24, Months(10, 11, 12, 1, 2), Rarity.Common, true, "NT",
            "The highest-living native fish of the Alps; warmer rivers are pushing it uphill."),
        new("Lake trout", "Seeforelle", "Truite lacustre", "Salmo trutta lacustris", ItemId.LakeTrout,
            LL, Rh | Ro | Ti, 190, 800, 40, 90, 1.0f, 40, Months(10, 11, 12), Rarity.Uncommon, true, "EN",
            "Swims up the rivers each autumn to spawn, like a salmon: fish ladders were built for it."),
        new("Rainbow trout", "Regenbogenforelle", "Truite arc-en-ciel", "Oncorhynchus mykiss", ItemId.RainbowTrout,
            SL | R | AL, Every, 200, 1800, 25, 50, 1.0f, 0, 0, Rarity.Common, true, "introduced",
            "From North America: the mainstay of put-and-take ponds.") { Introduced = true },
        new("Brook trout", "Bachsaibling", "Saumon de fontaine", "Salvelinus fontinalis", ItemId.BrookTrout,
            AL | MS, Every, 1200, 2700, 18, 32, 1.0f, 22, 0, Rarity.Common, true, "introduced",
            "Not a trout but a North American char, stocked in many mountain lakes.") { Introduced = true },
        new("Arctic char", "Seesaibling (Rötel)", "Omble chevalier", "Salvelinus umbla", ItemId.ArcticChar,
            LL | AL, Rh | Ro | In, 190, 2500, 22, 45, 1.0f, 30, Months(11, 12), Rarity.Uncommon, true, "VU",
            "Red-bellied at spawning; the Zuger Rötel is a delicacy. On Léman every char caught must be kept."),
        new("Lake trout of Canada", "Namaycush", "Cristivomer", "Salvelinus namaycush", ItemId.Namaycush,
            AL, In, 1700, 2000, 50, 100, 0.9f, 0, 0, Rarity.Uncommon, true, "introduced",
            "Stocked in the 1960s; Lake Sils now holds an ice-fishing cull for it every winter.") { Introduced = true },
        new("Grayling", "Äsche", "Ombre commun", "Thymallus thymallus", ItemId.Grayling,
            R, Rh | In | Do | Ro, 250, 1700, 25, 48, 0.9f, 30, Months(2, 3, 4), Rarity.Uncommon, true, "EN",
            "Smells of thyme, hence Thymallus. The 2003 and 2018 heatwaves killed thousands in the Schaffhausen Rhine."),
        new("Whitefish", "Felchen", "Corégone", "Coregonus spp.", ItemId.Whitefish,
            LL | AL, Rh | Ro | In | Ti, 190, 1900, 22, 45, 0.9f, 28, Months(12, 1), Rarity.Common, false, "NT",
            "Each big lake has its own species: a third of them died out with the lakes' pollution."),

        // ---- hunters ----
        new("Perch", "Egli", "Perche", "Perca fluviatilis", ItemId.Perch,
            LL | SL | R, Every, 190, 1200, 12, 35, 1.3f, 15, Months(5), Rarity.Common, true, "LC",
            "Filets de perche are the lakeside dish: Léman fishers landed 438 t of perch in 2022."),
        new("Pike", "Hecht", "Brochet", "Esox lucius", ItemId.Pike,
            LL | SL | R, Rh | Ro | Do | In | Ti, 190, 1200, 40, 110, 0.65f, 50, Months(3, 4), Rarity.Common, true, "LC",
            "Waits in the reeds and strikes in a flash. South of the Alps it was brought in, and crosses with the Italian pike."),
        new("Zander", "Zander", "Sandre", "Sander lucioperca", ItemId.Zander,
            LL | R, Rh | Ti, 190, 450, 35, 80, 0.85f, 45, Months(4, 5), Rarity.Uncommon, true, "introduced",
            "Brought from eastern Europe; a big share of Lake Lugano's catch. Hunts at night.") { Introduced = true, Night = true },
        new("Wels catfish", "Wels", "Silure", "Silurus glanis", ItemId.Wels,
            LL | R, Rh | Ro, 190, 450, 70, 200, 0.8f, 0, 0, Rarity.Uncommon, true, "LC",
            "Europe's largest freshwater fish, native around Murten, Neuchâtel and Biel and spreading.") { Night = true },
        new("Burbot", "Trüsche", "Lotte", "Lota lota", ItemId.Burbot,
            LL | R, Rh | Ro | In | Do, 190, 1800, 30, 55, 0.7f, 0, 0, Rarity.Uncommon, true, "LC",
            "The only freshwater cod: it spawns under the ice in midwinter.") { Night = true },
        new("Largemouth bass", "Forellenbarsch", "Black-bass", "Micropterus salmoides", ItemId.LargemouthBass,
            LL | SL, Ti, 190, 300, 25, 50, 1.4f, 0, 0, Rarity.Uncommon, true, "introduced",
            "An American import, the lure fisher's target in Ticino.") { Introduced = true },

        // ---- the carp family and other "Weissfisch" ----
        new("Carp", "Karpfen", "Carpe", "Cyprinus carpio", ItemId.Carp,
            SL | LL, Rh | Ro | Ti | Do, 190, 800, 35, 80, 1.5f, 0, 0, Rarity.Common, false, "NT",
            "The wild form is near threatened; most carp are domestic strains."),
        new("Tench", "Schleie", "Tanche", "Tinca tinca", ItemId.Tench,
            SL, Every, 190, 1000, 22, 45, 1.4f, 0, 0, Rarity.Common, false, "LC",
            "The \"doctor fish\": legend had sick fish rub against its slime."),
        new("Roach", "Rotauge", "Gardon", "Rutilus rutilus", ItemId.Roach,
            LL | SL | R, Every, 190, 1000, 12, 30, 1.2f, 0, 0, Rarity.Common, false, "LC",
            "The classic Weissfisch; introduced in Ticino, where it crosses with the natives."),
        new("Rudd", "Rotfeder", "Rotengle", "Scardinius erythrophthalmus", ItemId.Rudd,
            SL, Rh | Ro | Do, 190, 800, 12, 30, 1.2f, 0, 0, Rarity.Common, false, "LC",
            "Bright red fins, and it feeds at the surface."),
        new("Bream", "Brachsmen", "Brème", "Abramis brama", ItemId.Bream,
            LL, Rh | Ro, 190, 600, 30, 60, 1.2f, 0, 0, Rarity.Common, false, "LC",
            "Feeds head down, stirring up the lake bed."),
        new("Chub", "Alet", "Chevaine", "Squalius cephalus", ItemId.Chub,
            R | LL, Rh | Ro | Do, 190, 1000, 20, 50, 1.1f, 0, 0, Rarity.Common, false, "LC",
            "Eats almost anything, even cherries falling into the river."),
        new("Barbel", "Barbe", "Barbeau", "Barbus barbus", ItemId.Barbel,
            R, Rh | Do | Ro, 250, 600, 30, 70, 0.9f, 35, Months(5, 6), Rarity.Uncommon, false, "NT",
            "Four barbels round its mouth, and its roe is poisonous."),
        new("Agone", "Agone", "Agone", "Alosa agone", ItemId.Agone,
            LL, Ti, 190, 300, 18, 35, 0.9f, 0, 0, Rarity.Uncommon, false, "VU",
            "Dried and pressed as missoltini on Lake Como and Maggiore."),
        new("Round goby", "Schwarzmundgrundel", "Gobie à taches noires", "Neogobius melanostomus", ItemId.RoundGoby,
            R, Rh, 250, 300, 8, 20, 1.3f, 0, 0, Rarity.Common, false, "invasive",
            "Came up the Rhine in ships' ballast around 2011; it now covers the river bed at Basel.") { Introduced = true },

        // ---- never kept: released (protected) or culled ----
        new("Three-spined stickleback", "Stichling", "Épinoche", "Gasterosteus aculeatus", ItemId.None,
            LL, Rh | Ro, 190, 450, 4, 8, 1.0f, 0, 0, Rarity.Common, false, "invasive in lakes",
            "Exploded in open Lake Constance and helped crash the whitefish.") { Culled = true, Introduced = true },
        new("Bullhead", "Groppe", "Chabot", "Cottus gobio", ItemId.None,
            MS | R | AL, Every, 200, 2300, 8, 15, 1.2f, 0, 0, Rarity.Common, false, "NT",
            "No swim bladder: it hops along the stream bed."),
        new("Minnow", "Elritze", "Vairon", "Phoxinus spp.", ItemId.None,
            MS | AL, Every, 200, 2600, 5, 10, 1.0f, 0, 0, Rarity.Common, false, "LC",
            "Lives in the highest alpine lakes of all."),
        new("Marble trout", "Marmorataforelle", "Truite marbrée", "Salmo marmoratus", ItemId.None,
            R, Ti, 200, 600, 40, 80, 1.0f, 0, 0, Rarity.VeryRare, true, "CR",
            "Marbled and spotless; only a few remain in the Ticino rivers."),
        new("Zebra trout", "Zebraforelle", "Truite zébrée", "Salmo rhodanensis", ItemId.None,
            MS | R, Do | Ro, 400, 1000, 20, 35, 1.0f, 0, 0, Rarity.VeryRare, true, "EN",
            "A native Rhone trout with vertical bars, only described as a species recently."),
        new("Danube trout", "Donauforelle", "Truite du Danube", "Salmo labrax", ItemId.None,
            AL | R, In, 1000, 1900, 30, 50, 1.0f, 0, 0, Rarity.VeryRare, true, "CR",
            "A relict population survives in Lake Sils."),
        new("Adriatic grayling", "Adriatische Äsche", "Ombre adriatique", "Thymallus aeliani", ItemId.None,
            R, Ti, 200, 600, 30, 45, 0.9f, 0, 0, Rarity.VeryRare, true, "CR",
            "The south-Alpine grayling, a species of its own."),
        new("Deep-water char", "Tiefseesaibling", "Omble profond", "Salvelinus profundus", ItemId.None,
            LL, Rh, 390, 400, 15, 25, 1.0f, 0, 0, Rarity.VeryRare, false, "CR",
            "The dwarf char of Lake Constance's depths, long thought extinct."),
        new("Nase", "Nase", "Nase", "Chondrostoma nasus", ItemId.None,
            R, Rh, 250, 500, 30, 50, 1.0f, 0, 0, Rarity.Rare, false, "CR",
            "Once spawned in huge shoals; now critically endangered."),
        new("European eel", "Aal", "Anguille", "Anguilla anguilla", ItemId.None,
            R | LL, Rh | Ro | Ti, 190, 800, 50, 100, 0.15f, 0, 0, Rarity.Rare, false, "CR",
            "Swims 6000 km to spawn in the Sargasso Sea; dams and turbines stop it.") { Night = true },
        new("Apron", "Rhone-Streber", "Apron du Rhône", "Zingel asper", ItemId.None,
            R, Do, 400, 500, 12, 20, 0.9f, 0, 0, Rarity.VeryRare, false, "CR",
            "The \"king of the Doubs\": it walks the bottom on its fins, at night, in one short stretch.") { Night = true },
        new("Atlantic salmon", "Lachs", "Saumon atlantique", "Salmo salar", ItemId.None,
            R, Rh, 250, 300, 60, 100, 1.0f, 0, 0, Rarity.Legendary, true, "extinct in Switzerland",
            "Basel was once a salmon city. Stocked again, its return is still unproven."),
        new("Huchen", "Huchen", "Huchon", "Hucho hucho", ItemId.None,
            R, In, 1000, 1300, 80, 130, 0.9f, 0, 0, Rarity.Legendary, true, "extinct in Switzerland",
            "The Danube salmon, gone from the Inn."),
    };

    private static readonly Dictionary<ItemId, FishSpecies> ByItem =
        All.Where(s => s.Item != ItemId.None).ToDictionary(s => s.Item);

    public static FishSpecies? Of(ItemId id) => ByItem.GetValueOrDefault(id);

    public static FishSpecies? Named(string name) => All.FirstOrDefault(s => s.Name == name);

    /// <summary>The fish items, in catalogue order.</summary>
    public static IEnumerable<ItemId> Items => All.Where(s => s.Item != ItemId.None).Select(s => s.Item);

    /// <summary>What a fish is cooked into, and how many go in a pot (<c>Crafting.Recipes</c>).</summary>
    public static (Dish Dish, int Count) DishOf(ItemId id) => id switch
    {
        ItemId.Perch => (Dish.PerchFillets, 2),
        ItemId.Carp or ItemId.Tench or ItemId.Roach or ItemId.Rudd or ItemId.Bream or ItemId.Chub
            or ItemId.Barbel or ItemId.Agone or ItemId.RoundGoby => (Dish.FishSoup, 2),
        _ when Of(id) != null => (Dish.GrilledFish, 1),
        _ => (Dish.None, 0),
    };

    public static ItemId ItemOf(Dish d) => d switch
    {
        Dish.PerchFillets => ItemId.PerchFillets,
        Dish.GrilledFish => ItemId.GrilledFish,
        Dish.FishSoup => ItemId.FishSoup,
        _ => ItemId.None,
    };
}
