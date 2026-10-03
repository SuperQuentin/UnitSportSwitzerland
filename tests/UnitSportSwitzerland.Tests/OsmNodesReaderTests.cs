using UnitSport.Tools.RoadGen.Import;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>osm_nodes.tsv</c> (#347): signals, bike boxes and restrictions read back per TLM line.</summary>
public class OsmNodesReaderTests
{
    private const string File_ =
        "# osm_nodes v1 osm=a tlm=b bbox=0,0,1,1 (c) OpenStreetMap contributors, ODbL\n"
        + "kind\tosm_id\te\tn\tuuid\tpart\talong_m\tjunction\tline_end\tend_e\tend_n\tdir\tvalue\tto_uuid\tto_part\tto_end\ttags\n"
        + "asl\t15\t1.0\t-12.0\t{S}\t0\t88.0\tapproach\tend\t0.0\t0.0\t\t\t\t\t\tcycleway=asl\n"
        + "restriction\t900\t0.0\t1.0\t{W}\t0\t100.0\tvia\tend\t0.0\t0.0\t\tno_left_turn\t{N}\t0\tstart\t\n"
        + "signal\t11\t15.0\t1.0\t{E}\t0\t15.0\tapproach\tstart\t0.0\t0.0\t-\t\t\t\t\thighway=traffic_signals;traffic_signals:direction=forward\n"
        + "signal\t22\t1.0\t60.0\t{N}\t0\t60.0\tmid\t\t\t\t\t\t\t\t\tbutton_operated=yes;crossing=traffic_signals;highway=crossing\n"
        + "signal\t13\t-20.0\t1.0\t{W}\t0\t80.0\tapproach\tend\t0.0\t0.0\t+\t\t\t\t\thighway=traffic_signals\n";

    private static OsmNodesReader Load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"osm_nodes_{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllText(path, File_);
            return OsmNodesReader.TryLoad(path)!;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_gives_no_reader() =>
        Assert.Null(OsmNodesReader.TryLoad(Path.Combine(Path.GetTempPath(), $"none_{Guid.NewGuid():N}.tsv")));

    [Fact]
    public void Every_row_is_read()
    {
        var r = Load();
        Assert.Equal(5, r.All.Count);
        Assert.Equal(3, r.All.Count(e => e.Kind == OsmNodesReader.NodeKind.Signal));
    }

    [Fact]
    public void Signal_carries_its_line_end_and_facing()
    {
        var s = Load().ForLine("{E}", 0).Single();
        Assert.Equal(OsmNodesReader.NodeKind.Signal, s.Kind);
        Assert.Equal(15.0, s.Along);
        Assert.Equal(OsmNodesReader.JunctionKind.Approach, s.Junction);
        Assert.Equal(OsmNodesReader.End.Start, s.LineEnd);
        Assert.Equal(OsmNodesReader.Facing.Backward, s.Dir);
        Assert.Equal("forward", s.Tags["traffic_signals:direction"]);
        Assert.False(s.PedestrianOnly);
    }

    [Fact]
    public void Mid_block_pedestrian_signal_has_no_line_end()
    {
        var s = Load().ForLine("{N}", 0).Single();
        Assert.Equal(OsmNodesReader.JunctionKind.Mid, s.Junction);
        Assert.Equal(OsmNodesReader.End.None, s.LineEnd);
        Assert.Equal(OsmNodesReader.Facing.None, s.Dir);
        Assert.True(s.PedestrianOnly);
    }

    [Fact]
    public void Restriction_sits_on_its_from_line_and_names_the_to_line()
    {
        var onWest = Load().ForLine("{W}", 0);
        Assert.Equal(2, onWest.Count);
        var r = onWest.Single(e => e.Kind == OsmNodesReader.NodeKind.Restriction);
        Assert.Equal("no_left_turn", r.Value);
        Assert.Equal(OsmNodesReader.End.End, r.LineEnd);
        Assert.Equal(("{N}", 0, OsmNodesReader.End.Start), (r.ToUuid, r.ToPart, r.ToEnd));
        Assert.Empty(r.Tags);
    }
}
