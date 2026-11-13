using System;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The globe as the earth world's far view (#1629): its horizon, its holes, its ground in the palette.</summary>
public class GlobeGroundTests
{
    private static EcefPos Above(double latDeg, double lonDeg, double h) => Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg, h));

    [Theory]
    // Geneva: the camera's horizon over the ellipsoid plus a 9 km summit's reach past it (~339 km), radians
    [InlineData(2, 0.0540)]
    [InlineData(600, 0.0669)]
    [InlineData(3_000, 0.0838)]
    public void The_horizon_reaches_past_the_ground_at_low_altitude(double h, double angle)
    {
        // on a 6,371 km sphere a camera at Geneva below ~4 km was under the ground: a horizon of 0
        Assert.Equal(angle, GlobeGround.HorizonAngle(Above(46.2, 6.15, h)), 3);
    }

    [Fact]
    public void Missing_heights_are_filled_from_their_neighbours()
    {
        const int n = 5;
        var hs = new float[n * n];
        Array.Fill(hs, 1000f);
        for (int j = 1; j < 4; j++)
            for (int i = 1; i < 4; i++) hs[j * n + i] = float.NaN;
        Assert.True(GlobeGround.FillHoles(hs, n));
        Assert.All(hs, h => Assert.Equal(1000f, h, 3));

        var none = new float[4];
        Array.Fill(none, float.NaN);
        Assert.False(GlobeGround.FillHoles(none, 2));
    }

    [Fact]
    public void Missing_heights_come_from_the_coarser_levels_first()
    {
        // a parent whose heights rise 1 m per sample eastward; its child (the south-west quarter) lost everything
        const int bits = 3, side = (1 << bits) + 1;
        var parent = new CubeKey(0, 10, 5, 7);
        var child = new CubeKey(0, 11, 10, 14);
        var heights = new float[side * side];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++) heights[j * side + i] = 100 + i;
        var tile = new CubeTile { Key = parent, Bits = bits, Heights = heights };
        var hs = new float[side * side];
        Array.Fill(hs, float.NaN);
        GlobeGround.FillFromAncestors(hs, side, child, bits, 1, k => k == parent ? tile : null);
        // child vertex i is parent sample i / 2: 100 at the west edge, 104 at the east
        Assert.Equal(100f, hs[0], 3);
        Assert.Equal(102f, hs[4], 3);
        Assert.Equal(104f, hs[8], 3);
        Assert.Equal(101.5f, hs[5 * side + 3], 3);
    }

    [Fact]
    public void Still_water_is_level_and_land_is_not()
    {
        const int n = 3;
        var lake = new float[n * n];
        Array.Fill(lake, 422.01f);
        lake[0] = 422.03f;
        Assert.True(GlobeGround.Level(lake, n, 1, 1));
        lake[8] = 422.5f;
        Assert.False(GlobeGround.Level(lake, n, 1, 1));
    }

    private static Vector3 Lin(CoverClass cover, float altitude = 400)
    {
        var c = CoverPalette.ColorFor(cover, altitude, 0.5f).SrgbToLinear();
        return new Vector3(c.R, c.G, c.B);
    }

    /// <summary>The bake's encoding over the region (OurColours.ForCartoonShader), linear in and out.</summary>
    private static Vector3 Stored(Vector3 linear)
    {
        float l = linear.Dot(new Vector3(0.2126f, 0.7152f, 0.0722f));
        var d = new Vector3(l, l, l).Lerp(linear, 0.8f).Max(Vector3.Zero);
        return d * d;
    }

    [Fact]
    public void The_bakes_palette_colours_come_back_exactly()
    {
        foreach (var cover in new[] { CoverClass.Open, CoverClass.Forest, CoverClass.Water, CoverClass.Rock })
        {
            var back = GlobeGround.Unstore(Stored(Lin(cover)));
            Assert.Equal(Lin(cover).X, back.X, 3);
            Assert.Equal(Lin(cover).Y, back.Y, 3);
            Assert.Equal(Lin(cover).Z, back.Z, 3);
        }
    }

    [Theory]
    [InlineData(CoverClass.Water, 0)]
    [InlineData(CoverClass.Forest, 1)]
    [InlineData(CoverClass.Rock, 2)]
    [InlineData(CoverClass.Snowfield, 3)]
    public void The_palettes_own_colours_read_as_their_cover(CoverClass cover, int weight)
    {
        var w = GlobeGround.Classify(Lin(cover));
        Assert.True(w[weight] > 0.9f, $"{cover}: {string.Join(", ", w)}");
    }

    [Fact]
    public void Open_ground_reads_as_open_and_is_painted_in_its_altitude_band()
    {
        var w = GlobeGround.Classify(Lin(CoverClass.Open));
        Assert.All(w, x => Assert.True(x < 0.1f, string.Join(", ", w)));
        // a vertex with no cover weight is the palette's open ground at its altitude (valley farmland, alpine meadow)
        foreach (float alt in new[] { 400f, 1800f })
        {
            var c = GlobeGround.Colour(Vector4.Zero, alt, 0.5f);
            var want = Lin(CoverClass.Open, alt);
            Assert.Equal(want.X, c.R, 4);
            Assert.Equal(want.Y, c.G, 4);
            Assert.Equal(0f, c.A);
        }
    }

    [Fact]
    public void A_weight_grid_is_sampled_between_its_samples()
    {
        var grid = GlobeGround.Weigh(2, 2, (x, y) => x == 0 ? new Color(0.20f, 0.32f, 0.42f) : new Color(0.33f, 0.47f, 0.22f));
        var west = GlobeGround.Sample(grid, 0.1f, 0.5f);
        var east = GlobeGround.Sample(grid, 0.9f, 0.5f);
        Assert.True(west.X > 0.9f && east.X < 0.1f, $"{west} {east}");
        Assert.Equal(0, GlobeGround.Dry(new Vector4(1, 0, 0, 0)).Length(), 3);
    }

    [Fact]
    public void A_height_at_a_pyramid_lake_level_is_that_lake()
    {
        // within the stored quantum (1 cm at least), never with no lakes
        float[] lakes = { 422.2f };
        Assert.True(GlobeGround.OnLake(lakes, 422.2f, 0.0024f));
        Assert.True(GlobeGround.OnLake(lakes, 422.21f, 0.0024f));
        Assert.False(GlobeGround.OnLake(lakes, 422.25f, 0.0024f));
        Assert.True(GlobeGround.OnLake(lakes, 422.23f, 0.039f));
        Assert.False(GlobeGround.OnLake(null, 422.2f, 0.01f));
    }
    [Fact]
    public void Bright_sand_is_not_snow()
    {
        // #1802: a desert read as a snowfield. Blue Marble's sand as the globe reads it back (lifted), against snow
        var sand = GlobeGround.Unstore(new Vector3(0.62f, 0.50f, 0.34f));
        var snow = GlobeGround.Unstore(new Vector3(0.80f, 0.82f, 0.86f));
        Assert.True(GlobeGround.Classify(sand)[3] < 0.1f, string.Join(", ", GlobeGround.Classify(sand)));
        Assert.True(GlobeGround.Classify(snow)[3] > 0.9f, string.Join(", ", GlobeGround.Classify(snow)));
    }

    [Fact]
    public void The_palette_paints_its_own_region_and_the_photo_the_rest()
    {
        // #1802: inside Switzerland's box the palette as before, far from it the photo's colour on land, the palette's water
        Assert.Equal(1f, GlobeGround.OwnPalette(46.5, 7.5));
        Assert.Equal(1f, GlobeGround.OwnPalette(45.6, 5.8));
        Assert.Equal(0f, GlobeGround.OwnPalette(25.0, 45.0));
        Assert.Equal(0f, GlobeGround.OwnPalette(46.5, 13.0));
        float edge = GlobeGround.OwnPalette(46.5, 10.6 + 0.25 / Math.Cos(46.5 * Math.PI / 180));
        Assert.InRange(edge, 0.3f, 0.7f);

        var sand = new Vector3(0.6f, 0.45f, 0.25f);
        var land = GlobeGround.Colour(Vector4.Zero, 300, 0.5f, sand, 0f);
        Assert.Equal(sand.X, land.R, 4);
        Assert.Equal(sand.Z, land.B, 4);
        var swiss = GlobeGround.Colour(Vector4.Zero, 300, 0.5f, sand, 1f);
        Assert.Equal(GlobeGround.Colour(Vector4.Zero, 300, 0.5f).R, swiss.R, 4);
        var sea = GlobeGround.Colour(new Vector4(1, 0, 0, 0), 0, 0.5f, sand, 0f);
        Assert.Equal(Lin(CoverClass.Water).Z, sea.B, 3);
    }

    [Fact]
    public void A_weight_grid_keeps_its_blocks_colour()
    {
        var grid = GlobeGround.Weigh(2, 2, (x, y) => new Color(0.5f, 0.4f, 0.3f));
        var c = GlobeGround.SampleColour(grid, 0.5f, 0.5f);
        // read as the bake stored it (sRGB texels, linear stored values), then back as the palette's
        var l = new Color(0.5f, 0.4f, 0.3f).SrgbToLinear();
        var want = GlobeGround.Unstore(new Vector3(l.R, l.G, l.B));
        Assert.Equal(want.X, c.X, 2);
        Assert.Equal(want.Y, c.Y, 2);
        Assert.Equal(want.Z, c.Z, 2);
    }
}
