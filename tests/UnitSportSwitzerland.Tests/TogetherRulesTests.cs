using UnitSport.Adventure.Together;
using UnitSport.Items.Fishing;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The together rule and the group yield (#1266, adventure-together parts 1-2).</summary>
public class TogetherRulesTests
{
    private static Mate At(double de, double dn, double dalt = 0, bool active = true, bool down = false) =>
        new(2_600_000 + de, 1_200_000 + dn, 1000 + dalt, active, down);

    private static int Count(params Mate[] mates) => TogetherRules.Count(2_600_000, 1_200_000, 1000, mates);

    [Fact]
    public void CountsPartyMatesWithinSixtyMetresAndFortyUp()
    {
        Assert.Equal(0, Count());
        Assert.Equal(1, Count(At(59.9, 0)));
        Assert.Equal(0, Count(At(60.1, 0)));
        Assert.Equal(1, Count(At(42, 42)));            // 59.4 m across
        Assert.Equal(0, Count(At(43, 43)));            // 60.8 m
        Assert.Equal(1, Count(At(0, 10, 39.9)));
        Assert.Equal(0, Count(At(0, 10, 40.1)));
        Assert.Equal(0, Count(At(0, 10, -40.1)));
    }

    [Fact]
    public void LeavesOutTheInactiveAndTheKnockedOut()
    {
        Assert.Equal(0, Count(At(5, 0, active: false)));
        Assert.Equal(0, Count(At(5, 0, down: true)));
        Assert.Equal(1, Count(At(5, 0), At(6, 0, active: false), At(7, 0, down: true)));
    }

    [Fact]
    public void CapsAtThree()
    {
        Assert.Equal(3, Count(At(1, 0), At(2, 0), At(3, 0), At(4, 0), At(5, 0)));
        Assert.Equal(1.75, TogetherRules.YieldFactor(7), 9);
    }

    [Fact]
    public void PlayRadiusIsWider()
    {
        var far = new[] { At(150, 0) };
        Assert.Equal(0, TogetherRules.Count(2_600_000, 1_200_000, 1000, far));
        Assert.Equal(1, TogetherRules.Count(2_600_000, 1_200_000, 1000, far, TogetherRules.PlayRadius));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void YieldIsExactInExpectation(int baseCount, int n)
    {
        var rng = new Random(1266 + baseCount * 7 + n);
        long sum = 0;
        const int rolls = 100_000;
        for (int i = 0; i < rolls; i++) sum += TogetherRules.Yield(baseCount, n, rng.NextDouble());
        double expected = baseCount * (1 + 0.25 * n);
        Assert.InRange(sum / (double)rolls, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void YieldNeverLessThanAloneNorMoreThanOneOver()
    {
        for (double u = 0; u < 1; u += 0.05)
        {
            Assert.Equal(3, TogetherRules.Yield(3, 0, u));
            int v = TogetherRules.Yield(3, 1, u);
            Assert.InRange(v, 3, 4);
        }
        Assert.Equal(0, TogetherRules.Yield(0, 3, 0.9));
    }

    [Fact]
    public void BiteAndRareFactors()
    {
        Assert.Equal(1, TogetherRules.BiteFactor(0), 9);
        Assert.Equal(1 / 1.15, TogetherRules.BiteFactor(1), 9);
        Assert.Equal(1 / 1.45, TogetherRules.BiteFactor(3), 9);
        Assert.Equal(2.5, TogetherRules.RareFactor(3), 9);
        Assert.Equal(1.5, TogetherRules.RareFactor(1), 9);
    }

    [Fact]
    public void FishingTakesTheFactors()
    {
        var spot = FishWaters.Spot(true, 2_600_000, 1_200_000, 600, 0.01);
        double alone = FishRules.MeanBiteSeconds(spot, Bait.None, 12, 0);
        Assert.Equal(alone / 1.3, FishRules.MeanBiteSeconds(spot, Bait.None, 12, 0, together: 2), 6);
        foreach (var s in FishCatalog.All)
        {
            double w0 = FishRules.Weight(s, spot, Bait.None, 12), w3 = FishRules.Weight(s, spot, Bait.None, 12, together: 3);
            if (s.Rarity >= Rarity.Rare) Assert.Equal(w0 * 2.5, w3, 6);
            else Assert.Equal(w0, w3, 9);
        }
    }

    [Fact]
    public void RareFindsOnlyHighUp()
    {
        Assert.Equal(0, TogetherRules.RareFindChance(3, 1799));
        Assert.Equal(0.004, TogetherRules.RareFindChance(0, 2000), 9);
        Assert.Equal(0.004 * 2.5, TogetherRules.RareFindChance(3, 2000), 9);
    }

    [Fact]
    public void QuestCashBonusSkipsPostkarteCards()
    {
        Assert.True(TogetherRules.PaysTogetherCash("quest:first-summit"));
        Assert.False(TogetherRules.PaysTogetherCash("quest:pk:20261027:3"));
        Assert.False(TogetherRules.PaysTogetherCash("chest:ch:1_2"));
        Assert.Equal(0, TogetherRules.CashBonus(100, 0));
        Assert.Equal(20, TogetherRules.CashBonus(100, 2));
        Assert.Equal(30, TogetherRules.CashBonus(100, 5));
        Assert.Equal(1, TogetherRules.CashBonus(3, 1));
    }

    [Fact]
    public void ToastTextsComeFromATable()
    {
        Assert.Equal("", TogetherRules.BonusText(0));
        Assert.Equal(" (+1 together)", TogetherRules.BonusText(1));
        Assert.Same(TogetherRules.BonusText(2), TogetherRules.BonusText(2));
        Assert.Equal("Together ×2", TogetherRules.ChipText(2));
        Assert.Equal("", TogetherRules.ChipText(0));
    }
}
