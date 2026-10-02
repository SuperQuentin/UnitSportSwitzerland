using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;
using Xunit;

namespace UnitSport.Tests;

/// <summary>RoadSegment.Lv95 and RoadProfiles.For, each shared by several readers of .road tiles (#221).</summary>
public class RoadSegmentTests
{
    private static RoadSegment Segment(RoadClass c, RoadSurface surface, RoadFlags flags = 0, float width = 0f) =>
        new() { Class = c, Surface = surface, Flags = flags, Width = width, Points = new[] { 0f, 400f, 0f, 12.5f, 401f, 987.25f } };

    [Fact]
    public void Lv95_puts_tile_local_x_east_and_z_south_of_the_nw_corner()
    {
        var tile = new TileId(2579, 1109);
        var seg = Segment(RoadClass.Road, RoadSurface.Paved);
        Assert.Equal((2579000.0, 1110000.0), seg.Lv95(tile, 0));
        Assert.Equal((2579012.5, 1110000.0 - 987.25), seg.Lv95(tile, 1));
    }

    [Fact]
    public void Width_is_surveyed_when_given_and_scaled_when_divided_on_both_paths()
    {
        foreach (bool surface in new[] { true, false })
        {
            Assert.Equal(RoadProfile.Road.Width, RoadProfiles.For(Segment(RoadClass.Road, RoadSurface.Paved), 2, surface).Width);
            Assert.Equal(7.5, RoadProfiles.For(Segment(RoadClass.Road, RoadSurface.Paved, width: 7.5f), 2, surface).Width);
            Assert.Equal(15.0, RoadProfiles.For(Segment(RoadClass.Road, RoadSurface.Paved, RoadFlags.Divided, 7.5f), 2, surface).Width);
            Assert.Equal("lane", RoadProfiles.For(Segment(RoadClass.Square, RoadSurface.Paved), 1, surface).Name);
        }
    }

    [Fact]
    public void Importer_path_lets_an_unpaved_surface_clear_paving_and_markings()
    {
        var p = RoadProfiles.For(Segment(RoadClass.Road, RoadSurface.Natural), 1);
        Assert.False(p.Paved);
        Assert.Equal(MarkingPlan.None, p.Markings);

        var track = RoadProfiles.For(Segment(RoadClass.Track, RoadSurface.Paved), 1);
        Assert.True(track.Paved); // a paved track is paved, whatever its class says
    }

    [Fact]
    public void Rewriter_path_keeps_the_class_paving_and_markings_whatever_the_surface()
    {
        var p = RoadProfiles.For(Segment(RoadClass.Road, RoadSurface.Natural), 1, surfaceDecidesPaving: false);
        Assert.Equal(RoadProfile.Road with { Width = RoadProfile.Road.Width }, p);

        var track = RoadProfiles.For(Segment(RoadClass.Track, RoadSurface.Paved), 1, surfaceDecidesPaving: false);
        Assert.False(track.Paved);
    }
}
