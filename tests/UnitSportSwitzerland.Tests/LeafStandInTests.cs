using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A leaf as its stand-in tile (#1510): the name round trip, the 1 m grid against the leaf's own mesh, the records shifted.</summary>
public class LeafStandInTests
{
    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, LeafFrames.LeafLevel);

    [Fact]
    public void A_leaf_name_round_trips_and_no_LV95_tile_is_one()
    {
        foreach (var key in new[] { Geneva, new CubeKey(5, 14, 16383, 16383), new CubeKey(0, 14, 0, 0) })
        {
            var id = LeafStandIn.Id(key);
            Assert.True(LeafStandIn.TryLeaf(id, out var back));
            Assert.Equal(key, back);
        }
        Assert.False(LeafStandIn.IsStandIn(new TileId(2600, 1200)));
        Assert.False(LeafStandIn.IsStandIn(new TileId(2840, 1300)));
        Assert.NotEqual(LeafStandIn.Id(new CubeKey(0, 14, 1, 2)), LeafStandIn.Id(new CubeKey(1, 14, 1, 2)));
    }

    [Fact]
    public void The_grid_is_the_leaf_mesh_to_the_quantum()
    {
        int bits = CubeKey.LatticeBits(LeafFrames.LeafLevel), n = (1 << bits) + 1;
        var heights = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                heights[j * n + i] = 420f + 30f * MathF.Sin(i * 0.05f) + 12f * MathF.Cos(j * 0.031f) + (i * j % 7) * 0.2f;
        var v = CubeGeometry.Vertices(Geneva, bits, heights);
        var id = LeafStandIn.Id(Geneva);
        var nearest = new int[WaterTile.Size * WaterTile.Size];
        var grid = LeafStandIn.Grid(id, v, n, nearest);

        Assert.Equal(id, grid.Id);
        double half = ChunkFormat.HeightScale * 0.5 + 1e-4, worst = 0;
        int checkedPoints = 0;
        var hint = (0, 0);
        var rng = new Random(7);
        for (int k = 0; k < 2000; k++)
        {
            // a grid vertex near the middle (the leaf is ~610 m: ±250 m of the anchor is on it)
            int c = 250 + rng.Next(500), r = 250 + rng.Next(500);
            double mesh = CubeGeometry.MeshHeightAt(v, n, c - LeafStandIn.Offset, r - LeafStandIn.Offset, ref hint);
            if (double.IsNaN(mesh)) continue;
            worst = Math.Max(worst, Math.Abs(grid.HeightMetersAt(c, r) - mesh));
            checkedPoints++;
        }
        Assert.True(checkedPoints > 1500, $"{checkedPoints} points on the leaf");
        Assert.True(worst <= half, $"grid off the mesh by {worst * 100:F2} cm");
        // off the leaf: filled, not left at the quantum's floor
        Assert.True(grid.HeightMetersAt(0, 0) > 300 && grid.HeightMetersAt(1000, 1000) > 300);
        // the 2 m samples on the leaf know their lattice vertex
        Assert.True(nearest[250 * WaterTile.Size + 250] >= 0);
        Assert.Equal(-1, nearest[0]);
    }

    [Fact]
    public void Records_move_by_the_offset_and_keep_their_order()
    {
        var b = new Building { Kind = default(BuildingKind), Egid = 7, MinY = 400, MaxY = 410, Triangles = [-10, 400, -5, 10, 400, -5, 0, 410, 5] };
        var shifted = LeafStandIn.Buildings(new BuildingTile { Buildings = [b] }, LeafStandIn.Id(Geneva));
        Assert.Equal(LeafStandIn.Id(Geneva), shifted.Id);
        Assert.Equal(490f, shifted.Buildings[0].Triangles[0]);
        Assert.Equal(495f, shifted.Buildings[0].Triangles[2]);
        Assert.Equal(-10f, b.Triangles[0]);   // the leaf's own copy untouched
        var trees = LeafStandIn.Trees([new TreeInstance(1, 400, -2, 12, 0)]);
        Assert.Equal((501f, 498f), (trees[0].X, trees[0].Z));
    }

    [Fact]
    public void A_leafs_slopes_lie_on_its_stand_in_where_the_leaf_has_them()
    {
        // a leaf whose lattice has a slope over its middle third (x and z within 100 m of the anchor)
        int bits = CubeKey.LatticeBits(LeafFrames.LeafLevel), n = (1 << bits) + 1;
        var v = CubeGeometry.Vertices(Geneva, bits, new float[n * n].Select(_ => 1200f).ToArray());
        var slot = new byte[n * n];
        var aux = new ushort[n * n];
        for (int k = 0; k < n * n; k++)
            if (Math.Abs(v[3 * k]) < 100 && Math.Abs(v[3 * k + 2]) < 100)
            {
                slot[k] = 1;
                aux[k] = PisteFormat.PackAux(1f, v[3 * k]);
            }
        var record = new PisteRecord(9, PisteType.Downhill, PisteDifficulty.Easy, PisteGrooming.Classic, false, 30, "Middle", "",
            [-100f, 50f, 100f, -50f], 1190, 1210);
        var leaf = new PisteTile(default, [record], slot, aux);
        var id = LeafStandIn.Id(Geneva);
        var nearest = new int[WaterTile.Size * WaterTile.Size];
        LeafStandIn.Grid(id, v, n, nearest);

        var t = LeafStandIn.Pistes(leaf, id, n, nearest)!;
        Assert.Equal(id, t.Id);
        // the path: (x, z) of the anchor to (east, north) from the stand-in's south-west corner; altitudes kept
        Assert.Equal([400f, 450f, 600f, 550f], t.Records[0].Path);
        Assert.Equal((1190f, 1210f), (t.Records[0].AltLow, t.Records[0].AltHigh));
        Assert.Equal(-100f, leaf.Records[0].Path[0]);   // the leaf's own copy untouched
        // the raster: the slope 80 m round the stand-in's middle, none 120 m out or off the leaf
        var (e, n0) = id.Global(500, 500);
        foreach (var (dx, dz, on) in new[] { (0, 0, true), (80, -80, true), (-80, 60, true), (120, 0, false), (0, -130, false), (-400, -400, false) })
        {
            var (pe, pn) = id.Global(500 + dx, 500 + dz);
            Assert.Equal(on, t.At(pe, pn) is { Name: "Middle" });
        }
        Assert.Equal(1f, PisteFormat.MaskOf(t.Aux[t.IndexAt(e, n0)]));
        // the groom phase follows the leaf's: 37 m east of the anchor
        Assert.Equal(37f, PisteFormat.PhaseOf(t.Aux[t.IndexAt(id.Global(537, 500).E, id.Global(537, 500).N)]), 1.5f);
        Assert.Null(LeafStandIn.Pistes(null, id, n, nearest));
    }
}
