using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The road-to-facade strip measure of <c>--groundcheck</c> (#1419).</summary>
public class GroundStripTests
{
    private const int N = ChunkFormat.GridSize;

    private static RoadTile Street(float z) => new()
    {
        Id = new TileId(2600, 1100),
        Segments = [new RoadSegment { Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 6f, Points = [100, 0, z, 300, 0, z] }],
    };

    [Fact]
    public void Ribbon_edge_distance_starts_outside_the_cross_section()
    {
        var (dist, inside) = GroundStrip.RoadEdgeDistance(Street(500));
        // half the 6 m carriageway: 3 m out is the edge
        Assert.True(inside[500 * N + 200]);
        Assert.True(inside[503 * N + 200]);
        Assert.False(inside[504 * N + 200]);
        Assert.Equal(1f, dist[504 * N + 200], 3);
        Assert.Equal(10f, dist[513 * N + 200], 3);
    }

    [Fact]
    public void Strip_is_the_cells_between_the_ribbon_and_a_near_facade_and_not_under_it()
    {
        var (rd, onRoad) = GroundStrip.RoadEdgeDistance(Street(500));
        // a facade 12 m south of the street, from x = 150 to 250, and its footprint behind it
        var facades = new List<GroundStrip.Seg> { new(150, 512, 250, 512) };
        var tris = new List<float[]> { new float[] { 150, 512, 250, 512, 250, 540 }, new float[] { 150, 512, 250, 540, 150, 540 } };
        var (fd, under) = GroundStrip.FacadeDistance(facades, tris);
        var strip = GroundStrip.Members(rd, onRoad, fd, under);
        Assert.True(strip[508 * N + 200]);      // between street edge (503) and facade (512)
        Assert.False(strip[500 * N + 200]);     // on the road
        Assert.False(strip[520 * N + 200]);     // under the building
        Assert.False(strip[508 * N + 20]);      // beside the street, no facade near
    }

    [Fact]
    public void Slopes_and_steps_are_counted_on_strip_cells_only()
    {
        var h = new float[N * N];
        var strip = new bool[N * N];
        // a 1.1:1 ramp eastwards (47.7 deg) over a 10 x 10 patch
        for (int r = 100; r < 110; r++)
            for (int c = 100; c < 110; c++)
            {
                strip[r * N + c] = true;
                h[r * N + c] = (c - 100) * 1.1f;
            }
        var m = GroundStrip.Of(h, strip, 1);
        Assert.Equal(100, m.Cells);
        Assert.Equal(100, m.Slope.Sum());
        Assert.True(m.Slope[3] >= 81);   // 47.7 deg; the last row and column look off the patch, steeper
        Assert.Equal(180, m.Edges);
        Assert.Equal(90, m.Steps);      // every eastward edge rises 1.1 m, none southwards
        Assert.Equal(1.1f, m.WorstStep, 2);
    }
}
