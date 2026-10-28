using UnitSport.Combat;
using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A bot's decisions in Schwingen (#1269, src/Combat/SchwingBot.cs, linked in) and the Älplerfest's def.</summary>
public class SchwingBotTests
{
    [Fact]
    public void A_better_bot_reads_more_telegraphs_and_reacts_sooner()
    {
        var easy = SchwingSkill.Of(0);
        var normal = SchwingSkill.Of(1);
        var hard = SchwingSkill.Of(2);
        Assert.True(easy.Answer < normal.Answer && normal.Answer < hard.Answer);
        Assert.True(easy.Reaction > normal.Reaction && normal.Reaction > hard.Reaction);
        Assert.True(easy.TugGap > hard.TugGap);
        Assert.True(easy.Aggression < hard.Aggression);
    }

    [Fact]
    public void A_bot_that_reads_it_answers_with_the_real_counter_or_brace_and_one_that_does_not_stands_there()
    {
        foreach (var d in SchwingRules.Throws)
        {
            var counter = SchwingBot.Answer(d.Throw, SchwingSkill.Of(2), 0.0, 0.1);
            Assert.Equal((d.Counter, false), counter);
            Assert.Equal(SchwingAnswer.Countered, SchwingRules.Answer(d.Throw, counter.Lean, counter.Brace));
            var brace = SchwingBot.Answer(d.Throw, SchwingSkill.Of(2), 0.0, 0.9);
            Assert.Equal((d.Brace, true), brace);
            Assert.Equal(SchwingAnswer.Braced, SchwingRules.Answer(d.Throw, brace.Lean, brace.Brace));
            Assert.Equal((SchwingLean.None, false), SchwingBot.Answer(d.Throw, SchwingSkill.Of(0), 0.99, 0.1));
        }
    }

    [Fact]
    public void A_bots_attack_is_always_a_throw_a_player_can_make()
    {
        var seen = new HashSet<SchwingThrow>();
        for (double k = 0; k < 1; k += 0.1)
            for (double s = 0; s < 1; s += 0.05)
            {
                var t = SchwingBot.Attack(k, s);
                Assert.True(SchwingRules.Valid((int)t));
                seen.Add(t);
            }
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void A_wandering_bot_leans_every_way_and_rests_half_the_time()
    {
        var seen = new HashSet<SchwingLean>();
        for (double r = 0; r < 1; r += 0.05) seen.Add(SchwingBot.Wander(r));
        Assert.Equal(5, seen.Count);
        Assert.Equal(SchwingLean.None, SchwingBot.Wander(0.2));
    }

    [Fact]
    public void A_bot_vs_a_bot_bout_ends_without_stalling()
    {
        // two bots of every skill play a whole bout on the pure rules: it always ends inside the clock
        foreach (int skill in new[] { 0, 1, 2 })
        {
            var m = new SchwingMatch(-1, -2, 0);
            var rng = new Random(skill + 11);
            var s = SchwingSkill.Of(skill);
            double now = 0;
            while (m.Phase != SchwingPhase.Done && now < 400)
            {
                now += 0.1;
                m.Tick(now);
                foreach (long who in new[] { -1L, -2L })
                {
                    if (m.Phase == SchwingPhase.Live)
                    {
                        if (m.Pending != SchwingThrow.None && m.ThrowBy != who && rng.NextDouble() < 0.3)
                        {
                            var (lean, brace) = SchwingBot.Answer(m.Pending, s, rng.NextDouble(), rng.NextDouble());
                            m.SetLean(who, lean, brace);
                        }
                        else if (m.Pending == SchwingThrow.None && rng.NextDouble() < 0.02)
                        {
                            m.SetLean(who, SchwingBot.Wander(rng.NextDouble()), false);
                            if (rng.NextDouble() < s.Aggression) m.StartThrow(who, SchwingBot.Attack(rng.NextDouble(), rng.NextDouble()), now);
                        }
                    }
                    else if (m.Phase == SchwingPhase.Ground && rng.NextDouble() < 0.5) m.PressTug(who, now);
                }
            }
            Assert.Equal(SchwingPhase.Done, m.Phase);
        }
    }

    [Fact]
    public void The_Aelplerfest_def_is_sound_and_opens_the_Schwingen_on_day_5()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.AelplerfestId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.False(def.IsOpen(FestivalCatalog.SchwingGame, 4));
        Assert.True(def.IsOpen(FestivalCatalog.SchwingGame, 5));
        Assert.True(def.IsOpen(FestivalCatalog.SchwingGame, 11), "it stays open to the end");
        // a day's play is worth no more than the game's daily cap, and three bouts a day reach it
        var cap = def.Game(FestivalCatalog.SchwingGame)!.DayCap;
        var win = SchwingRules.Reward(true, false, false).Batzen;
        Assert.True(3 * win <= cap + 1, "three won bouts a day stay near the cap");
    }
}
