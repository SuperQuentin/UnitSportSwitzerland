using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>How an airliner flies (Settings -> Vehicles, <c>--airliner arcade|sim</c>, #414/#415).</summary>
public enum AirlinerHandling : byte { Arcade, Sim }

/// <summary>
/// A heavy aircraft's motion (#414): pure maths over Godot's managed types, no engine calls, so tier 0
/// tests it (tests/…/AirlinerTests). <see cref="Airliner"/> wraps it as a <see cref="Flyer"/>.
///
/// <para>
/// A point mass with a real polar: lift perpendicular to the airflow along the wings' up, drag along
/// it, thrust along the nose, gravity. The nose weathercocks into the airflow, so the heading follows
/// the bank by itself, as a real aircraft's does. The stick is a fly-by-wire rate command, the
/// A320's normal law: it moves the flight path, and let go it holds it (and the bank, up to 33°),
/// with protections on the angle of attack, the pitch, the bank and the speed. Arcade adds wings
/// level when the stick is let go and keeps the protections on every type.
/// </para>
///
/// <para>
/// Frames, as every craft (<see cref="Flyer"/>): the attitude's X is the right wing, Y up, −Z the
/// nose; the yaw's nose is (−sin, 0, −cos). The body that carries it stands on the main gear.
/// </para>
/// </summary>
public static class AirlinerFlight
{
    public const float G = 9.81f, SeaLevelDensity = 1.225f;

    /// <summary>ISA air density at an altitude above sea level, kg/m³.</summary>
    public static float Density(float altitude) =>
        SeaLevelDensity * Mathf.Pow(Mathf.Max(1f - 2.25577e-5f * altitude, 0.05f), 4.2559f);

    /// <summary>What the pilot does this step. Edges (<c>…Toggle</c>, <see cref="FlapsDelta"/>) are presses, not holds.</summary>
    /// <param name="Stick">x right, y back (pull, nose up), up to length 1.</param>
    /// <param name="LeverUp">Thrust levers forward, 0..1 (held).</param>
    /// <param name="LeverDown">Thrust levers back, 0..1; held at idle on the ground: reverse thrust.</param>
    /// <param name="Brake">Wheel brakes, 0..1.</param>
    /// <param name="EnginesOff">The engines are switched off (inverted so <c>new Controls()</c> is a pilot with engines on).</param>
    /// <param name="NoPilot">Nobody at the controls: brakes set, stick centred.</param>
    /// <param name="StartToggle">Sim (#415): start the engines from cold (battery, APU, one engine after the other), or shut them down.</param>
    /// <param name="AutopilotToggle">Sim (#415): engage the autopilot and autothrust on what it is flying now, or disengage them.</param>
    /// <param name="Trim">Sim, conventional types (#415): pitch trim held, −1 nose down .. +1 nose up.</param>
    public readonly record struct Controls(
        Vector2 Stick = default,
        float LeverUp = 0f,
        float LeverDown = 0f,
        float Brake = 0f,
        int FlapsDelta = 0,
        bool GearToggle = false,
        bool SpeedbrakeCycle = false,
        bool ParkingToggle = false,
        bool EnginesOff = false,
        bool NoPilot = false,
        AirlinerHandling Handling = AirlinerHandling.Arcade,
        bool StartToggle = false,
        bool AutopilotToggle = false,
        float Trim = 0f);

    /// <param name="OnFloor">The body stands on something (runway, grass, a roof).</param>
    /// <param name="Altitude">Above sea level, m: the air's density.</param>
    public readonly record struct Env(bool OnFloor, float Altitude = 500f);

    public enum Event : byte { None, Crashed }

    public struct State
    {
        public Vector3 Velocity;
        public Basis Attitude;
        public float Yaw;
        public float Mass;

        /// <summary>Thrust levers 0..1; reverse 0..1 (deployed and the reverse thrust asked, together).</summary>
        public float Lever, Reverse;
        /// <summary>Engine spool, N1 share: idle ~0.22, take-off 1.</summary>
        public float Spool;

        /// <summary>The flap lever's setting, and where the flaps are (settings, moving toward it).</summary>
        public int FlapLever;
        public float Flaps;
        /// <summary>Gear lever down, where the gear is (0 up .. 1 down and locked), and whether it has given way.</summary>
        public bool GearDown;
        public float Gear;
        public bool GearBroken;
        /// <summary>Speedbrake lever 0 / ½ / full (0..2), spoilers' deflection 0..1.</summary>
        public int SpeedBrake;
        public float Spoilers;
        public bool ParkingBrake;
        /// <summary>The brakes applied last step, 0..1, for the sound and the others.</summary>
        public float Braking;

        /// <summary>The stick as the pilot holds it (x right, y back): what the drawn surfaces and the sidestick show.</summary>
        public Vector2 Stick;
        /// <summary>Body pitch and roll rates, rad/s: the stick asks for them, the airframe gets there.</summary>
        public float PitchRate, RollRate;
        /// <summary>The flight path the law holds hands-off, rad.</summary>
        public float PathTarget;
        public float LastGamma;

        public bool OnGround;

        // ---- light sim (#415) ----
        /// <summary>Systems powered (battery on), the APU's run-up 0..1, engines running (a count, fractional while one starts).</summary>
        public bool Battery;
        public float Apu, Lit;
        /// <summary>Starting (true) or shutting down: where <see cref="Lit"/> is heading.</summary>
        public bool Starting;
        /// <summary>Fuel aboard, kg (part of <see cref="Mass"/>).</summary>
        public float Fuel;
        /// <summary>Autopilot and autothrust engaged, and what they hold: heading (yaw), altitude above sea level, indicated airspeed.</summary>
        public bool Autopilot;
        public float ApHeading, ApAltitude, ApSpeed;
        /// <summary>Seconds the stick has been held hard over with the autopilot on: past 1 s it disconnects.</summary>
        public float ApOverride;
        /// <summary>The angle of attack the trim holds hands-off (conventional types in Sim).</summary>
        public float TrimAlpha;
        /// <summary>Sink last airborne step, m/s (positive down): what the touchdown is judged by.</summary>
        public float LastSink;
        /// <summary>Damage not yet taken by the vehicle's health (<see cref="Airliner.TakeDamage"/>).</summary>
        public float Damage;
        /// <summary>Seconds left in which a touchdown is only settling onto the wheels (just put in the world), not a landing.</summary>
        public float Settle;

        // ---- read-outs, written each step ----
        public float Alpha, Tas, Ias, Thrust;
        /// <summary>The angle of attack at which the wing stalls in this configuration, rad.</summary>
        public float AlphaStall;
    }

    /// <summary>A fresh aircraft standing on its wheels, heading <paramref name="yaw"/>, levers at idle, flaps up, engines at idle.</summary>
    public static State Parked(AirlinerSpec spec, float yaw, bool enginesRunning = true) => new()
    {
        Attitude = new Basis(Vector3.Up, yaw),
        Yaw = yaw,
        Mass = spec.OperatingMass,
        Spool = enginesRunning ? spec.IdleSpool : 0f,
        GearDown = true,
        Gear = 1f,
        OnGround = true,
        Battery = enginesRunning,
        Lit = enginesRunning ? spec.Engines : 0f,
        Starting = enginesRunning,
        Fuel = spec.FuelCapacity * SpawnFuel,
        TrimAlpha = TakeoffTrim,
    };

    /// <summary>Share of the tanks full at spawn (part of the operating mass), and the trim a cold aircraft is left at.</summary>
    public const float SpawnFuel = 0.45f, TakeoffTrim = 0.09f;

    /// <summary>Seconds the APU takes to come up (#415).</summary>
    public const float ApuTime = 10f;

    public static Vector3 Heading(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));

    /// <summary>Pitch, + nose up, rad.</summary>
    public static float PitchOf(Basis att) => Mathf.Asin(Mathf.Clamp(-att.Z.Y, -1f, 1f));

    /// <summary>Bank, + right wing down, rad.</summary>
    public static float BankOf(Basis att) => -Mathf.Asin(Mathf.Clamp(att.X.Y, -1f, 1f));

    /// <summary>The polar at a flap position (settings, fractional while they move): lift at zero alpha, the stall's lift, the flaps' drag.</summary>
    public static (float Cl0, float ClMax, float Cd) Polar(AirlinerSpec spec, float flaps)
    {
        int n = spec.FlapSettings - 1;
        float f = Mathf.Clamp(flaps, 0f, n);
        int i = Mathf.Min((int)f, n - 1);
        float t = f - i;
        if (n == 0) return (spec.FlapCl0[0], spec.FlapClMax[0], spec.FlapCd[0]);
        return (Mathf.Lerp(spec.FlapCl0[i], spec.FlapCl0[i + 1], t),
            Mathf.Lerp(spec.FlapClMax[i], spec.FlapClMax[i + 1], t),
            Mathf.Lerp(spec.FlapCd[i], spec.FlapCd[i + 1], t));
    }

    /// <summary>
    /// The lift coefficient at an angle of attack: linear to the stall, then it falls away (a third of
    /// the slope back) to half the stall's lift; the same mirrored below a negative stall.
    /// </summary>
    public static float LiftCoefficient(AirlinerSpec spec, float cl0, float clMax, float alpha)
    {
        float alphaStall = (clMax - cl0) / spec.LiftSlope;
        float alphaNeg = -0.6f * alphaStall;
        if (alpha <= alphaStall && alpha >= alphaNeg) return cl0 + spec.LiftSlope * alpha;
        if (alpha > alphaStall)
            return Mathf.Max(clMax - (alpha - alphaStall) * spec.LiftSlope * 0.35f, 0.5f * clMax);
        float clNeg = cl0 + spec.LiftSlope * alphaNeg;
        return Mathf.Min(clNeg + (alphaNeg - alpha) * spec.LiftSlope * 0.35f, 0.5f * clNeg);
    }

    /// <summary>Thrust share of one engine at a spool (idle a few percent, full 1).</summary>
    public static float ThrustShare(AirlinerSpec spec, float spool)
    {
        float lo = spec.IdleSpool * 0.8f;
        return Mathf.Pow(Mathf.Clamp((spool - lo) / (1f - lo), 0f, 1f), 1.1f);
    }

    private const float BankHold = 0.576f;     // 33°: hands-off the law keeps the bank up to here
    private const float BankLimit = 1.17f;     // 67°
    private const float PitchUp = 0.52f, PitchDown = -0.26f;   // +30° / −15°
    private const float PathGain = 1.0f, PathDamping = 1.4f;

    public static Event Step(AirlinerSpec spec, ref State s, in Controls c, in Env env, float dt)
    {
        if (s.Attitude == default) s.Attitude = new Basis(Vector3.Up, s.Yaw);
        if (s.Mass <= 0f) s.Mass = spec.OperatingMass;
        bool arcade = c.Handling == AirlinerHandling.Arcade;
        bool protect = arcade || spec.FlyByWire;
        var stick = !c.NoPilot ? c.Stick : Vector2.Zero;
        s.Stick = stick;

        // ---- levers and systems ----------------------------------------------------------------
        if (!c.NoPilot) s.Lever = Mathf.Clamp(s.Lever + (c.LeverUp - c.LeverDown) * 0.5f * dt, 0f, 1f);
        bool wheels = s.Gear > 0.95f && !s.GearBroken;
        bool reverseAsked = !c.NoPilot && env.OnFloor && wheels && s.Lever <= 0.001f && c.LeverDown > 0.5f;
        s.Reverse = Mathf.MoveToward(s.Reverse, reverseAsked ? 1f : 0f, (reverseAsked ? 1.4f : 1f) * dt);

        bool sim = c.Handling == AirlinerHandling.Sim;
        bool running = sim ? Systems(spec, ref s, c, dt) : !c.EnginesOff;
        if (!sim) s.Lit = running ? spec.Engines : 0f;
        float idle = spec.IdleSpool;
        float spoolTarget = running ? idle + (1f - idle) * Mathf.Max(s.Lever, s.Reverse) : 0f;
        float spoolRate = running ? (1f - idle) / spec.SpoolTime : 0.06f;
        if (spoolTarget < s.Spool && running) spoolRate *= 1.3f;
        // a turbine cold below idle takes its time to light up to it
        if (running && s.Spool < idle) spoolRate = idle / 20f;
        s.Spool = Mathf.MoveToward(s.Spool, spoolTarget, spoolRate * dt);

        if (c.FlapsDelta != 0) s.FlapLever = Mathf.Clamp(s.FlapLever + c.FlapsDelta, 0, spec.FlapSettings - 1);
        s.Flaps = Mathf.MoveToward(s.Flaps, s.FlapLever, spec.FlapRate * dt);

        // weight on the wheels locks the lever down: nobody retracts the gear on the ground
        if (c.GearToggle && !(env.OnFloor && s.GearDown)) s.GearDown = !s.GearDown;
        if (!s.GearBroken) s.Gear = Mathf.MoveToward(s.Gear, s.GearDown ? 1f : 0f, dt / spec.GearTransit);

        if (c.SpeedbrakeCycle) s.SpeedBrake = (s.SpeedBrake + 1) % 3;
        if (c.ParkingToggle) s.ParkingBrake = !s.ParkingBrake;
        float brake = c.NoPilot || s.ParkingBrake ? 1f : Mathf.Clamp(c.Brake, 0f, 1f);
        s.Braking = env.OnFloor && wheels ? brake : 0f;

        var v = s.Velocity;
        float speed = v.Length();
        // ground spoilers: rolling with the levers at idle they stand up and kill the lift
        bool groundSpoilers = env.OnFloor && wheels && s.Lever < 0.05f && speed > 15f;
        float spoilerTarget = groundSpoilers ? 1f : s.SpeedBrake * 0.5f;
        s.Spoilers = Mathf.MoveToward(s.Spoilers, spoilerTarget, 1.5f * dt);

        // ---- the air ---------------------------------------------------------------------------
        float rho = Density(env.Altitude);
        var att = s.Attitude.Orthonormalized();
        var vb = att.Transposed() * v;
        float alpha = speed > 1f ? Mathf.Atan2(-vb.Y, -vb.Z) : 0f;
        float beta = speed > 1f ? Mathf.Atan2(vb.X, -vb.Z) : 0f;
        float q = 0.5f * rho * speed * speed;
        var (cl0, clMax, cdFlaps) = Polar(spec, s.Flaps);
        s.AlphaStall = (clMax - cl0) / spec.LiftSlope;
        float cl = LiftCoefficient(spec, cl0, clMax, alpha) - spec.SpoilerCl * s.Spoilers;
        float cd = spec.Cd0 + spec.InducedK * cl * cl + cdFlaps + spec.GearCd * s.Gear + spec.SpoilerCd * s.Spoilers;

        float lapse = Mathf.Max(0.35f, 1f - spec.ThrustLapse * speed) * Mathf.Pow(rho / SeaLevelDensity, 0.7f);
        // only the engines running push (one still starting does not yet)
        float lit = sim ? Mathf.Floor(s.Lit + 0.001f) : spec.Engines;
        float thrust = lit * spec.StaticThrust * ThrustShare(spec, s.Spool) * lapse
            * Mathf.Lerp(1f, -spec.ReverseShare, s.Reverse);
        s.Thrust = thrust;
        if (sim) Burn(spec, ref s, thrust, lit, env, speed, dt);

        var flow = speed > 0.5f ? v / speed : -att.Z;
        var liftDir = att.Y - flow * att.Y.Dot(flow);
        liftDir = liftDir.LengthSquared() > 1e-6f ? liftDir.Normalized() : att.Y;
        var force = liftDir * (q * spec.WingArea * cl) - flow * (q * spec.WingArea * cd) - att.Z * thrust;
        var accel = force / s.Mass + Vector3.Down * G;

        s.Alpha = alpha;
        s.Tas = speed;
        s.Ias = speed * Mathf.Sqrt(rho / SeaLevelDensity);
        var ev = Event.None;

        s.Settle = Mathf.Max(0f, s.Settle - dt);
        if (env.OnFloor)
        {
            if (!s.OnGround && s.Settle <= 0f) ev = Touchdown(spec, ref s, att);
            s.OnGround = true;
            wheels = s.Gear > 0.95f && !s.GearBroken;
            Roll(spec, ref s, c, stick, force, accel, wheels, brake, q, protect, dt);
        }
        else
        {
            if (s.OnGround) s.PathTarget = Gamma(v);
            s.OnGround = false;
            s.LastSink = -v.Y;
            s.Velocity = v + accel * dt;
            if (sim) Limits(spec, ref s, dt);
            Fly(spec, ref s, c, stick, alpha, beta, q, protect, arcade, env.Altitude, dt);
        }
        return ev;
    }

    /// <summary>Trim wheel's travel, rad of trimmed alpha a second; static stability, 1/s.</summary>
    private const float TrimRate = 0.02f, PitchStability = 0.7f;

    /// <summary>
    /// Battery, APU and engine start (#415, Sim). The start toggle from cold: battery on, the APU comes
    /// up (<see cref="ApuTime"/>), then the engines light one after the other
    /// (<see cref="AirlinerSpec.EngineStartTime"/> each). Toggled again: they shut down, the APU off.
    /// Out of fuel, they flame out. Returns whether any engine runs.
    /// </summary>
    private static bool Systems(AirlinerSpec spec, ref State s, in Controls c, float dt)
    {
        if (c.StartToggle)
        {
            s.Starting = !s.Starting;
            if (s.Starting) s.Battery = true;
        }
        bool fuel = s.Fuel > 0f;
        if (s.Starting && fuel)
        {
            // the APU first, then its bleed air spins up each engine in turn; all running, it is not needed
            bool allLit = s.Lit >= spec.Engines;
            s.Apu = Mathf.MoveToward(s.Apu, allLit ? 0f : 1f, dt / ApuTime);
            if (s.Apu >= 1f && !allLit) s.Lit = Mathf.MoveToward(s.Lit, spec.Engines, dt / spec.EngineStartTime);
        }
        else
        {
            s.Lit = 0f;
            s.Apu = Mathf.MoveToward(s.Apu, 0f, dt / ApuTime);
            if (!fuel) s.Starting = false;
        }
        return s.Lit >= 1f;
    }

    /// <summary>Fuel burnt (#415, Sim) by the running engines from their thrust; refuelled stopped on the ground with them off.</summary>
    private static void Burn(AirlinerSpec spec, ref State s, float thrust, float lit, in Env env, float speed, float dt)
    {
        if (lit > 0f)
        {
            float perEngine = Mathf.Abs(thrust) / lit;
            float burn = Mathf.Min(lit * (spec.IdleFuelFlow + spec.FuelPerNewton * perEngine) * dt, s.Fuel);
            s.Fuel -= burn;
            s.Mass -= burn;
        }
        else if (env.OnFloor && speed < 0.5f && s.Fuel < spec.FuelCapacity)
        {
            float add = Mathf.Min(RefuelRate * dt, spec.FuelCapacity - s.Fuel);
            s.Fuel += add;
            s.Mass += add;
        }
    }

    /// <summary>Refuelling stopped with the engines off, kg/s (#415).</summary>
    public const float RefuelRate = 150f;

    /// <summary>Flying the flaps or the gear past their speeds (#415, Sim): the airframe takes it.</summary>
    private static void Limits(AirlinerSpec spec, ref State s, float dt)
    {
        int notch = Mathf.Clamp(Mathf.CeilToInt(s.Flaps - 0.05f), 0, spec.FlapSettings - 1);
        float over = s.Ias - spec.FlapLimit[notch] - 2.5f;
        if (notch > 0 && over > 0f) s.Damage += over * 0.8f * dt;
        if (s.Gear > 0.05f && s.Ias > spec.GearLimit + 2.5f) s.Damage += (s.Ias - spec.GearLimit - 2.5f) * 0.8f * dt;
    }

    /// <summary>
    /// The autopilot and autothrust (#415, Sim): engaged on what it is flying, it holds a heading, an
    /// altitude and a speed. The stick turns the selected heading (sideways) and altitude (fore and
    /// aft), the levers the selected speed; held hard over for a second, the stick disconnects it.
    /// Returns the stick the law is flown with: the autopilot's.
    /// </summary>
    private static Vector2 Autopilot(AirlinerSpec spec, ref State s, in Controls c, Vector2 stick, float altitude, float dt)
    {
        if (c.AutopilotToggle)
        {
            s.Autopilot = !s.Autopilot;
            if (s.Autopilot)
            {
                s.ApHeading = s.Yaw;
                s.ApAltitude = Mathf.Round(altitude / 30.48f) * 30.48f;
                s.ApSpeed = s.Ias;
                s.ApOverride = 0f;
            }
        }
        if (!s.Autopilot) return stick;
        s.ApOverride = stick.Length() > 0.95f ? s.ApOverride + dt : 0f;
        if (s.ApOverride > 1f) { s.Autopilot = false; return stick; }

        // the knobs: the stick turns them, the levers the speed
        int flaps = Mathf.Clamp(Mathf.RoundToInt(s.Flaps), 0, spec.FlapSettings - 1);
        s.ApHeading = Mathf.Wrap(s.ApHeading - stick.X * 0.35f * dt, -Mathf.Pi, Mathf.Pi);
        s.ApAltitude = Mathf.Max(s.ApAltitude + stick.Y * 20f * dt, 300f);
        s.ApSpeed = Mathf.Clamp(s.ApSpeed + (c.LeverUp - c.LeverDown) * 2.5f * dt,
            spec.StallSpeed(s.Mass, flaps) * 1.25f, Mathf.Min(spec.Vmo, spec.FlapLimit[flaps]) - 3f);

        // autothrust: the levers chase the speed (the lever keys turned the knob instead)
        float leverRate = Mathf.Clamp((s.ApSpeed - s.Ias) * 0.08f - s.Velocity.Y * 0.01f, -1f, 1f) * 0.5f;
        s.Lever = Mathf.Clamp(s.Lever + leverRate * dt, 0f, 1f);

        // heading: a bank toward it, at most 25°; altitude: a vertical speed toward it, at most ~2000 fpm
        float bank = BankOf(s.Attitude);
        float err = Mathf.AngleDifference(s.Yaw, s.ApHeading);
        float wantBank = Mathf.Clamp(-err * 1.6f, -0.44f, 0.44f);   // + err: the heading is to the left
        float roll = Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f);
        float speed = Mathf.Max(s.Velocity.Length(), 1f);
        float vs = Mathf.Clamp((s.ApAltitude - altitude) * 0.08f, -10f, 10f);
        s.PathTarget = Mathf.Asin(Mathf.Clamp(vs / speed, -0.2f, 0.2f));
        return new Vector2(roll, 0f);
    }

    private static float Gamma(Vector3 v) => v.LengthSquared() > 1f ? Mathf.Asin(Mathf.Clamp(v.Y / v.Length(), -1f, 1f)) : 0f;

    /// <summary>The wheels (or the belly) meet the ground: judged by the sink and the bank.</summary>
    private static Event Touchdown(AirlinerSpec spec, ref State s, Basis att)
    {
        float sink = s.LastSink;
        float bank = Mathf.Abs(BankOf(att));
        bool wheels = s.Gear > 0.95f && !s.GearBroken;
        if (bank > 0.4f) return Event.Crashed;
        if (bank > 0.15f) s.Damage += 20f;   // a wingtip or an engine pod on the runway
        if (!wheels)
        {
            if (sink > spec.HardLanding) return Event.Crashed;
            s.Damage += 15f;
            return Event.None;
        }
        if (sink > spec.CrashSink) return Event.Crashed;
        if (sink > spec.GearCollapse)
        {
            s.GearBroken = true;
            s.Gear = 0f;
            s.Damage += 40f;
        }
        else if (sink > spec.HardLanding) s.Damage += (sink - spec.HardLanding) * 15f;
        return Event.None;
    }

    /// <summary>On the ground: wheels roll and steer (or the belly slides), the nose rotates for take-off, lift lifts it off.</summary>
    private static void Roll(AirlinerSpec spec, ref State s, in Controls c, Vector2 stick, Vector3 force, Vector3 accel,
        bool wheels, float brake, float q, bool protect, float dt)
    {
        var fwd = Heading(s.Yaw);
        var right = fwd.Cross(Vector3.Up);
        var v = s.Velocity;
        float vf = v.Dot(fwd), vs = v.Dot(right);
        float normal = Mathf.Max(0f, s.Mass * G - force.Y);
        float mu = wheels ? 0.012f + spec.BrakeMu * brake : 0.45f;

        vf += force.Dot(fwd) / s.Mass * dt;
        // no push-back: the reversers stop it, they do not back it up
        vf = Mathf.Max(Mathf.MoveToward(vf, 0f, mu * normal / s.Mass * dt), 0f);
        vs = Mathf.MoveToward(vs, 0f, (wheels ? 0.8f : 0.45f) * G * dt);

        if (wheels)
        {
            // the tiller at walking pace, the rudder pedals' 6° at speed; the arc a nose wheel turned δ rolls
            float lockAngle = Mathf.Lerp(spec.MaxTiller, 0.105f, Mathf.Clamp(vf / 25f, 0f, 1f));
            float delta = -stick.X * lockAngle;
            s.Yaw += vf * Mathf.Tan(delta) / spec.Wheelbase * dt;
        }
        else
        {
            // the belly scrapes: the airframe takes it
            s.Damage += vf * 0.35f * dt;
        }

        // lift off once the wing carries it; until then the floor holds it down
        float vy = accel.Y > 0f ? Mathf.Max(v.Y, 0f) + accel.Y * dt : -0.3f;
        fwd = Heading(s.Yaw);
        right = fwd.Cross(Vector3.Up);
        s.Velocity = fwd * vf + right * vs + Vector3.Up * vy;

        // rotation: the elevator lifts the nose about the main gear once there is air enough over it
        float pitch = PitchOf(s.Attitude);
        float qRotate = 0.5f * SeaLevelDensity * Mathf.Pow(spec.StallSpeed(s.Mass, s.FlapLever) * 1.02f, 2f);
        float authority = Mathf.Clamp(q / qRotate, 0f, 1f);
        float rate = stick.Y > 0.05f ? stick.Y * spec.MaxPitchRate * 0.5f * authority
            : stick.Y < -0.05f ? -0.07f : pitch > 0f ? -0.035f : 0f;
        if (!wheels) rate = -0.2f;
        float limit = protect ? spec.TailStrike * 0.92f : spec.TailStrike;
        float next = Mathf.Clamp(pitch + rate * dt, 0f, limit);
        // a tail strike: only without the protections, rotating too far while rolling
        if (!protect && pitch + rate * dt > spec.TailStrike && vf > 20f) s.Damage += 8f * dt;
        s.PitchRate = (next - pitch) / Mathf.Max(dt, 1e-4f);
        s.RollRate = 0f;
        s.Attitude = new Basis(Vector3.Up, s.Yaw) * new Basis(Vector3.Right, next);
        s.PathTarget = Gamma(s.Velocity);
        s.LastGamma = s.PathTarget;
    }

    /// <summary>In the air: the stick's law, the protections, the nose into the airflow.</summary>
    private static void Fly(AirlinerSpec spec, ref State s, in Controls c, Vector2 stick, float alpha, float beta, float q,
        bool protect, bool arcade, float altitude, float dt)
    {
        bool sim = !arcade;
        if (sim) stick = Autopilot(spec, ref s, c, stick, altitude, dt);
        // a conventional aircraft flown by hand in Sim: the trim, not a law, decides what it does hands-off
        bool manual = sim && !spec.FlyByWire && !s.Autopilot;
        if (sim) s.TrimAlpha = Mathf.Clamp(s.TrimAlpha + c.Trim * TrimRate * dt, -0.05f, 0.2f);
        var att = s.Attitude.Orthonormalized();
        var v = s.Velocity;
        float speed = Mathf.Max(v.Length(), 1f);
        float gamma = Gamma(v);
        float gammaRate = (gamma - s.LastGamma) / Mathf.Max(dt, 1e-4f);
        s.LastGamma = gamma;
        float pitch = PitchOf(att), bank = BankOf(att);
        float qRef = 0.5f * SeaLevelDensity * Mathf.Pow(spec.StallSpeed(s.Mass, 0), 2f) * 0.6f;
        float authority = Mathf.Clamp(q / qRef, 0.15f, 1f);

        // pitch: the stick moves the flight path; let go, the path is held. Turning takes more lift,
        // which in a bank is pitching about the wings (the feed-forward: ω·sin φ, ω = g·tan φ / V).
        float clampedBank = Mathf.Clamp(bank, -1.4f, 1.4f);
        float turnPitch = G / speed * Mathf.Tan(clampedBank) * Mathf.Sin(clampedBank);
        float qCmd;
        if (manual)
        {
            // static stability: the nose seeks the trimmed angle of attack, so letting go returns to the
            // trimmed speed; a turn needs back pressure; the stick adds to it
            qCmd = stick.Y * spec.MaxPitchRate * 2.5f + (s.TrimAlpha - alpha) * PitchStability - gammaRate * 0.3f;
            s.PathTarget = gamma;
        }
        else if (Mathf.Abs(stick.Y) > 0.05f)
        {
            s.PathTarget = gamma;
            qCmd = stick.Y * spec.MaxPitchRate + turnPitch;
        }
        else qCmd = PathGain * (s.PathTarget - gamma) - PathDamping * gammaRate * 0.5f + turnPitch;

        if (protect)
        {
            float alphaProt = s.AlphaStall - 0.035f;
            if (alpha > alphaProt) qCmd = Mathf.Min(qCmd, -(alpha - alphaProt) * 3f);
            if (pitch > PitchUp) qCmd = Mathf.Min(qCmd, (PitchUp - pitch) * 2f);
            if (pitch < PitchDown) qCmd = Mathf.Max(qCmd, (PitchDown - pitch) * 2f);
            // high speed: the nose comes up on its own past VMO
            float over = s.Ias - spec.Vmo * 1.02f;
            if (over > 0f) qCmd = Mathf.Max(qCmd, over * 0.01f);
            // alpha floor: near the stall the thrust goes to full whatever the levers say; not with
            // no airflow to speak of (dropped onto its wheels, the "alpha" of falling straight down is 90°)
            if (alpha > alphaProt - 0.01f && speed > spec.StallSpeed(s.Mass, 0) * 0.5f) s.Lever = 1f;
            if (alpha > alphaProt || pitch > PitchUp || pitch < PitchDown) s.PathTarget = gamma;
        }
        qCmd = Mathf.Clamp(qCmd, -spec.MaxPitchRate * 1.5f, spec.MaxPitchRate * 1.5f);

        // roll: the stick asks a roll rate; let go, arcade rolls level, the law holds the bank to 33°
        float pCmd;
        if (Mathf.Abs(stick.X) > 0.05f) pCmd = stick.X * spec.MaxRollRate;
        else if (manual) pCmd = 0f;
        else if (arcade) pCmd = Mathf.Clamp(-bank * 0.6f, -0.09f, 0.09f);
        else if (protect && Mathf.Abs(bank) > BankHold) pCmd = -(bank - Mathf.Sign(bank) * BankHold) * 0.6f;
        else pCmd = 0f;
        if (protect && Mathf.Abs(bank) > BankLimit) pCmd = Mathf.Sign(bank) > 0 ? Mathf.Min(pCmd, -0.05f) : Mathf.Max(pCmd, 0.05f);

        s.PitchRate += (qCmd - s.PitchRate) * MathX.Damp(2.2f * authority, dt);
        s.RollRate += (pCmd - s.RollRate) * MathX.Damp(2.5f * authority, dt);

        // without the protections a stalled wing drops the nose (and one wing)
        if (!protect && alpha > s.AlphaStall)
        {
            float deep = Mathf.Clamp((alpha - s.AlphaStall) / 0.1f, 0f, 1f);
            s.PitchRate -= 0.25f * deep * dt * 10f;
            s.RollRate += 0.15f * deep * dt * 10f;
        }

        att = att * new Basis(Vector3.Right, s.PitchRate * dt);
        att = att * new Basis(Vector3.Back, -s.RollRate * dt);
        // the fin weathercocks the nose into the airflow: no sideslip, no rudder to work
        att = att * new Basis(Vector3.Up, -beta * Mathf.Clamp(q / qRef, 0.3f, 2f) * 1.5f * dt);
        att = att.Orthonormalized();
        s.Attitude = att;
        var nose = -att.Z;
        var flat = new Vector2(nose.X, nose.Z);
        if (flat.Length() > 0.05f) s.Yaw = Mathf.Atan2(-flat.X, -flat.Y);
    }
}
