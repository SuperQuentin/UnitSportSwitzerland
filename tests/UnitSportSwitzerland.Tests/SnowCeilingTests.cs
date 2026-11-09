using UnitSport.Occasions;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Falling snow stops above the cloud layer (#1637): by height over the ground, with a fade, none in space. Pure.</summary>
public class SnowCeilingTests
{
    [Fact]
    public void It_snows_fully_under_the_clouds_in_the_mountains()
    {
        Assert.Equal(1f, SnowCeiling.Share(2_500, 2_000), 3);   // a skier 500 m over a 2,000 m valley floor
        Assert.Equal(1f, SnowCeiling.Share(3_700, 2_000), 3);   // 1,700 m up: still under the cloud base
    }

    [Fact]
    public void Low_valleys_keep_their_thinner_snow()
    {
        Assert.Equal(0.45f, SnowCeiling.Share(400, 400), 3);
        Assert.Equal(0.45f, SnowCeiling.Share(700, 300), 3);
    }

    [Fact]
    public void Above_the_cloud_base_there_is_none()
    {
        Assert.Equal(0f, SnowCeiling.Share(2_700, 500));          // a plane 2.2 km over the plateau
        Assert.Equal(0f, SnowCeiling.Share(12_000, 500));         // an airliner
        Assert.Equal(0f, SnowCeiling.Share(60_000, 0));           // the shuttle, ground unknown
        Assert.Equal(0f, SnowCeiling.Share(400_000, 500));        // orbit
    }

    [Fact]
    public void Climbing_through_the_base_fades_it_out_without_a_jump()
    {
        float last = SnowCeiling.Share(1_800, 0);
        Assert.Equal(1f, last, 3);
        for (double alt = 1_805; alt <= 2_400; alt += 5)
        {
            float now = SnowCeiling.Share(alt, 0);
            Assert.True(now <= last + 1e-6f, $"rises at {alt}");
            Assert.True(last - now < 0.03f, $"jumps at {alt}: {last} -> {now}");
            last = now;
        }
        Assert.Equal(0f, last);
        Assert.Equal(0.5f, SnowCeiling.Share(SnowCeiling.CloudBaseAboveGroundM, 0), 3);   // half way at the base
    }
}

/// <summary>The cloud deck while it snows (#1643): over the snow's fade, full under 12 km, gone in space. Pure.</summary>
public class CloudDeckTests
{
    [Fact]
    public void The_snow_stops_inside_the_deck()
    {
        Assert.True(SnowCeiling.DeckBaseAboveGroundM < SnowCeiling.CloudBaseAboveGroundM);
        Assert.True(SnowCeiling.DeckTopAboveGroundM >= SnowCeiling.CloudBaseAboveGroundM + SnowCeiling.FadeM / 2);
    }

    [Fact]
    public void Full_while_it_snows_none_without_snow_or_in_space()
    {
        Assert.Equal(1f, SnowCeiling.DeckStrength(0.6f, 500), 3);      // Christmas, on the ground
        Assert.Equal(1f, SnowCeiling.DeckStrength(0.6f, 11_000), 3);   // an airliner over it
        Assert.Equal(0.5f, SnowCeiling.DeckStrength(0.15f, 500), 3);   // light snow, a thinner deck
        Assert.Equal(0f, SnowCeiling.DeckStrength(0f, 500));
        Assert.Equal(0f, SnowCeiling.DeckStrength(0.6f, 60_000));      // the shuttle
        float mid = SnowCeiling.DeckStrength(0.6f, 16_000);
        Assert.InRange(mid, 0.4f, 0.6f);
    }
}
