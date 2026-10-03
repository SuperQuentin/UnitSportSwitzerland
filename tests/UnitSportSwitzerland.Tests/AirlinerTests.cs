using Godot;
using UnitSport.Player;
using Xunit;
using Xunit.Abstractions;
using static UnitSport.Player.AirlinerFlight;

namespace UnitSportSwitzerland.Tests;

/// <summary>The heavy aircraft model (#414, src/Player/AirlinerFlight.cs and AirlinerCatalog.cs, linked in).</summary>
public class AirlinerTests
{
    private const float Dt = 1f / 60f;
    private const float Kt = 0.514444f;
    private readonly ITestOutputHelper _out;

    public AirlinerTests(ITestOutputHelper output) => _out = output;

    /// <summary>One aircraft over flat ground at <see cref="Field"/> m: position integrated as the body would carry it.</summary>
    private sealed class Sim
    {
        public const float Field = 400f;
        public readonly AirlinerSpec Spec;
        public State S;
        public Vector3 Pos;
        public bool Crashed;
        public float T;

        public Sim(AirlinerSpec spec, AirlinerHandling handling = AirlinerHandling.Arcade)
        {
            Spec = spec;
            S = Parked(spec, 0f);
            Handling = handling;
        }

        public AirlinerHandling Handling;

        public bool OnFloor => Pos.Y <= 0.001f;

        public void Step(Controls c)
        {
            var ev = AirlinerFlight.Step(Spec, ref S, c with { Handling = Handling }, new Env(OnFloor, Field + Pos.Y), Dt);
            if (ev == Event.Crashed) Crashed = true;
            Pos += S.Velocity * Dt;
            if (Pos.Y < 0f)
            {
                Pos.Y = 0f;
                if (S.Velocity.Y < 0f) S.Velocity = S.Velocity with { Y = 0f };
            }
            T += Dt;
        }

        public void Run(float seconds, System.Func<Sim, Controls> pilot)
        {
            for (float t = 0; t < seconds && !Crashed; t += Dt) Step(pilot(this));
        }

        /// <summary>In the air at <paramref name="height"/> m, level, at <paramref name="ias"/>, trimmed by thrust.</summary>
        public static Sim Airborne(AirlinerSpec spec, float height, float speed, int flaps = 0, bool gear = false, float lever = 0.6f,
            AirlinerHandling handling = AirlinerHandling.Arcade)
        {
            var sim = new Sim(spec, handling);
            sim.Pos = new Vector3(0, height, 0);
            sim.S.Velocity = Vector3.Forward * speed;
            sim.S.FlapLever = flaps;
            sim.S.Flaps = flaps;
            sim.S.GearDown = gear;
            sim.S.Gear = gear ? 1f : 0f;
            sim.S.OnGround = false;
            sim.S.Lever = lever;
            sim.S.Spool = spec.IdleSpool + (1f - spec.IdleSpool) * lever;
            // the angle of attack that carries it level at this speed
            var (cl0, _, _) = Polar(spec, flaps);
            float rho = Density(Field + height);
            float cl = 2f * sim.S.Mass * G / (rho * speed * speed * spec.WingArea);
            float alpha = (cl - cl0) / spec.LiftSlope;
            sim.S.Attitude = new Basis(Vector3.Right, alpha);
            return sim;
        }
    }

    private static float Bank(State s) => BankOf(s.Attitude);
    private static float Pitch(State s) => PitchOf(s.Attitude);

    [Fact]
    public void A320_stall_speeds_match_the_book()
    {
        var a = AirlinerCatalog.A320;
        Assert.InRange(a.StallSpeed(64000f, 0) / Kt, 138f, 152f);
        Assert.InRange(a.StallSpeed(64000f, 5) / Kt, 98f, 110f);
    }

    /// <summary>Full thrust, flaps 1+F, rotate at ~1.05 Vs: off the runway inside 1.6 km between 125 and 160 kt.</summary>
    private (float Roll, float Speed, Sim Sim) TakeOff(AirlinerSpec spec, int flaps)
    {
        var sim = new Sim(spec);
        sim.S.FlapLever = flaps;
        sim.S.Flaps = flaps;
        float vr = spec.StallSpeed(sim.S.Mass, flaps) * 1.05f;
        float roll = -1f, liftoff = 0f;
        sim.Run(90f, s =>
        {
            if (roll < 0f && !s.OnFloor) { roll = -s.Pos.Z; liftoff = s.S.Ias; }
            float pull = s.S.Ias > vr && Pitch(s.S) < 0.21f ? 0.7f : 0f;
            return new Controls(new Vector2(0, pull), LeverUp: 1f);
        });
        return (roll, liftoff, sim);
    }

    [Fact]
    public void A320_takes_off_and_climbs()
    {
        var (roll, speed, sim) = TakeOff(AirlinerCatalog.A320, 2);
        _out.WriteLine($"roll {roll:0} m at {speed / Kt:0} kt; after 90 s {sim.Pos.Y:0} m, {sim.S.Ias / Kt:0} kt, vs {sim.S.Velocity.Y:0.0}");
        Assert.False(sim.Crashed);
        Assert.InRange(roll, 900f, 1700f);
        Assert.InRange(speed / Kt, 125f, 165f);
        Assert.True(sim.Pos.Y > 400f, $"climbed {sim.Pos.Y:0} m");
        Assert.True(sim.S.Alpha < sim.S.AlphaStall);
    }

    [Theory]
    [InlineData("An124", 1)]
    [InlineData("Freighter", 1)]
    public void Freighters_take_off_inside_three_km(string which, int flaps)
    {
        var spec = which == "An124" ? AirlinerCatalog.An124 : AirlinerCatalog.Freighter;
        var (roll, speed, sim) = TakeOff(spec, flaps);
        _out.WriteLine($"{which}: roll {roll:0} m at {speed / Kt:0} kt; after 90 s {sim.Pos.Y:0} m");
        Assert.False(sim.Crashed);
        Assert.InRange(roll, 400f, 3000f);
        Assert.True(sim.Pos.Y > 150f);
    }

    [Fact]
    public void Hands_off_holds_the_flight_path()
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 3000f, 130f);
        float h0 = sim.Pos.Y;
        float worst = 0f;
        sim.Run(90f, s =>
        {
            worst = Mathf.Max(worst, Mathf.Abs(s.S.Velocity.Y));
            return new Controls();
        });
        _out.WriteLine($"Δh {sim.Pos.Y - h0:0} m, worst vs {worst:0.0} m/s, {sim.S.Ias / Kt:0} kt, pitch {Mathf.RadToDeg(Pitch(sim.S)):0.0}°");
        Assert.InRange(sim.Pos.Y - h0, -150f, 150f);
        Assert.InRange(Mathf.Abs(sim.S.Velocity.Y), 0f, 2.5f);
        Assert.InRange(Mathf.Abs(Bank(sim.S)), 0f, 0.02f);
    }

    [Fact]
    public void Pulling_climbs_and_letting_go_holds_the_climb()
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 2000f, 130f, lever: 0.9f);
        sim.Run(4f, _ => new Controls(new Vector2(0, 0.6f)));
        float gamma = Mathf.Asin(sim.S.Velocity.Y / sim.S.Velocity.Length());
        sim.Run(15f, _ => new Controls());
        float after = Mathf.Asin(sim.S.Velocity.Y / sim.S.Velocity.Length());
        _out.WriteLine($"path {Mathf.RadToDeg(gamma):0.0}° then {Mathf.RadToDeg(after):0.0}°");
        Assert.True(gamma > 0.05f);
        Assert.InRange(after, gamma - 0.05f, gamma + 0.05f);
    }

    [Fact]
    public void Sim_law_holds_the_bank_and_turns_level()
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 2000f, 130f, handling: AirlinerHandling.Sim);
        float h0 = sim.Pos.Y;
        sim.Run(2f, _ => new Controls(new Vector2(1f, 0)));
        float yaw0 = sim.S.Yaw;
        sim.Run(30f, _ => new Controls());
        float bank = Bank(sim.S);
        float turned = Mathf.AngleDifference(yaw0, sim.S.Yaw);
        _out.WriteLine($"bank {Mathf.RadToDeg(bank):0.0}°, turned {Mathf.RadToDeg(turned):0}°, Δh {sim.Pos.Y - h0:0} m");
        Assert.InRange(bank, 0.3f, 0.6f);
        Assert.True(turned < -0.5f, "a right bank turns right (yaw falls)");
        Assert.InRange(sim.Pos.Y - h0, -120f, 120f);
    }

    [Fact]
    public void Arcade_rolls_wings_level_when_let_go()
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 2000f, 130f);
        sim.Run(3f, _ => new Controls(new Vector2(1f, 0)));
        Assert.True(Bank(sim.S) > 0.4f);
        sim.Run(25f, _ => new Controls());
        Assert.InRange(Mathf.Abs(Bank(sim.S)), 0f, 0.05f);
    }

    [Fact]
    public void The_angle_of_attack_is_protected()
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 2000f, 110f, lever: 0f);
        float worst = 0f;
        sim.Run(40f, s =>
        {
            worst = Mathf.Max(worst, s.S.Alpha - s.S.AlphaStall);
            return new Controls(new Vector2(0, 1f));
        });
        _out.WriteLine($"alpha - stall worst {Mathf.RadToDeg(worst):0.0}°, lever {sim.S.Lever:0.00}, {sim.S.Ias / Kt:0} kt");
        Assert.True(worst < 0.01f);
        Assert.Equal(1f, sim.S.Lever);   // alpha floor
    }

    [Fact]
    public void A320_lands_and_stops()
    {
        var spec = AirlinerCatalog.A320;
        float vref = spec.StallSpeed(spec.OperatingMass, 5) * 1.23f;
        var sim = Sim.Airborne(spec, 300f, vref, flaps: 5, gear: true, lever: 0.45f);
        // a 3° path: the stick puts the nose down to it, then the law holds it
        sim.S.Velocity = new Vector3(0, -vref * 0.0523f, -vref);
        sim.S.PathTarget = -0.0523f;
        float touchdown = 0f, sink = 0f;
        bool flared = false;
        sim.Run(200f, s =>
        {
            if (s.OnFloor && touchdown == 0f) { touchdown = s.Pos.Z; sink = s.S.LastSink; }
            if (touchdown != 0f)
                return new Controls(Brake: 1f, LeverDown: s.S.Ias > 40f ? 1f : 0f);
            // a speed hold on the levers, the flare at 12 m
            float lever = s.S.Ias < vref ? 0.15f : -0.15f;
            if (s.Pos.Y < 12f) flared = true;
            return new Controls(new Vector2(0, flared && s.S.Velocity.Y < -1.0f ? 0.4f : 0f), LeverUp: lever > 0 ? lever : 0f, LeverDown: flared ? 1f : lever < 0 ? -lever : 0f);
        });
        float rollout = touchdown - sim.Pos.Z;
        _out.WriteLine($"sink {sink:0.00} m/s, rollout {rollout:0} m, speed {sim.S.Velocity.Length():0.0}, damage {sim.S.Damage:0}");
        Assert.False(sim.Crashed);
        Assert.True(touchdown != 0f);
        Assert.InRange(sink, 0f, spec.HardLanding);
        Assert.Equal(0f, sim.S.Damage);
        Assert.InRange(rollout, 400f, 1800f);
        Assert.True(sim.S.Velocity.Length() < 0.5f);
    }

    [Theory]
    [InlineData(5.5f, false, false)]
    [InlineData(9f, false, true)]
    public void Hard_landings_break_the_gear_or_crash(float sink, bool _, bool crash)
    {
        var sim = Sim.Airborne(AirlinerCatalog.A320, 0.5f, 70f, flaps: 5, gear: true, lever: 0f);
        sim.S.Velocity = new Vector3(0, -sink, -70f);
        sim.Run(2f, _ => new Controls());
        Assert.Equal(crash, sim.Crashed);
        if (!crash) Assert.True(sim.S.GearBroken);
    }

    [Fact]
    public void The_gear_stays_down_on_the_ground()
    {
        var sim = new Sim(AirlinerCatalog.A320);
        sim.Step(new Controls(GearToggle: true));
        sim.Run(12f, _ => new Controls());
        Assert.True(sim.S.GearDown);
        Assert.Equal(1f, sim.S.Gear);
    }

    [Fact]
    public void Nobody_aboard_it_stays_put()
    {
        var sim = new Sim(AirlinerCatalog.A320);
        sim.S.Lever = 0.3f;
        sim.Run(20f, _ => new Controls(NoPilot: true));
        Assert.True(sim.Pos.Length() < 0.05f, $"rolled {sim.Pos.Length():0.00} m");
    }

    [Fact]
    public void Idle_thrust_taxis_and_the_tiller_turns_tight()
    {
        var sim = new Sim(AirlinerCatalog.A320);
        sim.Run(40f, _ => new Controls(LeverUp: 0f));
        float taxi = sim.S.Velocity.Length();
        float yaw0 = sim.S.Yaw;
        sim.Run(10f, _ => new Controls(new Vector2(-1f, 0)));
        _out.WriteLine($"idle taxi {taxi:0.0} m/s, turned {Mathf.RadToDeg(sim.S.Yaw - yaw0):0}°");
        Assert.InRange(taxi, 1f, 15f);
        Assert.True(sim.S.Yaw - yaw0 > 0.5f, "stick left turns left");
    }
}
