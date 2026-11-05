using System;
using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// <c>GlobalPos</c>'s Earth-position API (#1443 G2, A1 #1502): what code reads instead of <c>E</c> / <c>N</c>.
/// While LV95 is still inside, each member is pinned to the LV95 arithmetic it replaces, bit for bit,
/// so moving a site onto it changes nothing.
/// </summary>
public class GlobalPosTests
{
    private static readonly GlobalPos Bern = GlobalPos.FromLv95(2_600_000.25, 1_200_000.75, 540.5);
    private static readonly GlobalPos Geneva = GlobalPos.FromLv95(2_500_123.5, 1_117_456.125, 372.25);

    [Fact]
    public void Lv95_notation_goes_in_and_out_unchanged()
    {
        var p = GlobalPos.FromLv95(2_581_234.5, 1_105_678.3125, 1_234.75);
        Assert.Equal(new Lv95Coord(2_581_234.5, 1_105_678.3125, 1_234.75), p.Lv95);
        Assert.Equal(new GlobalPos(2_581_234.5, 1_105_678.3125, 1_234.75), p);
        Assert.Equal("2581234.5/1105678.3", p.Lv95.ToString());
    }

    [Fact]
    public void The_offset_to_another_place_is_the_grid_difference()
    {
        var (east, north, up) = Bern.OffsetTo(Geneva);
        Assert.Equal(Geneva.E - Bern.E, east);
        Assert.Equal(Geneva.N - Bern.N, north);
        Assert.Equal(Geneva.Alt - Bern.Alt, up);
    }

    [Fact]
    public void Moving_a_place_adds_metres_east_north_and_up()
    {
        Assert.Equal(new GlobalPos(Bern.E + 12.5, Bern.N - 3.25, Bern.Alt + 2), Bern.Moved(12.5, -3.25, 2));
        Assert.Equal(new GlobalPos(Bern.E + 1, Bern.N + 2, Bern.Alt), Bern.Moved(1, 2));
        var back = Bern.Moved(Bern.OffsetTo(Geneva).East, Bern.OffsetTo(Geneva).North, Bern.OffsetTo(Geneva).Up);
        Assert.Equal(0, Geneva.HorizontalDistanceTo(back), 6);
    }

    [Fact]
    public void Another_altitude_keeps_the_place()
    {
        Assert.Equal(new GlobalPos(Bern.E, Bern.N, 1_000), Bern.WithAlt(1_000));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(100, 0, 90)]
    [InlineData(0, -100, 180)]
    [InlineData(-100, 0, -90)]
    public void The_bearing_is_clockwise_from_true_north(double east, double north, double degrees)
    {
        // Bern's meridian: grid and true north agree (#1504)
        Assert.Equal(degrees, Bern.BearingTo(Bern.Moved(east, north)) * 180 / Math.PI, 2);
        // elsewhere the grid's bearing less the meridian convergence there
        Assert.Equal(Math.Atan2(Bern.E - Geneva.E, Bern.N - Geneva.N) - Geneva.Convergence, Geneva.BearingTo(Bern));
    }

    [Fact]
    public void The_tile_under_a_place_is_its_kilometre()
    {
        Assert.Equal(Terrain.Format.TileId.FromLv95(Bern.E, Bern.N), Bern.Tile);
        Assert.Equal(Terrain.Format.TileId.FromLv95(Geneva.E, Geneva.N), Geneva.Tile);
    }
}
