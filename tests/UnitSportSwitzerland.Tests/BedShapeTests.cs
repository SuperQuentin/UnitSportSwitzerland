using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// A tipping body's pallet (#615): it holds until the body is steep enough, then slides to the
/// open end; the mini dumper's skip pours it out ahead of the tracks; the parked flags keep it
/// beside the skip's state.
/// </summary>
public class BedShapeTests
{
    [Fact]
    public void A_pallet_holds_then_slides_to_the_edge_as_the_body_rises()
    {
        Assert.Equal(0f, BedShape.Slid(0f));
        Assert.Equal(0f, BedShape.Slid(BedShape.SlideFrom));
        Assert.Equal(1f, BedShape.Slid(BedShape.SlideTo));
        Assert.Equal(1f, BedShape.Slid(1f));
        float last = 0f;
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            Assert.True(BedShape.Slid(t) >= last);
            last = BedShape.Slid(t);
        }
    }

    [Fact]
    public void The_skip_slides_toward_its_front_lip_and_pours_ahead_of_the_tracks()
    {
        var bed = MiniDumperLayout.Bed;
        // node space faces −Z: the skip is at the front, its open end further forward still
        Assert.Equal(-1f, bed.Out.Z);
        Assert.True(bed.Travel > 0.5f);
        Assert.True(bed.Over(bed.Floor));
        Assert.False(bed.Over(bed.Floor + new Vector3(bed.HalfWidth + 0.1f, 0f, 0f)));
        // set down clear of the tipped skip's lip, ahead of the tracks
        Assert.True(-bed.Spill.Z > -bed.Hinge.Z + Pallets.Length * 0.5f);
        Assert.True(-bed.Spill.Z > MiniDumperLayout.TrackHalfLength + Pallets.Length * 0.5f);
        Assert.Equal(0f, bed.Spill.Y);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 41)]
    [InlineData(true, 297)]
    [InlineData(true, 512)]
    public void Parked_flags_keep_the_skip_and_its_pallet(bool tipped, int bed)
    {
        int flags = MiniDumperLayout.Pack(tipped, bed);
        Assert.Equal(tipped, MiniDumperLayout.Unpack(flags));
        Assert.Equal(bed, MiniDumperLayout.BedOf(flags));
    }

    [Fact]
    public void A_dumper_parked_before_it_had_a_pallet_keeps_its_skip_and_an_empty_one()
    {
        // #614's flags: 1 down, 2 up, nothing above them
        Assert.False(MiniDumperLayout.Unpack(1));
        Assert.True(MiniDumperLayout.Unpack(2));
        Assert.Equal(0, MiniDumperLayout.BedOf(2));
    }
}
