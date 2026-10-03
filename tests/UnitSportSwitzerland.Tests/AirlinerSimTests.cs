using Godot;
using UnitSport.Player;
using Xunit;
using Xunit.Abstractions;
using static UnitSport.Player.AirlinerFlight;

namespace UnitSportSwitzerland.Tests;

/// <summary>The airliner's light-sim layer (#415): engine start, fuel, autopilot, trim, limits.</summary>
public class AirlinerSimTests
{
    private const float Dt = 1f / 60f;
    private const float Kt = 0.514444f;
    private const float Field = 400f;
    private readonly ITestOutputHelper _out;

    public AirlinerSimTests(ITestOutputHelper output) => _out = output;

    private static Controls Sim(Controls c) => c with { Handling = AirlinerHandling.Sim };

    /// <summary>Steps one aircraft over flat ground, integrating its position as the body would.</summary>
    private static Event Run(AirlinerSpec spec, ref State s, ref Vector3 pos, float seconds, System.Func<State, Vector3, Controls> pilot)
    {
        for (float t = 0; t < seconds; t += Dt)
        {
            bool floor = pos.Y <= 0.001f;
            var ev = Step(spec, ref s, Sim(pilot(s, pos)), new Env(floor, Field + pos.Y), Dt);
            if (ev != Event.None) return ev;
            pos += s.Velocity * Dt;
            if (pos.Y < 0f) { pos.Y = 0f; if (s.Velocity.Y < 0f) s.Velocity = s.Velocity with { Y = 0f }; }
        }
        return Event.None;
    }

    private static State Airborne(AirlinerSpec spec, float speed, float lever)
    {
        var s = Parked(spec, 0f);
        s.Velocity = Vector3.Forward * speed;
        s.OnGround = false;
        s.GearDown = false;
        s.Gear = 0f;
        s.Lever = lever;
        s.Spool = spec.IdleSpool + (1f - spec.IdleSpool) * lever;
        var (cl0, _, _) = Polar(spec, 0);
        float cl = 2f * s.Mass * G / (Density(Field + 2000f) * speed * speed * spec.WingArea);
        s.Attitude = new Basis(Vector3.Right, (cl - cl0) / spec.LiftSlope);
        s.TrimAlpha = (cl - cl0) / spec.LiftSlope;
        return s;
    }

    [Fact]
    public void Cold_start_takes_the_APU_then_each_engine()
    {
        var spec = AirlinerCatalog.A320;
        var s = Parked(spec, 0f, enginesRunning: false);
        var pos = Vector3.Zero;
        Run(spec, ref s, ref pos, 5f, (_, _) => new Controls());
        Assert.Equal(0f, s.Spool);
        bool toggled = false;
        float firstLit = -1f, allLit = -1f;
        for (float t = 0f; t < 120f; t += Dt)
        {
            var c = new Controls(StartToggle: !toggled);
            toggled = true;
            Step(spec, ref s, Sim(c), new Env(true, Field), Dt);
            if (firstLit < 0f && s.Lit >= 1f) firstLit = t;
            if (allLit < 0f && s.Lit >= spec.Engines) allLit = t;
        }
        _out.WriteLine($"first engine {firstLit:0} s, all {allLit:0} s, spool {s.Spool:0.00}, apu {s.Apu:0.0}");
        Assert.InRange(firstLit, ApuTime + spec.EngineStartTime - 1f, ApuTime + spec.EngineStartTime + 1f);
        Assert.InRange(allLit, ApuTime + 2 * spec.EngineStartTime - 1f, ApuTime + 2 * spec.EngineStartTime + 1f);
        Assert.InRange(s.Spool, spec.IdleSpool - 0.01f, spec.IdleSpool + 0.01f);
        Assert.Equal(0f, s.Apu);
        // shut down: no thrust, the spool winds down
        Step(spec, ref s, Sim(new Controls(StartToggle: true)), new Env(true, Field), Dt);
        for (float u = 0; u < 10f; u += Dt) Step(spec, ref s, Sim(new Controls()), new Env(true, Field), Dt);
        Assert.Equal(0f, s.Lit);
        Assert.True(s.Spool < 0.05f);
    }

    [Fact]
    public void Take_off_thrust_burns_about_eight_tonnes_an_hour()
    {
        var spec = AirlinerCatalog.A320;
        var s = Parked(spec, 0f);
        s.ParkingBrake = true;
        float fuel0 = s.Fuel, mass0 = s.Mass;
        var pos = Vector3.Zero;
        Run(spec, ref s, ref pos, 60f, (_, _) => new Controls(LeverUp: 1f));
        float perHour = (fuel0 - s.Fuel) * 60f;
        _out.WriteLine($"{perHour:0} kg/h at N1 {s.Spool:0.00}");
        Assert.InRange(perHour, 6000f, 9500f);
        Assert.InRange(mass0 - s.Mass, fuel0 - s.Fuel - 5f, fuel0 - s.Fuel + 5f);
    }

    [Fact]
    public void Out_of_fuel_the_engines_flame_out_and_it_refuels_stopped()
    {
        var spec = AirlinerCatalog.A320;
        var s = Parked(spec, 0f);
        s.Fuel = 1f;
        s.ParkingBrake = true;
        var pos = Vector3.Zero;
        Run(spec, ref s, ref pos, 5f, (_, _) => new Controls());
        Assert.Equal(0f, s.Lit);
        Run(spec, ref s, ref pos, 20f, (_, _) => new Controls());
        Assert.True(s.Fuel > 2000f);
    }

    [Fact]
    public void Autopilot_holds_altitude_and_turns_to_a_new_heading()
    {
        var spec = AirlinerCatalog.A320;
        var s = Airborne(spec, 130f, 0.6f);
        var pos = new Vector3(0, 2000f, 0);
        bool engaged = false;
        float yaw0 = s.Yaw;
        Run(spec, ref s, ref pos, 30f, (_, _) =>
        {
            var c = new Controls(AutopilotToggle: !engaged);
            engaged = true;
            return c;
        });
        Assert.True(s.Autopilot);
        float alt0 = s.ApAltitude;
        // the stick right for 2 s turns the selected heading ~40° right
        Run(spec, ref s, ref pos, 2f, (_, _) => new Controls(new Vector2(0.9f, 0)));
        float wanted = s.ApHeading;
        float worst = 0f;
        Run(spec, ref s, ref pos, 90f, (_, p) =>
        {
            worst = Mathf.Max(worst, Mathf.Abs(Field + p.Y - alt0));
            return new Controls();
        });
        float turned = Mathf.AngleDifference(yaw0, s.Yaw);
        _out.WriteLine($"turned {Mathf.RadToDeg(turned):0}° (selected {Mathf.RadToDeg(Mathf.AngleDifference(yaw0, wanted)):0}°), worst altitude error {worst:0} m, {s.Ias / Kt:0} kt for {s.ApSpeed / Kt:0}");
        Assert.InRange(Mathf.AngleDifference(s.Yaw, wanted), -0.03f, 0.03f);
        Assert.True(turned < -0.4f);
        Assert.InRange(worst, 0f, 40f);
        Assert.InRange(s.Ias, s.ApSpeed - 3f, s.ApSpeed + 3f);
    }

    [Fact]
    public void Autopilot_climbs_to_a_selected_altitude_and_a_hard_stick_disconnects_it()
    {
        var spec = AirlinerCatalog.A320;
        var s = Airborne(spec, 130f, 0.6f);
        var pos = new Vector3(0, 2000f, 0);
        Step(spec, ref s, Sim(new Controls(AutopilotToggle: true)), new Env(false, Field + pos.Y), Dt);
        // pull 0.9 for 15 s: +270 m selected
        Run(spec, ref s, ref pos, 15f, (_, _) => new Controls(new Vector2(0, 0.9f)));
        float target = s.ApAltitude;
        Run(spec, ref s, ref pos, 120f, (_, _) => new Controls());
        _out.WriteLine($"selected {target - Field:0} m, at {pos.Y:0} m");
        Assert.InRange(Field + pos.Y, target - 15f, target + 15f);
        Run(spec, ref s, ref pos, 1.5f, (_, _) => new Controls(new Vector2(1f, 0)));
        Assert.False(s.Autopilot);
    }

    [Fact]
    public void A_conventional_type_returns_to_its_trimmed_angle_hands_off()
    {
        var spec = AirlinerCatalog.An124;
        var s = Airborne(spec, 120f, 0.55f);
        var pos = new Vector3(0, 2000f, 0);
        // pull for 4 s and let go: it does not hold the new path, it noses back toward the trim
        Run(spec, ref s, ref pos, 4f, (_, _) => new Controls(new Vector2(0, 0.6f)));
        float alphaPulled = s.Alpha;
        Run(spec, ref s, ref pos, 40f, (_, _) => new Controls());
        _out.WriteLine($"alpha pulled {Mathf.RadToDeg(alphaPulled):0.0}°, after {Mathf.RadToDeg(s.Alpha):0.0}°, trim {Mathf.RadToDeg(s.TrimAlpha):0.0}°");
        Assert.InRange(s.Alpha, s.TrimAlpha - 0.02f, s.TrimAlpha + 0.02f);
        // trimming nose up raises the angle it holds
        float before = s.TrimAlpha;
        Run(spec, ref s, ref pos, 2f, (_, _) => new Controls(Trim: 1f));
        Assert.True(s.TrimAlpha > before + 0.03f);
    }

    [Fact]
    public void A_conventional_type_stalls_in_sim()
    {
        var spec = AirlinerCatalog.An124;
        var s = Airborne(spec, 95f, 0f);
        var pos = new Vector3(0, 2000f, 0);
        float worst = 0f;
        Run(spec, ref s, ref pos, 20f, (st, _) =>
        {
            worst = Mathf.Max(worst, st.Alpha - st.AlphaStall);
            return new Controls(new Vector2(0, 1f));
        });
        Assert.True(worst > 0.01f, "past the stall");
        Assert.True(s.Lever < 0.5f, "no alpha floor");
    }

    [Fact]
    public void Flaps_flown_past_their_speed_are_damaged()
    {
        var spec = AirlinerCatalog.A320;
        var s = Airborne(spec, 150f, 0.8f);
        s.FlapLever = 5;
        s.Flaps = 5;
        var pos = new Vector3(0, 2000f, 0);
        Run(spec, ref s, ref pos, 5f, (_, _) => new Controls());
        Assert.True(s.Damage > 10f);
    }
}
