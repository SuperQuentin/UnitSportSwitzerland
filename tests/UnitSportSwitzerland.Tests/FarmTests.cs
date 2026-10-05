using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Farming (#494): the tables, the cell rules, the sparse store and its packed form, the strip and the raster.</summary>
public class FarmTests
{
    private static readonly CropKind[] Crops = Enum.GetValues<CropKind>();

    [Fact]
    public void EverySownCropHasASeedAndAHarvest()
    {
        foreach (var crop in Crops)
        {
            if (crop is CropKind.None) continue;
            Assert.NotEqual(ItemId.None, FarmTables.YieldOf(crop));
            Assert.True(FarmTables.YieldPerCell(crop) > 0, $"{crop} gives nothing");
            if (FarmTables.IsGrass(crop) || crop == CropKind.OtherArable) continue;
            var seed = FarmTables.SeedFor(crop);
            Assert.NotEqual(ItemId.None, seed);
            Assert.Equal(crop, FarmTables.CropOf(seed));
        }
    }

    [Fact]
    public void EveryCropHasAStageEveryMonth_AndArableOnesRipenOnce()
    {
        foreach (var crop in Crops)
        {
            int ripe = 0;
            for (int m = 1; m <= 12; m++)
            {
                var s = FarmTables.NaturalStage(crop, m);
                Assert.NotEqual(FieldStage.Natural, s);
                if (s == FieldStage.Ripe) ripe++;
            }
            if (crop is not (CropKind.None or CropKind.Meadow or CropKind.Pasture or CropKind.Fallow))
                Assert.True(ripe >= 1, $"{crop} never ripens");
        }
        Assert.Equal(FieldStage.Ripe, FarmTables.NaturalStage(CropKind.Wheat, 7));
        Assert.Equal(FieldStage.Ripe, FarmTables.NaturalStage(CropKind.Maize, 9));
    }

    [Fact]
    public void ASownCropSproutsGrowsAndRipens_FasterFertilised()
    {
        var sown = new CellState(FieldStage.Sown, CropKind.Wheat, 1000);
        int grow = FarmTables.GrowSeconds(CropKind.Wheat);
        Assert.Equal(FieldStage.Sown, FarmTables.Now(sown, 1000 + (long)(grow * 0.1), out _));
        Assert.Equal(FieldStage.Growing, FarmTables.Now(sown, 1000 + grow / 2, out float g));
        Assert.InRange(g, 0.49f, 0.51f);
        Assert.Equal(FieldStage.Ripe, FarmTables.Now(sown, 1000 + grow, out _));
        var fed = sown with { Fertilised = true };
        Assert.Equal(FieldStage.Ripe, FarmTables.Now(fed, 1000 + (long)(grow * FarmTables.FertilisedGrowth) + 1, out _));
        var mown = new CellState(FieldStage.Mown, CropKind.Meadow, 1000);
        Assert.Equal(FieldStage.Grass, FarmTables.Now(mown, 1000 + FarmTables.GrowSeconds(CropKind.Meadow), out _));
    }

    [Fact]
    public void TheYearOfACell_ByHandOrMachine()
    {
        const int july = 7;
        uint t = 1_000_000;
        // ripe natural wheat: harvest -> stubble -> plough -> sow -> ripe again -> harvest
        var s = FarmRules.Work(FarmTool.Harvest, CropKind.Wheat, null, CropKind.None, july, t);
        Assert.Equal(FieldStage.Stubble, s?.Stage);
        Assert.Null(FarmRules.Work(FarmTool.Harvest, CropKind.Wheat, s, CropKind.None, july, t));
        Assert.Null(FarmRules.Work(FarmTool.Sow, CropKind.Wheat, s, CropKind.Barley, july, t));
        s = FarmRules.Work(FarmTool.Plough, CropKind.Wheat, s, CropKind.None, july, t);
        Assert.Equal(FieldStage.Ploughed, s?.Stage);
        Assert.Null(FarmRules.Work(FarmTool.Sow, CropKind.Wheat, s, CropKind.Meadow, july, t));   // grass is never sown
        s = FarmRules.Work(FarmTool.Sow, CropKind.Wheat, s, CropKind.Barley, july, t);
        Assert.Equal(new CellState(FieldStage.Sown, CropKind.Barley, t), s);
        s = FarmRules.Work(FarmTool.Fertilise, CropKind.Wheat, s, CropKind.None, july, t);
        Assert.True(s?.Fertilised);
        Assert.Null(FarmRules.Work(FarmTool.Fertilise, CropKind.Wheat, s, CropKind.None, july, t));   // once
        uint ripe = t + (uint)(FarmTables.GrowSeconds(CropKind.Barley) * FarmTables.FertilisedGrowth) + 1;
        Assert.Equal(FieldStage.Ripe, FarmRules.StageOf(CropKind.Wheat, s, july, ripe, out _, out var crop));
        Assert.Equal(CropKind.Barley, crop);
        Assert.Equal(FieldStage.Stubble, FarmRules.Work(FarmTool.Harvest, CropKind.Wheat, s, CropKind.None, july, ripe)?.Stage);
    }

    [Fact]
    public void GrassIsMownNotHarvested_GrainIsNeverMown()
    {
        Assert.Equal(FieldStage.Mown, FarmRules.Work(FarmTool.Mow, CropKind.Meadow, null, CropKind.None, 5, 100)?.Stage);
        Assert.Null(FarmRules.Work(FarmTool.Harvest, CropKind.Meadow, null, CropKind.None, 5, 100));
        Assert.Null(FarmRules.Work(FarmTool.Mow, CropKind.Wheat, null, CropKind.None, 7, 100));
        Assert.Null(FarmRules.Work(FarmTool.Plough, CropKind.None, null, CropKind.None, 7, 100));
    }

    [Fact]
    public void ANaturalGrowingCropKeepsGrowingFromWhereItWasDrawn()
    {
        // June wheat is drawn half grown; touched, it is stored sown half a growing time ago
        uint now = 5_000_000;
        var state = FarmRules.NaturalState(CropKind.Wheat, 6, now);
        Assert.Equal(FieldStage.Sown, state.Stage);
        FarmRules.StageOf(CropKind.Wheat, state, 6, now, out float g, out _);
        Assert.InRange(g, FarmRules.NaturalGrowingShare - 0.01f, FarmRules.NaturalGrowingShare + 0.01f);
        Assert.Equal(FieldStage.Ploughed, FarmRules.NaturalState(CropKind.Wheat, 10, now).Stage);
    }

    [Fact]
    public void HandYieldIsAtLeastTheMinimum_RootsAFew()
    {
        foreach (var crop in Crops)
            if (crop != CropKind.None) Assert.True(FarmRules.HandYield(crop) >= FarmTables.HandYieldMin);
        Assert.True(FarmRules.HandYield(CropKind.Potato) >= 2);
        Assert.Equal(FarmTables.YieldPerCell(CropKind.Wheat), FarmRules.UnitsPerCell(FarmTool.Harvest, CropKind.Wheat));
        Assert.Equal(1f / FarmTables.CellsPerSeed, FarmRules.UnitsPerCell(FarmTool.Sow, CropKind.Barley));
    }

    [Fact]
    public void PackedCellsRoundTrip_AndNaturalForgets()
    {
        var store = new FieldCells();
        var a = new CellState(FieldStage.Sown, CropKind.SugarBeet, 1_760_000_000u, true);
        var b = new CellState(FieldStage.Stubble, CropKind.Maize, 7);
        Assert.True(store.Set(62_499, a));
        Assert.True(store.Set(0, b));
        Assert.False(store.Set(0, b));   // unchanged
        Assert.False(store.Set(FieldFormat.CellCount, b));   // out of the tile
        var packed = store.PackAll();
        Assert.Equal(2, packed.Length);
        var copy = new FieldCells();
        Assert.Equal(2, copy.Apply(packed));
        Assert.Equal(a, copy.Get(62_499));
        Assert.Equal(b, copy.Get(0));
        Assert.Equal(1, copy.Apply(new[] { FieldCells.PackNatural(0) }));
        Assert.Null(copy.Get(0));
        Assert.Equal(1, copy.Count);
        Assert.Equal(FieldCells.PackNatural(5), copy.PackCell(5));
    }

    [Fact]
    public void ChunkVersionsMoveOnlyWhereACellChanged()
    {
        var store = new FieldCells();
        int cell = FieldFormat.CellIndex(30, 60);   // chunk (1, 2)
        int chunk = FieldTile.ChunkOf(cell);
        Assert.Equal(2 * FieldTile.ChunksPerSide + 1, chunk);
        store.Set(cell, new CellState(FieldStage.Ploughed, CropKind.Wheat, 1));
        Assert.Equal(1, store.ChunkVersions[chunk]);
        Assert.Equal(1, store.ChunkVersions.Sum());
        int version = store.Version;
        store.Clear();
        Assert.Equal(2, store.ChunkVersions[chunk]);
        Assert.True(store.Version > version);
    }

    [Fact]
    public void TheStripHoldsTheCellsWhoseCentresItCovers()
    {
        var keys = new List<long>();
        // 3 m wide, north along E = 2 600 002 (a cell centre column): one column, 10 cells
        FarmRules.CellsInStrip(2_600_002, 1_200_000, 2_600_002, 1_200_040, 3f, keys);
        Assert.Equal(10, keys.Count);
        foreach (long k in keys)
        {
            var (tile, cell) = FarmRules.Unkey(k);
            Assert.Equal(new TileId(2600, 1200), tile);
            Assert.Equal(0, cell % FieldFormat.CellsPerSide);
        }
        keys.Clear();
        FarmRules.CellsInStrip(10, 10, 10, 10, 5f, keys);   // no length: nothing
        Assert.Empty(keys);
        // a cell west and south of the origin stays in its own tile
        var (t2, c2) = FarmRules.Unkey(FarmRules.KeyAt(-1, -1));
        Assert.Equal(new TileId(-1, -1), t2);
        Assert.Equal(FieldFormat.CellCount - 1, c2);
    }

    [Fact]
    public void ATileRastersItsFields_FirstOneWins()
    {
        var a = new FieldPolygon(1, CropKind.Wheat, FieldSource.Osm, 0, new[] { new float[] { 0, 0, 40, 0, 40, 40, 0, 40 } });
        var b = new FieldPolygon(2, CropKind.Potato, FieldSource.Osm, 0, new[] { new float[] { 20, 0, 60, 0, 60, 40, 20, 40 } });
        var tile = FieldTile.Build(new TileId(2600, 1200), new List<FieldPolygon> { a, b });
        Assert.Equal(15 * 10, tile.CellCount);
        Assert.Equal(CropKind.Wheat, tile.CropAt(FieldFormat.CellIndex(5, 5)));
        Assert.Equal(CropKind.Potato, tile.CropAt(FieldFormat.CellIndex(12, 5)));
        Assert.Equal(1u, tile.FieldAt(FieldFormat.CellIndex(0, 0)));
        Assert.Equal(CropKind.None, tile.CropAt(FieldFormat.CellIndex(20, 5)));
        Assert.Equal(150, tile.ChunkCellCounts[0]);
    }
}
