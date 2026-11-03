using Godot;
using UnitSport.Player;
using Xunit;
using static UnitSport.Player.UfoFlight;

namespace UnitSportSwitzerland.Tests;

/// <summary>The flying saucer's hover flight (#861, src/Player/UfoFlight.cs, linked in).</summary>
public class UfoFlightTests
{
    private const float Dt = 1f / 60f;
    private static readonly Env High = new(false, 80f);

    private static State Run(State s, Controls c, Env env, float seconds)
    {
        for (float t = 0; t < seconds; t += Dt) s = Step(s, c, env, Dt);
        return s;
    }

    private static State Flying() => new() { Legs = 0f };

    [Fact]
    public void Lets_go_and_holds_its_height()
    {
        var s = Flying();
        s.Velocity = new Vector3(0, 6f, 0);
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), High, 2f);
        Assert.InRange(s.Velocity.Y, -0.01f, 0.01f);
        // no gravity, no stall: still at rest a minute later
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), High, 60f);
        Assert.True(s.Velocity.Length() < 0.01f);
    }

    [Fact]
    public void Cruises_forward_and_strafes_alike()
    {
        var fwd = Run(Flying(), new Controls(new Vector2(0, -1), 0f, 0f, false, 0f), High, 5f);
        Assert.InRange(fwd.Velocity.Dot(Heading(0f)), Cruise - 0.1f, Cruise + 0.1f);
        var side = Run(Flying(), new Controls(new Vector2(1, 0), 0f, 0f, false, 0f), High, 5f);
        Assert.InRange(side.Velocity.Dot(Right(0f)), Cruise - 0.1f, Cruise + 0.1f);
        var boost = Run(Flying(), new Controls(new Vector2(0, -1), 0f, 0f, true, 0f), High, 5f);
        Assert.InRange(boost.Velocity.Length(), Dash - 0.1f, Dash + 0.1f);
    }

    [Fact]
    public void Climbs_and_descends()
    {
        var up = Run(Flying(), new Controls(Vector2.Zero, 1f, 0f, false, 0f), High, 2f);
        Assert.InRange(up.Velocity.Y, Climb - 0.01f, Climb + 0.01f);
        var down = Run(Flying(), new Controls(Vector2.Zero, 0f, 1f, true, 0f), High, 3f);
        Assert.InRange(down.Velocity.Y, -DashClimb - 0.01f, -DashClimb + 0.01f);
    }

    [Fact]
    public void Turns_on_the_spot_to_the_look()
    {
        var s = Run(Flying(), new Controls(Vector2.Zero, 0f, 0f, false, 1.5f), High, 1.5f);
        Assert.InRange(s.Yaw, 1.49f, 1.51f);
        Assert.True(s.Velocity.Length() < 0.01f);
    }

    [Fact]
    public void Wobbles_when_it_stops()
    {
        var s = Run(Flying(), new Controls(new Vector2(0, -1), 0f, 0f, false, 0f), High, 6f);
        // a steady lean into the travel, nose down
        Assert.True(s.Tilt.X > 0.02f);
        // let go: it brakes nose up, then swings past level and back (under-damped)
        float min = 0f, max = 0f;
        for (float t = 0; t < 4f; t += Dt)
        {
            s = Step(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), High, Dt);
            min = Mathf.Min(min, s.Tilt.X);
            max = Mathf.Max(max, s.Tilt.X);
        }
        Assert.True(min < -0.1f, $"nose up while braking: {min}");
        Assert.True(max > 0.01f, $"swings back past level: {max}");
        Assert.True(Mathf.Abs(s.Tilt.X) < 0.01f, "settles level");
        Assert.True(MathF.Abs(s.Tilt.X) <= MaxTilt * 1.3f);
    }

    [Fact]
    public void Legs_come_down_near_the_ground_and_fold_high()
    {
        var s = Run(Flying(), new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(false, 4f), 2f);
        Assert.Equal(1f, s.Legs);
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(false, 30f), 2f);
        Assert.Equal(0f, s.Legs);
        // the pilot's hatch press in the air puts them down anyway, until it lands
        s = Step(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f, Hatch: true), new Env(false, 30f), Dt);
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(false, 30f), 2f);
        Assert.Equal(1f, s.Legs);
    }

    [Fact]
    public void Lands_settles_and_opens_the_ramp_then_lifts_off_shut()
    {
        var s = Flying();
        s.Velocity = new Vector3(2f, -3f, 0f);
        s.Legs = 1f;
        s = Run(s, new Controls(Vector2.Zero, 0f, 1f, false, 0f), new Env(true, 0f), 3f);
        Assert.True(s.Landed);
        Assert.True(MathX_Flat(s.Velocity) < 0.01f);
        Assert.Equal(1f, s.Ramp);
        // the hatch press shuts it on the ground
        s = Step(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f, Hatch: true), new Env(true, 0f), Dt);
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(true, 0f), 2f);
        Assert.Equal(0f, s.Ramp);
        // open again and lift off: the ramp comes in, it climbs slowly until it is
        s = Step(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f, Hatch: true), new Env(true, 0f), Dt);
        s = Run(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(true, 0f), 2f);
        Assert.Equal(1f, s.Ramp);
        s = Step(s, new Controls(new Vector2(0, -1), 1f, 0f, false, 0f), new Env(true, 0f), Dt);
        Assert.False(s.Landed);
        s = Run(s, new Controls(new Vector2(0, -1), 1f, 0f, false, 0f), new Env(false, 1f), 0.3f);
        Assert.True(MathX_Flat(s.Velocity) < 1f, "no dash off with the ramp down");
        s = Run(s, new Controls(new Vector2(0, -1), 1f, 0f, false, 0f), new Env(false, 5f), 3f);
        Assert.Equal(0f, s.Ramp);
        Assert.True(MathX_Flat(s.Velocity) > 20f);
    }

    [Fact]
    public void Flares_near_the_ground_and_settles_from_a_low_hover()
    {
        var s = Flying();
        s.Velocity = new Vector3(0, -DashClimb, 0);
        s = Run(s, new Controls(Vector2.Zero, 0f, 1f, true, 0f), new Env(false, 3f), 1f);
        Assert.True(s.Velocity.Y > -(FlareSink + 3f * FlarePerMetre) - 0.01f, $"flared: {s.Velocity.Y}");
        // let go 2 m up: it does not hang there, it sinks onto its legs
        var low = Run(Flying(), new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(false, 2f), 1.5f);
        Assert.True(low.Velocity.Y < -1.9f);
        Assert.Equal(1f, low.Legs, 2);
    }

    [Fact]
    public void A_fast_touchdown_is_no_landing()
    {
        var s = Flying();
        s.Velocity = new Vector3(20f, -1f, 0f);
        s = Step(s, new Controls(Vector2.Zero, 0f, 0f, false, 0f), new Env(true, 0f), Dt);
        Assert.False(s.Landed);
    }

    private static float MathX_Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}

/// <summary>The tractor beam's cone and pull (#861, src/Player/TractorBeamMath.cs, linked in).</summary>
public class TractorBeamTests
{
    private static readonly Vector3 Apex = new(10f, 50f, -4f);

    [Fact]
    public void The_cone_widens_downward_and_ends_at_its_reach()
    {
        Assert.True(TractorBeamMath.InCone(Apex, Apex + Vector3.Down * 10f));
        Assert.True(TractorBeamMath.InCone(Apex, Apex + new Vector3(1.2f, -0.5f, 0)));
        Assert.False(TractorBeamMath.InCone(Apex, Apex + new Vector3(3f, -0.5f, 0)));
        // 40 m down it is ~16 m across
        Assert.True(TractorBeamMath.InCone(Apex, Apex + new Vector3(0, -40f, 15f)));
        Assert.False(TractorBeamMath.InCone(Apex, Apex + new Vector3(0, -40f, 18f)));
        Assert.False(TractorBeamMath.InCone(Apex, Apex + Vector3.Down * (TractorBeamMath.Reach + 1f)));
        Assert.False(TractorBeamMath.InCone(Apex, Apex + Vector3.Up));
    }

    [Fact]
    public void Pulls_up_and_in_then_holds_under_the_hatch()
    {
        var p = Apex + new Vector3(4f, -35f, 0f);
        float dt = 1f / 30f;
        for (float t = 0; t < 30f; t += dt) p += TractorBeamMath.Pull(Apex, p, Vector3.Zero, t) * dt;
        Assert.InRange(Apex.Y - p.Y, TractorBeamMath.HoldDepth - 0.4f, TractorBeamMath.HoldDepth + 0.4f);
        Assert.True(new Vector2(p.X - Apex.X, p.Z - Apex.Z).Length() < 0.1f);
        // the lift itself is gentle
        var v = TractorBeamMath.Pull(Apex, Apex + Vector3.Down * 30f, Vector3.Zero);
        Assert.InRange(v.Y, TractorBeamMath.LiftSpeed - 0.01f, TractorBeamMath.LiftSpeed + 0.01f);
    }

    [Fact]
    public void A_held_thing_travels_with_the_craft()
    {
        var craft = new Vector3(25f, 0f, 0f);
        var held = Apex + Vector3.Down * TractorBeamMath.HoldDepth;
        var v = TractorBeamMath.Pull(Apex, held, craft);
        Assert.InRange(v.X, 24.9f, 25.1f);
    }
}
