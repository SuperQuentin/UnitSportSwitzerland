using Godot;
using UnitSport.Adventure.Together;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The soft follow constraint (#1266 part 3.2).</summary>
public class TetherTests
{
    private static TetherState State(Vector3 self, Vector3 partner, float length = 1.6f) =>
        new() { Self = self, Partner = partner, Length = length };

    [Fact]
    public void InsideTheLengthTheWishIsUntouched()
    {
        var wish = new Vector3(0.3f, 0, -0.9f);
        var (w, run) = Tether.Apply((wish, true), State(Vector3.Zero, new Vector3(1, 0, 0)));
        Assert.Equal(wish, w);
        Assert.True(run);
    }

    [Fact]
    public void BeyondTheLengthTheAwayPartGoesAndAPullComes()
    {
        // partner 4 m east, length 1.6: 2.4 over, the pull capped at 1
        var (w, _) = Tether.Apply((new Vector3(-1, 0, 0), false), State(Vector3.Zero, new Vector3(4, 0, 0)));
        Assert.True(w.X > 0.99f, $"pulled east ({w})");
        // a wish across the line keeps its across part, plus the pull
        var (w2, _) = Tether.Apply((new Vector3(0, 0, 1), false), State(Vector3.Zero, new Vector3(2.1f, 0, 0)));
        Assert.True(w2.X > 0.35f && w2.X < 0.45f, $"0.8 x 0.5 m over toward the partner ({w2})");
        Assert.True(w2.Z > 0.85f, $"across part kept ({w2})");
        Assert.True(w2.Length() <= 1.0001f);
    }

    [Fact]
    public void NoPullOverADrop()
    {
        var s = State(Vector3.Zero, new Vector3(4, 0, 0));
        s.PullBlocked = true;
        var (w, _) = Tether.Apply((new Vector3(-1, 0, 0), false), s);
        Assert.Equal(0f, w.Length(), 4);   // the away part gone, nothing pulls over the edge
    }

    [Fact]
    public void LedAlongTheTrailWithNoInput()
    {
        var trail = new TetherTrail();
        // the leader walked 3 m north (-Z), point by point
        for (float z = 0; z >= -3f; z -= 0.1f) trail.Push(new Vector3(0, 0, z));
        var leader = new Vector3(0, 0, -3f);
        var target = trail.Target(leader)!.Value;
        Assert.Equal(-1.9f, target.Z, 1);
        var s = State(new Vector3(0, 0, 0), leader);
        s.Led = true;
        s.TrailTarget = target;
        s.PartnerSpeed = 5f;
        var (w, run) = Tether.Apply((Vector3.Zero, false), s);
        Assert.True(w.Z < -0.9f, $"walked toward the trail ({w})");
        Assert.True(run, "runs when the leader runs");
        // at the point: stands
        s.Self = target;
        s.Partner = leader;
        var (still, _) = Tether.Apply((Vector3.Zero, false), s);
        Assert.Equal(0f, still.Length(), 3);
        // fallen behind: runs to catch up even when the leader walks
        s.Self = new Vector3(0, 0, 0);
        s.Partner = leader;
        s.PartnerSpeed = 1.4f;
        var (_, catchUp) = Tether.Apply((Vector3.Zero, false), s);
        Assert.True(catchUp, "behind its point: it runs");
        // own input wins
        s.Self = Vector3.Zero;
        var (mine, _) = Tether.Apply((new Vector3(1, 0, 0) * 0.5f, false), s);
        Assert.True(mine.X > 0.4f);
    }

    [Fact]
    public void TheTrailIsARingAndStartsAgainAfterAJump()
    {
        var trail = new TetherTrail();
        for (int i = 0; i < 100; i++) trail.Push(new Vector3(i * 0.5f, 0, 0));
        Assert.Equal(TetherTrail.Size, trail.Count);
        trail.Push(new Vector3(500, 0, 0));
        Assert.Equal(1, trail.Count);
        trail.Push(new Vector3(500.1f, 0, 0));
        Assert.Equal(1, trail.Count);   // under the spacing
        Assert.Null(new TetherTrail().Target(Vector3.Zero));
    }
}
