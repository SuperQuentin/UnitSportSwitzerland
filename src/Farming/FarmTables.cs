using UnitSport.Items;
using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md), like the crafting rules.

namespace UnitSport.Farming;

/// <summary>
/// Where a field cell is in its year (#494). <see cref="Natural"/> is the default of every cell
/// nobody has touched: it shows the field's real crop at the stage the calendar month gives it
/// (<see cref="FarmTables.NaturalStage"/>). Only what players do is stored.
///
/// <para><b>Append-only</b>: saved and replicated as a byte.</para>
/// </summary>
public enum FieldStage : byte
{
    /// <summary>Untouched: the real crop at the season's stage.</summary>
    Natural = 0,
    /// <summary>Cut stalks after a harvest: can be ploughed.</summary>
    Stubble = 1,
    /// <summary>Turned soil: can be sown.</summary>
    Ploughed = 2,
    /// <summary>Sown; the crop grows from <see cref="CellState.Since"/> (drawn as sprouting, growing, ripe).</summary>
    Sown = 3,
    /// <summary>A grown crop (computed from <see cref="Sown"/> and time, never stored).</summary>
    Ripe = 4,
    /// <summary>Grass (meadow, pasture, fallow): can be mown or ploughed.</summary>
    Grass = 5,
    /// <summary>Mown grass, regrowing from <see cref="CellState.Since"/> to <see cref="Grass"/>.</summary>
    Mown = 6,
    /// <summary>Young crop, before ripe (computed, never stored).</summary>
    Growing = 7,
}

/// <summary>What works the ground (#494): an implement lowered behind a tractor, a combine, or a hand tool.</summary>
public enum FarmTool : byte
{
    None = 0,
    /// <summary>Stubble, grass or a ripe/growing crop -> ploughed (a plough, or the hoe by hand).</summary>
    Plough = 1,
    /// <summary>Ploughed -> sown with a seed (a seed drill, or a seed bag by hand).</summary>
    Sow = 2,
    /// <summary>Ripe -> stubble, giving the crop's harvest (a combine, or by hand).</summary>
    Harvest = 3,
    /// <summary>Grass -> mown, giving hay (a mower).</summary>
    Mow = 4,
    /// <summary>Sown: ripens sooner (fertiliser by hand).</summary>
    Fertilise = 5,
}

/// <summary>
/// A worked cell as stored and replicated: the stage, the crop it carries (a sown crop, or the
/// field's crop when stubble), when it entered the stage (server Unix seconds) and whether it was fertilised.
/// </summary>
public readonly record struct CellState(FieldStage Stage, CropKind Crop, uint Since, bool Fertilised = false);

/// <summary>The farming numbers: which seed and harvest each crop has, how long it grows, how much a cell gives.</summary>
public static class FarmTables
{
    /// <summary>Seconds a sown crop takes to ripen (compressed: a field grows within a session).</summary>
    public static int GrowSeconds(CropKind crop) => crop switch
    {
        CropKind.Vegetables => 25 * 60,
        CropKind.Legumes => 30 * 60,
        CropKind.Barley => 35 * 60,
        CropKind.Wheat or CropKind.Sunflower or CropKind.OtherArable => 40 * 60,
        CropKind.Potato or CropKind.Rapeseed => 45 * 60,
        CropKind.Maize => 50 * 60,
        CropKind.SugarBeet => 55 * 60,
        _ => 20 * 60, // grass regrowing after the mower
    };

    /// <summary>Fertilised, a crop grows in this share of its time.</summary>
    public const float FertilisedGrowth = 0.6f;

    /// <summary>A sown crop is drawn ripe from this share of its growing time (sprouting before 0.2, growing between).</summary>
    public const float SproutShare = 0.2f;

    /// <summary>The seed that sows a crop; None for grass (meadows are not sown in the game).</summary>
    public static ItemId SeedFor(CropKind crop) => crop switch
    {
        CropKind.Wheat => ItemId.WheatSeed,
        CropKind.Barley => ItemId.BarleySeed,
        CropKind.Maize => ItemId.MaizeSeed,
        CropKind.Potato => ItemId.SeedPotato,
        CropKind.Rapeseed => ItemId.RapeSeed,
        CropKind.Sunflower => ItemId.SunflowerSeed,
        CropKind.SugarBeet => ItemId.SugarBeetSeed,
        CropKind.Vegetables => ItemId.VegetableSeeds,
        CropKind.Legumes => ItemId.PeaSeed,
        _ => ItemId.None,
    };

    /// <summary>The crop a seed item sows; None when it is not a seed.</summary>
    public static CropKind CropOf(ItemId seed) => seed switch
    {
        ItemId.WheatSeed => CropKind.Wheat,
        ItemId.BarleySeed => CropKind.Barley,
        ItemId.MaizeSeed => CropKind.Maize,
        ItemId.SeedPotato => CropKind.Potato,
        ItemId.RapeSeed => CropKind.Rapeseed,
        ItemId.SunflowerSeed => CropKind.Sunflower,
        ItemId.SugarBeetSeed => CropKind.SugarBeet,
        ItemId.VegetableSeeds => CropKind.Vegetables,
        ItemId.PeaSeed => CropKind.Legumes,
        _ => CropKind.None,
    };

    /// <summary>What a ripe crop (or mown grass) gives.</summary>
    public static ItemId YieldOf(CropKind crop) => crop switch
    {
        CropKind.Wheat or CropKind.OtherArable => ItemId.Wheat,
        CropKind.Barley => ItemId.Barley,
        CropKind.Maize => ItemId.Maize,
        CropKind.Potato => ItemId.Potato,
        CropKind.Rapeseed => ItemId.Rapeseed,
        CropKind.Sunflower => ItemId.SunflowerSeeds,
        CropKind.SugarBeet => ItemId.SugarBeet,
        CropKind.Vegetables => ItemId.Carrot,
        CropKind.Legumes => ItemId.Peas,
        CropKind.Meadow or CropKind.Pasture or CropKind.Fallow => ItemId.HayBale,
        _ => ItemId.None,
    };

    /// <summary>Whether an item is a crop's harvest (what a co-op takes by the load); seeds and fertiliser are not.</summary>
    public static bool IsHarvest(ItemId item)
    {
        if (item == ItemId.None) return false;
        foreach (var c in Enum.GetValues<CropKind>()) if (YieldOf(c) == item) return true;
        return false;
    }

    /// <summary>
    /// Items one 4 m cell gives (fractions add up along a machine's pass). Scaled to the real
    /// yields (wheat 6.5 t/ha, potatoes 40 t/ha, hay 8 t DM/ha) with one item a 50 kg sack of
    /// grain, a 10 kg sack of potatoes / beet / carrots, a 250 kg round bale of hay.
    /// </summary>
    public static float YieldPerCell(CropKind crop) => crop switch
    {
        CropKind.Wheat or CropKind.OtherArable => 0.2f,
        CropKind.Barley => 0.2f,
        CropKind.Maize => 0.3f,
        CropKind.Rapeseed or CropKind.Sunflower or CropKind.Legumes => 0.1f,
        CropKind.Potato or CropKind.Vegetables => 1.0f,
        CropKind.SugarBeet => 1.5f,
        CropKind.Meadow or CropKind.Pasture or CropKind.Fallow => 0.05f,
        _ => 0f,
    };

    /// <summary>Hand-harvesting a cell gives at least this many items (a machine gives <see cref="YieldPerCell"/>).</summary>
    public const int HandYieldMin = 1;

    /// <summary>One seed item sows this many cells (a bag covers 800 m²; gameplay, not agronomy).</summary>
    public const int CellsPerSeed = 50;

    /// <summary>Whether a tool works a cell at this stage.</summary>
    public static bool CanWork(FarmTool tool, FieldStage stage) => tool switch
    {
        FarmTool.Plough => stage is FieldStage.Stubble or FieldStage.Grass or FieldStage.Mown or FieldStage.Growing or FieldStage.Ripe,
        FarmTool.Sow => stage == FieldStage.Ploughed,
        FarmTool.Harvest => stage == FieldStage.Ripe,
        FarmTool.Mow => stage == FieldStage.Grass,
        FarmTool.Fertilise => stage is FieldStage.Sown or FieldStage.Growing,
        _ => false,
    };

    /// <summary>Grass crops: mown, never harvested by a combine, never sown.</summary>
    public static bool IsGrass(CropKind crop) => crop is CropKind.Meadow or CropKind.Pasture or CropKind.Fallow;

    /// <summary>
    /// The stage an untouched field of <paramref name="crop"/> shows in <paramref name="month"/>
    /// (1..12), from the Swiss Plateau's calendar: winter wheat and barley sown in October and
    /// harvested in July, maize sown in May and cut in September-October, potatoes planted in
    /// April and lifted in August-September, rapeseed sown in August and threshed in July,
    /// sunflower and beet sown in April, beet lifted from October. Grass is grass the year round.
    /// </summary>
    public static FieldStage NaturalStage(CropKind crop, int month) => crop switch
    {
        CropKind.Meadow or CropKind.Pasture or CropKind.Fallow or CropKind.None => FieldStage.Grass,
        CropKind.Wheat or CropKind.Barley or CropKind.OtherArable => month switch
        {
            7 => FieldStage.Ripe, 8 or 9 => FieldStage.Stubble, 10 => FieldStage.Ploughed, 11 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.Rapeseed => month switch
        {
            7 => FieldStage.Ripe, 8 => FieldStage.Ploughed, 9 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.Maize => month switch
        {
            9 or 10 => FieldStage.Ripe, 11 or 12 or 1 or 2 or 3 => FieldStage.Stubble, 4 => FieldStage.Ploughed, 5 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.Potato => month switch
        {
            8 or 9 => FieldStage.Ripe, 10 or 11 or 12 or 1 or 2 => FieldStage.Stubble, 3 => FieldStage.Ploughed, 4 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.SugarBeet => month switch
        {
            10 or 11 => FieldStage.Ripe, 12 or 1 or 2 => FieldStage.Stubble, 3 => FieldStage.Ploughed, 4 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.Sunflower or CropKind.Legumes => month switch
        {
            8 or 9 => FieldStage.Ripe, 10 or 11 or 12 or 1 or 2 => FieldStage.Stubble, 3 => FieldStage.Ploughed, 4 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        CropKind.Vegetables => month switch
        {
            >= 6 and <= 10 => FieldStage.Ripe, 11 or 12 or 1 or 2 => FieldStage.Stubble, 3 => FieldStage.Ploughed, 4 => FieldStage.Sown, _ => FieldStage.Growing,
        },
        _ => FieldStage.Grass,
    };

    /// <summary>
    /// What a stored cell shows at <paramref name="now"/> (server Unix seconds): a sown crop
    /// sprouts (<see cref="FieldStage.Sown"/>), grows (<see cref="FieldStage.Growing"/>) and
    /// ripens (<see cref="FieldStage.Ripe"/>) with time; mown grass grows back to
    /// <see cref="FieldStage.Grass"/>. <paramref name="growth"/> is 0..1 of the way to ripe.
    /// </summary>
    public static FieldStage Now(CellState s, long now, out float growth)
    {
        growth = 0f;
        if (s.Stage is not (FieldStage.Sown or FieldStage.Mown)) { growth = s.Stage == FieldStage.Ripe ? 1f : 0f; return s.Stage; }
        float total = s.Stage == FieldStage.Mown ? GrowSeconds(CropKind.Meadow) : GrowSeconds(s.Crop) * (s.Fertilised ? FertilisedGrowth : 1f);
        growth = Math.Clamp((now - s.Since) / total, 0f, 1f);
        if (s.Stage == FieldStage.Mown) return growth >= 1f ? FieldStage.Grass : FieldStage.Mown;
        return growth >= 1f ? FieldStage.Ripe : growth >= SproutShare ? FieldStage.Growing : FieldStage.Sown;
    }

    /// <summary>
    /// The cell a tool leaves behind. Null when the tool does not work this stage. Ploughing keeps
    /// the field's crop on the cell (for what it was); sowing sets the seed's crop.
    /// </summary>
    public static CellState? Apply(FarmTool tool, FieldStage stageNow, CellState current, CropKind seed, uint now)
    {
        if (!CanWork(tool, stageNow)) return null;
        return tool switch
        {
            FarmTool.Plough => new CellState(FieldStage.Ploughed, current.Crop, now),
            FarmTool.Sow when seed != CropKind.None && !IsGrass(seed) => new CellState(FieldStage.Sown, seed, now),
            FarmTool.Harvest => new CellState(FieldStage.Stubble, current.Crop, now),
            FarmTool.Mow => new CellState(FieldStage.Mown, current.Crop, now),
            FarmTool.Fertilise when !current.Fertilised => current with { Fertilised = true, Stage = FieldStage.Sown },
            _ => null,
        };
    }
}
