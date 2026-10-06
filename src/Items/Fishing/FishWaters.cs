namespace UnitSport.Items.Fishing;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A big lake: a centre and a reach in LV95 (approximate, ±2 km), the local whitefish's name and the
/// fish it is known for (more likely there). Names and species: Eawag / Projet Lac, the cantonal catch
/// statistics (docs/notes/items/fishing.md).
/// </summary>
public sealed record Lake(string Name, double E, double N, double Reach, int Altitude, string Whitefish,
    ItemId[] Known, bool WhitefishBan = false);

/// <summary>Where a float landed, as the catch rules see it.</summary>
public readonly record struct FishSpot(WaterKind Kind, Basin Basin, double Altitude, Lake? Lake, string Place)
{
    public string Describe() => Lake?.Name ?? Kind switch
    {
        WaterKind.River => "a river",
        WaterKind.MountainStream => "a mountain stream",
        WaterKind.AlpineLake => "an alpine lake",
        _ => "a pond",
    };
}

public static class FishWaters
{
    private static ItemId[] K(params ItemId[] ids) => ids;

    /// <summary>The lakes the catch rules know by name, biggest first.</summary>
    public static readonly Lake[] Lakes =
    {
        new("Lake Geneva", 2_530_000, 1_140_000, 36_000, 372, "Féra",
            K(ItemId.Perch, ItemId.Whitefish, ItemId.Pike, ItemId.ArcticChar, ItemId.LakeTrout, ItemId.Burbot, ItemId.Roach)),
        new("Lake Constance", 2_742_000, 1_278_000, 22_000, 395, "Blaufelchen",
            K(ItemId.Perch, ItemId.Whitefish, ItemId.LakeTrout, ItemId.ArcticChar, ItemId.Pike, ItemId.Zander, ItemId.Burbot),
            WhitefishBan: true),
        new("Lake Neuchâtel", 2_554_000, 1_194_000, 20_000, 429, "Palée",
            K(ItemId.Whitefish, ItemId.Perch, ItemId.Pike, ItemId.Zander, ItemId.Wels, ItemId.Burbot)),
        new("Lake Maggiore", 2_705_000, 1_110_000, 8_000, 193, "Lavarello",
            K(ItemId.Agone, ItemId.Whitefish, ItemId.Perch, ItemId.Zander, ItemId.LargemouthBass, ItemId.Roach)),
        new("Lake Lucerne", 2_675_000, 1_207_000, 15_000, 434, "Edelfisch",
            K(ItemId.Whitefish, ItemId.Perch, ItemId.ArcticChar, ItemId.LakeTrout, ItemId.Pike, ItemId.Burbot)),
        new("Lake Zurich", 2_690_000, 1_236_000, 15_000, 406, "Albeli",
            K(ItemId.Whitefish, ItemId.Perch, ItemId.Pike, ItemId.ArcticChar, ItemId.LakeTrout, ItemId.Zander)),
        new("Lake Lugano", 2_720_000, 1_093_000, 9_000, 271, "Lavarello",
            K(ItemId.Perch, ItemId.Zander, ItemId.Whitefish, ItemId.LargemouthBass, ItemId.Pike)),
        new("Lake Thun", 2_622_000, 1_170_000, 9_000, 558, "Balchen",
            K(ItemId.Whitefish, ItemId.LakeTrout, ItemId.ArcticChar, ItemId.Perch, ItemId.Pike)),
        new("Lake Biel", 2_580_000, 1_217_000, 8_000, 429, "Bondelle",
            K(ItemId.Whitefish, ItemId.Perch, ItemId.Pike, ItemId.Zander, ItemId.Wels)),
        new("Lake Zug", 2_680_000, 1_217_000, 7_000, 413, "Zugerbalchen",
            K(ItemId.ArcticChar, ItemId.Whitefish, ItemId.Perch, ItemId.Pike, ItemId.LakeTrout)),
        new("Lake Brienz", 2_640_000, 1_176_000, 8_000, 564, "Brienzlig",
            K(ItemId.Whitefish, ItemId.LakeTrout, ItemId.ArcticChar)),
        new("Lake Walen", 2_730_000, 1_220_000, 8_000, 419, "Balchen",
            K(ItemId.Whitefish, ItemId.ArcticChar, ItemId.LakeTrout, ItemId.Perch, ItemId.Pike)),
        new("Lake Murten", 2_575_000, 1_197_000, 5_000, 429, "Felchen",
            K(ItemId.Wels, ItemId.Zander, ItemId.Perch, ItemId.Pike, ItemId.Roach, ItemId.Bream)),
        new("Lake Sarnen", 2_662_000, 1_191_000, 4_000, 469, "Sarnerfelchen",
            K(ItemId.Whitefish, ItemId.ArcticChar, ItemId.LakeTrout, ItemId.Perch)),
        new("Lake Sils", 2_776_000, 1_143_000, 3_500, 1797, "Felchen",
            K(ItemId.Namaycush, ItemId.ArcticChar, ItemId.BrownTrout)),
        new("Lake Silvaplana", 2_780_000, 1_146_000, 2_500, 1791, "Felchen",
            K(ItemId.ArcticChar, ItemId.Namaycush, ItemId.BrookTrout)),
        new("Lake St. Moritz", 2_784_000, 1_152_000, 1_500, 1768, "Felchen",
            K(ItemId.ArcticChar, ItemId.RainbowTrout, ItemId.BrownTrout)),
    };

    /// <summary>The named lake at a point of still water: the one whose reach covers it the most, if any.</summary>
    public static Lake? LakeAt(double e, double n, double altitude)
    {
        Lake? best = null;
        double bestShare = 1;
        foreach (var l in Lakes)
        {
            // lake levels are known to the metre: a reservoir above Lake Lucerne is not Lake Lucerne,
            // and Lake Zug (413 m) is not Lake Lucerne (434 m) where their reaches overlap
            if (Math.Abs(altitude - l.Altitude) > 12) continue;
            double share = Math.Sqrt((e - l.E) * (e - l.E) + (n - l.N) * (n - l.N)) / l.Reach;
            if (share < bestShare) (best, bestShare) = (l, share);
        }
        return best;
    }

    /// <summary>
    /// The drainage basin at an LV95 point, from a few lines along the watersheds (checked against towns
    /// in <c>FishingTests</c>): south of the Alps the Po, the Engadin the Inn, Valais and Léman the Rhone,
    /// the far Jura the Doubs; the rest of the country drains to the Rhine.
    /// </summary>
    public static Basin BasinAt(double e, double n)
    {
        double ek = e / 1000 - 2000, nk = n / 1000 - 1000;   // km, LV03-sized numbers
        // Poschiavo and the south: the Adda, Po basin
        if (ek >= 795 && nk < 140) return Basin.Ticino;
        // Ticino, Misox, Bergell: south of the main ridge (the Rheinwald behind Splügen is the Rhine's)
        if (ek >= 676 && ek < 775 && nk < 165 && !(ek >= 733 && nk >= 150)) return Basin.Ticino;
        // the Engadin, east of a line from Maloja (770, 140) to the Silvretta (805, 195)
        if (nk >= 135 && nk < 215 && ek > 770 + 35 * (nk - 140) / 55) return Basin.Inn;
        // Léman's shores, and Valais below its ridges
        if ((ek < 570 && nk < 162) || (ek < 640 && nk < 140) || (ek >= 640 && ek < 676 && nk < 157)) return Basin.Rhone;
        // the Jura west of a line from Le Locle (545, 205) to Delémont (590, 250)
        if (nk >= 205 && nk < 265 && ek < 600 && ek < 545 + (nk - 205)) return Basin.Doubs;
        return Basin.Rhine;
    }

    /// <summary>
    /// What kind of water a float lies in. <paramref name="onLayer"/>: the water layer has a surface there
    /// (lakes, ponds, the wider rivers); otherwise a stream line. <paramref name="slope"/>: the still surface's
    /// fall per metre (a lake is flat, a river is not). <paramref name="lake"/>: the named lake there, if any.
    /// </summary>
    public static WaterKind Classify(bool onLayer, double altitude, double slope, Lake? lake)
    {
        if (!onLayer) return altitude > 1000 ? WaterKind.MountainStream : WaterKind.River;
        if (slope > 0.002) return altitude > 1300 ? WaterKind.MountainStream : WaterKind.River;
        if (lake != null) return lake.Altitude > 1500 ? WaterKind.AlpineLake : WaterKind.LargeLake;
        return altitude > 1500 ? WaterKind.AlpineLake : WaterKind.SmallLake;
    }

    public static FishSpot Spot(bool onLayer, double e, double n, double altitude, double slope, string place = "")
    {
        var lake = onLayer && slope <= 0.002 ? LakeAt(e, n, altitude) : null;
        return new FishSpot(Classify(onLayer, altitude, slope, lake), BasinAt(e, n), altitude, lake, place);
    }
}
