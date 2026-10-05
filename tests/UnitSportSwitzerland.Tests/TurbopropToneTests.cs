using UnitSport.Audio;
using Xunit;

namespace UnitSportSwitzerland.Tests;

public class TurbopropToneTests
{
    [Fact]
    public void Governed_prop_holds_full_rpm_above_ground_idle()
    {
        Assert.Equal(740f, TurbopropTone.PropRpm(0f, 740f, 1020f), 3);
        Assert.Equal(1020f, TurbopropTone.PropRpm(TurbopropTone.GovernedFrom, 740f, 1020f), 3);
        Assert.Equal(1020f, TurbopropTone.PropRpm(1f, 740f, 1020f), 3);
        Assert.Equal(1020f, TurbopropTone.PropRpm(2f, 740f, 1020f), 3);
        Assert.True(TurbopropTone.PropRpm(0.3f, 740f, 1020f) > TurbopropTone.PropRpm(0.1f, 740f, 1020f));
    }

    [Fact]
    public void Four_blades_at_1020_rpm_pass_68_times_a_second()
    {
        Assert.Equal(68f, TurbopropTone.BladePassHz(1020f, 4), 3);
    }

    [Fact]
    public void Engines_beat_slowly_and_stay_centred()
    {
        Assert.Equal(1f, TurbopropTone.Detune(0, 1));
        float mean = 0f;
        for (int i = 0; i < 4; i++) mean += TurbopropTone.Detune(i, 4) / 4f;
        Assert.Equal(1f, mean, 4);
        float beat = TurbopropTone.BeatHz(68f, 4);
        Assert.InRange(beat, 0.5f, 1.5f);
    }

    [Fact]
    public void Loaded_blades_are_louder()
    {
        Assert.Equal(0.3f, TurbopropTone.BladeLoudness(0f), 3);
        Assert.Equal(1f, TurbopropTone.BladeLoudness(1f), 3);
        Assert.True(TurbopropTone.BladeLoudness(0.8f) > TurbopropTone.BladeLoudness(0.4f));
    }
}
