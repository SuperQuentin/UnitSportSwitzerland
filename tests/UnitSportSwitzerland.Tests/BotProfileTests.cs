using System.Collections.Generic;
using UnitSport.Bots;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Who a bot is and how hard it plays (#837, src/Bots/BotProfile.cs, linked in).</summary>
public class BotProfileTests
{
    [Fact]
    public void The_same_seed_makes_the_same_bot()
    {
        Assert.Equal(BotProfile.For(42, 3, BotDifficulty.Normal), BotProfile.For(42, 3, BotDifficulty.Normal));
        Assert.NotEqual(BotProfile.For(42, 3, BotDifficulty.Normal).Name + BotProfile.For(42, 3, BotDifficulty.Normal).AimErrorDeg,
            BotProfile.For(43, 3, BotDifficulty.Normal).Name + BotProfile.For(43, 3, BotDifficulty.Normal).AimErrorDeg);
    }

    [Fact]
    public void Harder_bots_react_faster_and_aim_better()
    {
        for (int n = 0; n < 20; n++)
        {
            var easy = BotProfile.For(7, n, BotDifficulty.Easy);
            var normal = BotProfile.For(7, n, BotDifficulty.Normal);
            var hard = BotProfile.For(7, n, BotDifficulty.Hard);
            Assert.True(easy.ReactionSeconds > normal.ReactionSeconds && normal.ReactionSeconds > hard.ReactionSeconds);
            Assert.True(easy.AimErrorDeg > normal.AimErrorDeg && normal.AimErrorDeg > hard.AimErrorDeg);
        }
    }

    [Fact]
    public void Names_are_not_repeated_in_one_match()
    {
        var taken = new HashSet<string>();
        for (int n = 0; n < BotProfile.Names.Length + 5; n++) BotProfile.For(1, n, BotDifficulty.Normal, taken);
        Assert.Equal(BotProfile.Names.Length + 5, taken.Count);
    }

    [Fact]
    public void Difficulty_parses_any_case()
    {
        Assert.Equal(BotDifficulty.Hard, BotProfile.ParseDifficulty("HARD"));
        Assert.Null(BotProfile.ParseDifficulty("brutal"));
    }
}
