using Godot;
using UnitSport.Player;
using Xunit;
using static UnitSport.Player.PigeonFlight;

namespace UnitSportSwitzerland.Tests;

/// <summary>The playable pigeon's motion (#217, src/Player/PigeonFlight.cs, linked in).</summary>
public class PigeonFlightTests
{
    private const float Dt = 1f / 60f;

    private static State Run(State s, Controls c, bool onFloor, float seconds)
    {
        for (float t = 0; t < seconds; t += Dt) s = Step(s, c, onFloor, Dt);
        return s;
    }

    [Fact]
    public void Flapping_climbs_and_cruises()
    {
        var s = Run(new State { Mode = Mode.Air }, new Controls(Vector2.Zero, true, false, false), false, 5f);
        Assert.Equal(Mode.Air, s.Mode);
        Assert.InRange(s.Velocity.Y, 3f, 3.6f);
        Assert.InRange(new Vector2(s.Velocity.X, s.Velocity.Z).Length(), 15f, 16.1f);
        Assert.True(s.Flap > 0.95f);
    }

    [Fact]
    public void Gliding_sinks_about_six_to_one()
    {
        var s = Run(new State { Mode = Mode.Air }, new Controls(Vector2.Zero, false, false, false), false, 6f);
        float glide = new Vector2(s.Velocity.X, s.Velocity.Z).Length() / -s.Velocity.Y;
        Assert.InRange(glide, 5f, 7f);
        Assert.True(s.Flap < 0.1f);
    }

    [Fact]
    public void Dive_drops_fast()
    {
        var s = Run(new State { Mode = Mode.Air }, new Controls(Vector2.Zero, false, true, false), false, 4f);
        Assert.True(s.Velocity.Y < -12f);
    }

    [Fact]
    public void Ground_walks_and_flap_takes_off()
    {
        var walk = Run(new State { Mode = Mode.Ground }, new Controls(new Vector2(0, -1), false, false, false), true, 2f);
        Assert.Equal(Mode.Ground, walk.Mode);
        Assert.InRange(new Vector2(walk.Velocity.X, walk.Velocity.Z).Length(), WalkSpeed - 0.01f, WalkSpeed + 0.01f);
        Assert.True(walk.Velocity.Z < 0f);   // yaw 0 walks toward −Z

        var up = Step(walk, new Controls(Vector2.Zero, true, false, false), true, Dt);
        Assert.Equal(Mode.Air, up.Mode);
        Assert.True(up.Velocity.Y > 4f);
        // still touching the floor the next step, rising: it stays in the air
        Assert.Equal(Mode.Air, Step(up, new Controls(Vector2.Zero, true, false, false), true, Dt).Mode);
    }

    [Fact]
    public void Landing_turns_into_walking()
    {
        var s = new State { Mode = Mode.Air, Velocity = new Vector3(0, -2, -8) };
        Assert.Equal(Mode.Ground, Step(s, default, true, Dt).Mode);
    }

    [Fact]
    public void Perch_catches_only_slow_unflapping_birds_close_by()
    {
        var at = new Vector3(0, 10, 0);
        var slow = new State { Mode = Mode.Air, Velocity = new Vector3(0, -1, -6) };
        Assert.True(Catches(slow, default, at, at + new Vector3(0.5f, 0, 0.5f)));
        Assert.False(Catches(slow, default, at, at + new Vector3(2f, 0, 0)));
        Assert.False(Catches(slow, new Controls(Vector2.Zero, true, false, false), at, at));
        Assert.False(Catches(slow with { Velocity = new Vector3(0, 0, -11) }, default, at, at));
        Assert.False(Catches(slow with { Mode = Mode.Ground }, default, at, at));
    }

    [Fact]
    public void Perched_stays_still_until_flap_or_push()
    {
        var p = Perch(new State { Mode = Mode.Air, Velocity = new Vector3(0, -1, -6) });
        var still = Run(p, new Controls(new Vector2(1, 0), false, false, false), false, 1f);
        Assert.Equal(Mode.Perched, still.Mode);
        Assert.Equal(Vector3.Zero, still.Velocity);
        Assert.True(still.Yaw < -2f);   // the stick turns it on the spot
        Assert.Equal(Mode.Air, Step(still, new Controls(Vector2.Zero, true, false, false), false, Dt).Mode);
        Assert.Equal(Mode.Air, Step(still, new Controls(new Vector2(0, -1), false, false, false), false, Dt).Mode);
    }
}
