using UnitSport.Avatar.Face;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A dancing face following the music (src/Avatar/Face/DanceFace.cs, linked in, #728).</summary>
public class DanceFaceTests
{
    private static FaceState At(int style, int section, float level = 0.8f, float kick = 0f, float bar = 0f, float burst = 0f,
        int beat = 1, int floor = 0, uint seed = 0u) =>
        DanceFace.Target(new DanceHearing(style, section, level, kick, bar, burst, beat, 2, floor), seed);

    [Fact]
    public void A_calm_part_dreams_and_a_peak_grins_and_opens_on_the_hit()
    {
        var calm = At(0, 0);
        Assert.True(calm.OpenL < 0.6f && calm.Smile > 0f);
        var peakHit = At(0, 2, kick: 1f);
        var peakRest = At(0, 2, kick: 0f);
        Assert.True(peakHit.Jaw > peakRest.Jaw + 0.3f);
        Assert.True(peakRest.Smile > calm.Smile);
    }

    [Fact]
    public void A_chorus_sings_oh_and_ee_on_alternate_beats()
    {
        Assert.True(At(0, 3, beat: 0).Wide < 0f);
        Assert.True(At(0, 3, beat: 1).Wide > 0f);
        Assert.True(At(0, 3).Jaw > At(0, 1).Jaw);
    }

    [Fact]
    public void A_new_section_is_a_wow_and_the_bar_lifts_the_brows()
    {
        Assert.True(At(0, 2, burst: 1f).Brow > 0.8f);
        Assert.True(At(0, 1, bar: 1f).Brow > At(0, 1).Brow + 0.3f);
    }

    [Fact]
    public void Each_style_has_its_temper()
    {
        Assert.True(At(1, 2).Brow < 0f, "rock snarls in a peak");
        Assert.True(At(3, 1).Squint >= 0.35f, "hip-hop is half-lidded");
        Assert.True(At(4, 1).OpenL <= 0.35f, "chill floats with the eyes closed");
        Assert.Equal(FaceEyes.Hearts, At(6, 1).Special);
    }

    [Fact]
    public void Breaking_concentrates_and_silence_rests()
    {
        var floor = At(3, 2, kick: 1f, burst: 1f, floor: 1);
        Assert.True(floor.Squint >= 0.5f && floor.Jaw < 0.4f);
        Assert.True(At(0, 2, level: 0.01f, kick: 1f).Jaw < At(0, 2, kick: 1f).Jaw);
    }

    [Fact]
    public void The_same_music_and_dancer_give_the_same_face()
    {
        Assert.Equal(At(2, 3, kick: 0.7f, seed: 9u), At(2, 3, kick: 0.7f, seed: 9u));
    }
}
