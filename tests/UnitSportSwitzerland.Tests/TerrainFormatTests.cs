using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The tile formats the preprocessor writes and the game reads (both through TerrainFormat):
/// a round trip here is the contract between the two.
/// </summary>
public class TerrainFormatTests
{
    private static readonly TileId Id = new(2579, 1109);

    private static ushort[] RandomHeights(int size, int seed)
    {
        var rng = new Random(seed);
        var h = new ushort[size * size];
        for (int i = 0; i < h.Length; i++) h[i] = (ushort)rng.Next(0, 65536);
        return h;
    }

    private static T RoundTrip<T>(Action<Stream> write, Func<Stream, T> read)
    {
        using var ms = new MemoryStream();
        write(ms);
        ms.Position = 0;
        var result = read(ms);
        Assert.Equal(ms.Length, ms.Position); // the reader consumed exactly what was written
        return result;
    }

    [Theory]
    [InlineData(2579000.0, 1109000.0, 2579, 1109)]
    [InlineData(2579999.999, 1109999.999, 2579, 1109)]
    [InlineData(2580000.0, 1110000.0, 2580, 1110)]
    public void TileId_FromLv95_floors_to_the_owning_kilometre(double e, double n, int te, int tn)
    {
        var id = TileId.FromLv95(e, n);
        Assert.Equal(new TileId(te, tn), id);
        Assert.True(e >= id.MinE && e < id.MinE + 1000 && n >= id.MinN && n < id.MaxN);
    }

    [Fact]
    public void TileId_ToString_matches_the_file_names()
    {
        Assert.Equal("2579_1109", Id.ToString());
        Assert.Equal("chunk_2579_1109.terr", ChunkFormat.ChunkFileName(Id));
        Assert.Equal("cover_2579_1109.cover", CoverFormat.FileName(Id));
    }

    [Fact]
    public void SwissProjection_matches_the_swisstopo_reference_point()
    {
        // swisstopo's worked example: 46 02 38.87 N, 8 43 49.79 E -> E 2 700 000, N 1 100 000 (within 1 m)
        var (e, n) = SwissProjection.ToLv95(46 + 2 / 60.0 + 38.87 / 3600, 8 + 43 / 60.0 + 49.79 / 3600);
        Assert.InRange(e, 2699999.0, 2700001.0);
        Assert.InRange(n, 1099999.0, 1100001.0);
    }

    [Theory]
    [InlineData(46.9480, 7.4474, 1.0)]   // Bern
    [InlineData(46.5197, 6.6323, 1.0)]   // Lausanne
    [InlineData(45.9766, 7.6586, 1.0)]   // Matterhorn
    [InlineData(47.6950, 8.6380, 1.0)]   // Schaffhausen
    // Veigy-Foncenex, France: outside Switzerland the inverse formula drifts ~1.5 m (measured)
    [InlineData(46.2380, 6.3010, 2.0)]
    public void SwissProjection_round_trips_within_tolerance(double lat, double lon, double tolM)
    {
        var (e, n) = SwissProjection.ToLv95(lat, lon);
        var (lat2, lon2) = SwissProjection.ToWgs84(e, n);
        var (e2, n2) = SwissProjection.ToLv95(lat2, lon2);
        Assert.True(Math.Abs(e2 - e) < tolM && Math.Abs(n2 - n) < tolM, $"drift {e2 - e:F3} / {n2 - n:F3} m");
    }

    [Fact]
    public void Quantize_clamps_and_round_trips_within_half_a_step()
    {
        Assert.Equal(0, ChunkFormat.Quantize(-50));
        Assert.Equal(65535, ChunkFormat.Quantize(99999));
        foreach (double m in new[] { 0.0, 372.4, 1234.56, 4634.0 })
            Assert.InRange(ChunkFormat.Dequantize(ChunkFormat.Quantize(m)) - m, -ChunkFormat.HeightScale / 2, ChunkFormat.HeightScale / 2);
    }

    [Fact]
    public void Chunk_round_trips_bit_identical()
    {
        var grid = new ChunkGrid(Id, RandomHeights(ChunkFormat.GridSize, 1), 372.5f, 1680.25f);
        var back = RoundTrip(s => ChunkCodec.Encode(grid, s), ChunkCodec.Decode);
        Assert.Equal(grid.Id, back.Id);
        Assert.Equal(1, back.Stride);
        Assert.Equal(grid.MinHeight, back.MinHeight);
        Assert.Equal(grid.MaxHeight, back.MaxHeight);
        Assert.Equal(grid.Heights, back.Heights);
    }

    [Fact]
    public void Coarse_chunk_round_trips_and_keeps_the_full_grid_vertices()
    {
        var full = new ChunkGrid(Id, RandomHeights(ChunkFormat.GridSize, 2), 0, 1);
        var coarse = full.Decimate(ChunkFormat.CoarseStride);
        var back = RoundTrip(s => ChunkCodec.Encode(coarse, s), ChunkCodec.Decode);
        Assert.Equal(ChunkFormat.CoarseStride, back.Stride);
        Assert.Equal(101, back.Size);
        // decimation, not averaging: every kept vertex is the full grid's own, edges included
        foreach (int r in new[] { 0, 10, 500, 990, 1000 })
            foreach (int c in new[] { 0, 20, 730, 1000 })
                Assert.Equal(full.HeightAt(c, r), back.HeightAt(c, r));
    }

    [Fact]
    public void Chunk_written_with_stride_zero_reads_as_full_resolution()
    {
        var grid = new ChunkGrid(Id, RandomHeights(ChunkFormat.GridSize, 3), 0, 1);
        using var ms = new MemoryStream();
        ChunkCodec.Encode(grid, ms);
        var bytes = ms.ToArray();
        bytes[18] = bytes[19] = 0; // files from before the stride word was used
        var back = ChunkCodec.Decode(new MemoryStream(bytes));
        Assert.Equal(1, back.Stride);
        Assert.Equal(grid.Heights, back.Heights);
    }

    [Fact]
    public void Chunk_decode_rejects_bad_magic_and_truncation()
    {
        var grid = new ChunkGrid(Id, RandomHeights(ChunkFormat.GridSize, 4), 0, 1);
        using var ms = new MemoryStream();
        ChunkCodec.Encode(grid, ms);
        var bytes = ms.ToArray();

        var bad = (byte[])bytes.Clone();
        bad[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ChunkCodec.Decode(new MemoryStream(bad)));
        Assert.Throws<EndOfStreamException>(() => ChunkCodec.Decode(new MemoryStream(bytes[..^2])));
    }

    [Fact]
    public void SampleHeight_hits_vertices_exactly_and_mesh_height_follows_the_triangles()
    {
        int n = ChunkFormat.GridSize;
        var h = new ushort[n * n];
        // a non-planar quad at (col 0..1, row 0..1): three corners low, the SE corner high
        h[n + 1] = 1000;
        var grid = new ChunkGrid(Id, h, 0, 100);
        double top = ChunkFormat.Dequantize(1000);
        double sp = ChunkFormat.SpacingM;

        Assert.Equal(top, grid.SampleHeight(Id.MinE + sp, Id.MaxN - sp), 9);
        Assert.Equal(0, grid.SampleHeight(Id.MinE, Id.MaxN), 9);
        // quad centre: bilinear averages the four corners, the rendered mesh lies on the diagonal v10-v01
        Assert.Equal(top / 4, grid.SampleHeight(Id.MinE + sp / 2, Id.MaxN - sp / 2), 9);
        Assert.Equal(0, grid.SampleMeshHeight(Id.MinE + sp / 2, Id.MaxN - sp / 2), 9);
        // outside the tile clamps to the edge instead of reading out of range
        Assert.Equal(0, grid.SampleHeight(Id.MinE - 50, Id.MaxN + 50), 9);
    }

    [Fact]
    public void ChunkGrid_rejects_a_stride_that_does_not_divide_the_tile()
    {
        Assert.Throws<ArgumentException>(() => new ChunkGrid(Id, new ushort[4], 0, 1, 3));
        var grid = new ChunkGrid(Id, new ushort[ChunkFormat.GridSize * ChunkFormat.GridSize], 0, 1);
        Assert.Throws<InvalidOperationException>(() => grid.Decimate(10).RequireFull("collision"));
    }

    [Fact]
    public void Cover_round_trips_through_deflate()
    {
        var cells = new byte[CoverFormat.Size * CoverFormat.Size];
        var rng = new Random(5);
        for (int i = 0; i < cells.Length; i += 37) cells[i] = (byte)rng.Next(0, (int)CoverClass.Platform + 1);
        using var ms = new MemoryStream();
        CoverFormat.Encode(Id, cells, ms);
        ms.Position = 0;
        Assert.Equal(cells, CoverFormat.Decode(ms));
        Assert.Throws<ArgumentException>(() => CoverFormat.Encode(Id, new byte[10], Stream.Null));
    }

    [Fact]
    public void Water_round_trips_through_deflate()
    {
        var layer = WaterGrid.Dry(Id);
        var rng = new Random(298);
        for (int i = 0; i < layer.Levels.Length; i += 41)
        {
            layer.Levels[i] = (ushort)rng.Next(1, 65536);
            layer.Fetch[i] = (byte)rng.Next(1, 256);
        }
        using var ms = new MemoryStream();
        WaterFormat.Encode(layer, ms);
        ms.Position = 0;
        var back = WaterFormat.Decode(ms); // deflate reads ahead, so no exact-consumption check
        Assert.Equal(Id, back.Id);
        Assert.Equal(layer.Levels, back.Levels);
        Assert.Equal(layer.Fetch, back.Fetch);
        Assert.Equal("water_2579_1109.water", WaterFormat.FileName(Id));
        Assert.Throws<ArgumentException>(() => new WaterGrid(Id, new ushort[10], new byte[10]));
    }

    [Fact]
    public void Water_level_samples_only_the_wet_corners()
    {
        var layer = WaterGrid.Dry(Id);
        int s = WaterGrid.Size;
        ushort q = ChunkFormat.Quantize(372.14);
        // one wet vertex at (10, 20): its cell's other corners are dry
        layer.Levels[20 * s + 10] = q;
        double e = Id.MinE + 10, n = Id.MaxN - 20;
        Assert.True(layer.TrySampleLevel(e + 0.5, n - 0.5, out double level));
        Assert.Equal(ChunkFormat.Dequantize(q), level, 6);
        Assert.True(layer.IsWet(10, 20));
        Assert.False(layer.IsWet(11, 20));
        Assert.Equal(1, layer.WetCount);
        Assert.False(layer.TrySampleLevel(e + 5.5, n - 5.5, out _));

        // a falling river: the level interpolates between wet corners
        layer.Levels[20 * s + 11] = ChunkFormat.Quantize(371.14);
        Assert.True(layer.TrySampleLevel(e + 0.5, n, out level));
        Assert.Equal((ChunkFormat.Dequantize(q) + ChunkFormat.Dequantize(layer.Levels[20 * s + 11])) / 2, level, 6);
        Assert.Equal(255, WaterFormat.QuantizeFetch(1e6));
        Assert.Equal(1, WaterFormat.QuantizeFetch(0));
    }

    [Fact]
    public void Cover_classification_maps_the_tlm_names()
    {
        Assert.Equal(CoverClass.Forest, CoverFormat.Parse("Wald"));
        Assert.Equal(CoverClass.Open, CoverFormat.Parse(null));
        Assert.Equal(CoverClass.Vineyard, CoverFormat.ParseLandUse("Reben"));
        Assert.Equal(CoverClass.PavedArea, CoverFormat.ParseTrafficArea("Gleisareal"));
        Assert.Equal(SurfacePattern.ParkingBays, CoverFormat.PatternFor(CoverFormat.ParseTrafficArea("Rastplatzareal")));
        Assert.True(CoverFormat.IsWooded(CoverClass.Shrub));
        Assert.Equal(0f, CoverFormat.TreeDensity(CoverClass.Open));
    }

    [Fact]
    public void Holes_round_trip_including_the_far_corner()
    {
        int last = HoleFormat.QuadsPerSide - 1;
        var cells = new HashSet<int>
        {
            HoleFormat.CellIndex(0, 0), HoleFormat.CellIndex(last, 0), HoleFormat.CellIndex(0, last),
            HoleFormat.CellIndex(last, last), HoleFormat.CellIndex(123, 456),
        };
        var back = RoundTrip(s => HoleFormat.Encode(Id, cells, s), HoleFormat.Decode);
        Assert.Equal(cells, back);
    }

    [Fact]
    public void Trees_round_trip()
    {
        var trees = new List<TreeInstance> { new(0, 512.25f, 999.9f, 18.5f, 0), new(-0.5f, 2000f, 1f, 3f, 255) };
        var back = RoundTrip(s => TreeFormat.Encode(Id, trees, s), TreeFormat.Decode);
        Assert.Equal(trees, back);
        Assert.Empty(RoundTrip(s => TreeFormat.Encode(Id, [], s), TreeFormat.Decode));
    }

    [Fact]
    public void Buildings_round_trip()
    {
        var tile = new BuildingTile
        {
            Id = Id,
            Buildings =
            [
                new Building
                {
                    Kind = (BuildingKind)1, Floors = 3, YearBuilt = 1972, Egid = 190123456, MinY = 450.5f, MaxY = 462f,
                    Triangles = Enumerable.Range(0, 18).Select(i => i * 1.5f).ToArray(),
                },
                new Building { Kind = 0, Triangles = [] },
            ],
        };
        var back = RoundTrip(s => BuildingCodec.Encode(tile, s), BuildingCodec.Decode);
        Assert.Equal(Id, back.Id);
        Assert.Equal(2, back.Buildings.Count);
        var (a, b) = (tile.Buildings[0], back.Buildings[0]);
        Assert.Equal((a.Kind, a.Floors, a.YearBuilt, a.Egid, a.MinY, a.MaxY), (b.Kind, b.Floors, b.YearBuilt, b.Egid, b.MinY, b.MaxY));
        Assert.Equal(a.Triangles, b.Triangles);
        Assert.Equal(2, b.TriangleCount);
        Assert.Empty(back.Buildings[1].Triangles);
    }

    [Fact]
    public void Horizon_round_trips_the_extracted_lattice()
    {
        var grid = new ChunkGrid(Id, RandomHeights(ChunkFormat.GridSize, 6), 0, 1);
        var samples = HorizonFormat.Extract(grid);
        var other = new TileId(2580, 1109);
        var back = RoundTrip(
            s => HorizonFormat.Encode(new Dictionary<TileId, ushort[]> { [Id] = samples, [other] = samples }, s),
            HorizonFormat.Decode);
        Assert.Equal(2, back.Count);
        Assert.True(back.TryGet(Id, out var got));
        Assert.Equal(samples, got);
        // the lattice is a decimation of the grid: the SE corner sample is the SE corner vertex
        int last = HorizonFormat.SamplesPerSide - 1;
        Assert.Equal(grid.HeightMetersAt(ChunkFormat.GridSize - 1, ChunkFormat.GridSize - 1), back.HeightMetersAt(Id, last, last));
        Assert.Empty(back.Water);
    }

    [Fact]
    public void Horizon_carries_the_water_level_and_draws_the_higher_of_bed_and_water()
    {
        var bed = new ushort[HorizonFormat.SamplesPerTile];
        Array.Fill(bed, ChunkFormat.Quantize(330));
        bed[0] = ChunkFormat.Quantize(400);   // a bank above the lake's level
        var water = WaterGrid.Dry(Id);
        Array.Fill(water.Levels, ChunkFormat.Quantize(372.14));
        var levels = HorizonFormat.ExtractWater(water)!;
        Assert.Null(HorizonFormat.ExtractWater(WaterGrid.Dry(Id)));

        using var ms = new MemoryStream();
        HorizonFormat.Encode(new Dictionary<TileId, ushort[]> { [Id] = bed }, ms, new Dictionary<TileId, ushort[]> { [Id] = levels });
        Assert.Equal(HorizonFormat.Version, BitConverter.ToUInt16(ms.ToArray(), 4));
        ms.Position = 0;
        var back = HorizonFormat.Decode(ms);
        Assert.Equal(ms.Length, ms.Position);
        Assert.True(back.TryGet(Id, out var heights));
        Assert.Equal(bed, heights);   // the heights stay the bed: the generated fill blends on them
        Assert.Equal(ChunkFormat.Dequantize(levels[1]), back.SurfaceMetersAt(Id, 1, 0));
        Assert.Equal(400, back.SurfaceMetersAt(Id, 0, 0), 0);
        HorizonIndex.Surface(bed[1], levels, 1, out bool wet);
        Assert.True(wet);

        // no water: still written as version 1, which an older build reads
        using var dry = new MemoryStream();
        HorizonFormat.Encode(new Dictionary<TileId, ushort[]> { [Id] = bed }, dry, new Dictionary<TileId, ushort[]>());
        Assert.Equal(1, BitConverter.ToUInt16(dry.ToArray(), 4));
    }

    [Fact]
    public void Manifest_round_trips_as_camel_case_json()
    {
        var m = new TerrainManifest
        {
            SuggestedOriginLv95 = new Lv95Point { E = 2579500.5, N = 1109500.25 },
            BoundsLv95 = new Lv95Bounds { MinE = 2579000, MinN = 1109000, MaxE = 2581000, MaxN = 1110000 },
            Tiles = [new ManifestTile { E = 2579, N = 1109, Min = 372.5f, Max = 1680.25f }, new ManifestTile { E = 2580, N = 1109 }],
        };
        string json = m.ToJson();
        Assert.Contains("\"suggestedOriginLv95\"", json);
        Assert.Contains($"\"formatVersion\": {ChunkFormat.Version}", json);

        var back = TerrainManifest.FromJson(json);
        Assert.Equal(ChunkFormat.GridSize, back.GridSize);
        Assert.Equal(2579500.5, back.SuggestedOriginLv95.E);
        Assert.Equal(2581000, back.BoundsLv95.MaxE);
        Assert.Equal([Id, new TileId(2580, 1109)], back.Tiles.Select(t => t.Id));
        Assert.Equal(1680.25f, back.Tiles[0].Max);
        Assert.Throws<InvalidDataException>(() => TerrainManifest.FromJson("null"));
    }
}
