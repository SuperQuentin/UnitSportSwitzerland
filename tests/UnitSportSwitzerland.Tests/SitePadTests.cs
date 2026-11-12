using Godot;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A building site's graded ground on a leaf (#1731): level over its area, blended back over the skirt, untouched past it.</summary>
public class SitePadTests
{
    [Fact]
    public void A_pad_levels_its_area_and_blends_back_over_the_skirt()
    {
        // a slope rising 0.2 m a metre to the east, a vertex every metre from -40 to 40
        var v = new List<float>();
        for (int z = -40; z <= 40; z++)
            for (int x = -40; x <= 40; x++)
                v.AddRange([x, 0.2f * x, z]);
        var vertices = v.ToArray();
        var pad = new SitePad(Vector2.Zero, Vector2.Right, 20, 10, 1.5f);
        SitePads.Apply(vertices, [pad]);

        float At(int x, int z) => vertices[((z + 40) * 81 + (x + 40)) * 3 + 1];
        Assert.Equal(1.5f, At(0, 0));
        Assert.Equal(1.5f, At(10, 5));
        Assert.Equal(1.5f, At(-10, -5));
        // half way across the skirt, between the pad and the ground
        float mid = At(14, 0);
        Assert.InRange(mid, 1.5f, 0.2f * 14);
        // past the skirt: the ground as it was
        Assert.Equal(0.2f * (10 + (int)SitePads.Skirt), At(10 + (int)SitePads.Skirt, 0), 4);
        Assert.Equal(0.2f * 30, At(30, 20), 4);
    }
}
