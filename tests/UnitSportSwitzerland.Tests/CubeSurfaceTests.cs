using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>#1654: the pyramid's levels are averaged from the leaves at their surface, a lake's water over its bed.</summary>
public class CubeSurfaceTests
{
    private const int Bits = 3;   // 9² vertices, 5² water samples

    private static CubeTile Ground(float h)
    {
        int n = (1 << Bits) + 1;
        var a = new float[n * n];
        Array.Fill(a, h);
        return new CubeTile { Key = new CubeKey(0, 14, 0, 0), Bits = Bits, Quantum = 0.01f, Heights = a };
    }

    [Fact]
    public void No_water_keeps_the_same_tile()
    {
        var t = Ground(500);
        Assert.Same(t, CubeSurface.Of(t, null));
        Assert.Same(t, CubeSurface.Of(t, CubeWater.Dry(Bits)));
    }

    [Fact]
    public void A_bed_under_water_is_raised_to_the_level()
    {
        var t = Ground(100);   // the bed, 272 m down
        var w = CubeWater.Dry(Bits);
        Array.Fill(w.Level, 372.3f);
        var s = CubeSurface.Of(t, w);
        Assert.All(s.Heights, h => Assert.Equal(372.3f, h));
        Assert.Equal(100, t.Heights[0]);   // the leaf's own heights untouched
    }

    [Fact]
    public void Ground_over_the_level_stays()
    {
        var t = Ground(380);
        var w = CubeWater.Dry(Bits);
        Array.Fill(w.Level, 372.3f);
        Assert.Same(t, CubeSurface.Of(t, w));
    }

    [Fact]
    public void A_vertex_between_samples_takes_its_wet_neighbours()
    {
        int side = CubeWater.SideFor(Bits);
        var level = new float[side * side];
        Array.Fill(level, float.NaN);
        level[0 * side + 0] = 10;   // vertex (0, 0)
        level[0 * side + 1] = 12;   // vertex (2, 0)
        Assert.Equal(11, CubeSurface.LevelAt(level, side, 1, 0));   // on the edge: the two along it
        Assert.Equal(11, CubeSurface.LevelAt(level, side, 1, 1));   // inside: the wet of the four round it
        Assert.Equal(10, CubeSurface.LevelAt(level, side, 0, 1));
        Assert.True(float.IsNaN(CubeSurface.LevelAt(level, side, 3, 3)));
    }

    [Fact]
    public void A_bed_left_dry_by_the_water_is_its_surface()
    {
        // the water stage leaves a lake leaf's outer samples dry over the bed: the edge row stood 300 m down
        var t = Ground(120);
        var w = CubeWater.Dry(Bits);
        int side = w.Side;
        for (int r = 1; r < side; r++)
            for (int c = 0; c < side; c++) w.Level[r * side + c] = 422.3f;
        var s = CubeSurface.Of(t, w);
        Assert.All(s.Heights, h => Assert.Equal(422.3f, h));
    }

    [Fact]
    public void Ground_by_water_but_not_under_it_stays()
    {
        // a bank a few metres under a pond (or a reach below a weir) is not a bed
        var w = CubeWater.Dry(Bits);
        w.Level[0] = 430f;
        Assert.Equal(415f, CubeSurface.SurfaceAt(w, 4, 4, 415f));
        Assert.Equal(400f, CubeSurface.SurfaceAt(w, 8, 8, 400f));   // too far from it
        Assert.Equal(430f, CubeSurface.SurfaceAt(w, 4, 4, 300f));   // a bed, within reach
    }
}
