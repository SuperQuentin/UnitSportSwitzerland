using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The gameplay folders' moves onto the tile key (#1443, G0c; BattleRoyale, Player, Loot, Occasions,
/// Adventure, Gpx, Playtest): each call site now asks the tile for its frame, its neighbours and its
/// distances instead of LV95 kilometre arithmetic. These pin the rewrites that are not plainly the same
/// expression to the arithmetic they replaced, bit for bit, over LV95 points in and around a tile.
/// </summary>
public class TileKeyMovesTests
{
    private static readonly TileId T = new(2583, 1113);

    /// <summary>LV95 points on the tile, on its edges, and up to 40 m outside it.</summary>
    private static IEnumerable<(double E, double N)> Points()
    {
        var rng = new Random(1443);
        yield return (T.MinE, T.MaxN);
        yield return (T.MinE, T.MinN);
        yield return (T.MinE + 1000, T.MaxN);
        for (int i = 0; i < 20000; i++)
            yield return (T.MinE - 40 + rng.NextDouble() * 1080, T.MinN - 40 + rng.NextDouble() * 1080);
    }

    [Fact]
    public void Local_is_exact_on_LV95_numbers_so_Global_of_it_gives_the_point_back()
    {
        // what RewardPlacer.Inside, YardIndex and the Corridor's edge test rely on: e - MinE loses nothing
        foreach (var (e, n) in Points())
        {
            var (x, z) = T.Local(e, n);
            Assert.Equal((e, n), T.Global(x, z));
        }
    }

    [Fact]
    public void Clamping_inside_the_tile_in_its_frame_is_the_LV95_clamp()
    {
        // RewardPlacer.Inside
        foreach (var (e, n) in Points())
        {
            var before = (Math.Clamp(e, T.MinE + 2, T.MinE + 998), Math.Clamp(n, T.MinN + 2, T.MinN + 998));
            var (x, z) = T.Local(e, n);
            Assert.Equal(before, T.Global(Math.Clamp(x, 2, 998), Math.Clamp(z, 2, 998)));
        }
    }

    [Fact]
    public void How_far_a_point_is_outside_the_tile_is_the_same_in_its_frame()
    {
        // YardIndex.Update
        foreach (var (e, n) in Points())
        {
            double de0 = Math.Max(0, Math.Max(T.MinE - e, e - (T.MinE + ChunkFormat.TileSizeM)));
            double dn0 = Math.Max(0, Math.Max(T.MinN - n, n - (T.MinN + ChunkFormat.TileSizeM)));
            var (x, z) = T.Local(e, n);
            double dx = Math.Max(0, Math.Max(-x, x - ChunkFormat.TileSizeM));
            double dz = Math.Max(0, Math.Max(-z, z - ChunkFormat.TileSizeM));
            Assert.Equal(de0, dx);
            Assert.Equal(dn0, dz);
        }
    }

    [Fact]
    public void The_south_edge_test_in_the_tiles_frame_is_the_LV95_one()
    {
        // Corridor.Sharing
        foreach (var (e, n) in Points().Append((T.MinE + 3, T.MinN)).Append((T.MinE + 3, T.MinN + 0.0005)))
        {
            var (_, z) = T.Local(e, n);
            Assert.Equal(Math.Abs(n - (T.MaxN - ChunkFormat.TileSizeM)), Math.Abs(ChunkFormat.TileSizeM - z));
        }
    }

    [Fact]
    public void A_reward_cells_south_west_corner_comes_from_the_tile()
    {
        // RewardPlacer.Place: cy counts north from the south edge
        for (int cx = 0; cx < 4; cx++)
            for (int cy = 0; cy < 4; cy++)
                Assert.Equal((T.MinE + cx * 250.0, T.MinN + cy * 250.0), T.Global(cx * 250.0, ChunkFormat.TileSizeM - cy * 250.0));
    }

    [Fact]
    public void A_road_point_keeps_its_east_sum_in_the_old_order()
    {
        // BrLoot.RoadPoints: MinE + x0 + dx * t, the float step added last
        var rng = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            float x0 = (float)(rng.NextDouble() * 1000), z0 = (float)(rng.NextDouble() * 1000);
            float dx = (float)(rng.NextDouble() * 60 - 30), dz = (float)(rng.NextDouble() * 60 - 30), t = i / 1000f;
            var (ge, pn) = T.Global(x0, z0 + dz * t);
            Assert.Equal(T.MinE + x0 + dx * t, ge + dx * t);
            Assert.Equal(T.MaxN - (z0 + dz * t), pn);
        }
    }

    [Fact]
    public void Neighbour_loops_visit_the_tiles_in_the_old_order()
    {
        // HitboxProbe, LootProbe, YardIndex, OccasionDecor, RewardWorld, RaceRoute: east outer, north inner
        foreach (int r in new[] { 1, 4 })
        {
            var before = new List<TileId>();
            for (int de = -r; de <= r; de++)
                for (int dn = -r; dn <= r; dn++)
                    before.Add(new TileId(T.E + de, T.N + dn));
            var after = new List<TileId>();
            for (int dx = -r; dx <= r; dx++)
                for (int dz = r; dz >= -r; dz--)
                    after.Add(T.Neighbour(dx, dz));
            Assert.Equal(before, after);
        }

        // RewardStats: Enumerable.Range(-r, 2r) on both axes
        const int k = 3;
        var stats0 = new List<TileId>();
        foreach (var de in Enumerable.Range(-k, 2 * k))
            foreach (var dn in Enumerable.Range(-k, 2 * k))
                stats0.Add(new TileId(T.E + de, T.N + dn));
        var stats1 = new List<TileId>();
        for (int dx = -k; dx < k; dx++)
            for (int dz = k; dz > -k; dz--)
                stats1.Add(T.Neighbour(dx, dz));
        Assert.Equal(stats0, stats1);

        // GatherProbe, ExportPrefetcher: ring by ring, the perimeter only
        for (int ring = 0; ring <= 3; ring++)
        {
            var ring0 = new List<TileId>();
            for (int de = -ring; de <= ring; de++)
                for (int dn = -ring; dn <= ring; dn++)
                    if (Math.Max(Math.Abs(de), Math.Abs(dn)) == ring) ring0.Add(new TileId(T.E + de, T.N + dn));
            var ring1 = new List<TileId>();
            for (int dx = -ring; dx <= ring; dx++)
                for (int dz = ring; dz >= -ring; dz--)
                    if (T.Neighbour(dx, dz) is var t && t.RingDistance(T) == ring) ring1.Add(t);
            Assert.Equal(ring0, ring1);
        }
    }

    [Fact]
    public void The_point_to_lake_box_distance_is_the_distance_to_the_boxs_nearest_point()
    {
        // PoiCatalogue.Near
        double minE = T.MinE + 100, maxE = T.MinE + 400, minN = T.MinN + 300, maxN = T.MinN + 350;
        foreach (var (e, n) in Points())
        {
            double de = Math.Max(0, Math.Max(minE - e, e - maxE));
            double dn = Math.Max(0, Math.Max(minN - n, n - maxN));
            double before = de * de + dn * dn;
            Assert.Equal(before, GlobalPos.HorizontalDistanceSquared(e, n, Math.Clamp(e, minE, maxE), Math.Clamp(n, minN, maxN)));
        }
    }

    [Theory]
    [InlineData(2583000, 1113000)]
    [InlineData(2580000, 1111000)]
    public void Into_a_tiles_frame_and_back_is_the_old_corner_subtraction(double oe, double on)
    {
        // Gathering.NearestTree
        var origin = new OriginFrame(oe, on, 0);
        var corner = origin.ToWorld(T.MinE, T.MaxN, 0);
        var xf = origin.TileTransform(T);
        var rng = new Random(11);
        for (int i = 0; i < 1000; i++)
        {
            var world = corner + new Vector3((float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 900 + 400), (float)(rng.NextDouble() * 1000));
            var local = world - corner;
            Assert.Equal(local, xf.AffineInverse() * world);
            Assert.Equal(local + corner, xf * local);
        }
    }
}
