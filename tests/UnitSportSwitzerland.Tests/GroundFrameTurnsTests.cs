using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The earth world's ground frame turns (#1512, G2 C3, <c>OriginShifter.GroundMode</c>): every 3 km the world moves to
/// the tangent anchor under the camera at altitude 0, its axes turned by the least rotation; what is kept in world
/// space through the shifts stays on its Earth place, up stays the ellipsoid's; boxes are moved round their corners;
/// and the shaders' curvature on round geometry is refraction's lift only.
/// </summary>
public class GroundFrameTurnsTests
{
    // Geneva, the D: region's densest leaf, and a drive 60 km towards Lausanne
    private const double GenevaE = 2_500_956, GenevaN = 1_117_569;

    /// <summary>What <c>OriginShifter.TurnedAnchor</c> builds at altitude 0 under a camera.</summary>
    private static FrameAxes Under(OriginFrame frame, Vector3 camera) =>
        FrameAxes.Tangent(frame.ToEcef(camera), frame.AltitudeOf(camera), frame.Axes!);

    [Fact]
    public void A_drive_across_twenty_turning_re_anchors_keeps_what_is_parked_on_its_place_and_up_the_ellipsoids()
    {
        var frame = new OriginFrame(FrameAxes.Enu(SwissGeodesy.ToEcef(GenevaE, GenevaN, 0)), 0);
        // a car parked beside the start, kept as a world point and moved by each shift in floats (as a node is)
        var parkedAt = GlobalPos.FromLv95(GenevaE + 40, GenevaN - 25, 375);
        var parked = frame.ToWorld(parkedAt);
        // the camera drives north-east along the world's level, re-anchoring past 3 km (its altitude rises along each
        // tangent plane, 0.7 m at 3 km, and each new anchor's level takes it back)
        var camera = new Vector3(0, 400, 0);
        var heading = new Vector3(1, 0, -1).Normalized();
        int shifts = 0;
        double turned = 0;
        for (int step = 0; step < 600; step++)
        {
            camera += heading * 100;
            if (new Vector2(camera.X, camera.Z).Length() <= 3000) continue;
            var next = new OriginFrame(Under(frame, camera), frame.Epoch + 1);
            var shift = next.Since(frame);
            var cameraBefore = frame.ToGlobal(camera);
            double altitude = frame.AltitudeOf(camera);
            camera = shift.Point(camera);
            parked = shift.Point(parked);
            heading = shift.Direction(heading) with { Y = 0 };
            heading = heading.Normalized();
            turned += Math.Asin(Math.Min(1.0, Vector3.Up.Cross(shift.Direction(Vector3.Up)).Length()));
            frame = next;
            shifts++;

            // the camera is straight over the new origin, its height the altitude, and nothing jumped
            Assert.True(new Vector2(camera.X, camera.Z).Length() < 0.01f, $"camera {camera} off the new origin's normal at shift {shifts}");
            Assert.InRange(camera.Y - altitude, -0.01, 0.01);
            Assert.InRange(altitude, 400, 420);
            Assert.True(frame.ToGlobal(camera).DistanceTo(cameraBefore) < 0.005, $"the camera moved across shift {shifts}");
            // up at the camera is the ellipsoid's: gravity along -Y is true there
            Assert.True(frame.UpAt(camera).AngleTo(Vector3.Up) < 1e-5f, $"up off by {frame.UpAt(camera).AngleTo(Vector3.Up)} rad at shift {shifts}");
        }
        Assert.InRange(shifts, 19, 21);
        // 60 km over R: about 0.54° in all
        Assert.InRange(turned * 180 / Math.PI, 0.5, 0.58);
        // the parked car, moved in floats through every turning shift, is still on its place: it ends 57 km from the
        // origin, where a float's step is 4 mm and the float basis turns 3 mm off a shift (a random walk, 2.3 cm here).
        // A body that far is dormant, kept by its Earth place.
        var drift = parked.DistanceTo(frame.ToWorld(parkedAt));
        Assert.True(drift < 0.05f, $"the parked car drifted {drift * 100:F2} cm over {shifts} turning shifts");
        // and what is near the camera (a body beside it) moves through a turning shift to a tenth of a millimetre
        var nearAt = frame.ToGlobal(camera + new Vector3(25, -1.5f, 12));
        var near = frame.ToWorld(nearAt);
        var beyond = new OriginFrame(Under(frame, camera + heading * 3001), frame.Epoch + 1);
        var nearDrift = beyond.Since(frame).Point(near).DistanceTo(beyond.ToWorld(nearAt));
        Assert.True(nearDrift < 1e-3f, $"a body 30 m away moved {nearDrift * 1000:F2} mm through a turning shift");
    }

    [Fact]
    public void A_teleport_lands_on_the_tangent_anchor_with_the_least_rotation()
    {
        var start = FrameAxes.Enu(SwissGeodesy.ToEcef(GenevaE, GenevaN, 0));
        // what OriginShifter.ShiftTo builds in the earth world for a point 150 km away (Bern)
        var at = SwissGeodesy.ToEcef(2_600_000, 1_200_000, 0);
        var anchor = FrameAxes.Tangent(at, 0, start);
        var (x, y, z) = anchor.FromEcef(at);
        Assert.True(Math.Abs(x) < 1e-6 && Math.Abs(y) < 1e-6 && Math.Abs(z) < 1e-6);
        // its up is the ellipsoid's there, and X turned about as much as the up did (~1.3°), no spin
        var enu = FrameAxes.Enu(at);
        Assert.True(FrameAxes.Dot(anchor.Y, enu.Y) > 1 - 1e-12);
        double upTurn = Math.Acos(FrameAxes.Dot(start.Y, anchor.Y));
        Assert.True(Math.Acos(Math.Min(1, FrameAxes.Dot(start.X, anchor.X))) <= upTurn + 1e-7);
    }

    [Fact]
    public void A_box_moved_by_a_turning_shift_holds_its_turned_corners()
    {
        var box = new Aabb(new Vector3(100, 2, -50), new Vector3(4, 2, 10));
        var translate = new OriginShift(new Transform3D(Basis.Identity, new Vector3(-3000, 0, 1000)));
        var moved = translate.Apply(box);
        Assert.Equal(box.Position + new Vector3(-3000, 0, 1000), moved.Position);
        Assert.Equal(box.Size, moved.Size);

        var turn = new OriginShift(new Transform3D(new Basis(new Vector3(1, 0, 1).Normalized(), 0.3f), new Vector3(-3000, 5, 1000)));
        var turned = turn.Apply(box);
        var grown = turned.Grow(1e-3f);
        for (int k = 0; k < 8; k++)
        {
            var corner = new Vector3((k & 1) != 0 ? box.End.X : box.Position.X, (k & 2) != 0 ? box.End.Y : box.Position.Y,
                (k & 4) != 0 ? box.End.Z : box.Position.Z);
            Assert.True(grown.HasPoint(turn.Point(corner)), $"corner {k} outside the moved box");
        }
        // and no bigger than its corners: each face touches one
        Assert.True(turned.Size.X < box.Size.Length() + 0.01f && turned.Size.Y < box.Size.Length() + 0.01f);
    }

    [Fact]
    public void A_turn_too_small_for_IsEqualApprox_still_turns()
    {
        Assert.False(OriginShift.Identity.Turns);
        Assert.False(new OriginShift(new Transform3D(Basis.Identity, new Vector3(20, 0, 0))).Turns);
        // a 20 m stress shift in the earth world: 20 / R rad
        var tiny = new OriginShift(new Transform3D(new Basis(Vector3.Right, (float)(20 / 6_371_000.0)), new Vector3(20, 0, 0)));
        Assert.True(tiny.Turns);
    }

    [Fact]
    public void On_round_geometry_the_curvature_is_refractions_lift_only()
    {
        const double r = Curvature.EarthRadiusM;
        // the flat world is as before
        Assert.Equal(Curvature.DropPerM2At(0), Curvature.CurvaturePerM2At(0, round: false));
        Assert.Equal(Curvature.SphereBlendAt(40_000), Curvature.CurveBlendAt(40_000, round: false));
        // round geometry already falls by d²/2R: with the lift it shows what the flat world's drop shows
        double lift = Curvature.CurvaturePerM2At(0, round: true);
        Assert.True(lift < 0, "a lift");
        Assert.Equal(Curvature.DropPerM2At(0), 1 / (2 * r) + lift, 15);
        // 1 cm at 1 km, gone from 20 km, no sphere wrap at any height
        Assert.InRange(-lift * 1e6, 0.0101, 0.0103);
        Assert.Equal(0, Curvature.CurvaturePerM2At(20_000, round: true));
        Assert.Equal(0, Curvature.CurveBlendAt(400_000, round: true));
        // a marker's drawn drop follows the same rule
        Curvature.RoundGeometry = true;
        try { Assert.Equal(lift * 1e6, Curvature.DrawnDrop(1000), 9); }
        finally { Curvature.RoundGeometry = false; }
        Assert.Equal(Curvature.Drop(1000), Curvature.DrawnDrop(1000));
    }
}
