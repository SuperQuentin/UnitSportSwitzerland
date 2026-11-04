using System;
using Godot;
using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// True north against the game's axes (#1504, G2 A3): the meridian convergence on the LV95 grid, true north in a
/// frame's world axes, and the turns data stored in grid north goes through at load.
/// </summary>
public class TrueNorthTests
{
    private const double Deg = Math.PI / 180;

    [Fact]
    public void The_convergence_is_nothing_on_berns_meridian()
    {
        // 0.0007° (2.5″) is the datum's: CH1903+'s Bessel normals against WGS84's, not the projection
        Assert.Equal(0, TrueNorth.GridConvergence(2_600_000, 1_200_000) / Deg, 2);
        // along Bern's meridian it stays nothing, north and south
        Assert.Equal(0, TrueNorth.GridConvergence(2_600_000, 1_080_000) / Deg, 2);
        Assert.Equal(0, TrueNorth.GridConvergence(2_600_000, 1_290_000) / Deg, 2);
    }

    [Fact]
    public void At_geneva_true_north_lies_about_nine_tenths_of_a_degree_east_of_grid_north()
    {
        // west of Bern the meridians lean toward the pole over Bern's: true north bears east on the grid,
        // Δλ·sin φ = 1.29° · sin 46.2° ≈ 0.93°
        double g = TrueNorth.GridConvergence(2_500_000, 1_118_000) / Deg;
        Assert.InRange(g, 0.85, 1.0);
        // east of Bern the other way: Scuol, Δλ = 2.86°
        Assert.InRange(TrueNorth.GridConvergence(2_817_000, 1_186_000) / Deg, -2.2, -1.9);
    }

    [Fact]
    public void A_grid_bearing_turns_to_true_by_the_convergence()
    {
        double g = TrueNorth.GridConvergence(2_500_000, 1_118_000);
        // a bearing of grid north at Geneva is true north less the convergence: a little west of north
        Assert.Equal(-g, TrueNorth.GridToTrue(2_500_000, 1_118_000, 0), 12);
        // a yaw turns the other way (a yaw is minus a bearing)
        Assert.Equal((float)g, TrueNorth.GridYawToTrue(2_500_000, 1_118_000, 0f), 6);
        Assert.Equal(-TrueNorth.GridToTrue(2_500_000, 1_118_000, 0.7), TrueNorth.GridYawToTrue(2_500_000, 1_118_000, -0.7f), 6);
    }

    [Fact]
    public void A_true_yaw_kept_in_a_record_is_the_grid_one_while_the_offset_axes_are_the_grids()
    {
        var geneva = GlobalPos.FromLv95(2_500_000, 1_118_000, 372);
        float grid = 1.234f;
        float trueYaw = TrueNorth.GridYawToTrue(2_500_000, 1_118_000, grid);
        Assert.NotEqual(grid, trueYaw);
        Assert.Equal(grid, TrueNorth.OffsetYaw(geneva, trueYaw), 5);
        Assert.Equal(trueYaw, TrueNorth.TrueYawOfOffset(geneva, TrueNorth.OffsetYaw(geneva, trueYaw)), 5);
    }

    [Fact]
    public void In_the_flat_world_true_north_is_minus_z_turned_by_the_convergence_at_the_point()
    {
        // the flat ground frame's axes are the grid's everywhere: north there is the convergence, not the origin's
        var frame = new OriginFrame(2_600_000, 1_200_000, 1);
        var geneva = frame.ToWorld(2_500_000, 1_118_000, 372);
        double g = TrueNorth.GridConvergence(2_500_000, 1_118_000, 372);
        Assert.Equal(g / Deg, TrueNorth.NorthBearing(frame, geneva) / Deg, 3);
        Assert.Equal(0, TrueNorth.NorthBearing(frame, frame.ToWorld(2_600_000, 1_200_000, 540)) / Deg, 2);
        var n = TrueNorth.WorldNorth(frame, geneva);
        Assert.Equal(1f, n.Length(), 5);
        Assert.Equal(0f, n.Y, 6);
        // world −Z there reads as a little west of true north
        Assert.Equal(-g / Deg, TrueNorth.Bearing(frame, geneva, Vector3.Forward) / Deg, 3);
    }

    [Fact]
    public void A_true_heading_placed_in_the_flat_world_comes_back_as_the_grid_heading()
    {
        var frame = new OriginFrame(2_500_000, 1_118_000, 1);
        var at = frame.ToWorld(2_499_500, 1_117_800, 380);
        float grid = -2.5f;
        float trueYaw = TrueNorth.GridYawToTrue(2_499_500, 1_117_800, grid);
        Assert.Equal(grid, TrueNorth.WorldYaw(frame, at, trueYaw), 4);
        Assert.Equal(trueYaw, TrueNorth.TrueYaw(frame, at, TrueNorth.WorldYaw(frame, at, trueYaw)), 4);
    }

    [Fact]
    public void In_an_anchor_frame_true_north_is_the_anchors_turn()
    {
        // an anchor frame at Geneva with true east-up-south axes: its −Z is true north
        var p = Terrain.Format.SwissGeodesy.ToEcef(2_500_000, 1_118_000, 400);
        var enu = new Terrain.Format.EnuFrame(Terrain.Format.Ellipsoid.Wgs84.ToGeodetic(p));
        var n = enu.North;
        var frame = new OriginFrame(new FrameAxes(p, enu.East, enu.Up, new Terrain.Format.EcefPos(-n.X, -n.Y, -n.Z)), 1);
        Assert.Equal(0, TrueNorth.NorthBearing(frame, Vector3.Zero) / Deg, 4);
        // a world direction is its own bearing there, and a GlobalPos offset turns by the grid's convergence
        var near = new TrueNorth.Near();
        Assert.Equal(0, near.World(frame, Vector3.Zero) / Deg, 4);
        Assert.Equal(TrueNorth.GridConvergence(2_500_000, 1_118_000) / Deg, near.Offset(frame, Vector3.Zero) / Deg, 2);
        var offset = near.WorldToOffset(frame, Vector3.Zero, new Vector2(0, 1));
        Assert.Equal(TrueNorth.GridConvergence(2_500_000, 1_118_000) / Deg, Mathf.Atan2(offset.X, offset.Y) / Deg, 2);
    }

    [Fact]
    public void Turning_east_north_by_a_quarter_takes_north_to_east()
    {
        var v = TrueNorth.Turn(new Vector2(0, 1), 0f, Mathf.Pi / 2);
        Assert.Equal(1f, v.X, 5);
        Assert.Equal(0f, v.Y, 5);
        Assert.Equal(new Vector2(0.3f, 0.7f), TrueNorth.Turn(new Vector2(0.3f, 0.7f), 0.2f, 0.2f));
    }
}
