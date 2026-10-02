using UnitSport.Tools.RoadGen.Import;
using Xunit;

namespace UnitSport.Tests;

/// <summary>OSM <c>turn:lanes</c> parsing and the overlay reader's per-lane columns (#347).</summary>
public class TurnLanesTests
{
    [Fact]
    public void Empty_or_missing_value_has_no_lanes()
    {
        Assert.Empty(TurnLanes.Parse(null));
        Assert.Empty(TurnLanes.Parse(""));
        Assert.Empty(TurnLanes.Parse("  "));
    }

    [Fact]
    public void Lanes_are_split_on_bars_left_to_right()
    {
        Assert.Equal([TurnMove.Left, TurnMove.Through, TurnMove.Through | TurnMove.Right],
            TurnLanes.Parse("left|through|through;right"));
    }

    [Fact]
    public void Empty_lanes_and_none_mean_no_marking()
    {
        Assert.Equal([TurnMove.Left, TurnMove.None, TurnMove.None, TurnMove.None], TurnLanes.Parse("left||none|"));
    }

    [Theory]
    [InlineData("slight_left", TurnMove.SlightLeft)]
    [InlineData("sharp_left", TurnMove.SharpLeft)]
    [InlineData("slight_right", TurnMove.SlightRight)]
    [InlineData("sharp_right", TurnMove.SharpRight)]
    [InlineData("reverse", TurnMove.Reverse)]
    [InlineData("merge_to_left", TurnMove.MergeToLeft)]
    [InlineData("merge_to_right", TurnMove.MergeToRight)]
    [InlineData(" through ", TurnMove.Through)]
    [InlineData("reverse;left", TurnMove.Reverse | TurnMove.Left)]
    [InlineData("left;Left", TurnMove.Left | TurnMove.Unknown)]   // OSM values are lower case
    [InlineData("bus", TurnMove.Unknown)]
    public void Each_movement_maps_to_its_flag(string value, TurnMove expected)
    {
        Assert.Equal([expected], TurnLanes.Parse(value));
    }

    [Fact]
    public void Any_left_and_any_right_cover_the_slight_and_sharp_variants()
    {
        Assert.NotEqual(TurnMove.None, TurnLanes.Parse("slight_left")[0] & TurnLanes.AnyLeft);
        Assert.Equal(TurnMove.None, TurnLanes.Parse("slight_left")[0] & TurnLanes.AnyRight);
        Assert.NotEqual(TurnMove.None, TurnLanes.Parse("sharp_right")[0] & TurnLanes.AnyRight);
    }

    [Fact]
    public void Overlay_reader_exposes_turn_lanes_per_tlm_direction()
    {
        string path = Path.Combine(Path.GetTempPath(), $"osm_overlay_{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllText(path,
                "# osm_overlay v1 osm=a tlm=b bbox=0,0,1,1\n"
                + "uuid\tpart\tfrom_m\tto_m\tosm_way\tdir\thighway\toneway\tlanes\tlanes_fwd\tlanes_bwd\twidth"
                + "\tsidewalk_left\tsidewalk_right\tcycleway_left\tcycleway_right\tturn_lanes_fwd\tturn_lanes_bwd\troundabout\ttram\n"
                + "{u}\t0\t0.0\t80.5\t7\t-\tsecondary\t0\t3\t2\t1\t\t\t\t\t\tleft|through;right\t\t0\t0\n");
            var reader = OsmOverlayReader.TryLoad(path);
            Assert.NotNull(reader);
            var row = reader!.Best("{u}", 0, 10, 70);
            Assert.NotNull(row);
            Assert.Equal([TurnMove.Left, TurnMove.Through | TurnMove.Right], row!.TurnLanesFwd);
            Assert.Empty(row.TurnLanesBwd);
        }
        finally { File.Delete(path); }
    }
}
