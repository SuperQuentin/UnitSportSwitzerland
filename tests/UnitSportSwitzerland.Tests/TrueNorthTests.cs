using System;
using Godot;
using UnitSport.Adventure.Errands;
using UnitSport.Adventure.Rewards;
using UnitSport.Core;
using UnitSport.Occasions.Festivals.August1;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// True north against the game's axes (#1504, G2 A3): the meridian convergence on the LV95 grid, true north in a
/// frame's world axes, and each kind of data stored from grid north read into true north at load.
/// </summary>
public class TrueNorthTests
{
    private const double Deg = Math.PI / 180;
    private const double GenevaE = 2_500_000, GenevaN = 1_118_000;

    [Fact]
    public void The_convergence_is_nothing_on_berns_meridian()
    {
        // 0.0007° (2.5″) is the datum's: CH1903+'s Bessel normals against WGS84's, not the projection
        Assert.Equal(0, SwissGeodesy.GridConvergenceDeg(2_600_000, 1_200_000), 2);
        // along Bern's meridian it stays nothing, north and south
        Assert.Equal(0, SwissGeodesy.GridConvergenceDeg(2_600_000, 1_080_000), 2);
        Assert.Equal(0, SwissGeodesy.GridConvergenceDeg(2_600_000, 1_290_000), 2);
    }

    [Fact]
    public void At_geneva_true_north_lies_about_nine_tenths_of_a_degree_east_of_grid_north()
    {
        // west of Bern the meridians lean toward the pole over Bern's: true north bears east on the grid,
        // Δλ·sin φ = 1.29° · sin 46.2° ≈ 0.93°
        Assert.InRange(SwissGeodesy.GridConvergenceDeg(GenevaE, GenevaN), 0.85, 1.0);
        // east of Bern the other way: Scuol, Δλ = 2.86°
        Assert.InRange(SwissGeodesy.GridConvergenceDeg(2_817_000, 1_186_000), -2.2, -1.9);
        // the same as the flat world's own grid axes say (FrameAxes.GroundAt: what its −Z is on the Earth)
        var axes = FrameAxes.GroundAt(GenevaE, GenevaN, 500);
        var north = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(axes.Origin)).North;
        Assert.Equal(Math.Atan2(FrameAxes.Dot(north, axes.X), -FrameAxes.Dot(north, axes.Z)), SwissGeodesy.GridConvergence(GenevaE, GenevaN), 9);
    }

    [Fact]
    public void A_grid_bearing_turns_to_true_by_the_convergence_and_back()
    {
        double g = SwissGeodesy.GridConvergence(GenevaE, GenevaN);
        // grid north at Geneva is a little west of true north
        Assert.Equal(-g, TrueNorth.GridToTrue(GenevaE, GenevaN, 0), 12);
        Assert.Equal(0.7, TrueNorth.TrueToGrid(GenevaE, GenevaN, TrueNorth.GridToTrue(GenevaE, GenevaN, 0.7)), 12);
        // a yaw turns the other way (a yaw is minus a bearing)
        Assert.Equal((float)g, TrueNorth.GridYawToTrue(GenevaE, GenevaN, 0f), 6);
        Assert.Equal(-TrueNorth.GridToTrue(GenevaE, GenevaN, 0.7), TrueNorth.GridYawToTrue(GenevaE, GenevaN, -0.7f), 6);
    }

    [Fact]
    public void A_true_yaw_kept_in_a_record_is_the_grid_one_while_the_offset_axes_are_the_grids()
    {
        var geneva = GlobalPos.FromLv95(GenevaE, GenevaN, 372);
        float grid = 1.234f;
        float trueYaw = TrueNorth.GridYawToTrue(GenevaE, GenevaN, grid);
        Assert.NotEqual(grid, trueYaw);
        Assert.Equal(grid, TrueNorth.OffsetYaw(geneva, trueYaw), 4);
        Assert.Equal(trueYaw, TrueNorth.TrueYawOfOffset(geneva, TrueNorth.OffsetYaw(geneva, trueYaw)), 5);
    }

    [Fact]
    public void The_wire_s_offset_yaw_from_G2_A2_agrees_with_true_north_s()
    {
        // #1503's GlobalPos.OffsetYaw/TrueYaw (the wire's turn) and #1504's must be one turn
        var geneva = GlobalPos.FromLv95(GenevaE, GenevaN, 372);
        Assert.Equal(TrueNorth.OffsetYaw(geneva, 1.234f), geneva.OffsetYaw(1.234f), 4);
        Assert.Equal(TrueNorth.TrueYawOfOffset(geneva, 1.234f), geneva.TrueYaw(1.234f), 4);
    }

    [Fact]
    public void In_the_flat_world_true_north_is_minus_z_turned_by_the_convergence_at_the_point()
    {
        // the flat ground frame's axes are the grid's everywhere: north there is the convergence at the point, not the origin's
        var frame = new OriginFrame(2_600_000, 1_200_000, 1);
        var geneva = frame.ToWorld(GenevaE, GenevaN, 372);
        double g = SwissGeodesy.GridConvergence(GenevaE, GenevaN, 372);
        Assert.Equal(g / Deg, TrueNorth.NorthBearing(frame, geneva) / Deg, 3);
        Assert.Equal(0, TrueNorth.NorthBearing(frame, frame.ToWorld(2_600_000, 1_200_000, 540)) / Deg, 2);
        var n = TrueNorth.WorldNorth(frame, geneva);
        Assert.Equal(1f, n.Length(), 5);
        Assert.Equal(0f, n.Y, 6);
        // world −Z there reads as a little west of true north
        Assert.Equal(-g / Deg, TrueNorth.Bearing(frame, geneva, Vector3.Forward) / Deg, 3);
    }

    [Fact]
    public void A_heading_read_into_true_north_is_set_down_in_the_flat_world_as_the_grid_one()
    {
        var frame = new OriginFrame(GenevaE, GenevaN, 1);
        var at = frame.ToWorld(2_499_500, 1_117_800, 380);
        float grid = -2.5f;
        float trueYaw = TrueNorth.GridYawToTrue(2_499_500, 1_117_800, grid);
        Assert.Equal(grid, TrueNorth.WorldYaw(frame, at, trueYaw), 4);
        Assert.Equal(trueYaw, TrueNorth.TrueYaw(frame, at, TrueNorth.WorldYaw(frame, at, trueYaw)), 4);
        // and a world yaw written back to a store still in grid comes back as it was
        Assert.Equal(grid, TrueNorth.WorldYawToGrid(frame, at, TrueNorth.WorldYaw(frame, at, trueYaw)), 4);
    }

    [Fact]
    public void In_an_anchor_frame_true_north_is_the_anchors_turn()
    {
        // an anchor frame at Geneva with true east-up-south axes: its −Z is true north
        var p = SwissGeodesy.ToEcef(GenevaE, GenevaN, 400);
        var enu = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(p));
        var n = enu.North;
        var frame = new OriginFrame(new FrameAxes(p, enu.East, enu.Up, new EcefPos(-n.X, -n.Y, -n.Z)), 1);
        Assert.Equal(0, TrueNorth.NorthBearing(frame, Vector3.Zero) / Deg, 4);
        // a heading from true north is its own world yaw there
        Assert.Equal(0.6f, TrueNorth.WorldYaw(frame, Vector3.Zero, 0.6f), 4);
        // a world direction is its own bearing there and in true east and north (#1502); on the grid it turns by the convergence
        var near = new TrueNorth.Near();
        Assert.Equal(0, near.World(frame, Vector3.Zero) / Deg, 4);
        double g = SwissGeodesy.GridConvergenceDeg(GenevaE, GenevaN);
        Assert.Equal(g, near.Grid(frame, Vector3.Zero) / Deg, 2);
        Assert.Equal(new Vector2(0, 1), near.WorldToEnu(frame, Vector3.Zero, new Vector2(0, 1)));
        var offset = near.WorldToGrid(frame, Vector3.Zero, new Vector2(0, 1));
        Assert.Equal(g, Mathf.Atan2(offset.X, offset.Y) / Deg, 2);
    }

    [Fact]
    public void Turning_east_north_by_a_quarter_takes_north_to_east()
    {
        var v = TrueNorth.Turn(new Vector2(0, 1), 0f, Mathf.Pi / 2);
        Assert.Equal(1f, v.X, 5);
        Assert.Equal(0f, v.Y, 5);
        Assert.Equal(new Vector2(0.3f, 0.7f), TrueNorth.Turn(new Vector2(0.3f, 0.7f), 0.2f, 0.2f));
    }

    // ---- each kind of data, read into true north at load ----------------------------------------------

    [Fact]
    public void A_steamer_berth_and_an_airport_stand_read_their_file_heading_into_true_north()
    {
        double g = SwissGeodesy.GridConvergenceDeg(GenevaE, GenevaN);
        var berth = new LandingBerth { E = GenevaE, N = GenevaN, Heading = 40 };
        Assert.Equal(40 - g, berth.TrueHeading, 9);
        var stand = new AirportStand { E = GenevaE, N = GenevaN, Heading = 220 };
        Assert.Equal(220 - g, stand.TrueHeading, 9);
        // the file keeps its own field: written out and read back, the berth is as it was read
        var index = new LandingIndex { Landings = { new Landing { Name = "Test", E = GenevaE, N = GenevaN, Berth = berth } } };
        string json = index.ToJson();
        Assert.DoesNotContain("TrueHeading", json);
        Assert.Equal(40, LandingIndex.FromJson(json)!.Landings[0].Berth!.Heading);
    }

    [Fact]
    public void A_marina_berth_is_from_true_north_along_its_jettys_grid_line()
    {
        // a jetty along grid north at Geneva: its berths face 0 or 180 on the grid, less the convergence
        var jetty = new Jetty { Id = "{T}", Ribbon = new PierRibbon { Kind = PierKind.Jetty, Width = 2.2,
            Points = { new double[] { GenevaE, GenevaN, 372 }, new double[] { GenevaE, GenevaN + 60, 372 } } } };
        double g = SwissGeodesy.GridConvergenceDeg(GenevaE, GenevaN);
        Assert.NotEmpty(jetty.BoatSlots());
        Assert.All(jetty.BoatSlots(), b => Assert.True(Math.Abs(Math.IEEERemainder(b.Heading + g, 180)) < 0.02, $"{b.Heading}"));
    }

    [Fact]
    public void A_giver_faces_from_true_north()
    {
        var g = new Giver("t", GiverKind.Farmer, GenevaE, GenevaN, 1.0f, "GE", 400, "Test", "test");
        Assert.Equal(1.0 - SwissGeodesy.GridConvergence(GenevaE, GenevaN), g.TrueHeading, 5);
        Assert.Equal(1.0f, g.Heading);
    }

    [Fact]
    public void A_stadiums_azimuth_is_from_true_north_and_turns_back_for_its_grid_maths()
    {
        var s = StadiumCatalog.MontreuxJazzOpenAir;
        double g = SwissGeodesy.GridConvergenceDeg(s.E, s.N);
        Assert.Equal(s.GridAzimuthDeg - g, s.AzimuthDeg, 4);
        Assert.Equal(s.GridAzimuthDeg, s.LocalAzimuthDeg, 4);
        // in GlobalPos's offset axes (the grid's until A1 ends) it is the grid one too
        Assert.Equal(s.GridAzimuthDeg, s.OffsetAzimuthDeg(GlobalPos.FromLv95(s.E, s.N, 400)), 3);
    }

    [Fact]
    public void A_lakes_bearing_is_from_true_north_and_its_site_stays_where_it_was()
    {
        var geneva = August1Rules.Lakes[0];
        var (qe, qn) = geneva.Quay;
        Assert.Equal(geneva.BearingDeg - SwissGeodesy.GridConvergenceDeg(qe, qn), geneva.Bearing / Deg, 6);
        // the site is an LV95 place along the map's grid bearing, as before
        var (se, sn) = geneva.Site;
        Assert.Equal(qe - Math.Sin(geneva.BearingDeg * Deg) * August1Rules.Inland, se, 6);
        Assert.Equal(qn - Math.Cos(geneva.BearingDeg * Deg) * August1Rules.Inland, sn, 6);
    }

    [Fact]
    public void At_geneva_a_photo_is_judged_from_true_north()
    {
        // a spot at Geneva, its target due grid north 5 km off: true north is ~0.93° east of grid north there, so the
        // target lies at a true bearing of about −0.93°
        var spot = new PhotoSpot("ph:t", "T", GenevaE, GenevaN, 150, GenevaE, GenevaN + 5000, 2000, "A peak");
        float eye = 402;
        float elev = (float)(Math.Atan2(2000 - Curvature.Drop(5000) - eye, 5000) / Deg);
        float g = (float)SwissGeodesy.GridConvergenceDeg(GenevaE, GenevaN, eye);
        // a 2° frame: half a degree or so either way
        Assert.Equal(PhotoVerdict.Taken, PhotoJudge.Judge(spot, GenevaE, GenevaN, eye, -g, elev, 2, (_, _) => null));
        // aimed the grid's way round (+0.93°), it is 1.86° off: out of that frame
        Assert.Equal(PhotoVerdict.OffTarget, PhotoJudge.Judge(spot, GenevaE, GenevaN, eye, g, elev, 2, (_, _) => null));
    }
}
