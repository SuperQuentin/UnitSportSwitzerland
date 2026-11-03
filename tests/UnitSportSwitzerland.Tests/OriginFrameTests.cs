using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The anchor frame (#1430 H3, src/Core/WorldOrigin.cs): an origin and axes on the Earth beside the
/// flat LV95 ground frame, and the rigid moves between any two frames.
/// </summary>
public class OriginFrameTests
{
    private const double BernE = 2_600_000, BernN = 1_200_000;

    private static OriginFrame GroundAt(double e, double n) => new(e, n, 0);

    /// <summary>An anchor frame with the ground frame's own axes at an LV95 place and height.</summary>
    private static OriginFrame AnchorAt(double e, double n, double alt, int epoch = 1) => new(FrameAxes.GroundAt(e, n, alt), epoch);

    [Fact]
    public void The_ground_frame_is_as_before()
    {
        var f = GroundAt(BernE, BernN);
        Assert.False(f.IsAnchor);
        Assert.Equal(new Vector3(120, 540, -340), f.ToWorld(BernE + 120, BernN + 340, 540));
        Assert.Equal(new GlobalPos(BernE + 120, BernN + 340, 540), f.ToGlobal(new Vector3(120, 540, -340)));
        var shift = GroundAt(BernE + 1000, BernN).Since(f);
        Assert.Equal(Basis.Identity, shift.Transform.Basis);
        Assert.Equal(new Vector3(-1000, 0, 0), shift.Transform.Origin);
    }

    [Fact]
    public void Its_axes_at_a_place_are_orthonormal_and_up_is_the_ellipsoids()
    {
        var a = FrameAxes.GroundAt(BernE, BernN, 540);
        Assert.Equal(1, a.X.Length, 12);
        Assert.Equal(1, a.Y.Length, 12);
        Assert.Equal(1, a.Z.Length, 12);
        Assert.Equal(0, FrameAxes.Dot(a.X, a.Y), 12);
        Assert.Equal(0, FrameAxes.Dot(a.Y, a.Z), 12);
        Assert.Equal(0, FrameAxes.Dot(a.Z, a.X), 12);
        // up points away from the Earth's centre, within the deflection of geodetic from geocentric (~0.19°)
        double cos = FrameAxes.Dot(a.Y, a.Origin) / a.Origin.Length;
        Assert.True(cos > Math.Cos(0.25 * Math.PI / 180), $"up is the vertical ({Math.Acos(cos) * 180 / Math.PI:F3}°)");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1200, -30, 800)]
    [InlineData(25_000, 3_000, -18_000)]
    [InlineData(-20_000, -9_000, 20_000)]
    public void An_anchor_frame_round_trips_to_a_millimetre(float x, float y, float z)
    {
        var f = AnchorAt(BernE, BernN, 100_000);
        Assert.True(f.IsAnchor);
        var w = new Vector3(x, y, z);
        var g = f.ToGlobal(w);
        var back = f.ToWorld(g);
        Assert.True(back.DistanceTo(w) < 0.004f, $"{w} -> {g} -> {back}");
        // and the other way, an LV95 place near it
        var p = new GlobalPos(BernE + 7_000, BernN - 4_000, 98_000);
        Assert.True(f.ToGlobal(f.ToWorld(p)).DistanceTo(p) < 0.004, "an LV95 place round-trips");
    }

    [Fact]
    public void Near_its_place_an_anchor_with_the_grounds_axes_is_the_ground_frame()
    {
        // at the anchor's place the two frames agree but for the origin; a kilometre away the flat plane
        // has fallen d²/2R below the anchor's tangent plane: 8 cm (the global tiling plan's figure)
        var ground = GroundAt(BernE, BernN);
        var anchor = AnchorAt(BernE, BernN, 0);
        var shift = anchor.Since(ground);
        var here = ground.ToWorld(BernE, BernN, 0);
        Assert.True(shift.Point(here).Length() < 0.01f, $"the anchor's place maps to its origin ({shift.Point(here)})");
        var km = ground.ToWorld(BernE + 1000, BernN, 0);
        var moved = shift.Point(km);
        var exact = anchor.ToWorld(BernE + 1000, BernN, 0);
        Assert.True(moved.DistanceTo(exact) < 0.12f, $"1 km away within the curvature's 8 cm ({moved.DistanceTo(exact):F3} m)");
        Assert.True(shift.Transform.Basis.IsEqualApprox(Basis.Identity), "and the axes turn by nothing there");
    }

    [Fact]
    public void Moves_between_anchors_are_rigid_and_compose()
    {
        var a = AnchorAt(BernE, BernN, 80_000, 1);
        // 30 km east, its up the ellipsoid's there: a turn of ~0.27°
        var b = AnchorAt(BernE + 30_000, BernN, 80_000, 2);
        var ab = b.Since(a);
        var ba = a.Since(b);
        var w = new Vector3(5_000, 200, -3_000);
        Assert.True(ba.Point(ab.Point(w)).DistanceTo(w) < 0.01f, "there and back is nothing");
        Assert.True(Math.Abs(ab.Transform.Basis.Determinant() - 1) < 1e-5, "a rotation, no scale");
        Assert.True(ab.Point(w).DistanceTo(b.ToWorld(a.ToGlobal(w))) < 0.01f, "the move agrees with the frames themselves");
        float turn = Mathf.RadToDeg(ab.Direction(Vector3.Up).AngleTo(Vector3.Up));
        Assert.InRange(turn, 0.2f, 0.35f);
    }

    [Fact]
    public void Same_frame_is_place_and_axes_not_epoch()
    {
        Assert.True(GroundAt(BernE, BernN).SameFrame(new OriginFrame(BernE, BernN, 7)));
        Assert.False(GroundAt(BernE, BernN).SameFrame(GroundAt(BernE + 1000, BernN)));
        Assert.True(AnchorAt(BernE, BernN, 1000, 1).SameFrame(AnchorAt(BernE, BernN, 1000, 9)));
        Assert.False(AnchorAt(BernE, BernN, 1000).SameFrame(GroundAt(BernE, BernN)));
    }

    [Fact]
    public void A_tile_in_an_anchor_frame_sits_at_its_corner_with_its_grid_axes()
    {
        var f = AnchorAt(BernE + 500, BernN - 500, 0);
        var tile = TileId.FromLv95(BernE + 500, BernN - 500);
        var t = f.TileTransform(tile);
        Assert.True(t.Origin.DistanceTo(f.ToWorld(tile.MinE, tile.MaxN, 0)) < 0.001f);
        // a point in the tile's local frame lands where the LV95 mapping puts it, to the curvature's centimetres
        var local = new Vector3(400, 30, 700);
        Assert.True((t * local).DistanceTo(f.TileToWorld(tile, local)) < 0.05f, $"{t * local} / {f.TileToWorld(tile, local)}");
        var back = f.TileLocal(tile, f.TileToWorld(tile, local));
        Assert.True(back.DistanceTo(local) < 0.004f, $"tile-local round trip ({back})");
    }

    [Fact]
    public void A_shifts_yaw_is_its_turn_about_up()
    {
        Assert.Equal(0f, OriginShift.Identity.Yaw, 6);
        Assert.False(OriginShift.Identity.Turns);
        var turned = new OriginShift(new Transform3D(new Basis(Vector3.Up, 0.035f), new Vector3(20, 0, 0)));
        Assert.True(turned.Turns);
        Assert.Equal(0.035f, turned.Yaw, 5);
        // a heading kept as an angle and turned by Yaw points where the turned direction does
        float heading = 1.2f;
        var dir = new Basis(Vector3.Up, heading) * Vector3.Forward;
        var after = new Basis(Vector3.Up, heading + turned.Yaw) * Vector3.Forward;
        Assert.True(after.DistanceTo(turned.Direction(dir)) < 1e-5f);
    }

    [Fact]
    public void Altitude_and_up_in_both_kinds_of_frame()
    {
        var ground = GroundAt(BernE, BernN);
        Assert.Equal(612.5, ground.AltitudeOf(new Vector3(30, 612.5f, -40)), 3);
        Assert.Equal(Vector3.Up, ground.UpAt(new Vector3(30, 612.5f, -40)));
        var anchor = AnchorAt(BernE, BernN, 120_000);
        var p = new GlobalPos(BernE + 15_000, BernN - 8_000, 118_000);
        Assert.Equal(p.Alt, anchor.AltitudeOf(anchor.ToWorld(p)), 0.01);
        Assert.True(anchor.UpAt(Vector3.Zero).DistanceTo(Vector3.Up) < 1e-5f, "up at the anchor is its Y");
        // 17 km away the ground's up has turned ~0.15° from the anchor's
        float turn = Mathf.RadToDeg(anchor.UpAt(anchor.ToWorld(p)).AngleTo(Vector3.Up));
        Assert.InRange(turn, 0.1f, 0.2f);
    }

    [Fact]
    public void The_sun_from_an_anchor_with_the_grounds_axes_is_where_the_ground_has_it()
    {
        var date = new DateOnly(2026, 8, 1);
        try
        {
            World.SunPath.Frame = null;
            var flat = World.SunPath.Toward(SkyBody.Sun, date, 15, BernE, BernN, out _);
            // the place cell's centre: the anchor at it has the very axes the ground path turns into
            World.SunPath.Frame = AnchorAt(BernE + 500, BernN + 500, 0);
            var anchored = World.SunPath.Toward(SkyBody.Sun, date, 15, BernE, BernN, out _);
            Assert.True(Mathf.RadToDeg(flat.AngleTo(anchored)) < 0.01f, $"{flat} / {anchored}");
        }
        finally { World.SunPath.Frame = null; }
    }

    [Fact]
    public void A_ground_frame_with_a_vertical_origin_for_the_Space_regime()
    {
        var low = GroundAt(BernE, BernN);
        var high = new OriginFrame(BernE + 3_000, BernN, 72_000, 1);
        var p = new GlobalPos(BernE + 3_200, BernN - 100, 71_500);
        var w = high.ToWorld(p);
        Assert.Equal(new Vector3(200, -500, 100), w);
        Assert.Equal(p, high.ToGlobal(w));
        Assert.Equal(71_500, high.AltitudeOf(w), 6);
        // the move up is a pure translation, the vertical included
        var up = high.Since(low);
        Assert.Equal(Basis.Identity, up.Transform.Basis);
        Assert.Equal(new Vector3(-3_000, -72_000, 0), up.Transform.Origin);
        Assert.True(up.Point(low.ToWorld(p)).DistanceTo(w) < 0.01f);
        Assert.False(high.SameFrame(new OriginFrame(BernE + 3_000, BernN, 1)), "the vertical origin is part of the frame");
        // a network anchor (altitude 0) in it
        Assert.Equal(new Vector3(-3_000, -72_000, 0), high.SinceAnchor(BernE, BernN).Transform.Origin);
    }

    [Theory]
    [InlineData(false, 59_000, false)]
    [InlineData(false, 61_000, true)]
    [InlineData(true, 55_000, true)]
    [InlineData(true, 49_000, false)]
    public void The_Space_regime_has_hysteresis(bool inSpace, double altitude, bool expected) =>
        Assert.Equal(expected, Curvature.SpaceRegime(inSpace, altitude));
}
