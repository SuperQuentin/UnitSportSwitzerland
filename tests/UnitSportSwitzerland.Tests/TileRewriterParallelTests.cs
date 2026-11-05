using System.Collections.Concurrent;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The network stage's blocks at once (#1539): the same bytes and the same numbers as one after another.</summary>
public class TileRewriterParallelTests
{
    /// <summary>A row of one-tile blocks in memory, each tile with its own crossroads of streets.</summary>
    private sealed class Row(int tiles, bool independent = true) : ITileLoop
    {
        public readonly ConcurrentDictionary<TileId, byte[]> Written = new();
        private readonly List<TileId> _ids = Enumerable.Range(0, tiles).Select(i => new TileId(2600 + i * 3, 1200)).ToList();
        public IReadOnlyList<HashSet<TileId>> Blocks => _ids.Select(id => new HashSet<TileId> { id }).ToList();
        public HashSet<TileId> Wanted => new(_ids);
        public bool BlocksIndependent => independent;

        public TileRewriter.Inputs Inputs => new()
        {
            Roads = id => (_ids.Contains(id) ? Crossroads(id) : null, null),
            Buildings = _ => null,
        };

        public void Write(RoadTile tile, byte[] bytes) => Written[tile.Id] = bytes;

        public void Finish() { }

        private static RoadTile Crossroads(TileId id)
        {
            // a little different from tile to tile, so a tile's bytes could not pass for another's
            float bend = (id.E - 2600) * 0.5f;
            return new RoadTile
            {
                Id = id,
                Segments =
                [
                    Street(100, 500 + bend, 500, 500, 900, 500 - bend),
                    Street(500, 100, 500, 500, 500 + bend, 900),
                    Street(500, 500, 700, 300 + bend, 950, 100),
                ],
            };
        }

        private static RoadSegment Street(params float[] planXz)
        {
            var points = new float[planXz.Length / 2 * 3];
            for (int i = 0; i < planXz.Length / 2; i++)
            {
                points[i * 3] = planXz[i * 2];
                points[i * 3 + 1] = 500;
                points[i * 3 + 2] = planXz[i * 2 + 1];
            }
            return new RoadSegment { Class = RoadClass.Minor, Surface = RoadSurface.Paved, Width = 6, Points = points };
        }
    }

    [Fact]
    public void Blocks_at_once_write_the_same_bytes_and_numbers()
    {
        var one = new Row(12);
        var options = new TileRewriter.Options(Jobs: 1, Gantries: false);
        var serial = TileRewriter.Run(one, options, _ => { });
        Assert.Equal(12, one.Written.Count);
        Assert.True(serial.Junctions > 0);

        for (int run = 0; run < 4; run++)
        {
            var many = new Row(12);
            var parallel = TileRewriter.Run(many, options with { Jobs = 6 }, _ => { });
            foreach (var (id, bytes) in one.Written) Assert.Equal(bytes, many.Written[id]);
            Assert.Equal((serial.TilesRead, serial.TilesWritten, serial.Junctions, serial.SegmentsWritten),
                (parallel.TilesRead, parallel.TilesWritten, parallel.Junctions, parallel.SegmentsWritten));
            Assert.Equal(serial.Network.Format(12), parallel.Network.Format(12));
        }
    }

    [Fact]
    public void Blocks_that_read_what_others_write_stay_one_after_another()
    {
        Assert.Equal(1, TileRewriter.BlockJobs(new Row(4, independent: false), new TileRewriter.Options(Jobs: 4)));
        Assert.Equal(4, TileRewriter.BlockJobs(new Row(4), new TileRewriter.Options(Jobs: 4)));
        Assert.Equal(2, TileRewriter.BlockJobs(new Row(2), new TileRewriter.Options(Jobs: 4)));
    }

    /// <summary>A stats field no rule covers would throw in the first real run: every class is walked here, and the kinds of field add up.</summary>
    [Fact]
    public void Stats_add_up_field_by_field()
    {
        TileRewriter.NetworkStats a = new(), b = new();
        a.Roads = 2; b.Roads = 3;
        a.RoadKm = 1.5; b.RoadKm = 2;
        a.MaxBytes = 7; a.MaxBytesTile = "first"; b.MaxBytes = 7; b.MaxBytesTile = "second";
        a.Priority.Kinds[0] = 1; b.Priority.Kinds[0] = 4;
        a.Priority.Placed["x"] = 1; b.Priority.Placed["x"] = 2; b.Priority.Placed["y"] = 5;
        a.Parts["v2"] = (1, 2); b.Parts["v2"] = (10, 20);
        a.Signals.InvalidExamples.AddRange(["a", "b", "c", "d"]); b.Signals.InvalidExamples.AddRange(["e", "f"]);
        b.Carriageways.CrowdedTiles.Add("t");

        a.Absorb(b);

        Assert.Equal(5, a.Roads);
        Assert.Equal(3.5, a.RoadKm);
        Assert.Equal(7, a.MaxBytes);
        Assert.Equal("first", a.MaxBytesTile);   // the first of equals, as one after another
        Assert.Equal(5, a.Priority.Kinds[0]);
        Assert.Equal(3, a.Priority.Placed["x"]);
        Assert.Equal(5, a.Priority.Placed["y"]);
        Assert.Equal((11L, 22L), a.Parts["v2"]);
        Assert.Equal(["a", "b", "c", "d", "e"], a.Signals.InvalidExamples);   // its cap of examples
        Assert.Contains("t", a.Carriageways.CrowdedTiles);

        foreach (var stats in new object[] { new EmbankmentPlanner.Stats(), new StreetPlanner.Stats(), new CornerPlanner.Stats(),
            new RoadHeights.Stats(), new RailingPlanner.Stats(), new TileRewriter.ParkingStats() })
            StatsMerge.Add(stats, Activator.CreateInstance(stats.GetType())!);
    }
}
