using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The ground round a flush pad (#603): a car park's pad lay in a shallow cut, the road blend
/// pinned only the lattice points inside it, and the terrain triangles across its edge rose to
/// the raw ground one cell out — through the pad, as straight-edged wedges on the diagonals.
/// </summary>
public class PadBlendTests
{
    private const int N = ChunkFormat.GridSize;
    private const double Sp = ChunkFormat.SpacingM;

    /// <summary>A 30 x 12 m pad at height 0 round (100, 100), turned 14° so its edges cross the lattice.</summary>
    private static RoadAreaProp Pad(AreaPropType type)
    {
        double a = 14 * Math.PI / 180, ux = Math.Cos(a), uz = Math.Sin(a);
        var v = new List<float>();
        foreach (var (du, dv) in new[] { (-15.0, -6.0), (15.0, -6.0), (15.0, 6.0), (-15.0, 6.0) })
            v.AddRange([(float)(100 + du * ux - dv * uz), 0f, (float)(100 + du * uz + dv * ux)]);
        return new RoadAreaProp { Type = type, Vertices = v.ToArray(), Indices = [0, 1, 2, 0, 2, 3] };
    }

    /// <summary>The terrain surface at (x, z) under either diagonal of its quad, whichever is higher.</summary>
    private static double SurfaceAt(float[] map, double x, double z)
    {
        int c = (int)Math.Floor(x / Sp), r = (int)Math.Floor(z / Sp);
        double fx = x / Sp - c, fz = z / Sp - r;
        double h00 = map[r * N + c], h10 = map[r * N + c + 1], h01 = map[(r + 1) * N + c], h11 = map[(r + 1) * N + c + 1];
        double a = fx >= fz ? h00 + fx * (h10 - h00) + fz * (h11 - h10) : h00 + fz * (h01 - h00) + fx * (h11 - h01);
        double b = fx + fz <= 1 ? h00 + fx * (h10 - h00) + fz * (h01 - h00) : h11 + (1 - fx) * (h01 - h11) + (1 - fz) * (h10 - h11);
        return Math.Max(a, b);
    }

    private static bool Inside(RoadAreaProp pad, double x, double z)
    {
        var v = pad.Vertices;
        for (int k = 0; k < 4; k++)
        {
            int i = k * 3, j = (k + 1) % 4 * 3;
            if ((v[j] - v[i]) * (z - v[i + 2]) - (v[j + 2] - v[i + 2]) * (x - v[i]) < 0) return false;
        }
        return true;
    }

    [Theory]
    [InlineData(AreaPropType.ParkingPad, 0.0)]
    [InlineData(AreaPropType.ParkingPad, TerrainMeshBuilder.VisualBlendClearance)]
    [InlineData(AreaPropType.Pavement, 0.0)]
    public void Ground_never_rises_through_a_pad_in_a_cut(AreaPropType type, double clearance)
    {
        var pad = Pad(type);
        var blend = TerrainMeshBuilder.ComputeRoadBlend(new RoadTile { Segments = [], AreaProps = [pad] });
        var map = new float[N * N];
        Array.Fill(map, 0.8f);   // the lot is dug 0.8 m into the ground round it
        TerrainMeshBuilder.ApplyRoadBlend(map, blend, clearance);

        double worst = double.NegativeInfinity;
        for (double z = 80; z <= 120; z += 0.1)
            for (double x = 80; x <= 120; x += 0.1)
                if (Inside(pad, x, z)) worst = Math.Max(worst, SurfaceAt(map, x, z));
        Assert.True(worst <= 1e-4, $"the ground stands {worst:0.000} m over the pad");
    }

    [Fact]
    public void Ground_climbs_back_to_its_own_height_past_the_rim()
    {
        var pad = Pad(AreaPropType.ParkingPad);
        var blend = TerrainMeshBuilder.ComputeRoadBlend(new RoadTile { Segments = [], AreaProps = [pad] });
        var map = new float[N * N];
        Array.Fill(map, 0.8f);
        TerrainMeshBuilder.ApplyRoadBlend(map, blend, 0);

        // 5 m out from the pad's long side, past the slope's reach, the ground is untouched
        Assert.Equal(0.8f, map[(int)Math.Round(100 + 11 * Math.Cos(14 * Math.PI / 180)) * N + (int)Math.Round(100 - 11 * Math.Sin(14 * Math.PI / 180))]);
    }
}
