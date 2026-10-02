using Godot;
using UnitSport.World;
using Xunit;
using static UnitSport.World.PedestrianRules;

namespace UnitSportSwitzerland.Tests;

/// <summary>The server pedestrians' rules (#217, src/World/PedestrianRules.cs, linked in).</summary>
public class PedestrianRulesTests
{
    // looking north (−Z), standing still
    private static readonly View North = new(Vector3.Zero, 0f, 0f, Vector3.Zero);

    [Fact]
    public void View_cone_and_range()
    {
        Assert.True(InView(North, new Vector3(0, 0, -50)));
        Assert.True(InView(North, new Vector3(40, 0, -50)));     // 39° off: inside the 57° half cone
        Assert.False(InView(North, new Vector3(0, 0, 50)));      // behind
        Assert.False(InView(North, new Vector3(100, 0, -20)));   // 79° off
        Assert.False(InView(North, new Vector3(0, 0, -200)));    // too far
        Assert.True(InView(North, new Vector3(0, 0, 1)));        // at your feet counts
    }

    [Fact]
    public void Never_spawns_in_plain_sight()
    {
        Assert.False(SpawnOk(North, new Vector3(0, 0, -40)));
        Assert.False(SpawnOk(North, new Vector3(20, 0, -60)));
        Assert.True(SpawnOk(North, new Vector3(0, 0, -130)));    // the far band: a few pixels tall
        Assert.False(SpawnOk(North, new Vector3(0, 0, 60)));     // straight behind a still player
        Assert.True(SpawnOk(North, new Vector3(-60, 0, -10)));   // just past the left edge
    }

    [Fact]
    public void Anticipates_the_turn_and_the_motion()
    {
        var p = new Vector3(-50, 0, 30);                         // behind-left, 121° off
        Assert.False(SpawnOk(North, p));
        var turningLeft = North with { YawRate = 1.2f };         // 1.8 rad in 1.5 s: it will look there
        Assert.True(SpawnOk(turningLeft, p));
        Assert.False(InView(turningLeft, p));                    // not yet seen: no pop-in

        var driving = North with { Vel = new Vector3(0, 0, -25) };
        Assert.Equal(new Vector3(0, 0, -37.5f), Ahead(driving).Pos);
    }

    [Fact]
    public void Bodies_near_whatever_the_view_and_ahead_of_fast_ones()
    {
        Assert.True(NeedsBody(North, new Vector3(0, 0, 10)));    // behind you, unseen: still solid
        Assert.False(NeedsBody(North, new Vector3(0, 0, -45)));
        var fast = North with { Vel = new Vector3(0, 0, -20) };
        Assert.True(NeedsBody(fast, new Vector3(0, 0, -45)));    // 30 m ahead of where the car will be
    }

    [Fact]
    public void Forgotten_only_far_or_long_unseen()
    {
        Assert.False(PedestrianRules.Forget(500f, 60f));
        Assert.True(PedestrianRules.Forget(3500f, 0f));
        Assert.True(PedestrianRules.Forget(100f, 1300f));
    }

    [Fact]
    public void Walks_to_its_goal_and_picks_the_same_next_one_every_time()
    {
        var pos = Vector3.Zero;
        Assert.False(Walk(ref pos, new Vector3(0, 0, -10), 1.5f, 1f));
        Assert.Equal(new Vector3(0, 0, -1.5f), pos);
        Assert.True(Walk(ref pos, new Vector3(0, 0, -2), 1.5f, 1f));

        var spots = new[] { new Vector3(0, 0, -15), new Vector3(0, 0, 15), new Vector3(3, 0, 0), new Vector3(40, 0, 0) };
        int a = PickNext(Vector3.Zero, 0f, spots, 42, 3);
        Assert.Equal(a, PickNext(Vector3.Zero, 0f, spots, 42, 3));   // replayable
        Assert.Equal(0, a);                                           // keeps going north, not back, not too near or far
        Assert.Equal(1, PickNext(Vector3.Zero, 0f, spots, 42, 3, (_, b) => b.Z < 0));   // that way is a wall
        Assert.Equal(-1, PickNext(Vector3.Zero, 0f, new[] { new Vector3(2, 0, 0) }, 1, 1));
        Assert.InRange(Speed(7), 1.1f, 1.6f);
    }

    [Fact]
    public void Budgets_hold_at_16_players()
    {
        // every player's share fits the server's cap, and one client never draws more than its cap
        Assert.True(16 * WantedPerPlayer <= RecordCap);
        Assert.True(VisibleCap >= WantedPerPlayer);
    }
}
