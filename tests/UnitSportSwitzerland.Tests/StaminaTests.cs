using UnitSport.Adventure;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The stamina rules (#1225, src/Player/Stamina.cs) and the max upgrades (src/Adventure/StaminaUpgrades.cs).</summary>
public class StaminaTests
{
    [Fact]
    public void Max_grows_by_ten_a_step_up_to_two_hundred()
    {
        Assert.Equal(100f, Stamina.Full().Max);
        Assert.Equal(150f, Stamina.Full(5).Max);
        Assert.Equal(200f, Stamina.Full(10).Max);
        Assert.Equal(200f, Stamina.Full(14).Max);
    }

    [Theory]
    [InlineData(StaminaKind.SprintCombat, 12f)]
    [InlineData(StaminaKind.ClimbMove, 9f)]
    [InlineData(StaminaKind.ClimbHold, 1f)]
    [InlineData(StaminaKind.SwimSprint, 8f)]
    [InlineData(StaminaKind.SwimDive, 8f)]
    [InlineData(StaminaKind.PigeonFlap, 5f)]
    [InlineData(StaminaKind.PigeonFlapBoost, 10f)]
    public void Each_rate_costs_what_the_plan_says(StaminaKind kind, float perSecond)
    {
        var s = Stamina.Full();
        Assert.True(s.Spend(kind, 1f));
        Assert.Equal(100f - perSecond, s.Points, 3);
    }

    [Fact]
    public void A_climb_jump_costs_eighteen_and_a_half_stick_half()
    {
        var s = Stamina.Full();
        Assert.True(s.Pay(StaminaKind.ClimbJump));
        Assert.Equal(82f, s.Points, 3);
        s.Spend(StaminaKind.ClimbMove, 1f, 0.5f);
        Assert.Equal(77.5f, s.Points, 3);
    }

    [Fact]
    public void Loose_rock_costs_more_and_warm_less()
    {
        var s = Stamina.Full();
        s.Spend(StaminaKind.ClimbMove, 1f, 1f, loose: true);
        Assert.Equal(100f - 9f * Stamina.LooseFactor, s.Points, 3);
        var w = Stamina.Full();
        w.Buff(StaminaBuff.Warm, 240f);
        w.Spend(StaminaKind.ClimbMove, 1f);
        Assert.Equal(100f - 9f * Stamina.WarmClimbFactor, w.Points, 3);
        // warmth is for climbing only
        w.Spend(StaminaKind.SwimSprint, 1f);
        Assert.Equal(100f - 9f * Stamina.WarmClimbFactor - 8f, w.Points, 3);
    }

    [Fact]
    public void Regen_waits_a_second_then_goes_by_where_you_rest()
    {
        var s = Stamina.Full();
        s.Spend(StaminaKind.ClimbMove, 5f);   // 55
        s.Step(0.9f, StaminaRest.Floor);
        Assert.Equal(55f, s.Points, 3);
        s.Step(0.2f, StaminaRest.Floor);      // past the delay: 25 /s
        Assert.True(s.Points > 55f);
        float p = s.Points;
        s.Step(1f, StaminaRest.Seated);
        Assert.Equal(p + 35f, s.Points, 2);
        p = s.Points;
        s.Step(1f, StaminaRest.None);
        Assert.Equal(p, s.Points, 3);
        s.Step(10f, StaminaRest.Riding);
        Assert.Equal(100f, s.Points, 3);
    }

    [Fact]
    public void Empty_is_winded_until_a_quarter_is_back()
    {
        var s = Stamina.Full();
        Assert.True(s.Spend(StaminaKind.ClimbMove, 20f));   // the step that empties it still succeeds
        Assert.Equal(0f, s.Points);
        Assert.True(s.Winded);
        Assert.False(s.Spend(StaminaKind.ClimbHold, 0.1f));
        Assert.False(s.Pay(StaminaKind.ClimbJump));
        s.Step(0.5f, StaminaRest.Floor);  // the delay: nothing yet
        Assert.Equal(0f, s.Points);
        s.Step(0.5f, StaminaRest.Floor);  // 12.5
        s.Step(0.4f, StaminaRest.Floor);  // 22.5
        Assert.True(s.Winded);
        s.Step(0.2f, StaminaRest.Floor);  // 27.5
        Assert.False(s.Winded);
    }

    [Fact]
    public void A_jump_short_of_its_cost_is_refused_and_takes_nothing()
    {
        var s = Stamina.Full();
        s.Points = 10f;
        Assert.False(s.Pay(StaminaKind.ClimbJump));
        Assert.Equal(10f, s.Points);
    }

    [Fact]
    public void Fed_adds_twenty_five_to_the_max_fills_and_wears_off()
    {
        var s = Stamina.Full();
        s.Points = 40f;
        s.Buff(StaminaBuff.Fed, 300f);
        Assert.Equal(125f, s.Max);
        Assert.Equal(125f, s.Points);
        s.Step(301f, StaminaRest.Floor);
        Assert.Equal(100f, s.Max);
        Assert.Equal(100f, s.Points);
    }

    [Fact]
    public void Restore_clamps_and_ends_winded()
    {
        var s = Stamina.Full();
        s.Spend(StaminaKind.ClimbMove, 20f);
        Assert.Equal(50f, s.Restore(50f));
        Assert.False(s.Winded);
        Assert.Equal(50f, s.Restore(80f));
    }

    [Fact]
    public void Infinite_never_drains()
    {
        var s = Stamina.Full();
        s.Infinite = true;
        Assert.True(s.Spend(StaminaKind.ClimbMove, 100f));
        Assert.True(s.Pay(StaminaKind.ClimbJump));
        Assert.Equal(100f, s.Points);
        Assert.Equal(0f, s.CostNow(StaminaKind.ClimbJump));
    }

    [Fact]
    public void A_raise_refills()
    {
        var s = Stamina.Full();
        s.Points = 30f;
        s.SetSteps(2);
        Assert.Equal(120f, s.Points);
    }

    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, false, true, false, false)]   // no Adventure here (a race server, a fixture)
    [InlineData(true, true, true, false, false)]     // creative mode
    [InlineData(true, false, false, false, false)]   // --stamina off
    [InlineData(true, false, true, true, false)]     // /stamina infinite
    public void When_the_meter_counts(bool adventure, bool creative, bool serverOn, bool infinite, bool applies) =>
        Assert.Equal(applies, Stamina.Applies(adventure, creative, serverOn, infinite));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]   // creative climbs without Adventure
    [InlineData(true, false, true, false)]   // never in a Battle Royale match
    [InlineData(false, true, true, false)]
    public void When_climbing_is_on(bool adventure, bool creative, bool inMatch, bool allowed) =>
        Assert.Equal(allowed, Stamina.ClimbingAllowed(adventure, creative, inMatch));

    [Theory]
    [InlineData("+1", 1)]
    [InlineData(" +3 ", 3)]
    [InlineData("+10", 10)]
    [InlineData("+0", null)]
    [InlineData("+11", null)]
    [InlineData("1", null)]
    [InlineData("-1", null)]
    [InlineData("+x", null)]
    [InlineData("", null)]
    public void Reward_arguments(string arg, int? steps) => Assert.Equal(steps, StaminaUpgrades.ParseSteps(arg));

    [Fact]
    public void Grant_writes_touches_then_raises_and_caps_at_ten()
    {
        var p = new PlayerProgress("k");
        var events = new AdventureEvents();
        var seen = new List<AdventureEvent>();
        events.Raised += seen.Add;
        Assert.Null(StaminaUpgrades.Grant(7, p, "+1", _ => true, events));
        Assert.Equal(1, p.Get(StaminaUpgrades.Section).Steps);
        Assert.Contains(StaminaUpgrades.Kind, p.TakeSync());
        var raised = Assert.IsType<StaminaRaised>(Assert.Single(seen));
        Assert.Equal(1, raised.Steps);
        Assert.Null(StaminaUpgrades.Grant(7, p, "+10", _ => true, events));
        Assert.Equal(10, p.Get(StaminaUpgrades.Section).Steps);
        Assert.NotNull(StaminaUpgrades.Grant(7, p, "+1", _ => true, events));   // at the cap
        Assert.NotNull(StaminaUpgrades.Grant(7, p, "+0", _ => true, events));
        Assert.Equal(2, seen.Count);
        Assert.Empty(p.Untouched());
    }

    [Fact]
    public void Grant_refuses_a_peer_not_adventuring()
    {
        var p = new PlayerProgress("k");
        Assert.NotNull(StaminaUpgrades.Grant(7, p, "+1", _ => false, new AdventureEvents()));
        Assert.Equal(0, p.Get(StaminaUpgrades.Section).Steps);
    }

    [Fact]
    public void The_watch_allows_a_full_ring_and_the_best_food()
    {
        // 100 base + Fed: 1.5 x (125 + 75) / 9 x 1.2 + 4 = 44 m
        Assert.Equal(44f, Stamina.PlausibleClimbM(125f), 1);
    }

    // ---- playing together (#1266) ----------------------------------------------------------------

    [Fact]
    public void Linked_on_the_floor_regens_a_fifth_faster()
    {
        var a = Stamina.Full();
        var b = Stamina.Full();
        a.Points = b.Points = 10f;
        for (int i = 0; i < 20; i++) { a.Step(0.05f, StaminaRest.Floor); b.Step(0.05f, StaminaRest.Floor, StaminaCompany.Linked); }
        // 1 s (nothing spent: no delay) at 25 and at 30 per second
        Assert.Equal(35f, a.Points, 1);
        Assert.Equal(40f, b.Points, 1);
        Assert.Equal(Stamina.RegenSeated * 1.2f, Stamina.RegenRate(StaminaRest.Seated, StaminaCompany.Linked), 3);
        Assert.Equal(Stamina.RegenRiding, Stamina.RegenRate(StaminaRest.Riding, StaminaCompany.Linked), 3);
    }

    [Fact]
    public void Belayed_on_a_face_holding_still_is_free_and_regens()
    {
        var s = Stamina.Full();
        s.Points = 20f;
        s.Step(0.05f, StaminaRest.None, StaminaCompany.Belayed);
        for (int i = 0; i < 60; i++)
        {
            Assert.True(s.Spend(StaminaKind.ClimbHold, 0.05f));
            s.Step(0.05f, StaminaRest.None, StaminaCompany.Belayed);
        }
        // nothing spent holding (so no regen delay either): 3 s at 6 /s
        Assert.InRange(s.Points, 37.5f, 38.6f);
        // alone the same hold costs and nothing comes back
        var alone = Stamina.Full();
        alone.Points = 20f;
        for (int i = 0; i < 60; i++) { alone.Spend(StaminaKind.ClimbHold, 0.05f); alone.Step(0.05f, StaminaRest.None); }
        Assert.Equal(17f, alone.Points, 1);
        // belayed and winded: the rope still holds
        var winded = Stamina.Full();
        winded.Points = 0f;
        winded.Winded = true;
        winded.Step(0.05f, StaminaRest.None, StaminaCompany.Belayed);
        Assert.True(winded.Spend(StaminaKind.ClimbHold, 0.05f));
    }

    [Fact]
    public void Hearty_regens_a_quarter_faster_for_its_time()
    {
        var s = Stamina.Full();
        s.Buff(StaminaBuff.Hearty, 2f);
        Assert.Equal(2f, s.BuffLeft(StaminaBuff.Hearty));
        s.Points = 0f;
        for (int i = 0; i < 40; i++) s.Step(0.05f, StaminaRest.Floor);
        // 2 s at 25 x 1.25
        Assert.Equal(62.5f, s.Points, 1);
        Assert.Equal(0f, s.BuffLeft(StaminaBuff.Hearty), 3);
    }
}
