using UnitSport.Interiors;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The underground car park's bay cars as real vehicles (#558, PR 3): naming, choice and yaw.</summary>
public class HallCarTests
{
    [Fact]
    public void An_owner_names_its_building_and_never_meets_a_forklifts_or_a_yards()
    {
        Assert.Equal("2583_1113_7_c", HallCarRule.OwnerOf("2583_1113_7"));
        Assert.Equal("2583_1113_7", HallCarRule.BuildingOf("2583_1113_7_c"));
        // a hall forklift's owner (_h), a yard's (E_N_Index) and a tile's car park (E_N) are not a bay car's
        Assert.Null(HallCarRule.BuildingOf("2583_1113_7_h"));
        Assert.Null(HallCarRule.BuildingOf("2583_1113_7"));
        Assert.Null(HallCarRule.BuildingOf("2583_1113"));
        Assert.Null(HallCarRule.BuildingOf("_c"));
    }

    [Fact]
    public void The_car_in_a_bay_is_a_function_of_the_building_and_the_piece()
    {
        for (int i = 0; i < 50; i++)
            Assert.Equal(HallCarRule.Pick("2583_1113_7", i, 23), HallCarRule.Pick("2583_1113_7", i, 23));
        Assert.Equal(-1, HallCarRule.Pick("2583_1113_7", 0, 0));
        // it is always in range, and every kind turns up across a car park's worth of bays and buildings
        var seen = new HashSet<int>();
        for (int b = 0; b < 40; b++)
            for (int i = 0; i < 30; i++)
            {
                int k = HallCarRule.Pick($"2583_1113_{b}", i, 23);
                Assert.InRange(k, 0, 22);
                seen.Add(k);
            }
        Assert.Equal(23, seen.Count);
        // neighbouring bays are not all the same car
        Assert.True(Enumerable.Range(0, 12).Select(i => HallCarRule.Pick("2583_1113_7", i, 23)).Distinct().Count() > 4);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 0.7f)]
    [InlineData(3, -2.1f)]
    public void A_vehicles_nose_lies_where_the_pieces_front_points(int turns, float interiorYaw)
    {
        float yaw = HallCarRule.NoseYaw(turns, interiorYaw);
        Assert.InRange(yaw, 0f, MathF.PI * 2);
        // the piece's front (+Z), turned about +Y by the quarter turns and then the interior's yaw
        float a = turns * MathF.PI / 2 + interiorYaw;
        float frontX = MathF.Sin(a), frontZ = MathF.Cos(a);
        // a vehicle's nose by its yaw: -Z turned, (-sin, -cos)
        Assert.Equal(frontX, -MathF.Sin(yaw), 4);
        Assert.Equal(frontZ, -MathF.Cos(yaw), 4);
    }
}
