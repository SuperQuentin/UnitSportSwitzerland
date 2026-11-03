using UnitSport.Adventure.Together;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The link book and its rules (#1266 part 3.1): pairs, rope teams of four, the modes, every break.</summary>
public class LinkBookTests
{
    [Fact]
    public void APairHoldsHands()
    {
        var b = new LinkBook();
        Assert.Null(b.Link(1, 2, LinkMode.Hand));
        Assert.Equal(1, b.LeaderOf(2));
        Assert.Equal(2, b.FollowerOf(1));
        Assert.Equal(LinkMode.Hand, b.ModeOf(1));
        Assert.Equal(LinkMode.Hand, b.ModeOf(2));
        Assert.Equal(2, b.PartnerOf(1));
        Assert.Equal(1, b.PartnerOf(2));
        Assert.Equal(new long[] { 1, 2 }, b.Chain(2));
    }

    [Fact]
    public void RefusesSelfAndHandsForThree()
    {
        var b = new LinkBook();
        Assert.NotNull(b.Link(1, 1, LinkMode.Hand));
        Assert.Null(b.Link(1, 2, LinkMode.Hand));
        Assert.NotNull(b.Link(2, 3, LinkMode.Hand));    // hands are for two
        Assert.NotNull(b.Link(4, 2, LinkMode.Rope));    // 2 already follows
        Assert.Null(b.Link(2, 3, LinkMode.Rope));       // a rope team of three
        Assert.Equal(LinkMode.Rope, b.ModeOf(1));       // all of it a rope now
        Assert.Equal(new long[] { 1, 2, 3 }, b.Chain(3));
    }

    [Fact]
    public void ARopeTeamIsFourAtMost()
    {
        var b = new LinkBook();
        Assert.Null(b.Link(1, 2, LinkMode.Rope));
        Assert.Null(b.Link(2, 3, LinkMode.Rope));
        Assert.Null(b.Link(3, 4, LinkMode.Rope));
        Assert.NotNull(b.Link(4, 5, LinkMode.Rope));
        Assert.Equal(4, b.ChainSize(2));
        Assert.Equal(1, b.Head(4));
        Assert.NotNull(b.Link(4, 1, LinkMode.Rope));    // no loop
    }

    [Fact]
    public void ChainsJoinTailToHead()
    {
        var b = new LinkBook();
        Assert.Null(b.Link(1, 2, LinkMode.Rope));
        Assert.Null(b.Link(3, 4, LinkMode.Rope));
        Assert.Null(b.Link(2, 3, LinkMode.Rope));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, b.Chain(4));
    }

    [Fact]
    public void ModeChangesAreForTheWholeChain()
    {
        var b = new LinkBook();
        b.Link(1, 2, LinkMode.Hand);
        Assert.True(b.SetMode(2, LinkMode.Rope));
        Assert.Equal(LinkMode.Rope, b.ModeOf(1));
        Assert.True(b.SetMode(1, LinkMode.Hand));
        b.Link(2, 3, LinkMode.Rope);
        Assert.False(b.SetMode(1, LinkMode.Hand));      // three: rope only
        Assert.Null(b.Link(1, 2, LinkMode.Rope));       // linking again is a mode change
    }

    [Fact]
    public void ForcedRopeStaysUntilUntied()
    {
        var b = new LinkBook();
        b.Link(1, 2, LinkMode.Rope);
        b.Force(1, true);
        Assert.True(b.Forced(2));
        float apart = 0;
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Rope, b.Forced(2), 2, rough: false, bothOnFoot: true, dist: 0.5f, ref apart, 0.25f));
        b.Unlink(2);
        Assert.False(b.Forced(1));
    }

    [Fact]
    public void UnlinkSplitsAChain()
    {
        var b = new LinkBook();
        b.Link(1, 2, LinkMode.Rope);
        b.Link(2, 3, LinkMode.Rope);
        b.Link(3, 4, LinkMode.Rope);
        var broken = b.Unlink(2);
        Assert.Equal(2, broken.Count);
        Assert.False(b.IsLinked(1));
        Assert.False(b.IsLinked(2));
        Assert.Equal(new long[] { 3, 4 }, b.Chain(4));
        Assert.Empty(b.Unlink(9));
        Assert.Equal(b.Between(3, 4), b.UnlinkPair(4, 3));
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void TheWireRoundTrips()
    {
        var b = new LinkBook();
        b.Link(1, 2, LinkMode.Hand);
        b.Link(5, 6, LinkMode.Rope);
        b.Link(6, 7, LinkMode.Rope);
        var (f, l, m) = b.Wire();
        var mirror = new LinkBook();
        mirror.Load(f, l, m);
        Assert.Equal(3, mirror.Count);
        Assert.Equal(LinkMode.Hand, mirror.ModeOf(2));
        Assert.Equal(new long[] { 5, 6, 7 }, mirror.Chain(7));
        mirror.Load(Array.Empty<long>(), Array.Empty<long>(), Array.Empty<byte>());
        Assert.Equal(0, mirror.Count);
    }

    [Fact]
    public void HandsPayOutToARopeAndBack()
    {
        float apart = 0;
        // pulled 2 m apart: still hands for a second, then a rope
        Assert.Equal(LinkMode.Hand, LinkRules.Next(LinkMode.Hand, false, 2, false, true, 2f, ref apart, 0.5f));
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Hand, false, 2, false, true, 2f, ref apart, 0.5f));
        // close again on easy ground: hands
        Assert.Equal(LinkMode.Hand, LinkRules.Next(LinkMode.Rope, false, 2, false, true, 1.0f, ref apart, 0.25f));
        // close, but on a glacier: a rope
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Rope, false, 2, true, true, 1.0f, ref apart, 0.25f));
        // rough ground pays out at once
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Hand, false, 2, true, true, 0.5f, ref apart, 0.25f));
        // close but one of them in the air: stays a rope
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Rope, false, 2, false, false, 1.0f, ref apart, 0.25f));
        // a chain of three is a rope whatever
        Assert.Equal(LinkMode.Rope, LinkRules.Next(LinkMode.Hand, false, 3, false, true, 0.5f, ref apart, 0.25f));
    }

    [Fact]
    public void EveryBreak()
    {
        float far = 0;
        Assert.True(LinkRules.Breaks(1f, ref far, 0.25f, ownVehicle: true, inactive: false));
        Assert.True(LinkRules.Breaks(1f, ref far, 0.25f, ownVehicle: false, inactive: true));
        far = 0;
        for (int i = 0; i < 11; i++) Assert.False(LinkRules.Breaks(30f, ref far, 0.25f, false, false));
        Assert.True(LinkRules.Breaks(30f, ref far, 0.25f, false, false));
        far = 2.9f;
        Assert.False(LinkRules.Breaks(10f, ref far, 0.25f, false, false));
        Assert.Equal(0f, far);
        Assert.Equal(8f, LinkRules.Length(LinkMode.Rope));
        Assert.Equal(1.6f, LinkRules.Length(LinkMode.Hand));
    }
}
