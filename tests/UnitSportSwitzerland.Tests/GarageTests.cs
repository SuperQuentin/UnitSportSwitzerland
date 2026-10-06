using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The underground garage door of a block of flats (#558): when a block gets one (a predicate the
/// footprint and the interior generator share) and what joins it to the road (chosen from the
/// road's distance and class). Pure, so pinned here rather than looked for in a town.
/// </summary>
public class GarageTests
{
    private const float H = 3.0f;

    [Fact]
    public void A_deep_wide_block_has_a_car_park_strip_and_a_narrow_or_shallow_one_does_not()
    {
        // 80 x 20 m, five storeys and a basement: the stairwell takes about 8 m, 12 m is left
        Assert.True(GarageRule.BehindStairwell(80, 20, 6, H) >= GarageRule.StripDepth);
        // 16 m deep: the stairwell leaves 8 m, less than two rows and an aisle
        Assert.True(GarageRule.BehindStairwell(80, 16, 6, H) < GarageRule.StripDepth);
        // narrower than the stairwell, its lift and a flat: no passage, no back landing, no strip
        Assert.Equal(0f, GarageRule.BehindStairwell(8, 30, 6, H));
    }

    [Fact]
    public void The_stairwell_depth_matches_what_the_generator_lays_out()
    {
        // front landing 2.0 + 9 steps of 0.28 m + half landing 1.3 + back landing 2.0 = 7.82 m
        Assert.Equal(20f - 7.82f, GarageRule.BehindStairwell(80, 20, 6, H), 2);
    }

    [Fact]
    public void A_block_with_no_basement_has_no_car_park()
    {
        string none = Enumerable.Range(0, 400).Select(i => $"2583_1113_{i}")
            .First(k => GarageRule.Basement(k, false, 3, 280f) == 0);
        Assert.False(GarageRule.HasCarPark(none, false, 3, 14, 20, H));
    }

    [Fact]
    public void Every_block_over_400_m2_has_a_basement()
    {
        for (int i = 0; i < 200; i++)
            Assert.Equal(1, GarageRule.Basement($"2583_1113_{i}", false, 3, 1280f));
    }

    [Fact]
    public void A_garage_is_wanted_only_with_flats_three_front_doors_a_car_park_and_the_roll()
    {
        string rolled = Enumerable.Range(0, 400).Select(i => $"2583_1113_{i}").First(k => GarageRule.Rolls(k));
        string unlucky = Enumerable.Range(0, 400).Select(i => $"2583_1113_{i}").First(k => !GarageRule.Rolls(k));
        Assert.True(GarageRule.Wanted(rolled, BuildingType.Apartments, 5, 80, 26, H, 3));
        Assert.False(GarageRule.Wanted(unlucky, BuildingType.Apartments, 5, 80, 26, H, 3));
        // two front doors is a small block, not a development with an underground garage
        Assert.False(GarageRule.Wanted(rolled, BuildingType.Apartments, 5, 80, 26, H, 2));
        // shops under flats and any other kind of building are not blocks of flats
        // shops under flats qualify too, rarer: 4 front doors and a 20 % roll
        string mixedRolled = Enumerable.Range(0, 400).Select(i => $"2583_1113_{i}").First(k => GarageRule.Rolls(k, mixed: true));
        string mixedUnlucky = Enumerable.Range(0, 400).Select(i => $"2583_1113_{i}").First(k => GarageRule.Rolls(k) && !GarageRule.Rolls(k, mixed: true));
        Assert.True(GarageRule.Wanted(mixedRolled, BuildingType.MixedUse, 5, 80, 26, H, 4));
        Assert.False(GarageRule.Wanted(mixedRolled, BuildingType.MixedUse, 5, 80, 26, H, 3));
        Assert.False(GarageRule.Wanted(mixedUnlucky, BuildingType.MixedUse, 5, 80, 26, H, 4));
        Assert.False(GarageRule.Wanted(rolled, BuildingType.None, 5, 80, 26, H, 3));
        // too shallow for a car park
        Assert.False(GarageRule.Wanted(rolled, BuildingType.Apartments, 5, 80, 14, H, 3));
        // a car park strip but not the 24 m a ramp down to it takes (PR 2)
        Assert.True(GarageRule.HasCarPark(rolled, false, 5, 80, 20, H));
        Assert.False(GarageRule.Wanted(rolled, BuildingType.Apartments, 5, 80, 20, H, 3));
    }

    [Fact]
    public void The_roll_keeps_garages_rare()
    {
        int rolled = Enumerable.Range(0, 2000).Count(i => GarageRule.Rolls($"2583_1113_{i}"));
        Assert.InRange(rolled / 2000.0, 0.34, 0.46);
    }

    [Fact]
    public void The_mixed_roll_is_a_fifth_and_inside_the_flats_roll()
    {
        int n = Enumerable.Range(0, 2000).Count(i => GarageRule.Rolls($"2583_1113_{i}", mixed: true));
        Assert.InRange(n / 2000.0, 0.15, 0.25);
        for (int i = 0; i < 500; i++)
            if (GarageRule.Rolls($"2583_1113_{i}", mixed: true)) Assert.True(GarageRule.Rolls($"2583_1113_{i}"));
    }

    [Fact]
    public void The_roll_is_a_pure_function_of_the_key()
    {
        Assert.Equal(GarageRule.Rolls("2583_1113_7"), GarageRule.Rolls("2583_1113_7"));
    }

    // ---- the road link ---------------------------------------------------------------------------

    /// <summary>An east-west road <paramref name="gap"/> metres south of a door at the origin facing south (+Z).</summary>
    private static GarageLink.Road RoadSouth(RoadClass cls, float gap, float width = 0) =>
        new(cls, width, [-100f, 5f, gap, 100f, 5f, gap]);

    private static readonly Vector2 Door = Vector2.Zero;
    private static readonly Vector2 South = new(0, 1);

    [Fact]
    public void A_road_close_by_takes_a_pavement_with_bollards()
    {
        var link = GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, 5f)]);
        Assert.Equal(LinkKind.Sidewalk, link.Kind);
        Assert.Equal(3f, link.Length, 2);   // 5 m to the centreline less half the 4 m road
        Assert.Equal(5f, link.RoadY, 2);
    }

    [Fact]
    public void A_road_further_off_takes_an_access_road_forming_a_T()
    {
        var link = GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Road, 18f)]);
        Assert.Equal(LinkKind.Stub, link.Kind);
        Assert.Equal(15f, link.Length, 2);
    }

    [Fact]
    public void The_pavement_reaches_eight_metres_from_the_roads_edge()
    {
        // a minor road is 4 m wide: its centreline is 2 m further than its edge
        Assert.Equal(LinkKind.Sidewalk, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, 7.9f + 2f)]).Kind);
        Assert.Equal(LinkKind.Stub, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, 8.1f + 2f)]).Kind);
    }

    [Fact]
    public void No_street_within_25_m_means_no_door()
    {
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, 25.1f + 2f)]).Kind);
        Assert.Equal(LinkKind.Stub, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, 24.9f + 2f)]).Kind);
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, []).Kind);
    }

    [Fact]
    public void A_big_road_gets_an_access_road_even_when_near_but_not_when_the_door_is_on_its_kerb()
    {
        // 10 m main road, 4.5 m from the door to its edge: the access road, a car does not cross its pavement
        Assert.Equal(LinkKind.Stub, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Major, 4.5f + 4.5f)]).Kind);
        // 1 m from the edge there is no room for one: the pavement
        Assert.Equal(LinkKind.Sidewalk, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Major, 1f + 4.5f)]).Kind);
    }

    [Theory]
    [InlineData(RoadClass.Motorway)]
    [InlineData(RoadClass.Expressway)]
    [InlineData(RoadClass.Ramp)]
    public void A_motorway_class_road_that_near_means_no_door_even_with_a_street_beyond(RoadClass cls)
    {
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(cls, 12f)]).Kind);
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(cls, 12f), RoadSouth(RoadClass.Minor, 6f)]).Kind);
        // a motorway far off does not stop a street near
        Assert.Equal(LinkKind.Sidewalk, GarageLink.Choose(Door, South, [RoadSouth(cls, 80f), RoadSouth(RoadClass.Minor, 6f)]).Kind);
    }

    [Fact]
    public void A_footpath_or_track_is_not_a_road_to_drive_to()
    {
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Path, 4f)]).Kind);
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Track, 4f)]).Kind);
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Railway, 4f)]).Kind);
    }

    [Fact]
    public void A_road_behind_the_door_or_alongside_it_is_no_link()
    {
        // the road is on the other side of the building
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Minor, -6f)]).Kind);
        // a road running north-south past a door facing south: a link would meet it edge on
        var along = new GarageLink.Road(RoadClass.Minor, 0, [6f, 0f, -50f, 6f, 0f, 50f]);
        Assert.Equal(LinkKind.None, GarageLink.Choose(Door, South, [along]).Kind);
    }

    [Fact]
    public void The_nearest_street_decides()
    {
        var link = GarageLink.Choose(Door, South, [RoadSouth(RoadClass.Road, 20f), RoadSouth(RoadClass.Minor, 5f)]);
        Assert.Equal(LinkKind.Sidewalk, link.Kind);
    }

    [Fact]
    public void A_skew_road_is_met_by_the_doors_own_line()
    {
        // the road runs at 45 degrees; the link goes straight out of the door and ends on its edge
        var road = new GarageLink.Road(RoadClass.Road, 6f, [-50f, 0f, -50f + 20f, 50f, 0f, 50f + 20f]);
        var link = GarageLink.Choose(Door, South, [road]);
        Assert.True(link.Any);
        // the centreline crosses x = 0 at z = 20; its edge, 3 m across the road, is 3 / cos(45) short of it
        Assert.Equal(20f - 3f / MathF.Cos(MathF.PI / 4), link.Length, 1);
    }

    [Fact]
    public void A_stub_over_a_rise_in_the_ground_is_humped_to_clear_it()
    {
        // 0.19 m of ground over the line at the middle: the sine hump is exactly that high there
        Assert.Equal(0.19f, GarageLink.HumpFor([(0.5f, 0.19f), (0.25f, 0.05f)]), 3);
        // flat or sunken ground needs none; a spike is capped
        Assert.Equal(0f, GarageLink.HumpFor([(0.5f, -0.2f), (0.3f, 0f)]));
        Assert.Equal(0.5f, GarageLink.HumpFor([(0.5f, 2f)]));
    }

    // ---- the ramp (#558, PR 2) -------------------------------------------------------------------

    [Fact]
    public void A_ramp_drops_one_storey_at_no_more_than_the_slope_with_a_vertical_curve_at_each_end()
    {
        float len = RampProfile.Length(H);
        Assert.Equal(H / RampProfile.Slope + RampProfile.Bevel, len, 3);
        Assert.Equal(0f, RampProfile.Drop(0, H), 4);
        Assert.Equal(H, RampProfile.Drop(len, H), 3);
        // monotone, never steeper than the slope, and flat at both ends
        float prev = 0;
        for (float t = 0.05f; t <= len; t += 0.05f)
        {
            float d = RampProfile.Drop(t, H);
            Assert.InRange(d - prev, 0f, RampProfile.Slope * 0.05f + 1e-4f);
            prev = d;
        }
        Assert.True(RampProfile.Drop(0.2f, H) < 0.01f);
        Assert.True(H - RampProfile.Drop(len - 0.2f, H) < 0.01f);
        // the height above the foot is the drop's complement
        Assert.Equal(H, RampProfile.Height(0, H), 4);
        Assert.Equal(0f, RampProfile.Height(len, H), 3);
    }

    [Fact]
    public void The_floor_slab_over_a_ramp_stops_where_a_car_fits_under_it()
    {
        const float clear = H - 0.2f;
        float hole = RampProfile.HoleLength(H, clear);
        Assert.InRange(hole, 0.5f * RampProfile.Length(H), RampProfile.Length(H));
        // at the end of the hole the surface leaves exactly the headroom
        Assert.Equal(clear - RampProfile.Headroom, RampProfile.Height(hole, H), 2);
        // a basement with no headroom to spare is open all the way down
        Assert.Equal(RampProfile.Length(H), RampProfile.HoleLength(H, RampProfile.Headroom - 0.5f), 2);
    }

    [Fact]
    public void A_block_needs_the_depth_for_the_ramp_its_turn_and_a_row_of_bays()
    {
        float need = GarageRule.RampDepth(H);
        Assert.Equal(GarageRule.RampApron + RampProfile.Length(H) + GarageRule.RampTurn + GarageRule.BayRow, need, 3);
        Assert.InRange(need, 22f, 26f);
        Assert.True(GarageRule.HasRamp(need, H));
        Assert.False(GarageRule.HasRamp(need - 0.1f, H));
        // a taller storey drops further and needs a longer run
        Assert.True(GarageRule.RampDepth(3.4f) > need);
        // the lane is a car's: 3 m door, 3.6 m between the walls
        Assert.True(GarageRule.RampWidth >= GarageRule.Width + 0.4f);
    }
}
