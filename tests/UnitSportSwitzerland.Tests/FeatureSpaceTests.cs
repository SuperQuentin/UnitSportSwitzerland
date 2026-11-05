using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// A feature stage's space (#1507, G2 B3 PR 3): the LV95 build's (<see cref="Lv95FeatureSpace"/>) and a work
/// frame's (<see cref="FrameFeatureSpace"/>) place the same building at the same Earth position, and a feature on
/// a frame edge has exactly one owner.
/// </summary>
public class FeatureSpaceTests
{
    private readonly ITestOutputHelper _out;

    public FeatureSpaceTests(ITestOutputHelper output) => _out = output;

    private const int Level = 14;

    private sealed class FlatGround(double h) : IGround
    {
        public double? Sample(double x, double y) => h;
    }

    private static HashSet<CubeKey> Block(CubeKey centre, int r)
    {
        var set = new HashSet<CubeKey> { centre };
        for (int step = 0; step < r; step++)
            foreach (var k in set.ToList())
                foreach (var n in k.Neighbours()) set.Add(n);
        return set;
    }

    /// <summary>A pitched BD TOPO house: a 24 × 12 m rectangle turned 30°, counter-clockwise, ground 400 m, eave 406, ridge 410.</summary>
    private static BdFeature House(double lat, double lon)
    {
        var ring = new List<(double Lat, double Lon)>();
        double mLat = 111_150, mLon = 111_320 * Math.Cos(lat * Math.PI / 180), a = Math.PI / 6;
        foreach (var (u, v) in new[] { (-12.0, -6.0), (12.0, -6.0), (12.0, 6.0), (-12.0, 6.0), (-12.0, -6.0) })
        {
            double e = u * Math.Cos(a) - v * Math.Sin(a), n = u * Math.Sin(a) + v * Math.Cos(a);
            ring.Add((lat + n / mLat, lon + e / mLon));
        }
        using var doc = JsonDocument.Parse("""{"altitude_minimale_sol": 400, "altitude_minimale_toit": 406, "altitude_maximale_toit": 410, "usage_1": "Résidentiel"}""");
        var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return new BdFeature
        {
            Properties = props,
            Rings = new() { ring.Select(p => SwissGeodesy.ToLv95(p.Lat, p.Lon)).ToList() },
            LatLonRings = new() { ring },
        };
    }

    [Fact]
    public void A_French_house_lands_at_the_same_Earth_position_from_both_spaces()
    {
        const double lat = 46.1925, lon = 6.2310;   // Annemasse
        var house = new[] { House(lat, lon) };
        var (e0, n0) = SwissGeodesy.ToLv95(lat, lon);
        var tiles = new List<TileId>();
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
                tiles.Add(new TileId(TileId.FromLv95(e0, n0).E + de, TileId.FromLv95(e0, n0).N + dn));
        var lv = FranceBuildings.Build(house, new Lv95FeatureSpace(tiles, new FlatGround(400)), new FranceBuildings.Stats());

        var leaf = CubeKey.FromLatLon(lat, lon, Level);
        var driver = new FrameDriver(Block(leaf, 1), 0, SwissSeaLevel.Instance);
        var plan = driver.Frames.Single(f => f.Core.Contains(leaf));
        var cube = FranceBuildings.Build(house, new FrameFeatureSpace(plan, new FlatGround(400), 0), new FranceBuildings.Stats());

        var (tile, lvList) = lv.Single();
        var (owner, cubeList) = cube.Single();
        Assert.Equal(leaf, owner);
        var a = lvList.Single();
        var b = cubeList.Single();
        Assert.Equal(a.Triangles.Length, b.Triangles.Length);
        Assert.Equal(a.Kind, b.Kind);
        double worst = 0;
        for (int i = 0; i < a.Triangles.Length; i += 3)
        {
            var pa = SwissGeodesy.ToEcef(tile.MinE + a.Triangles[i], tile.MaxN - a.Triangles[i + 2], a.Triangles[i + 1]);
            var pb = leaf.Anchor.ToEcef(b.Triangles[i], b.Triangles[i + 1], b.Triangles[i + 2]);
            worst = Math.Max(worst, Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y) + (pa.Z - pb.Z) * (pa.Z - pb.Z)));
        }
        _out.WriteLine($"{a.Triangles.Length / 9} triangles, worst vertex {worst * 1000:F2} mm apart");
        // the LV95 side takes the latitudes at the Plateau's height (no tiles here): ~3 cm a km of height off it
        Assert.True(worst < 0.02, $"worst {worst} m");
        // the solid's height range is its vertices' in the leaf's frame
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 1; i < b.Triangles.Length; i += 3) { lo = Math.Min(lo, b.Triangles[i]); hi = Math.Max(hi, b.Triangles[i]); }
        Assert.InRange(b.MinY, lo - 0.01f, lo + 0.01f);
        Assert.InRange(b.MaxY, hi - 0.01f, hi + 0.01f);
    }

    [Fact]
    public void A_point_on_a_frame_corner_has_exactly_one_owner()
    {
        var geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);
        var corner = new CubeKey(0, Level, geneva.X & ~31, geneva.Y & ~31);
        var driver = new FrameDriver(Block(corner, 3), 0, SwissSeaLevel.Instance);
        Assert.Equal(4, driver.Frames.Count);
        var spaces = driver.Frames.Select(p => new FrameFeatureSpace(p, new FlatGround(0), 0)).ToList();
        // the frame corner on the ellipsoid, and points round it a few centimetres to a few metres away
        var c = Ellipsoid.Wgs84.ToGeodetic(corner.Vertex(0, 0, 0, 0));
        int points = 0;
        foreach (double r in new[] { 0.0, 0.01, 1, 30 })
            for (int k = 0; k < 16; k++)
            {
                double ang = k * Math.PI / 8;
                double lat = c.LatDeg + r * Math.Sin(ang) / 111_150, lon = c.LonDeg + r * Math.Cos(ang) / (111_320 * Math.Cos(c.LatDeg * Math.PI / 180));
                int owners = 0;
                foreach (var s in spaces)
                {
                    var (x, y) = s.FromLatLon(lat, lon);
                    if (s.TryOwner(x, y, out var leaf))
                    {
                        owners++;
                        Assert.Equal(CubeKey.FromLatLon(lat, lon, Level), leaf);
                    }
                }
                Assert.Equal(1, owners);
                points++;
            }
        _out.WriteLine($"{points} points round the corner of 4 frames: one owner each");
    }

    [Fact]
    public void The_LV95_space_is_the_LV95_tile_frame()
    {
        var t = new TileId(2500, 1117);
        var s = new Lv95FeatureSpace(new[] { t }, new FlatGround(0));
        Assert.Equal((2500123.25, 1117456.5, 401.0), s.FromSource(Lv95Source.Instance, 2500123.25, 1117456.5, 401));
        Assert.Equal((123.25f, 401f, 543.5f), s.ToLocal(t, 2500123.25, 1117456.5, 401));
        Assert.True(s.TryOwner(2500999.9, 1117000, out var own));
        Assert.Equal(t, own);
        Assert.False(s.TryOwner(2501000, 1117000, out _));
        Assert.Throws<NotSupportedException>(() => s.FromSource(new GeographicSource(SwissSeaLevel.Instance), 46, 6, 0));
    }
}
