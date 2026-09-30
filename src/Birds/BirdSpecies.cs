using Godot;
using static UnitSport.Birds.BodyPlan;
using static UnitSport.Birds.FlightStyle;
using static UnitSport.Birds.Habitat;
using static UnitSport.Birds.Presence;

namespace UnitSport.Birds;

/// <summary>The body a bird is drawn with: proportions, neck, legs and wing shape (<see cref="BirdMesh"/>).</summary>
public enum BodyPlan { Passerine, Corvid, Pigeon, Woodpecker, Swift, Raptor, Owl, Grouse, Waterfowl, Gull, Wader, Heron }

/// <summary>How it moves through the air (<see cref="BirdLife"/>).</summary>
public enum FlightStyle
{
    /// <summary>Steady wingbeats in a straight line.</summary>
    Flap,
    /// <summary>Bursts of beats and closed-wing dips: finches, woodpeckers, wagtails.</summary>
    Undulate,
    /// <summary>Circles on thermals, rarely flapping: buzzards, eagles, vultures, storks.</summary>
    Soar,
    /// <summary>Hangs in the wind over one spot: kestrel.</summary>
    Hover,
    /// <summary>Long glides between a few beats: gulls, harriers, kites.</summary>
    Glide,
    /// <summary>Fast erratic hawking for insects: swifts, swallows, martins.</summary>
    Dart,
}

/// <summary>Where a species lives, in terms the cover raster can answer (<see cref="BirdLife.HabitatAt"/>).</summary>
[Flags]
public enum Habitat
{
    None = 0,
    Forest = 1,       // closed forest
    Edge = 2,         // open forest, copses, hedges, clearcuts, parks
    Farm = 4,         // open ground below ~1000 m
    Meadow = 8,       // open ground 1000–1900 m
    Alpine = 16,      // open ground above ~1900 m
    Rock = 32,        // cliffs, scree, boulders, quarries
    Water = 64,       // lakes and rivers
    Wetland = 128,    // reeds, marsh, shores
    Town = 256,       // built-up, paved, gardens, cemeteries
    Vineyard = 512,
    Orchard = 1024,
    Snow = 2048,      // glacier and firn
}

/// <summary>When a species is in the country.</summary>
public enum Presence { Resident, Summer, Winter, Passage }

/// <summary>
/// One bird species as the game needs it. Sizes are real (length and wingspan in metres), colours
/// are the adult plumage reduced to back, belly and one accent.
/// </summary>
public sealed record BirdSpecies(
    int Index,
    string Name,
    string Latin,
    BodyPlan Body,
    float Length,
    float Wingspan,
    Color Back,
    Color Belly,
    Color Accent,
    FlightStyle Flight,
    Habitat Habitat,
    int MinAltitude,
    int MaxAltitude,
    Presence Presence,
    float Abundance,
    int Flock,
    int SeasonFrom,
    int SeasonTo)
{
    /// <summary>Listed as huntable in the Federal Hunting Act (JSG, SR 922.0, Art. 5).</summary>
    public bool IsGame => SeasonFrom > 0;

    /// <summary>Open season on this species in <paramref name="month"/> (1–12). Seasons may wrap the new year.</summary>
    public bool InSeason(int month) => IsGame && (SeasonFrom <= SeasonTo
        ? month >= SeasonFrom && month <= SeasonTo
        : month >= SeasonFrom || month <= SeasonTo);

    /// <summary>How likely the species is to be in the country in <paramref name="month"/>, 0..1.</summary>
    public float PresenceIn(int month) => Presence switch
    {
        Resident => 1f,
        Summer => month is >= 4 and <= 9 ? 1f : month is 3 or 10 ? 0.3f : 0f,
        Winter => month is >= 11 or <= 2 ? 1f : month is 3 or 10 ? 0.5f : 0f,
        Passage => month is 3 or 4 or 5 or 8 or 9 or 10 ? 1f : 0f,
        _ => 0f,
    };

    /// <summary>Hard to hit: small and fast is worth more than big and slow.</summary>
    public int Points => Mathf.Clamp(Mathf.RoundToInt(60f / Mathf.Max(Length, 0.12f) * 0.5f), 10, 250)
        + (Flight is Dart or Undulate ? 20 : 0);
}

/// <summary>
/// Every bird species that occurs <b>regularly</b> in Switzerland: all regular breeders plus the
/// regular winter visitors and passage migrants.
///
/// <para>
/// Source: Swiss Ornithological Institute (Vogelwarte Sempach), "Liste der Vögel der Schweiz" and
/// the Swiss Breeding Bird Atlas 2013–2016; English names and taxonomy follow the IOC World Bird
/// List. Vagrants and species recorded only a handful of times are left out: nobody should meet a
/// once-a-decade rarity every afternoon. Lengths and wingspans are standard field-guide means.
/// Altitude ranges are where the species is usually met, not its extremes. Abundance is a relative
/// weight (100 = everywhere, 1 = a lucky day) and is an estimate from the atlas, not a count.
/// </para>
///
/// <para>
/// Hunting seasons (months, inclusive) follow the Federal Hunting Act, JSG Art. 5, as best known:
/// cantons may shorten them or protect a species outright, so a value here is the federal ceiling.
/// Black grouse is huntable only as the cock; the game does not tell the sexes apart and treats it
/// as the cock. Anything not listed as game is protected. Values marked <c>// est.</c> are
/// estimates that were not checked against the atlas.
/// </para>
/// </summary>
public static class BirdCatalog
{
    private static int _next;

    private static BirdSpecies B(string name, string latin, BodyPlan body, float lengthCm, float spanCm,
        string colours, FlightStyle flight, Habitat habitat, int minAlt, int maxAlt, Presence presence,
        float abundance, int flock = 1, int seasonFrom = 0, int seasonTo = 0)
    {
        var c = colours.Split(' ');
        return new BirdSpecies(_next++, name, latin, body, lengthCm / 100f, spanCm / 100f,
            new Color(c[0]), new Color(c[1]), new Color(c[2]), flight, habitat, minAlt, maxAlt,
            presence, abundance, flock, seasonFrom, seasonTo);
    }

    public static readonly BirdSpecies[] All =
    {
        // ---- gamebirds: grouse, partridges, quail, pheasant ------------------------------------------
        B("Hazel Grouse", "Tetrastes bonasia", Grouse, 36, 52, "#7a6a55 #c8b89a #2a2420", Flap, Forest, 600, 1900, Resident, 4),
        B("Rock Ptarmigan", "Lagopus muta", Grouse, 35, 58, "#9a9488 #f2f2ee #1a1a1a", Flap, Alpine | Rock | Snow, 1900, 3000, Resident, 6, 3, 10, 11),
        B("Black Grouse", "Lyrurus tetrix", Grouse, 55, 80, "#1c1e2a #2a2a30 #d02020", Flap, Edge | Alpine | Meadow, 1400, 2300, Resident, 6, 2, 10, 11),
        B("Western Capercaillie", "Tetrao urogallus", Grouse, 85, 110, "#2a2a2a #3a3a38 #2e4a2e", Flap, Forest, 900, 1800, Resident, 1),
        B("Rock Partridge", "Alectoris graeca", Grouse, 34, 50, "#8a8a86 #c8a878 #c02a20", Flap, Rock | Alpine, 1200, 2600, Resident, 2, 4),
        B("Grey Partridge", "Perdix perdix", Grouse, 30, 48, "#8a7a62 #9a9a92 #c07038", Flap, Farm, 300, 700, Resident, 0.5f, 6),
        B("Common Quail", "Coturnix coturnix", Grouse, 17, 34, "#8a6e48 #d8c09a #2a2018", Flap, Farm | Meadow, 300, 1500, Summer, 4),
        B("Common Pheasant", "Phasianus colchicus", Grouse, 70, 80, "#a8622a #7a4020 #1e5a4a", Flap, Farm | Edge | Wetland, 300, 700, Resident, 5, 1, 9, 1),

        // ---- swans, geese, ducks ---------------------------------------------------------------------
        B("Mute Swan", "Cygnus olor", Waterfowl, 150, 220, "#f4f4f0 #f4f4f0 #e87a20", Flap, Water, 190, 1000, Resident, 30, 2),
        B("Whooper Swan", "Cygnus cygnus", Waterfowl, 150, 220, "#f4f4f0 #f4f4f0 #e8d020", Flap, Water | Farm, 190, 700, Winter, 1, 4),
        B("Greylag Goose", "Anser anser", Waterfowl, 82, 164, "#8a8a80 #b0aca0 #e8903a", Flap, Water | Wetland | Farm, 190, 700, Resident, 5, 8),
        B("Greater White-fronted Goose", "Anser albifrons", Waterfowl, 70, 145, "#7a7064 #a09888 #f0f0ea", Flap, Water | Farm, 190, 600, Winter, 1, 8),
        B("Tundra Bean Goose", "Anser serrirostris", Waterfowl, 75, 150, "#5a5044 #9a9080 #e8902a", Flap, Water | Farm, 190, 600, Winter, 1, 6),
        B("Egyptian Goose", "Alopochen aegyptiaca", Waterfowl, 68, 140, "#b09a7a #c8b89a #6a3a20", Flap, Water | Farm, 190, 600, Resident, 2, 2),
        B("Ruddy Shelduck", "Tadorna ferruginea", Waterfowl, 64, 130, "#c0703a #c87a42 #f0e0c8", Flap, Water, 190, 600, Resident, 2, 4),
        B("Common Shelduck", "Tadorna tadorna", Waterfowl, 60, 110, "#f0f0ea #f0f0ea #1e4a3a", Flap, Water | Wetland, 190, 600, Passage, 1, 2),
        B("Mandarin Duck", "Aix galericulata", Waterfowl, 45, 70, "#6a4a3a #f0e8e0 #e8801a", Flap, Water | Edge, 190, 700, Resident, 2, 2),
        B("Eurasian Wigeon", "Mareca penelope", Waterfowl, 47, 80, "#9a9a98 #f0f0ec #a04a2a", Flap, Water | Wetland, 190, 700, Winter, 6, 10, 9, 1),
        B("Gadwall", "Mareca strepera", Waterfowl, 51, 85, "#7a7a78 #a8a8a4 #3a3a3a", Flap, Water | Wetland, 190, 700, Resident, 8, 6, 9, 1),
        B("Eurasian Teal", "Anas crecca", Waterfowl, 36, 60, "#8a8a8a #d8d0c8 #2a6a4a", Flap, Water | Wetland, 190, 1000, Winter, 10, 10, 9, 1),
        B("Mallard", "Anas platyrhynchos", Waterfowl, 58, 90, "#7a6a58 #a8a098 #1e6a3a", Flap, Water | Wetland | Town, 190, 1800, Resident, 90, 6, 9, 1),
        B("Northern Pintail", "Anas acuta", Waterfowl, 60, 90, "#8a8a8a #f0f0ec #5a3a2a", Flap, Water | Wetland, 190, 700, Winter, 2, 4, 9, 1),
        B("Garganey", "Spatula querquedula", Waterfowl, 38, 63, "#8a7a6a #c8c0b0 #f0f0ec", Flap, Wetland | Water, 190, 700, Passage, 2, 4),
        B("Northern Shoveler", "Spatula clypeata", Waterfowl, 48, 76, "#f0f0ec #b05a2a #1e5a3a", Flap, Water | Wetland, 190, 700, Winter, 4, 6, 9, 1),
        B("Red-crested Pochard", "Netta rufina", Waterfowl, 55, 87, "#6a5a4a #2a2a2a #e8702a", Flap, Water, 190, 700, Resident, 12, 12),
        B("Common Pochard", "Aythya ferina", Waterfowl, 45, 77, "#a8a8a8 #b8b8b8 #8a3a20", Flap, Water, 190, 700, Winter, 25, 25, 9, 1),
        B("Ferruginous Duck", "Aythya nyroca", Waterfowl, 40, 65, "#5a3020 #8a4a30 #f0f0ec", Flap, Water | Wetland, 190, 600, Winter, 1),
        B("Tufted Duck", "Aythya fuligula", Waterfowl, 43, 70, "#1e1e22 #f0f0ec #e8c020", Flap, Water, 190, 1000, Winter, 45, 30, 9, 1),
        B("Greater Scaup", "Aythya marila", Waterfowl, 46, 75, "#c0c0c0 #f0f0ec #1e3a2a", Flap, Water, 190, 600, Winter, 1, 4),
        B("Common Eider", "Somateria mollissima", Waterfowl, 60, 95, "#f0f0ec #1e1e1e #9ad0a0", Flap, Water, 190, 600, Resident, 1, 3),
        B("Velvet Scoter", "Melanitta fusca", Waterfowl, 55, 95, "#1e1e1e #1e1e1e #f0f0ec", Flap, Water, 190, 600, Winter, 1, 3),
        B("Common Scoter", "Melanitta nigra", Waterfowl, 49, 85, "#141414 #1e1e1e #e8a020", Flap, Water, 190, 600, Winter, 0.5f, 3),
        B("Long-tailed Duck", "Clangula hyemalis", Waterfowl, 45, 75, "#3a302a #f0f0ec #c8a898", Flap, Water, 190, 600, Winter, 0.5f),
        B("Common Goldeneye", "Bucephala clangula", Waterfowl, 46, 72, "#1e1e1e #f0f0ec #2a4a3a", Flap, Water, 190, 1000, Winter, 12, 8),
        B("Smew", "Mergellus albellus", Waterfowl, 40, 62, "#f0f0ec #f0f0ec #1e1e1e", Flap, Water, 190, 600, Winter, 2, 4),
        B("Goosander", "Mergus merganser", Waterfowl, 62, 90, "#1e2a2a #f0e8e0 #c02a20", Flap, Water, 190, 1800, Resident, 15, 4),
        B("Red-breasted Merganser", "Mergus serrator", Waterfowl, 55, 75, "#3a3a3a #d8d0c8 #a03a20", Flap, Water, 190, 600, Winter, 1, 2),

        // ---- grebes, loons, cormorant ----------------------------------------------------------------
        B("Little Grebe", "Tachybaptus ruficollis", Waterfowl, 27, 42, "#4a3a2a #8a6a4a #9a3a20", Flap, Water | Wetland, 190, 1000, Resident, 15, 2),
        B("Great Crested Grebe", "Podiceps cristatus", Waterfowl, 48, 85, "#5a4a3a #f0f0ec #c0602a", Flap, Water, 190, 1000, Resident, 40, 3),
        B("Red-necked Grebe", "Podiceps grisegena", Waterfowl, 43, 80, "#3a3a3a #d0d0cc #a03a20", Flap, Water, 190, 600, Winter, 1),
        B("Horned Grebe", "Podiceps auritus", Waterfowl, 34, 60, "#2a2a2a #f0f0ec #c07a2a", Flap, Water, 190, 600, Winter, 1),
        B("Black-necked Grebe", "Podiceps nigricollis", Waterfowl, 31, 56, "#1e1e1e #d0c8c0 #d8a020", Flap, Water, 190, 700, Winter, 4, 6),
        B("Red-throated Loon", "Gavia stellata", Waterfowl, 60, 110, "#6a6a6a #f0f0ec #8a2a20", Flap, Water, 190, 600, Winter, 0.5f),
        B("Black-throated Loon", "Gavia arctica", Waterfowl, 65, 120, "#2a2a2a #f0f0ec #5a5a5a", Flap, Water, 190, 600, Winter, 2),
        B("Great Northern Loon", "Gavia immer", Waterfowl, 80, 135, "#1e1e1e #f0f0ec #3a3a3a", Flap, Water, 190, 600, Winter, 0.5f),
        B("Great Cormorant", "Phalacrocorax carbo", Waterfowl, 90, 145, "#1e1e22 #2a2a2e #e8c020", Flap, Water, 190, 1000, Resident, 30, 8, 9, 1),

        // ---- herons, storks, spoonbill, crane --------------------------------------------------------
        B("Eurasian Bittern", "Botaurus stellaris", Heron, 75, 130, "#9a7a4a #c8a878 #2a2018", Flap, Wetland, 190, 600, Winter, 0.5f),
        B("Little Bittern", "Ixobrychus minutus", Heron, 35, 55, "#1e1e1e #d8b888 #c8a060", Flap, Wetland, 190, 600, Summer, 1),
        B("Black-crowned Night Heron", "Nycticorax nycticorax", Heron, 62, 110, "#8a8a8a #e0e0dc #1e2a2a", Flap, Wetland | Water, 190, 600, Summer, 1, 2),
        B("Squacco Heron", "Ardeola ralloides", Heron, 46, 85, "#c8a068 #f0f0ec #d8b888", Flap, Wetland, 190, 600, Passage, 0.5f),
        B("Western Cattle Egret", "Bubulcus ibis", Heron, 50, 92, "#f0f0ec #f0f0ec #e8b050", Flap, Farm | Wetland, 190, 600, Passage, 1, 4),
        B("Little Egret", "Egretta garzetta", Heron, 60, 95, "#f4f4f0 #f4f4f0 #1e1e1e", Flap, Wetland | Water, 190, 600, Passage, 2, 2),
        B("Great Egret", "Ardea alba", Heron, 90, 160, "#f4f4f0 #f4f4f0 #e8c020", Flap, Wetland | Farm | Water, 190, 700, Winter, 8, 2),
        B("Grey Heron", "Ardea cinerea", Heron, 95, 175, "#8a9098 #d8d8d8 #1e1e1e", Flap, Water | Wetland | Farm, 190, 1400, Resident, 35),
        B("Purple Heron", "Ardea purpurea", Heron, 85, 135, "#6a5a6a #8a4a2a #2a2a2a", Flap, Wetland, 190, 600, Summer, 0.5f),
        B("Black Stork", "Ciconia nigra", Heron, 100, 185, "#1e2220 #f0f0ec #c02a20", Soar, Wetland | Forest, 190, 1200, Passage, 1),
        B("White Stork", "Ciconia ciconia", Heron, 105, 165, "#f4f4f0 #f4f4f0 #d02a20", Soar, Farm | Wetland | Town, 190, 800, Summer, 10, 3),
        B("Eurasian Spoonbill", "Platalea leucorodia", Heron, 85, 125, "#f4f4f0 #f4f4f0 #2a2a2a", Flap, Wetland, 190, 600, Passage, 0.5f, 3),
        B("Glossy Ibis", "Plegadis falcinellus", Heron, 60, 95, "#5a3a3a #4a2e30 #2a5a4a", Flap, Wetland | Farm, 190, 600, Passage, 0.5f, 4),
        B("Common Crane", "Grus grus", Heron, 115, 215, "#8a8a8a #9a9a98 #c02a20", Soar, Farm | Wetland, 190, 1500, Passage, 1, 12),

        // ---- rails ------------------------------------------------------------------------------------
        B("Water Rail", "Rallus aquaticus", Wader, 26, 42, "#7a5a3a #6a6a78 #c02a20", Flap, Wetland, 190, 700, Resident, 3),
        B("Spotted Crake", "Porzana porzana", Wader, 22, 39, "#6a5a3a #7a7a80 #e8c020", Flap, Wetland, 190, 700, Passage, 0.5f),
        B("Little Crake", "Zapornia parva", Wader, 19, 36, "#7a6a4a #6a7a8a #c02a20", Flap, Wetland, 190, 600, Passage, 0.5f),
        B("Corn Crake", "Crex crex", Wader, 28, 49, "#a8885a #c8b090 #c07a3a", Flap, Meadow | Farm, 400, 1600, Summer, 0.5f),
        B("Common Moorhen", "Gallinula chloropus", Waterfowl, 33, 52, "#3a3a38 #2a2a30 #d02a20", Flap, Wetland | Water, 190, 700, Resident, 12),
        B("Eurasian Coot", "Fulica atra", Waterfowl, 38, 75, "#1e1e1e #2a2a2a #f0f0ec", Flap, Water, 190, 1000, Resident, 60, 20, 9, 1),

        // ---- birds of prey ----------------------------------------------------------------------------
        B("Western Osprey", "Pandion haliaetus", Raptor, 55, 160, "#4a3a2a #f0f0ec #2a2a2a", Hover, Water, 190, 1000, Passage, 1),
        B("European Honey Buzzard", "Pernis apivorus", Raptor, 55, 125, "#5a4a3a #d8c8b0 #7a7a80", Soar, Forest | Edge, 300, 1500, Summer, 4),
        B("Bearded Vulture", "Gypaetus barbatus", Raptor, 110, 275, "#3a3a3a #e8b070 #1e1e1e", Soar, Rock | Alpine, 1500, 3200, Resident, 1),
        B("Griffon Vulture", "Gyps fulvus", Raptor, 100, 260, "#9a7a5a #b08a62 #2a2018", Soar, Alpine | Rock, 1500, 3000, Summer, 1, 6),
        B("Cinereous Vulture", "Aegypius monachus", Raptor, 105, 270, "#2a2622 #3a3430 #8a7a8a", Soar, Alpine | Rock, 1500, 3000, Summer, 0.3f),
        B("Short-toed Snake Eagle", "Circaetus gallicus", Raptor, 65, 180, "#7a6a58 #f0ece4 #5a4a3a", Hover, Rock | Meadow, 600, 2000, Summer, 0.5f),
        B("Booted Eagle", "Hieraaetus pennatus", Raptor, 50, 120, "#6a5440 #f0ece4 #2a2a2a", Soar, Edge | Meadow, 190, 1800, Passage, 0.3f),
        B("Golden Eagle", "Aquila chrysaetos", Raptor, 85, 210, "#4a3a2a #5a4a36 #c8a060", Soar, Alpine | Rock | Meadow, 1200, 3200, Resident, 4),
        B("Western Marsh Harrier", "Circus aeruginosus", Raptor, 52, 125, "#6a4a2a #8a6a4a #9a9a9a", Glide, Wetland | Farm, 190, 800, Passage, 3),
        B("Hen Harrier", "Circus cyaneus", Raptor, 48, 110, "#9aa0a8 #f0f0ec #2a2a2a", Glide, Farm | Wetland, 190, 900, Winter, 2),
        B("Montagu's Harrier", "Circus pygargus", Raptor, 45, 110, "#8a9098 #d8d8d4 #1e1e1e", Glide, Farm, 190, 900, Passage, 0.5f),
        B("Eurasian Sparrowhawk", "Accipiter nisus", Raptor, 33, 68, "#5a6068 #e8d8c8 #c07a3a", Flap, Forest | Edge | Town, 190, 1800, Resident, 12),
        B("Northern Goshawk", "Accipiter gentilis", Raptor, 55, 110, "#4a5058 #e8e4dc #e8a020", Flap, Forest, 300, 1800, Resident, 4),
        B("Red Kite", "Milvus milvus", Raptor, 65, 160, "#a0482a #b8602a #d8d8d8", Glide, Farm | Edge | Meadow, 300, 1400, Resident, 20),
        B("Black Kite", "Milvus migrans", Raptor, 55, 145, "#4a3a2a #5a4632 #8a8a8a", Glide, Water | Farm | Town, 190, 1200, Summer, 20, 3),
        B("White-tailed Eagle", "Haliaeetus albicilla", Raptor, 85, 220, "#6a5642 #7a6650 #f0f0ec", Soar, Water, 190, 700, Winter, 0.3f),
        B("Common Buzzard", "Buteo buteo", Raptor, 52, 120, "#5a4632 #d8c8b0 #7a5a3a", Soar, Farm | Edge | Meadow | Forest, 190, 2000, Resident, 70),
        B("Rough-legged Buzzard", "Buteo lagopus", Raptor, 55, 130, "#7a6a58 #e8e0d4 #2a2a2a", Hover, Farm, 190, 900, Winter, 0.3f),
        B("Common Kestrel", "Falco tinnunculus", Raptor, 34, 75, "#b0683a #e8d0b0 #7a8088", Hover, Farm | Meadow | Alpine | Town | Rock, 190, 2600, Resident, 30),
        B("Red-footed Falcon", "Falco vespertinus", Raptor, 30, 72, "#4a4e56 #5a5e66 #c0402a", Hover, Farm, 190, 800, Passage, 0.5f, 3),
        B("Merlin", "Falco columbarius", Raptor, 28, 60, "#5a6a80 #d8b890 #2a2a2a", Flap, Farm, 190, 900, Winter, 0.5f),
        B("Eurasian Hobby", "Falco subbuteo", Raptor, 32, 80, "#3a4048 #e8e0d4 #b0402a", Dart, Wetland | Farm | Edge, 190, 1400, Summer, 3),
        B("Peregrine Falcon", "Falco peregrinus", Raptor, 42, 100, "#4a5462 #e8e4dc #e8c020", Flap, Rock | Town, 190, 2500, Resident, 3),

        // ---- waders -----------------------------------------------------------------------------------
        B("Eurasian Oystercatcher", "Haematopus ostralegus", Wader, 42, 83, "#1e1e1e #f0f0ec #e05a1a", Flap, Water | Wetland, 190, 600, Passage, 0.5f),
        B("Black-winged Stilt", "Himantopus himantopus", Wader, 36, 70, "#1e1e1e #f4f4f0 #e87a8a", Flap, Wetland, 190, 600, Passage, 0.5f),
        B("Pied Avocet", "Recurvirostra avosetta", Wader, 43, 75, "#f4f4f0 #f4f4f0 #1e1e1e", Flap, Wetland, 190, 600, Passage, 0.3f),
        B("Northern Lapwing", "Vanellus vanellus", Wader, 30, 85, "#2a4a3a #f0f0ec #1e1e1e", Flap, Farm | Wetland, 190, 900, Summer, 4, 10),
        B("European Golden Plover", "Pluvialis apricaria", Wader, 27, 72, "#8a7a3a #f0ece0 #1e1e1e", Flap, Farm, 190, 900, Passage, 1, 10),
        B("Grey Plover", "Pluvialis squatarola", Wader, 28, 77, "#8a8a88 #1e1e1e #f0f0ec", Flap, Wetland, 190, 600, Passage, 0.3f),
        B("Common Ringed Plover", "Charadrius hiaticula", Wader, 19, 50, "#8a7a62 #f0f0ec #1e1e1e", Flap, Wetland | Water, 190, 600, Passage, 1, 4),
        B("Kentish Plover", "Charadrius alexandrinus", Wader, 16, 45, "#b0a080 #f4f4f0 #1e1e1e", Flap, Wetland | Water, 190, 600, Passage, 0.3f),
        B("Little Ringed Plover", "Charadrius dubius", Wader, 16, 45, "#8a7a62 #f0f0ec #1e1e1e", Flap, Water | Rock | Wetland, 190, 900, Summer, 3, 2),
        B("Eurasian Dotterel", "Charadrius morinellus", Wader, 21, 60, "#6a5a4a #c0702a #f0f0ec", Flap, Alpine, 1800, 2800, Passage, 0.5f, 4),
        B("Eurasian Whimbrel", "Numenius phaeopus", Wader, 42, 82, "#7a6a55 #d8ccb8 #3a3028", Flap, Wetland | Farm, 190, 700, Passage, 0.5f),
        B("Eurasian Curlew", "Numenius arquata", Wader, 55, 90, "#8a7658 #d8ccb8 #3a3028", Flap, Wetland | Farm, 190, 800, Winter, 2, 4),
        B("Black-tailed Godwit", "Limosa limosa", Wader, 40, 75, "#8a6a4a #c0703a #1e1e1e", Flap, Wetland, 190, 600, Passage, 0.5f, 3),
        B("Bar-tailed Godwit", "Limosa lapponica", Wader, 38, 75, "#8a7a62 #b0602a #d8ccb8", Flap, Wetland, 190, 600, Passage, 0.3f),
        B("Ruddy Turnstone", "Arenaria interpres", Wader, 23, 54, "#a0602a #f0f0ec #1e1e1e", Flap, Water | Wetland, 190, 600, Passage, 0.3f),
        B("Ruff", "Calidris pugnax", Wader, 26, 55, "#7a6a55 #d8ccb8 #e8a040", Flap, Wetland | Farm, 190, 600, Passage, 2, 6),
        B("Curlew Sandpiper", "Calidris ferruginea", Wader, 20, 40, "#8a6a4a #a04a2a #f0f0ec", Flap, Wetland, 190, 600, Passage, 0.3f),
        B("Temminck's Stint", "Calidris temminckii", Wader, 14, 35, "#7a7060 #f0f0ec #8a8a78", Flap, Wetland, 190, 600, Passage, 0.5f),
        B("Sanderling", "Calidris alba", Wader, 20, 40, "#b0b0aa #f4f4f0 #1e1e1e", Flap, Water | Wetland, 190, 600, Passage, 0.3f, 3),
        B("Dunlin", "Calidris alpina", Wader, 18, 38, "#8a7a62 #f0f0ec #1e1e1e", Flap, Wetland, 190, 600, Passage, 2, 8),
        B("Little Stint", "Calidris minuta", Wader, 13, 30, "#9a7a5a #f0f0ec #1e1e1e", Flap, Wetland, 190, 600, Passage, 1, 4),
        B("Jack Snipe", "Lymnocryptes minimus", Wader, 19, 38, "#5a4a32 #e0d8c8 #c8a860", Flap, Wetland, 190, 700, Winter, 0.5f),
        B("Eurasian Woodcock", "Scolopax rusticola", Wader, 35, 58, "#8a6a48 #b09a78 #3a2a1e", Flap, Forest | Edge, 300, 1800, Resident, 5, 1, 9, 12),
        B("Common Snipe", "Gallinago gallinago", Wader, 26, 45, "#5a4a32 #e0d8c8 #c8a860", Flap, Wetland, 190, 900, Winter, 3),
        B("Common Sandpiper", "Actitis hypoleucos", Wader, 20, 38, "#7a6e5a #f0f0ec #5a5a4a", Flap, Water | Wetland, 190, 1600, Summer, 5),
        B("Green Sandpiper", "Tringa ochropus", Wader, 22, 58, "#3a3a32 #f0f0ec #6a6a5a", Flap, Wetland | Water, 190, 900, Winter, 3),
        B("Spotted Redshank", "Tringa erythropus", Wader, 31, 64, "#2a2a2a #3a3a3a #c02a20", Flap, Wetland, 190, 600, Passage, 0.5f),
        B("Common Greenshank", "Tringa nebularia", Wader, 32, 68, "#8a8a82 #f0f0ec #6a7a5a", Flap, Wetland, 190, 700, Passage, 2, 2),
        B("Wood Sandpiper", "Tringa glareola", Wader, 20, 56, "#6a5e4a #f0f0ec #c8c060", Flap, Wetland, 190, 700, Passage, 2, 3),
        B("Marsh Sandpiper", "Tringa stagnatilis", Wader, 23, 58, "#9a9a92 #f4f4f0 #5a5a52", Flap, Wetland, 190, 600, Passage, 0.3f),
        B("Common Redshank", "Tringa totanus", Wader, 28, 62, "#8a7a62 #e0d8c8 #d0402a", Flap, Wetland, 190, 600, Passage, 1),

        // ---- gulls, kittiwake, terns ------------------------------------------------------------------
        B("Black-legged Kittiwake", "Rissa tridactyla", Gull, 40, 100, "#a8b0b8 #f4f4f0 #e8d020", Glide, Water, 190, 600, Winter, 0.3f),
        B("Black-headed Gull", "Chroicocephalus ridibundus", Gull, 37, 100, "#b8c0c8 #f4f4f0 #3a2a2a", Glide, Water | Town | Farm, 190, 1200, Resident, 80, 15),
        B("Little Gull", "Hydrocoloeus minutus", Gull, 27, 75, "#b8c0c8 #f4f4f0 #1e1e1e", Glide, Water, 190, 600, Passage, 1, 6),
        B("Mediterranean Gull", "Ichthyaetus melanocephalus", Gull, 38, 98, "#d8e0e8 #f4f4f0 #1e1e1e", Glide, Water | Farm, 190, 600, Summer, 1, 3),
        B("Common Gull", "Larus canus", Gull, 42, 110, "#9aa4ae #f4f4f0 #e8e070", Glide, Water | Farm, 190, 800, Winter, 6, 6),
        B("Lesser Black-backed Gull", "Larus fuscus", Gull, 55, 135, "#3a3e44 #f4f4f0 #e8d020", Glide, Water, 190, 700, Passage, 2),
        B("Herring Gull", "Larus argentatus", Gull, 60, 140, "#a8b0b8 #f4f4f0 #e8d020", Glide, Water, 190, 700, Winter, 0.5f),
        B("Caspian Gull", "Larus cachinnans", Gull, 62, 145, "#a0a8b0 #f4f4f0 #e8d020", Glide, Water, 190, 700, Winter, 2),
        B("Yellow-legged Gull", "Larus michahellis", Gull, 58, 140, "#909aa4 #f4f4f0 #e8d020", Glide, Water | Town, 190, 1000, Resident, 30, 4),
        B("Common Tern", "Sterna hirundo", Gull, 34, 82, "#c0c8d0 #f4f4f0 #d02a20", Hover, Water, 190, 600, Summer, 6, 4),
        B("Whiskered Tern", "Chlidonias hybrida", Gull, 25, 65, "#8a929a #4a4e56 #1e1e1e", Hover, Water | Wetland, 190, 600, Passage, 0.5f, 3),
        B("Black Tern", "Chlidonias niger", Gull, 24, 64, "#6a7078 #2a2a2e #1e1e1e", Hover, Water | Wetland, 190, 600, Passage, 2, 6),

        // ---- pigeons and doves ------------------------------------------------------------------------
        B("Rock Dove", "Columba livia", Pigeon, 33, 66, "#8a929c #7a828c #4a7a6a", Flap, Town | Rock, 190, 1500, Resident, 60, 12, 1, 12),
        B("Stock Dove", "Columba oenas", Pigeon, 32, 66, "#7a828c #8a8a94 #5a7a6a", Flap, Forest | Edge | Farm, 190, 1200, Resident, 6, 3),
        B("Common Wood Pigeon", "Columba palumbus", Pigeon, 41, 75, "#7a808a #a8909a #f0f0ec", Flap, Forest | Edge | Farm | Town, 190, 1800, Resident, 70, 8, 8, 2),
        B("European Turtle Dove", "Streptopelia turtur", Pigeon, 27, 50, "#a86a3a #d8b8a8 #1e1e1e", Flap, Edge | Farm, 190, 800, Summer, 1),
        B("Eurasian Collared Dove", "Streptopelia decaocto", Pigeon, 32, 50, "#c8b8a8 #d8ccc0 #1e1e1e", Flap, Town | Farm, 190, 1200, Resident, 40, 2),

        // ---- cuckoo, nightjar ---------------------------------------------------------------------------
        B("Common Cuckoo", "Cuculus canorus", Passerine, 33, 58, "#7a8088 #e8e4dc #e8c020", Flap, Edge | Wetland | Meadow | Forest, 190, 2000, Summer, 10),
        B("European Nightjar", "Caprimulgus europaeus", Passerine, 27, 57, "#6a5a48 #8a7a62 #f0f0ec", Dart, Edge | Rock, 300, 1400, Summer, 0.5f),

        // ---- owls --------------------------------------------------------------------------------------
        B("Western Barn Owl", "Tyto alba", Owl, 34, 90, "#d8a86a #f4f0e8 #f4f4f0", Flap, Farm | Town, 190, 700, Resident, 3),
        B("Eurasian Scops Owl", "Otus scops", Owl, 19, 50, "#8a7a68 #9a8a78 #e8c020", Flap, Orchard | Edge | Vineyard, 190, 1000, Summer, 0.5f),
        B("Eurasian Eagle-Owl", "Bubo bubo", Owl, 68, 170, "#8a6a42 #c8a068 #e87a1a", Flap, Rock | Forest, 190, 2000, Resident, 1),
        B("Eurasian Pygmy Owl", "Glaucidium passerinum", Owl, 17, 36, "#6a5040 #e8e0d4 #e8d020", Flap, Forest, 900, 2000, Resident, 2),
        B("Little Owl", "Athene noctua", Owl, 22, 55, "#8a6a4a #e0d4c0 #e8d020", Flap, Orchard | Farm, 190, 800, Resident, 1),
        B("Tawny Owl", "Strix aluco", Owl, 38, 95, "#8a5a32 #c8a078 #2a1e14", Flap, Forest | Edge | Town, 190, 1800, Resident, 10),
        B("Long-eared Owl", "Asio otus", Owl, 36, 95, "#a07a4a #d8b888 #e87a1a", Flap, Edge | Farm, 190, 1500, Resident, 3),
        B("Short-eared Owl", "Asio flammeus", Owl, 37, 100, "#b0905a #e0d0a8 #e8d020", Glide, Wetland | Farm, 190, 800, Winter, 0.5f),
        B("Boreal Owl", "Aegolius funereus", Owl, 25, 55, "#6a5040 #e8e0d4 #e8d020", Flap, Forest, 1000, 2000, Resident, 2),

        // ---- swifts -----------------------------------------------------------------------------------
        B("Alpine Swift", "Tachymarptis melba", Swift, 22, 57, "#6a5a48 #f0ece4 #4a3e32", Dart, Town | Rock, 190, 2200, Summer, 8, 8),
        B("Common Swift", "Apus apus", Swift, 17, 42, "#2a2622 #2e2a26 #6a6660", Dart, Town | Water | Farm, 190, 2000, Summer, 50, 12),
        B("Pallid Swift", "Apus pallidus", Swift, 17, 42, "#5a4e44 #5e5248 #a09890", Dart, Town, 190, 600, Summer, 1, 6),

        // ---- kingfisher, bee-eater, roller, hoopoe -----------------------------------------------------
        B("Common Kingfisher", "Alcedo atthis", Passerine, 16, 25, "#1a8ab0 #e07a2a #3ad0e0", Flap, Water, 190, 900, Resident, 4),
        B("European Bee-eater", "Merops apiaster", Passerine, 28, 45, "#8a5a2a #3ab0c0 #e8d020", Glide, Farm | Rock | Vineyard, 190, 800, Summer, 1, 6),
        B("European Roller", "Coracias garrulus", Passerine, 31, 70, "#3a8ac0 #50b0c8 #8a5a2a", Flap, Farm | Orchard, 190, 800, Passage, 0.3f),
        B("Eurasian Hoopoe", "Upupa epops", Passerine, 27, 45, "#d8905a #e0a878 #1e1e1e", Undulate, Vineyard | Orchard | Farm, 190, 1200, Summer, 2),

        // ---- woodpeckers ------------------------------------------------------------------------------
        B("Eurasian Wryneck", "Jynx torquilla", Woodpecker, 17, 26, "#8a7a68 #d8c8a8 #5a4a3a", Undulate, Orchard | Edge | Vineyard, 190, 1400, Summer, 3),
        B("Grey-headed Woodpecker", "Picus canus", Woodpecker, 27, 40, "#6a8a4a #9aa4a0 #c02a20", Undulate, Forest | Edge, 300, 1200, Resident, 1),
        B("European Green Woodpecker", "Picus viridis", Woodpecker, 32, 50, "#5a8a3a #b8c89a #d02a20", Undulate, Edge | Orchard | Town | Farm, 190, 1800, Resident, 15),
        B("Black Woodpecker", "Dryocopus martius", Woodpecker, 45, 72, "#1a1a1a #1e1e1e #d02020", Undulate, Forest, 300, 1900, Resident, 6),
        B("Great Spotted Woodpecker", "Dendrocopos major", Woodpecker, 23, 40, "#1e1e1e #f0ece4 #d02a20", Undulate, Forest | Edge | Town, 190, 1900, Resident, 45),
        B("Middle Spotted Woodpecker", "Dendrocoptes medius", Woodpecker, 21, 35, "#1e1e1e #f0e4d0 #e0402a", Undulate, Forest | Orchard, 190, 800, Resident, 3),
        B("White-backed Woodpecker", "Dendrocopos leucotos", Woodpecker, 25, 40, "#1e1e1e #f0ece4 #d02a20", Undulate, Forest, 600, 1500, Resident, 0.5f),
        B("Lesser Spotted Woodpecker", "Dryobates minor", Woodpecker, 15, 27, "#1e1e1e #f0ece4 #d02a20", Undulate, Edge | Orchard | Wetland, 190, 1000, Resident, 2),
        B("Eurasian Three-toed Woodpecker", "Picoides tridactylus", Woodpecker, 22, 36, "#1e1e1e #e8e4dc #e8d020", Undulate, Forest, 1000, 2000, Resident, 3),

        // ---- larks ------------------------------------------------------------------------------------
        B("Woodlark", "Lullula arborea", Passerine, 15, 29, "#8a7458 #e0d4c0 #f0e8d8", Undulate, Vineyard | Edge | Meadow, 300, 1600, Summer, 1),
        B("Eurasian Skylark", "Alauda arvensis", Passerine, 18, 33, "#8a7458 #e0d8c8 #5a4a36", Hover, Farm | Meadow | Alpine, 190, 2400, Summer, 25, 2),

        // ---- swallows and martins ----------------------------------------------------------------------
        B("Sand Martin", "Riparia riparia", Swift, 12, 28, "#7a6a58 #f0f0ec #6a5a48", Dart, Water | Rock, 190, 700, Summer, 5, 10),
        B("Eurasian Crag Martin", "Ptyonoprogne rupestris", Swift, 15, 33, "#7a6e62 #b8aca0 #5a524a", Dart, Rock, 400, 2200, Summer, 5, 4),
        B("Barn Swallow", "Hirundo rustica", Swift, 18, 33, "#1e2a4a #f0e0c8 #b02a1e", Dart, Farm | Town | Meadow, 190, 1800, Summer, 50, 6),
        B("Western House Martin", "Delichon urbicum", Swift, 13, 28, "#1e2a3e #f4f4f0 #f4f4f0", Dart, Town | Rock, 190, 2000, Summer, 35, 10),

        // ---- pipits and wagtails -------------------------------------------------------------------------
        B("Tawny Pipit", "Anthus campestris", Passerine, 17, 27, "#b0a080 #e8dcc0 #6a5a40", Undulate, Farm | Rock, 190, 900, Passage, 0.5f),
        B("Tree Pipit", "Anthus trivialis", Passerine, 15, 26, "#7a6e50 #e8dcc0 #3a3226", Undulate, Edge | Meadow, 600, 2100, Summer, 15),
        B("Meadow Pipit", "Anthus pratensis", Passerine, 14, 24, "#7a7250 #e8e0c8 #3a3226", Undulate, Farm | Wetland | Meadow, 190, 1600, Winter, 8, 6),
        B("Red-throated Pipit", "Anthus cervinus", Passerine, 15, 26, "#7a6a50 #e8c8a8 #c8704a", Undulate, Farm | Wetland, 190, 800, Passage, 0.3f),
        B("Eurasian Rock Pipit", "Anthus petrosus", Passerine, 16, 27, "#6a6a5a #c8c4b0 #4a4a40", Undulate, Water | Wetland, 190, 600, Winter, 0.3f),
        B("Water Pipit", "Anthus spinoletta", Passerine, 16, 27, "#7a766a #f0dcd0 #4a4640", Undulate, Alpine | Wetland, 190, 2700, Resident, 20, 2),
        B("Western Yellow Wagtail", "Motacilla flava", Passerine, 16, 26, "#7a8040 #f0d820 #5a6a7a", Undulate, Farm | Wetland, 190, 900, Passage, 5, 6),
        B("Grey Wagtail", "Motacilla cinerea", Passerine, 19, 26, "#7a8088 #f0d020 #1e1e1e", Undulate, Water, 190, 2000, Resident, 12),
        B("White Wagtail", "Motacilla alba", Passerine, 18, 28, "#8a9098 #f4f4f0 #1e1e1e", Undulate, Farm | Town | Water, 190, 2200, Resident, 40, 2),

        // ---- waxwing, dipper, wren, accentors --------------------------------------------------------------
        B("Bohemian Waxwing", "Bombycilla garrulus", Passerine, 18, 34, "#b08a78 #c8a898 #e8d020", Flap, Town | Edge, 190, 1200, Winter, 0.5f, 15),
        B("White-throated Dipper", "Cinclus cinclus", Passerine, 18, 28, "#3a2e28 #f4f4f0 #7a4a2a", Flap, Water, 190, 2400, Resident, 10),
        B("Eurasian Wren", "Troglodytes troglodytes", Passerine, 10, 15, "#7a5a3a #9a7a5a #5a4028", Flap, Forest | Edge | Town | Rock, 190, 2300, Resident, 60),
        B("Dunnock", "Prunella modularis", Passerine, 14, 20, "#7a5e44 #7a8088 #5a4a3a", Flap, Edge | Town | Forest, 190, 2200, Resident, 35),
        B("Alpine Accentor", "Prunella collaris", Passerine, 18, 30, "#7a7a72 #a0603a #f0f0ec", Flap, Alpine | Rock, 1800, 3100, Resident, 8, 3),

        // ---- chats and thrushes -------------------------------------------------------------------------
        B("European Robin", "Erithacus rubecula", Passerine, 14, 21, "#7a6a4e #e07a2a #d8d8d0", Flap, Forest | Edge | Town, 190, 2000, Resident, 80),
        B("Common Nightingale", "Luscinia megarhynchos", Passerine, 16, 24, "#8a6a4a #d8ccb8 #a05a32", Flap, Edge | Wetland, 190, 700, Summer, 4),
        B("Bluethroat", "Luscinia svecica", Passerine, 14, 22, "#7a6a54 #e0d8c8 #2a5ab0", Flap, Wetland | Alpine, 190, 2200, Passage, 1),
        B("Black Redstart", "Phoenicurus ochruros", Passerine, 14, 24, "#3a3a3e #1e1e22 #d0602a", Flap, Town | Rock | Alpine, 190, 3000, Summer, 60),
        B("Common Redstart", "Phoenicurus phoenicurus", Passerine, 14, 23, "#8a9098 #e07a3a #1e1e1e", Flap, Orchard | Town | Edge | Forest, 190, 2000, Summer, 8),
        B("Whinchat", "Saxicola rubetra", Passerine, 13, 22, "#7a6a50 #e8b880 #f0f0ec", Flap, Meadow | Alpine, 800, 2200, Summer, 4),
        B("European Stonechat", "Saxicola rubicola", Passerine, 12, 20, "#2a2a2a #d0703a #f0f0ec", Flap, Farm | Vineyard | Edge, 190, 1200, Summer, 4),
        B("Northern Wheatear", "Oenanthe oenanthe", Passerine, 15, 28, "#9aa0a8 #f0e0c8 #1e1e1e", Flap, Alpine | Rock, 1500, 3000, Summer, 15),
        B("Common Rock Thrush", "Monticola saxatilis", Passerine, 19, 35, "#5a7098 #d0702a #f0f0ec", Flap, Rock | Alpine, 1400, 2500, Summer, 1),
        B("Blue Rock Thrush", "Monticola solitarius", Passerine, 21, 36, "#3a4a7a #3a4a70 #2a3050", Flap, Rock | Vineyard, 190, 1500, Resident, 0.5f),
        B("Ring Ouzel", "Turdus torquatus", Passerine, 25, 42, "#1e1e1e #2a2a2a #f0f0ec", Flap, Forest | Meadow, 1200, 2200, Summer, 12),
        B("Common Blackbird", "Turdus merula", Passerine, 25, 38, "#141414 #1a1a1a #e8a020", Flap, Town | Edge | Forest | Orchard, 190, 2000, Resident, 100),
        B("Fieldfare", "Turdus pilaris", Passerine, 25, 42, "#7a5a3a #e8c890 #8a929a", Flap, Farm | Orchard | Edge, 190, 1800, Resident, 20, 12),
        B("Song Thrush", "Turdus philomelos", Passerine, 22, 35, "#7a6448 #f0e4c8 #3a2e22", Flap, Forest | Edge | Town, 190, 2000, Resident, 40),
        B("Redwing", "Turdus iliacus", Passerine, 21, 34, "#6a5a44 #f0e8d8 #c0402a", Flap, Farm | Edge | Orchard, 190, 1000, Winter, 10, 15),
        B("Mistle Thrush", "Turdus viscivorus", Passerine, 28, 45, "#8a7a62 #f0e8d8 #3a3226", Flap, Forest | Meadow | Edge, 300, 2100, Resident, 20),

        // ---- warblers, crests ----------------------------------------------------------------------------
        B("Cetti's Warbler", "Cettia cetti", Passerine, 14, 18, "#7a4e32 #c8b8a8 #6a4228", Flap, Wetland, 190, 600, Resident, 1),
        B("Long-tailed Tit", "Aegithalos caudatus", Passerine, 14, 18, "#3a2e2e #f0e0e0 #d8909a", Undulate, Edge | Forest | Town, 190, 1500, Resident, 20, 8),
        B("Wood Warbler", "Phylloscopus sibilatrix", Passerine, 12, 22, "#7a8a42 #f4f4f0 #e0e060", Flap, Forest, 300, 1400, Summer, 4),
        B("Western Bonelli's Warbler", "Phylloscopus bonelli", Passerine, 11, 18, "#8a8a70 #f4f4f0 #b0c040", Flap, Forest | Rock, 400, 1800, Summer, 5),
        B("Willow Warbler", "Phylloscopus trochilus", Passerine, 11, 19, "#7a7a4a #e8e0b0 #d8d080", Flap, Edge | Wetland, 190, 1800, Summer, 5),
        B("Common Chiffchaff", "Phylloscopus collybita", Passerine, 11, 18, "#6a6a4a #d8d4b8 #3a3a2a", Flap, Forest | Edge | Town, 190, 2000, Summer, 60),
        B("Eurasian Blackcap", "Sylvia atricapilla", Passerine, 14, 22, "#7a7a72 #b8b8b0 #1e1e1e", Flap, Forest | Edge | Town, 190, 1800, Summer, 90),
        B("Garden Warbler", "Sylvia borin", Passerine, 14, 22, "#7a7462 #d8d0c0 #8a8472", Flap, Edge | Wetland, 190, 2000, Summer, 12),
        B("Lesser Whitethroat", "Curruca curruca", Passerine, 13, 19, "#7a7670 #f0f0ec #4a4a4a", Flap, Edge | Meadow, 190, 2300, Summer, 5),
        B("Common Whitethroat", "Curruca communis", Passerine, 14, 21, "#8a7054 #f0e0d8 #8a8a8a", Flap, Edge | Farm, 190, 1400, Summer, 3),
        B("Great Reed Warbler", "Acrocephalus arundinaceus", Passerine, 19, 26, "#8a7054 #e8dcc0 #f0e8d8", Flap, Wetland, 190, 600, Summer, 0.5f),
        B("Eurasian Reed Warbler", "Acrocephalus scirpaceus", Passerine, 13, 19, "#8a7054 #e8dcc0 #f0e8d8", Flap, Wetland, 190, 700, Summer, 10),
        B("Marsh Warbler", "Acrocephalus palustris", Passerine, 13, 19, "#7a7454 #e8e0c0 #f0e8d8", Flap, Wetland | Farm, 190, 1000, Summer, 5),
        B("Sedge Warbler", "Acrocephalus schoenobaenus", Passerine, 13, 19, "#8a7454 #e8dcc0 #f0e8d0", Flap, Wetland, 190, 600, Passage, 1),
        B("Melodious Warbler", "Hippolais polyglotta", Passerine, 13, 19, "#7a7a4a #f0e060 #6a6a4a", Flap, Edge, 190, 700, Summer, 1),
        B("Icterine Warbler", "Hippolais icterina", Passerine, 13, 22, "#7a8050 #f0e080 #d8d8b8", Flap, Edge | Forest, 190, 900, Summer, 0.3f),
        B("Common Grasshopper Warbler", "Locustella naevia", Passerine, 13, 18, "#7a6e50 #e0d8c0 #4a4232", Flap, Wetland | Edge, 190, 1200, Summer, 1),
        B("Savi's Warbler", "Locustella luscinioides", Passerine, 14, 20, "#7a5e42 #d8c8b0 #6a5a42", Flap, Wetland, 190, 600, Summer, 0.3f),
        B("Goldcrest", "Regulus regulus", Passerine, 9, 15, "#6a7a4a #d8d8b8 #f0c020", Flap, Forest, 190, 2200, Resident, 60, 3),
        B("Common Firecrest", "Regulus ignicapilla", Passerine, 9, 15, "#5a8a3a #e8e4d8 #f07a1a", Flap, Forest | Edge, 190, 1800, Summer, 40),

        // ---- flycatchers --------------------------------------------------------------------------------
        B("Spotted Flycatcher", "Muscicapa striata", Passerine, 14, 24, "#7a7266 #f0ece4 #5a524a", Dart, Edge | Town | Orchard | Forest, 190, 1900, Summer, 10),
        B("European Pied Flycatcher", "Ficedula hypoleuca", Passerine, 13, 23, "#2a2a2a #f4f4f0 #f4f4f0", Dart, Forest | Orchard, 190, 1200, Passage, 3),
        B("Collared Flycatcher", "Ficedula albicollis", Passerine, 13, 23, "#1e1e1e #f4f4f0 #f4f4f0", Dart, Forest | Orchard, 190, 900, Summer, 0.5f),

        // ---- reedling, tits, nuthatch, wallcreeper, treecreepers -------------------------------------------
        B("Bearded Reedling", "Panurus biarmicus", Passerine, 15, 17, "#c89a6a #d8b890 #8a9aa8", Undulate, Wetland, 190, 600, Winter, 0.5f, 6),
        B("Marsh Tit", "Poecile palustris", Passerine, 12, 19, "#8a7a6a #e8e0d4 #1e1e1e", Flap, Forest | Edge | Orchard, 190, 1400, Resident, 20),
        B("Willow Tit", "Poecile montanus", Passerine, 12, 19, "#8a7e70 #e8e0d4 #1e1e1e", Flap, Forest, 800, 2100, Resident, 12),
        B("European Crested Tit", "Lophophanes cristatus", Passerine, 12, 19, "#8a7a62 #e8e0d4 #1e1e1e", Flap, Forest, 300, 2100, Resident, 20),
        B("Coal Tit", "Periparus ater", Passerine, 11, 18, "#5a6068 #e0d8c8 #1e1e1e", Flap, Forest, 190, 2200, Resident, 70, 3),
        B("Great Tit", "Parus major", Passerine, 14, 24, "#6a8050 #f0d830 #1e1e1e", Flap, Forest | Edge | Town | Orchard, 190, 1900, Resident, 100, 2),
        B("Eurasian Blue Tit", "Cyanistes caeruleus", Passerine, 12, 19, "#6a8a5a #f0e030 #3a70c0", Flap, Edge | Town | Orchard | Forest, 190, 1600, Resident, 70, 2),
        B("Eurasian Penduline Tit", "Remiz pendulinus", Passerine, 11, 17, "#a0703a #e8d8c8 #1e1e1e", Flap, Wetland, 190, 600, Passage, 0.5f, 3),
        B("Eurasian Nuthatch", "Sitta europaea", Passerine, 14, 25, "#6a7a90 #e0a870 #1e1e1e", Flap, Forest | Edge | Town, 190, 1700, Resident, 45),
        B("Wallcreeper", "Tichodroma muraria", Passerine, 16, 29, "#8a9098 #6a6a6a #d02a4a", Undulate, Rock, 1000, 2800, Resident, 1),
        B("Eurasian Treecreeper", "Certhia familiaris", Passerine, 12, 19, "#7a6448 #f4f4f0 #a08a68", Flap, Forest, 400, 2000, Resident, 20),
        B("Short-toed Treecreeper", "Certhia brachydactyla", Passerine, 12, 19, "#7a6448 #e8e0d4 #8a7a62", Flap, Edge | Town | Orchard, 190, 1000, Resident, 15),

        // ---- oriole, shrikes ------------------------------------------------------------------------------
        B("Eurasian Golden Oriole", "Oriolus oriolus", Passerine, 24, 45, "#f0d020 #f0d020 #1e1e1e", Flap, Edge | Wetland, 190, 700, Summer, 3),
        B("Red-backed Shrike", "Lanius collurio", Passerine, 17, 28, "#a0603a #f0e0d8 #8a9098", Flap, Edge | Meadow | Farm, 190, 1800, Summer, 12),
        B("Great Grey Shrike", "Lanius excubitor", Passerine, 24, 34, "#9aa0a8 #f4f4f0 #1e1e1e", Flap, Farm | Wetland, 190, 900, Winter, 0.5f),
        B("Woodchat Shrike", "Lanius senator", Passerine, 18, 30, "#1e1e1e #f4f4f0 #b0502a", Flap, Orchard | Vineyard, 190, 800, Summer, 0.3f),

        // ---- crows ----------------------------------------------------------------------------------------
        B("Eurasian Jay", "Garrulus glandarius", Corvid, 34, 54, "#b89a8a #d0b8a8 #3a70c0", Flap, Forest | Edge | Town, 190, 1800, Resident, 40, 2, 8, 2),
        B("Eurasian Magpie", "Pica pica", Corvid, 45, 56, "#1e1e1e #f4f4f0 #2a4a6a", Flap, Town | Farm | Edge, 190, 1500, Resident, 40, 2, 8, 2),
        B("Northern Nutcracker", "Nucifraga caryocatactes", Corvid, 33, 55, "#5a4230 #6a5240 #f4f4f0", Flap, Forest, 1000, 2200, Resident, 15),
        B("Alpine Chough", "Pyrrhocorax graculus", Corvid, 38, 80, "#141414 #1a1a1a #e8d020", Soar, Alpine | Rock | Snow, 1500, 3500, Resident, 30, 15),
        B("Red-billed Chough", "Pyrrhocorax pyrrhocorax", Corvid, 40, 80, "#141418 #1a1a1e #d02a20", Soar, Alpine | Rock, 1500, 2800, Resident, 1, 6),
        B("Western Jackdaw", "Coloeus monedula", Corvid, 34, 70, "#2a2a2e #3a3a3e #9aa0a8", Flap, Town | Farm | Rock, 190, 1200, Resident, 10, 10),
        B("Rook", "Corvus frugilegus", Corvid, 45, 90, "#1a1a22 #1e1e26 #b8b0a8", Flap, Farm | Town, 190, 800, Resident, 10, 20),
        B("Carrion Crow", "Corvus corone", Corvid, 47, 98, "#141414 #181818 #1e1e1e", Flap, Farm | Town | Meadow | Edge, 190, 2200, Resident, 100, 4, 8, 2),
        B("Hooded Crow", "Corvus cornix", Corvid, 47, 98, "#8a8a8a #9a9a98 #141414", Flap, Farm | Town | Meadow, 190, 2200, Resident, 4, 3, 8, 2),
        B("Northern Raven", "Corvus corax", Corvid, 64, 130, "#101010 #141414 #1a1a2a", Soar, Rock | Alpine | Forest | Meadow, 400, 3000, Resident, 15, 2),

        // ---- starling, sparrows, snowfinch ------------------------------------------------------------------
        B("Common Starling", "Sturnus vulgaris", Passerine, 21, 40, "#2a2a30 #3a3a42 #e8d020", Flap, Farm | Town | Orchard | Wetland, 190, 1400, Resident, 60, 25),
        B("House Sparrow", "Passer domesticus", Passerine, 15, 23, "#8a6040 #b8b0a8 #1e1e1e", Flap, Town | Farm, 190, 2000, Resident, 90, 8),
        B("Italian Sparrow", "Passer italiae", Passerine, 15, 23, "#9a5a32 #d8d4cc #1e1e1e", Flap, Town | Vineyard, 190, 1500, Resident, 8, 6),
        B("Eurasian Tree Sparrow", "Passer montanus", Passerine, 14, 21, "#8a5a32 #d0c8bc #6a3a20", Flap, Farm | Orchard | Edge, 190, 900, Resident, 20, 8),
        B("White-winged Snowfinch", "Montifringilla nivalis", Passerine, 18, 36, "#8a7a68 #f0ece4 #f4f4f0", Flap, Alpine | Rock | Snow, 1900, 3200, Resident, 10, 8),

        // ---- finches ---------------------------------------------------------------------------------------
        B("Common Chaffinch", "Fringilla coelebs", Passerine, 15, 26, "#7a5a4a #d0907a #6a7a90", Undulate, Forest | Edge | Town | Orchard, 190, 2200, Resident, 100, 6),
        B("Brambling", "Fringilla montifringilla", Passerine, 15, 26, "#2a2a2a #e8a050 #f4f4f0", Undulate, Forest | Farm, 190, 1500, Winter, 10, 40),
        B("Hawfinch", "Coccothraustes coccothraustes", Passerine, 18, 31, "#a0703a #c8a088 #5a6a8a", Undulate, Forest | Edge, 190, 1100, Resident, 6, 3),
        B("Eurasian Bullfinch", "Pyrrhula pyrrhula", Passerine, 16, 26, "#8a9098 #e0603a #1e1e1e", Undulate, Forest | Edge, 300, 2000, Resident, 15, 2),
        B("Common Rosefinch", "Carpodacus erythrinus", Passerine, 15, 25, "#8a5a4a #e8d0c8 #c02a3a", Undulate, Edge | Meadow, 800, 2000, Summer, 0.3f),
        B("European Greenfinch", "Chloris chloris", Passerine, 15, 26, "#6a7a3a #a0b040 #f0d820", Undulate, Town | Edge | Orchard, 190, 1400, Resident, 30, 4),
        B("Common Linnet", "Linaria cannabina", Passerine, 13, 23, "#8a6a4a #d8b8a8 #c03a3a", Undulate, Vineyard | Meadow | Alpine, 190, 2400, Summer, 10, 8),
        B("Lesser Redpoll", "Acanthis cabaret", Passerine, 12, 21, "#7a6a50 #e8d8d0 #c02a3a", Undulate, Forest | Edge, 1000, 2200, Resident, 12, 6),
        B("Red Crossbill", "Loxia curvirostra", Passerine, 16, 28, "#b03a2a #c04a32 #5a3a2a", Undulate, Forest, 600, 2200, Resident, 20, 8),
        B("European Goldfinch", "Carduelis carduelis", Passerine, 12, 23, "#a08060 #f0ece4 #d02a20", Undulate, Town | Orchard | Farm | Edge, 190, 1500, Resident, 40, 8),
        B("Citril Finch", "Carduelis citrinella", Passerine, 12, 23, "#8a9a50 #b8c050 #8a9098", Undulate, Forest | Meadow, 1200, 2200, Summer, 8, 6),
        B("European Serin", "Serinus serinus", Passerine, 11, 21, "#8a8a3a #f0e030 #5a5a3a", Undulate, Town | Vineyard | Orchard, 190, 1300, Summer, 15, 4),
        B("Eurasian Siskin", "Spinus spinus", Passerine, 12, 21, "#6a7a3a #e0e050 #1e1e1e", Undulate, Forest, 190, 2000, Resident, 25, 15),

        // ---- buntings ---------------------------------------------------------------------------------------
        B("Corn Bunting", "Emberiza calandra", Passerine, 18, 29, "#8a7a5a #e0d4b8 #5a4a36", Flap, Farm, 190, 700, Summer, 0.5f),
        B("Yellowhammer", "Emberiza citrinella", Passerine, 16, 26, "#8a6a3a #f0d830 #f0e030", Undulate, Farm | Edge | Meadow, 190, 1800, Resident, 35, 4),
        B("Rock Bunting", "Emberiza cia", Passerine, 16, 25, "#9a6a4a #c07a4a #8a9098", Undulate, Rock | Vineyard | Meadow, 400, 2200, Resident, 5),
        B("Cirl Bunting", "Emberiza cirlus", Passerine, 16, 25, "#7a6a4a #e8d040 #3a4a2a", Undulate, Vineyard | Orchard, 190, 900, Resident, 1),
        B("Ortolan Bunting", "Emberiza hortulana", Passerine, 16, 25, "#8a7050 #c07a4a #b0b890", Undulate, Vineyard | Farm | Rock, 190, 1600, Passage, 0.3f),
        B("Common Reed Bunting", "Emberiza schoeniclus", Passerine, 15, 24, "#7a5a3a #e8e0d4 #1e1e1e", Undulate, Wetland, 190, 900, Resident, 8, 4),
        B("Snow Bunting", "Plectrophenax nivalis", Passerine, 16, 34, "#c8a878 #f4f4f0 #f4f4f0", Undulate, Alpine | Snow, 1500, 3000, Winter, 0.5f, 10),
    };

    public static BirdSpecies? ByName(string name) => All.FirstOrDefault(s => s.Name == name);
}
