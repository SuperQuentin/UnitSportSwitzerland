using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The first-run tutorial's steps (#517, src/Core/TutorialSteps.cs, linked in).</summary>
public class TutorialStepsTests
{
    private static TutorialGoal[] Goals(bool canSpawn, bool inMatch) =>
        TutorialSteps.For(canSpawn, inMatch).Select(s => s.Goal).ToArray();

    [Fact]
    public void Offline_every_step_runs_in_order_and_ends_on_done()
    {
        Assert.Equal(new[]
        {
            TutorialGoal.Look, TutorialGoal.Walk, TutorialGoal.RunJump, TutorialGoal.Ride,
            TutorialGoal.Camera, TutorialGoal.Map, TutorialGoal.Fly, TutorialGoal.Done,
        }, Goals(canSpawn: true, inMatch: false));
    }

    [Fact]
    public void Online_without_admin_skips_the_travel_menu()
    {
        Assert.DoesNotContain(TutorialGoal.Ride, Goals(canSpawn: false, inMatch: false));
        Assert.Contains(TutorialGoal.Fly, Goals(canSpawn: false, inMatch: false));
    }

    [Fact]
    public void A_match_skips_travel_map_and_fly_camera()
    {
        var goals = Goals(canSpawn: false, inMatch: true);
        Assert.DoesNotContain(TutorialGoal.Map, goals);
        Assert.DoesNotContain(TutorialGoal.Fly, goals);
        Assert.Equal(TutorialGoal.Done, goals[^1]);
    }

    [Fact]
    public void Nothing_done_meets_no_action_step()
    {
        var none = new TutorialSignals();
        foreach (var step in TutorialSteps.For(true, false))
            Assert.False(TutorialSteps.Met(step.Goal, none), step.Goal.ToString());
    }

    [Fact]
    public void Look_and_walk_need_their_amount()
    {
        Assert.False(TutorialSteps.Met(TutorialGoal.Look, new TutorialSignals { LookedDegrees = TutorialSteps.LookDegrees - 1 }));
        Assert.True(TutorialSteps.Met(TutorialGoal.Look, new TutorialSignals { LookedDegrees = TutorialSteps.LookDegrees }));
        Assert.Equal(0.5f, TutorialSteps.Progress(TutorialGoal.Walk, new TutorialSignals { WalkedMeters = TutorialSteps.WalkMeters / 2 }), 3);
        Assert.True(TutorialSteps.Met(TutorialGoal.Walk, new TutorialSignals { WalkedMeters = 40 }));
    }

    [Fact]
    public void Run_and_jump_needs_both()
    {
        Assert.False(TutorialSteps.Met(TutorialGoal.RunJump, new TutorialSignals { Ran = true }));
        Assert.False(TutorialSteps.Met(TutorialGoal.RunJump, new TutorialSignals { Jumped = true }));
        Assert.True(TutorialSteps.Met(TutorialGoal.RunJump, new TutorialSignals { Ran = true, Jumped = true }));
    }

    [Fact]
    public void The_map_and_the_last_card_move_on_by_themselves()
    {
        Assert.True(TutorialSteps.Met(TutorialGoal.Map, new TutorialSignals { Seconds = TutorialSteps.OptionalSeconds }));
        Assert.True(TutorialSteps.Met(TutorialGoal.Done, new TutorialSignals { Seconds = TutorialSteps.DoneSeconds }));
        Assert.True(TutorialSteps.Met(TutorialGoal.Done, new TutorialSignals { HelpOpened = true }));
        // the action steps never time out: the point is that the player did it once
        Assert.False(TutorialSteps.Met(TutorialGoal.Fly, new TutorialSignals { Seconds = 600 }));
    }

    [Fact]
    public void Each_device_gets_its_own_words()
    {
        var look = TutorialSteps.For(true, false)[0];
        Assert.Contains("mouse", TutorialSteps.Body(look, pad: false, vr: false));
        Assert.Contains("{look_right}", TutorialSteps.Body(look, pad: true, vr: false));
        Assert.Contains("head", TutorialSteps.Body(look, pad: true, vr: true));
        // VR falls back to the pad's words, then the keyboard's
        var walk = TutorialSteps.For(true, false).First(s => s.Goal == TutorialGoal.Walk);
        Assert.Equal(walk.Pad, TutorialSteps.Body(walk, pad: true, vr: true));
        var camera = TutorialSteps.For(true, false).First(s => s.Goal == TutorialGoal.Camera);
        Assert.Equal(camera.Keys, TutorialSteps.Body(camera, pad: true, vr: true));
    }
}
