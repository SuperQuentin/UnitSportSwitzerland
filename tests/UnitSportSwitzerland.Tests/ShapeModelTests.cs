using System;
using System.Collections.Generic;
using System.IO;
using UnitSport.Core;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Mars' moons (#1853): a shape model to heights (tools/TerrainPreprocessor/ShapeModel.cs), their circles and spheres.</summary>
public class ShapeModelTests
{
    /// <summary>A Celestia binary model of an axis-aligned box (half sizes in km, Celestia's axes), positions only.</summary>
    private static byte[] BoxCmod(double hx, double hy, double hz)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("#celmodel_binary"));
        // a material: a diffuse colour
        w.Write((short)1001); w.Write((short)1003); w.Write((short)7); w.Write(1f); w.Write(1f); w.Write(1f); w.Write((short)1002);
        w.Write((short)1009);
        w.Write((short)1011); w.Write((short)0); w.Write((short)2); w.Write((short)3); w.Write((short)2); w.Write((short)1012);
        w.Write((short)1013);
        var corners = new List<(float, float, float)>();
        for (int i = 0; i < 8; i++) corners.Add(((i & 1) == 0 ? (float)-hx : (float)hx, (i & 2) == 0 ? (float)-hy : (float)hy, (i & 4) == 0 ? (float)-hz : (float)hz));
        w.Write((uint)8);
        foreach (var (x, y, z) in corners) { w.Write(x); w.Write(y); w.Write(z); w.Write(0f); w.Write(0f); w.Write(1f); }
        int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        w.Write((short)0); w.Write((uint)0); w.Write((uint)faces.Length);
        foreach (int f in faces) w.Write((uint)f);
        w.Write((short)1010);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void A_cmod_comes_in_the_bodys_iau_axes_in_metres()
    {
        // Celestia's x 1 km, y (the pole) 2 km, z 3 km: the IAU's X 1 km, Y 3 km, Z 2 km
        var m = ShapeModel.ReadCmod(BoxCmod(1, 2, 3));
        Assert.Equal(8, m.Vertices);
        Assert.Equal(12, m.TriangleCount);
        double Max(double[] a) { double r = double.MinValue; foreach (var v in a) r = Math.Max(r, v); return r; }
        Assert.Equal(1000, Max(m.X), 3);
        Assert.Equal(3000, Max(m.Y), 3);
        Assert.Equal(2000, Max(m.Z), 3);
    }

    [Fact]
    public void Each_pixel_is_the_farthest_hit_along_its_direction()
    {
        var m = ShapeModel.ReadCmod(BoxCmod(1, 2, 3));
        const int W = 360;
        var h = ShapeModel.Heights(m, 1500, W, out int missed);
        Assert.Equal(0, missed);
        float At(double lat, double lon)
        {
            int row = (int)((90 - lat) / 180 * (W / 2)), col = (int)((lon + 180) / 360 * W);
            return h[Math.Clamp(row, 0, W / 2 - 1) * W + Math.Clamp(col, 0, W - 1)];
        }
        // along X (1 km), Y (3 km) and Z (2 km), less the 1.5 km sphere, within the pixel's half degree
        Assert.InRange(At(0.5, 0.5), -500 - 5, -500 + 5);
        Assert.InRange(At(0.5, 90.5), 1500 - 5, 1500 + 5);
        Assert.InRange(At(89.6, 0), 500 - 5, 500 + 5);
        Assert.InRange(At(-89.6, 0), 500 - 5, 500 + 5);
    }

    [Fact]
    public void The_raster_reads_back_as_a_whole_body_dem()
    {
        var m = ShapeModel.ReadCmod(BoxCmod(1, 2, 3));
        var h = ShapeModel.Heights(m, 1500, 720, out _);
        string path = Path.Combine(Path.GetTempPath(), $"shape_{Guid.NewGuid():N}.tif");
        try
        {
            File.WriteAllBytes(path, ShapeModel.Tiff(h, 720));
            using var r = CylRaster.Open(path);
            Assert.Equal(720, r.Width);
            Assert.Equal(360, r.Height);
            var win = r.Read(0, r.Height - 1, 0, r.Width);
            Assert.InRange(win.At(0.25, 0.25), -510, -490);
            Assert.InRange(win.At(0.25, 90.25), 1490, 1510);
        }
        finally { File.Delete(path); }
    }

    // JPL Horizons, Mars-centred ICRF vectors (km), 2026-01-01 0h and 12h TDB; the circles leave out the orbits' tilts
    // (Deimos' 2.5° off Mars' equator) and Phobos' eccentricity (2°)
    [Theory]
    [InlineData(SkyBody.Phobos, 0.0, -2628.223, -8355.790, -2928.529, 3.0)]
    [InlineData(SkyBody.Phobos, 0.5, -653.792, 8194.764, 4742.721, 3.0)]
    [InlineData(SkyBody.Deimos, 0.0, 10863.689, 20169.954, 5021.957, 3.0)]
    [InlineData(SkyBody.Deimos, 0.5, -19857.471, -11914.678, 3730.513, 3.0)]
    public void Mars_moons_are_where_horizons_has_them(SkyBody moon, double days, double x, double y, double z, double degrees)
    {
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days).AddSeconds(-69.184);
        var p = Ephemeris.FromParent(moon, utc);
        double dot = (p.X * x + p.Y * y + p.Z * z) / (p.Length * Math.Sqrt(x * x + y * y + z * z));
        double off = Math.Acos(Math.Clamp(dot, -1, 1)) * 180 / Math.PI;
        Assert.True(off < degrees, $"{moon} {off:F2}° off Horizons");
        Assert.Equal(1, p.Length / 1000 / Math.Sqrt(x * x + y * y + z * z), 1);
    }

    [Fact]
    public void Mars_moons_spheres_are_their_radius_and_20_km()
    {
        Assert.Equal(11_100 + 20_000, CelestialBody.Phobos.SphereOfInfluence, 0);
        Assert.Equal(6_200 + 20_000, CelestialBody.Deimos.SphereOfInfluence, 0);
        Assert.Equal(BodyId.Mars, CelestialBody.Phobos.Parent);
        Assert.InRange(CelestialBody.Phobos.GroundGravity, 0.005, 0.0065);
        Assert.InRange(CelestialBody.Deimos.GroundGravity, 0.002, 0.003);
    }
}
