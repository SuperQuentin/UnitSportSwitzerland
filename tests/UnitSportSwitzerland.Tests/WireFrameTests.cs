using System;
using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The wire in ECEF and local ENU (#1503, G2 A2, protocol 93): <c>GlobalPos</c>'s wire form and the turn between
/// its offset axes and the local east-up-south, and <c>OriginFrame</c>'s directions, headings, rotations and point
/// lists through the ENU, in the flat ground frame and in an anchor frame, at Bern and at Geneva.
/// </summary>
public class WireFrameTests
{
    private readonly ITestOutputHelper _out;

    public WireFrameTests(ITestOutputHelper output) => _out = output;

    private const double BernE = 2_600_000, BernN = 1_200_000;
    private const double GenevaE = 2_500_000, GenevaN = 1_118_000;

    public static TheoryData<double, double> Places => new() { { BernE, BernN }, { GenevaE, GenevaN } };

    /// <summary>A ground frame on the whole km near a place, as the game's origin is.</summary>
    private static OriginFrame Ground(double e, double n) => new(Math.Round(e / 1000) * 1000 + 3000, Math.Round(n / 1000) * 1000 - 2000, 0);

    /// <summary>An anchor frame at a place, its axes the ground's there turned 2° about up (as <c>--originyaw 2</c>).</summary>
    private static OriginFrame Turned(double e, double n)
    {
        var g = FrameAxes.GroundAt(e + 1500, n - 800, 0);
        double a = 2 * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        // about Y: X' = X cos − Z sin, Z' = X sin + Z cos
        var x = new EcefPos(g.X.X * c - g.Z.X * s, g.X.Y * c - g.Z.Y * s, g.X.Z * c - g.Z.Z * s);
        var z = new EcefPos(g.X.X * s + g.Z.X * c, g.X.Y * s + g.Z.Y * c, g.X.Z * s + g.Z.Z * c);
        return new OriginFrame(g with { X = x, Z = z }, 3);
    }

    /// <summary>An ECEF direction in the ENU at an ECEF point, independently of the code under test.</summary>
    private static Vector3 Enu(EcefPos at, EcefPos dir)
    {
        var (x, y, z) = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(at)).DirectionToLocal(dir);
        return new Vector3((float)x, (float)y, (float)z);
    }

    private static float YawOf(Vector3 forward) => Mathf.Atan2(-forward.X, -forward.Z);

    [Theory]
    [MemberData(nameof(Places))]
    public void A_position_crosses_the_wire_as_ECEF_to_a_tenth_of_a_millimetre(double e, double n)
    {
        var p = GlobalPos.FromLv95(e + 123.456, n - 78.9, 1234.5);
        var (x, y, z) = p.ToWire();
        var ecef = p.ToEcef();
        Assert.Equal((ecef.X, ecef.Y, ecef.Z), (x, y, z));
        var back = GlobalPos.FromWire(x, y, z);
        Assert.True(back.DistanceTo(p) < 1e-4, $"{p} -> {back}");
        // Write / Read (dictionaries, native Godot) are checked in the game by --origincheck
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void Offset_axes_turn_into_the_local_ENU_by_the_meridian_convergence(double e, double n)
    {
        var p = GlobalPos.FromLv95(e, n, 500);
        var b = p.Enu.Turn;
        Assert.InRange(p.Enu.Scale, 0.9999f, 1.0003f);
        Assert.True(b.IsEqualApprox(b.Orthonormalized()) && Math.Abs(b.Determinant() - 1) < 1e-5, "a rotation");
        Assert.True((b * Vector3.Up).DistanceTo(Vector3.Up) < 1e-5f, "about up");
        // grid north, measured independently: a step north on the grid, seen in the ENU there
        var step = GlobalPos.FromLv95(e, n + 1, 500).ToEcef() - p.ToEcef();
        var gridNorth = Enu(p.ToEcef(), step).Normalized();
        Assert.True(p.EnuFromOffset(new Vector3(0, 0, -1)).Normalized().DistanceTo(gridNorth) < 1e-5f, $"{p.EnuFromOffset(new Vector3(0, 0, -1))} vs {gridNorth}");
        var v = new Vector3(12.5f, -3f, 40f);
        Assert.True(p.OffsetFromEnu(p.EnuFromOffset(v)).DistanceTo(v) < 1e-4f);
        // the rate over a hundredth of a second is the linear form on the ground, to a hundredth of a mm/s
        var rate = p.RateOfEnu(p.EnuFromOffset(v));
        Assert.True(rate.DistanceTo(v) < 2e-3f, $"{rate} vs {v}");
        // headings: grid north's true yaw is the convergence, and the two are inverses
        float convergence = p.TrueYaw(0f);
        Assert.Equal(YawOf(gridNorth), convergence, 5);
        Assert.Equal(1.234f, p.TrueYaw(p.OffsetYaw(1.234f)), 5);
        float deg = Mathf.RadToDeg(Mathf.Abs(convergence));
        _out.WriteLine($"convergence at {e}/{n}: {Mathf.RadToDeg(convergence):F4}°");
        if (e == BernE) Assert.True(deg < 0.01f, $"none at Bern ({deg:F4}°)");
        else Assert.InRange(deg, 0.8f, 1.0f);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void Ground_frame_directions_and_headings_go_through_the_ENU(double e, double n) =>
        DirectionsAndHeadings(Ground(e, n), e, n);

    [Theory]
    [MemberData(nameof(Places))]
    public void Anchor_frame_directions_and_headings_go_through_the_ENU(double e, double n) =>
        DirectionsAndHeadings(Turned(e, n), e, n);

    private static void DirectionsAndHeadings(OriginFrame f, double e, double n)
    {
        var at = f.ToWorld(e + 200, n - 300, 600);
        var v = new Vector3(20f, 1.5f, -7f);
        var enu = f.EnuOf(at, v);
        // independently: the world direction as Earth-fixed, then seen in the ENU at the point
        var expected = Enu(f.ToEcef(at), f.EcefDirection(at, v));
        // a flat world's metre is a grid metre: its horizontal is scaled by the grid's scale there (1 to 5e-4)
        if (!f.IsAnchor) expected = new Vector3(expected.X * f.EnuAt(at).Scale, expected.Y, expected.Z * f.EnuAt(at).Scale);
        Assert.True(enu.DistanceTo(expected) < 1e-4f, $"{enu} vs {expected}");
        Assert.True(f.WorldOfEnu(at, enu).DistanceTo(v) < 1e-4f, "and back");
        // in the ground frame the world's axes are GlobalPos's offset axes
        if (!f.IsAnchor) Assert.True(enu.DistanceTo(f.ToGlobal(at).EnuFromOffset(v)) < 1e-5f);
        // a world heading's true yaw is the yaw of its forward in the ENU; and back
        float worldYaw = 0.7f;
        var forward = new Basis(Vector3.Up, worldYaw) * Vector3.Forward;
        float trueYaw = f.TrueYaw(at, worldYaw);
        Assert.Equal(YawOf(f.EnuOf(at, forward)), trueYaw, 4);
        Assert.Equal(worldYaw, f.WorldYaw(at, trueYaw), 5);
        // true north's world yaw is the world yaw of the ENU's −Z
        Assert.Equal(YawOf(f.WorldOfEnu(at, new Vector3(0, 0, -1))), f.WorldYaw(at, 0f), 4);
        // a record's heading (offset axes) as a world heading here and back
        var g = f.ToGlobal(at);
        float world = f.WorldYawOf(g, 0.3f);
        Assert.Equal(0.3f, f.OffsetYawOf(at, world), 4);
        Assert.Equal(g.TrueYaw(0.3f), f.TrueYaw(at, world), 4);
        Assert.True(f.OffsetOfWorld(at, f.WorldOfOffset(g, v)).DistanceTo(v) < 1e-4f);
        // a body's rotation: in the ENU and back, and its forward follows the heading
        var q = new Quaternion(Vector3.Up, worldYaw) * new Quaternion(Vector3.Right, 0.2f);
        var qe = f.EnuTurn(at, q);
        Assert.True(f.WorldTurn(at, qe).IsEqualApprox(q) || f.WorldTurn(at, qe).IsEqualApprox(-q));
        Assert.True((qe * Vector3.Forward).DistanceTo(f.EnuOf(at, q * Vector3.Forward).Normalized()) < 2e-4f, "(the grid scale tilts a scaled direction by 1e-4)");
    }

    [Fact]
    public void True_yaws_agree_between_a_ground_and_a_turned_peer_at_Geneva()
    {
        // the same body seen by a flat peer and a turned one: each has its own world yaw, the wire one yaw
        var flat = Ground(GenevaE, GenevaN);
        var turned = Turned(GenevaE, GenevaN);
        var g = GlobalPos.FromLv95(GenevaE + 50, GenevaN + 20, 400);
        float sent = flat.TrueYaw(flat.ToWorld(g), 1.1f);
        float there = turned.WorldYaw(turned.ToWorld(g), sent);
        // the turned peer's axes are turned +2°, so a fixed heading reads 2° less there
        Assert.Equal(1.1f - Mathf.DegToRad(2f), there, 3);
        Assert.Equal(sent, turned.TrueYaw(turned.ToWorld(g), there), 5);
        // a velocity likewise
        var v = new Vector3(30, 0, -10);
        var wire = flat.EnuOf(flat.ToWorld(g), v);
        var w2 = turned.WorldOfEnu(turned.ToWorld(g), wire);
        Assert.True(w2.DistanceTo(new Basis(Vector3.Up, -Mathf.DegToRad(2f)) * v) < 0.02f, $"{w2}");
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void A_point_list_in_the_ENU_at_an_ECEF_origin_lands_where_the_sender_had_it(double e, double n)
    {
        var sender = Ground(e, n);
        var points = new[] { new Vector3(10, 500, -20), new Vector3(1800, 650, 900), new Vector3(-2500, 420, -1700) };
        var origin = sender.ToEcef(points[0]);
        var anchor = OriginFrame.AnchorAt(origin);
        Assert.True(anchor.IsAnchor && anchor.Epoch == -1);
        // its axes are the ENU at the origin
        Assert.True(Math.Abs(FrameAxes.Dot(anchor.Axes!.Y, new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(origin)).Up) - 1) < 1e-12);
        var toWire = sender.ToAnchor(origin);
        foreach (var receiver in new[] { Ground(e + 4_000, n + 7_000), new OriginFrame(e - 3_000, n, 0), Turned(e, n) })
        {
            var fromWire = receiver.SinceAnchor(origin);
            foreach (var p in points)
            {
                var got = fromWire.Point(toWire.Point(p));
                var want = receiver.ToWorld(sender.ToGlobal(p));
                // two flat frames: their translation, exactly; a turned anchor: the flat plane's curvature (d²/2R, 3 km: 0.7 m)
                float tolerance = receiver.IsAnchor ? 1.0f : 0.01f;
                Assert.True(got.DistanceTo(want) < tolerance, $"{p}: {got} vs {want} ({got.DistanceTo(want):F3} m)");
            }
            // near the origin the wire's floats are true ENU
            var near = toWire.Point(points[0]);
            Assert.True(near.Length() < 0.01f, $"{near}");
        }
    }

    [Fact]
    public void Conversions_cost_microseconds()
    {
        var f = Ground(GenevaE, GenevaN);
        var at = f.ToWorld(GenevaE + 10, GenevaN, 400);
        var g = f.ToGlobal(at);
        float sink = 0;
        for (int i = 0; i < 2000; i++) sink += f.TrueYaw(at, 0.1f) + (float)GlobalPos.FromWire(1, 2, 3).Alt * 0;
        const int n = 20_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) sink += f.EnuAt(at).Turn.X.X;
        double basis = sw.Elapsed.TotalMilliseconds * 1000 / n;
        sw.Restart();
        for (int i = 0; i < n; i++) { var (x, y, z) = g.ToWire(); sink += (float)GlobalPos.FromWire(x, y, z).Alt; }
        double wire = sw.Elapsed.TotalMilliseconds * 1000 / n;
        _out.WriteLine($"EnuAt {basis:F2} µs, ToWire+FromWire {wire:F2} µs ({sink:F0})");
        Assert.True(basis < 50 && wire < 50, "per packet, never per vertex");
    }
}
