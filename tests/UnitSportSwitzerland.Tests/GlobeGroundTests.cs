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
}
