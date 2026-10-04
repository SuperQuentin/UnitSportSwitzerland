using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>What the driveline is asked for one frame.</summary>
/// <param name="Throttle">Accelerator pedal, 0..1.</param>
/// <param name="Brake">Service brake pedal, 0..1.</param>
/// <param name="Handbrake">The parking (spring) brake valve pulled.</param>
/// <param name="Speed">Forward speed at the driven wheels, m/s (− rolling back).</param>
/// <param name="Grade">Rise per metre along the heading, for the start gear.</param>
/// <param name="Mass">The whole train, kg, for the start gear.</param>
/// <param name="EngineOn">The ignition: off, the engine only drags.</param>
public readonly record struct DriveDemand(float Throttle, float Brake, bool Handbrake, float Speed, float Grade, float Mass, bool EngineOn);

/// <summary>
/// A truck's or a bus's engine, clutch or converter, gearbox, retarder and air brakes (#70).
///
/// <para>
/// Unlike the car, whose engine speed is simply the wheels' times the gearing, here the engine is
/// a flywheel of its own: torque from the published curve and an idle governor, friction, the
/// exhaust brake, against whatever the clutch passes. That is what a manual box needs — a clutch
/// that slips while it bites, an engine that stalls when it is let out in twelfth, rpm that fall
/// while the clutch is out between gears — and what makes an automated box honest: an AMT shift is
/// the clutch opening, the gear changing, the engine being brought down to the new speed and the
/// clutch closing again, half a second with no drive, which is why a loaded truck loses speed on
/// every upshift up a hill. A city bus's converter is modelled as one (pump torque ∝ ω², torque
/// ratio 2 at stall falling to 1 at the coupling point, a lock-up clutch above).
/// </para>
///
/// <para>
/// The air brakes take a moment: the chamber pressure follows the pedal with a lag (~0.3 s to
/// apply, ~0.45 s to release), each application draws on the tank, the engine's compressor refills
/// it, and a tank run below 4.5 bar lets the spring brakes on. Game applies them instantly and never
/// runs the tank down.
/// </para>
/// </summary>
public sealed class HeavyDriveline
{
    private readonly HeavySpec _s;
    private readonly float _wheelRadius;

    private const float Driveline = 0.9f;
    /// <summary>What the clutch holds fully closed, as a share of the engine's peak torque.</summary>
    private const float ClutchMargin = 1.6f;
    public const int RetarderLevels = 4;
    public const float AirMax = 10f, AirLow = 6f, AirSprings = 4.5f;

    public HeavyDriveline(HeavySpec spec, float wheelRadius)
    {
        _s = spec;
        _wheelRadius = wheelRadius;
        EngineRpm = spec.IdleRpm;
        // a converter pulls the engine down to its stall speed (a bus's ~1,900 rpm) when held at full throttle
        float stall = Mathf.Min(spec.StallRpm, spec.Redline * 0.8f);
        _converterK = stall * Mathf.Tau / 60f / Mathf.Sqrt(Mathf.Max(spec.TorqueAt(stall), 1f));
    }

    // ---- state ----
    public float EngineRpm { get; private set; }
    /// <summary>1..n forward, 0 neutral, −1 reverse.</summary>
    public int Gear { get; private set; }
    /// <summary>What the clutch does, 0 open .. 1 closed (the pedal's, or the automation's).</summary>
    public float Clutch { get; private set; }
    /// <summary>Closed and not slipping: the engine turns with the wheels.</summary>
    public bool Locked { get; private set; }
    /// <summary>The driver's clutch pedal, 0 up .. 1 floored; it comes back up at a foot's pace.</summary>
    public float ClutchPedal { get; private set; }
    /// <summary>0 off, 1 exhaust brake, 2..4 the retarder in thirds (the stalk).</summary>
    public int RetarderLevel { get; set; }
    /// <summary>The retarder's share this frame, 0..1, for the HUD.</summary>
    public float Retarding { get; private set; }
    /// <summary>Service brake pressure in the chambers, 0..1.</summary>
    public float BrakePressure { get; private set; }
    public float AirTank { get; private set; } = AirMax;
    public bool SpringBrakes { get; private set; }
    /// <summary>Counts the spring brakes going on or off: each is the hiss of air, for the sound.</summary>
    public int AirPuffs { get; private set; }
    /// <summary>Throttle actually reaching the engine (after the limiter and a shift's cut), 0..1.</summary>
    public float Throttle { get; private set; }
    /// <summary>The splitter in the H-pattern: 0 low, 1 high.</summary>
    public int Splitter { get; private set; } = 1;
    /// <summary>Something the driver should hear or read about: "grind", "stall", "spring", "air"; whoever reports it clears it.</summary>
    public string? Event { get; set; }
    /// <summary>The engine died this frame (let out in too high a gear): the owner switches it off.</summary>
    public bool Stalled { get; set; }

    public HeavyShift Mode { get; set; }
    public bool Arcade { get; set; }

    private float _shift;          // seconds left of an automated shift
    private int _shiftTo;          // the gear it is going to
    private bool _shiftSwapped;
    private float _converterK;
    private float _powershiftCut;  // converter box: torque held back for a moment after a shift
    private int _preselect = -1;   // H-pattern: a splitter move waiting for the clutch or a lift

    public bool Converter => _s.Box == Transmission.TorqueConverter;
    private bool AutoClutch => Converter || Mode is HeavyShift.Automatic or HeavyShift.Sequential;

    public float Ratio(int gear) => gear switch
    {
        0 => 0f,
        < 0 => _s.Reverse * _s.FinalDrive,
        _ => _s.Gears[Mathf.Clamp(gear, 1, _s.Gears.Length) - 1] * _s.FinalDrive,
    };

    /// <summary>Engine rpm the wheels would turn it at in <paramref name="gear"/>.</summary>
    public float RpmAt(int gear, float speed) =>
        gear == 0 ? 0f : speed * Mathf.Sign(gear) / _wheelRadius * Ratio(gear) * 60f / Mathf.Tau;

    public float Rpm01 => Mathf.Clamp((EngineRpm - _s.IdleRpm) / (_s.Redline - _s.IdleRpm), 0f, 1f);

    // ---- the driver's hands --------------------------------------------------------------

    /// <summary>Held: the clutch pedal goes down. Released, it comes back up over ~0.6 s.</summary>
    public bool ClutchHeld { get; set; }

    /// <summary>Sequential up (or the splitter up in the H-pattern).</summary>
    public void ShiftUp(float speed) => Request(+1, speed);
    public void ShiftDown(float speed) => Request(-1, speed);

    private void Request(int dir, float speed)
    {
        if (Mode == HeavyShift.Automatic) return;
        if (Mode is HeavyShift.HPatternSplitter && _s.SixGates)
        {
            _preselect = dir > 0 ? 1 : 0;
            return;
        }
        if (Mode is HeavyShift.HPattern && _s.SixGates) return;   // the splitter is the box's own
        int to = Gear + dir;
        if (Gear == 0) to = dir > 0 ? 1 : -1;
        else if (Gear < 0) to = dir > 0 ? 0 : -1;
        else if (to == 0 && Mathf.Abs(speed) > 1f) return;
        if (to > _s.Gears.Length) return;
        if (to < 0 && Gear > 0) to = 0;
        // never down into a gear that would throw the engine past its governor
        if (to > 0 && RpmAt(to, speed) > _s.Redline * 1.05f) { Event = "overrev"; return; }
        if (Mode == HeavyShift.SequentialClutch && !Converter && ClutchPedal < 0.75f && to != 0)
        {
            Event = "grind";
            return;
        }
        Engage(to, speed);
    }

    /// <summary>H-pattern: a gate on the number keys, 1..6; 0 neutral, −1 reverse.</summary>
    public void SelectGate(int gate, float speed)
    {
        if (Mode is not (HeavyShift.HPatternSplitter or HeavyShift.HPattern) || !_s.SixGates) return;
        if (gate == 0) { Gear = 0; Locked = false; return; }   // out of gear needs no clutch
        if (ClutchPedal < 0.75f) { Event = "grind"; return; }
        if (gate < 0)
        {
            if (Mathf.Abs(speed) > 1f) { Event = "grind"; return; }
            Gear = -1;
            return;
        }
        int split = Splitter;
        if (Mode == HeavyShift.HPattern)
            // the box picks the half of the gate that keeps the engine in its band
            split = RpmAt((gate - 1) * 2 + 2, speed) > _s.Redline * 0.47f ? 1 : 0;
        int to = (gate - 1) * 2 + 1 + split;
        if (RpmAt(to, speed) > _s.Redline * 1.05f) { Event = "overrev"; return; }
        Gear = to;
        Splitter = split;
    }

    /// <summary>The gate in the H-pattern (1..6), 0 neutral, −1 reverse.</summary>
    public int Gate => Gear <= 0 ? Gear : (Gear + 1) / 2;

    private void Engage(int to, float speed)
    {
        if (Converter || Mathf.Abs(speed) < 0.3f || Mode == HeavyShift.SequentialClutch && ClutchPedal >= 0.75f)
        {
            // a powershift, a gear picked at a standstill, or the driver's own clutch: at once
            if (Converter && Gear > 0 && to > 0) _powershiftCut = 0.25f;
            Gear = to;
            if (!Converter && Mode != HeavyShift.SequentialClutch) Locked = false;
            return;
        }
        _shift = _s.ShiftTime;
        _shiftTo = to;
        _shiftSwapped = false;
    }

    // ---- one frame ----------------------------------------------------------------------

    /// <summary>
    /// Runs the box's own logic, the clutch or converter and the engine for one frame. Returns the
    /// engine's force at the driven wheels (+ forward) and the retarder's (a magnitude).
    /// </summary>
    public (float Drive, float Retard) Step(in DriveDemand d, float dt)
    {
        float speed = d.Speed;
        float v = Mathf.Abs(speed);

        // the clutch pedal: down fast, back up at the pace a foot lets it
        ClutchPedal = ClutchHeld ? Mathf.MoveToward(ClutchPedal, 1f, 6f * dt) : Mathf.MoveToward(ClutchPedal, 0f, 1.7f * dt);

        UpdateAir(d, dt);

        // the limiter cuts fuel near the set speed; downhill the truck runs on past it
        float limit = _s.LimiterKmh / 3.6f;
        float throttle = d.Throttle * Mathf.Clamp((limit - v) / 1.5f, 0f, 1f);
        if (!d.EngineOn) throttle = 0f;

        // the box
        if (Mode == HeavyShift.Automatic) AutoSelect(d, throttle, speed, dt);
        if (Mode == HeavyShift.HPattern && _s.SixGates && !AutoClutch) AutoSplit(throttle, speed);
        if (Mode == HeavyShift.HPatternSplitter && _preselect >= 0 && Gear > 0 && (ClutchPedal > 0.5f || throttle < 0.1f))
        {
            int to = (Gate - 1) * 2 + 1 + _preselect;
            if (RpmAt(to, speed) <= _s.Redline * 1.05f) { Gear = to; Splitter = _preselect; }
            else Event = "overrev";
            _preselect = -1;
        }
        if (Mode == HeavyShift.Sequential && !Converter && Gear > 1 && Locked && RpmAt(Gear, speed) < _s.IdleRpm * 1.15f && throttle < 0.9f)
            Engage(Gear - 1, speed);   // the automated box will not let the engine labour below idle

        // an automated shift: clutch out, gear, engine to the new speed, clutch in
        float cut = 1f;
        if (_shift > 0f)
        {
            _shift -= dt;
            float t = 1f - _shift / _s.ShiftTime;
            cut = 0f;
            if (!_shiftSwapped && t >= 0.35f) { Gear = _shiftTo; _shiftSwapped = true; Locked = false; }
            if (_shift <= 0f) _shift = 0f;
        }
        if (_powershiftCut > 0f) { _powershiftCut -= dt; cut = Mathf.Min(cut, 0.6f); }
        Throttle = throttle * cut;

        // the retarder and the exhaust brake: the stalk, blended into the pedal on an automatic,
        // and Game's downhill control holding the limiter
        float share = RetarderLevel > 1 ? (RetarderLevel - 1) / (float)(RetarderLevels - 1) : 0f;
        bool exhaust = RetarderLevel >= 1;
        if (Mode == HeavyShift.Automatic || Converter)
        {
            float blend = Mathf.Clamp(d.Brake * 3f, 0f, 1f);
            share = Mathf.Max(share, blend);
            exhaust |= blend > 0f;
        }
        if (Arcade && d.Throttle < 0.02f && v > limit + 0.8f) { share = 1f; exhaust = true; }
        if (v < 1.5f) share = 0f;
        Retarding = share;
        float retard = share * Mathf.Min(_s.RetarderNm * _s.FinalDrive / _wheelRadius, _s.RetarderKw * 1000f / Mathf.Max(v, 1f))
            * Mathf.Clamp(v * v / 64f, 0f, 1f);

        // the engine against the clutch or converter, in four little steps
        float drive = 0f;
        const int n = 4;
        float h = dt / n;
        for (int i = 0; i < n; i++) drive += EngineStep(speed, Throttle, exhaust && Throttle < 0.02f, d, h) / n;

        if (d.EngineOn && EngineRpm < _s.IdleRpm * 0.5f && !AutoClutch && Gear != 0 && Clutch > 0.3f)
        {
            Stalled = true;
            Event = "stall";
        }
        return (drive, retard);
    }

    /// <summary>One slice of the engine and the clutch; returns the force at the driven wheels, N.</summary>
    private float EngineStep(float speed, float throttle, bool exhaust, in DriveDemand d, float h)
    {
        float omega = EngineRpm * Mathf.Tau / 60f;
        // an automated launch asks the engine for speed, not for a pedal's worth of fuel: while the
        // clutch slips the engine is fuelled up to the launch speed (a diesel at 800 rpm on half a
        // pedal would otherwise sit there, the clutch taking all it made)
        if (AutoClutch && !Converter && !Locked && Gear != 0 && throttle > 0.02f && _shift <= 0f)
            throttle = Mathf.Clamp(throttle + (LaunchRpm(throttle) - EngineRpm) / 150f, throttle, 1f);
        float engine = EngineTorque(EngineRpm, throttle, exhaust, d.EngineOn);
        float ratio = Ratio(Gear);
        float dir = Mathf.Sign(Gear);
        float inRpm = RpmAt(Gear, speed);
        float inOmega = inRpm * Mathf.Tau / 60f;
        float transmitted = 0f;

        if (Gear == 0 || ratio == 0f)
        {
            Clutch = 0f;
            Locked = false;
            omega += engine / _s.EngineInertia * h;
        }
        else if (Converter && !(Gear >= 2 && inOmega > 0.88f * omega && throttle < 0.97f))
        {
            // the converter: pump torque grows with the square of the engine's speed and falls away
            // as the turbine catches up; the turbine gets it multiplied, 2x at stall
            Locked = false;
            Clutch = 1f;
            float sr = omega > 1f ? inOmega / omega : 1f;
            float pump = omega * omega / (_converterK * _converterK);
            if (sr >= 1f) pump = -pump * Mathf.Min((sr - 1f) * 5f, 1f);
            else pump *= 1f - Mathf.Pow(Mathf.Clamp((sr - 0.8f) / 0.2f, 0f, 1f), 2f);
            // neutral at stop: held on the brake, a bus does not strain against its converter
            if (Mathf.Abs(speed) < 0.3f && d.Brake > 0.1f && throttle < 0.02f) pump = 0f;
            float torqueRatio = sr < 0.85f ? 2f - sr / 0.85f : 1f;
            transmitted = pump * (sr < 1f ? torqueRatio : 1f);
            omega += (engine - pump) / _s.EngineInertia * h;
        }
        else
        {
            // a dry clutch, closed by the driver's foot or by the automation
            Clutch = Converter ? 1f : AutoClutch ? AutoClutchTarget(throttle, d, inRpm) : ClutchEngagement();
            float capacity = Clutch * ClutchMargin * _s.PeakTorque;
            if (Locked)
            {
                if (Mathf.Abs(engine) > capacity * 1.02f || Clutch < 0.05f) Locked = false;
                else
                {
                    omega = inOmega;
                    transmitted = engine;
                }
            }
            if (!Locked)
            {
                float slip = omega - inOmega;
                float tc = capacity * Mathf.Sign(slip);
                float next = omega + (engine - tc) / _s.EngineInertia * h;
                if (Mathf.Sign(next - inOmega) != Mathf.Sign(slip) && capacity > Mathf.Abs(engine))
                {
                    Locked = true;
                    // just caught: let it pull a moment before the box thinks about another gear
                    if (AutoClutch) _hold = Mathf.Max(_hold, 1.2f);
                    next = inOmega;
                    tc = engine;
                }
                transmitted = tc;
                omega = next;
            }
        }

        EngineRpm = Mathf.Max(0f, omega * 60f / Mathf.Tau);
        return transmitted * ratio * Driveline / _wheelRadius * dir;
    }

    /// <summary>Where the automated clutch holds the engine while it slips: higher with more throttle, above the down-shift point.</summary>
    private float LaunchRpm(float throttle) => _s.IdleRpm + throttle * (_s.Redline * 0.62f - _s.IdleRpm);

    /// <summary>The pedal as the clutch sees it: it bites between 65% and 25% of its travel.</summary>
    private float ClutchEngagement() => Mathf.SmoothStep(0f, 1f, Mathf.Clamp((0.65f - ClutchPedal) / 0.4f, 0f, 1f));

    /// <summary>
    /// The automated clutch: open while the brakes hold the truck, then closing as the engine's speed
    /// rises past idle toward a launch speed that grows with the throttle — the engine settles there
    /// while the clutch slips, and it locks once the wheels have caught up. Barely closed with no
    /// throttle: the truck creeps.
    /// </summary>
    private float AutoClutchTarget(float throttle, in DriveDemand d, float inRpm)
    {
        if (_shift > 0f)
        {
            float t = 1f - _shift / _s.ShiftTime;
            return t < 0.35f ? Mathf.Clamp(1f - t / 0.35f, 0f, 1f) : t < 0.7f ? 0f : Mathf.Clamp((t - 0.7f) / 0.3f, 0f, 1f);
        }
        if (Locked && inRpm > _s.IdleRpm * 0.9f) return 1f;
        if (throttle < 0.02f && d.Brake > 0.05f && Mathf.Abs(d.Speed) < 1f) return 0f;
        float launch = LaunchRpm(throttle);
        // it bites only as the engine nears the launch speed: biting from idle, it took everything
        // a diesel makes at 600 rpm and the engine never got up to where it pulls
        float from = throttle < 0.02f ? _s.IdleRpm * 0.75f : launch - 200f;
        float bite = Mathf.Clamp((EngineRpm - from) / 250f, 0f, 1f);
        return throttle < 0.02f ? bite * 0.12f : bite;
    }

    private float EngineTorque(float rpm, float throttle, bool exhaust, bool on)
    {
        float peak = _s.PeakTorque;
        float friction = rpm > 5f ? peak * (0.04f + 0.06f * rpm / _s.Redline) : 0f;
        if (!on) return -friction;
        float fuel = rpm >= _s.Redline ? 0f : throttle * _s.TorqueAt(rpm);
        // the idle governor: holds idle against the clutch biting, up to about half the peak
        float governor = rpm < _s.IdleRpm ? Mathf.Min(0.45f * peak, (_s.IdleRpm - rpm) / _s.IdleRpm * 4f * peak) : 0f;
        float brake = exhaust ? _s.EngineBrakeNm * Mathf.Clamp((rpm - _s.IdleRpm) / (_s.Redline - _s.IdleRpm), 0f, 1f) : 0f;
        return Mathf.Max(fuel, governor) - friction - brake;
    }

    // ---- the automatic box's head ----------------------------------------------------------

    private void AutoSelect(in DriveDemand d, float throttle, float speed, float dt)
    {
        _gaining = Mathf.Lerp(_gaining, (Mathf.Abs(speed) - _lastSpeed) / Mathf.Max(dt, 1e-3f), MathX.Damp(3f, dt));
        _lastSpeed = Mathf.Abs(speed);
        if (_shift > 0f) return;
        int top = _s.Gears.Length;
        float v = Mathf.Abs(speed);

        // reverse and back, as on the cars (the owner decides from the pedals: WantsReverse)
        if (Gear >= 0 && v < 0.5f && WantsReverse) { Gear = -1; Locked = false; return; }
        if (Gear < 0 && speed > -0.5f && !WantsReverse) { Gear = StartGear(d); Locked = false; return; }
        if (Gear < 0) return;

        if (Gear == 0 || v < 0.5f && throttle > 0.05f && !Locked)
        {
            int start = StartGear(d);
            if (Gear != start) { Gear = start; Locked = false; }
            return;
        }

        float rpm = RpmAt(Gear, speed);
        float red = _s.Redline;
        // somehow over the governor (taken over rolling): straight to a gear that fits
        if (rpm > red * 1.05f && Gear < top) { Engage(GearFor(speed), speed); _hold = 1f; return; }
        float up = red * (Converter ? Mathf.Lerp(0.6f, 0.92f, throttle) : Mathf.Lerp(0.6f, 0.82f, throttle));
        float down = red * (Converter ? Mathf.Lerp(0.3f, 0.45f, throttle) : Mathf.Lerp(0.45f, 0.5f, throttle));
        // braking on the exhaust brake and retarder, the box keeps the engine spinning where they work
        if (Retarding > 0.3f && throttle < 0.02f) down = red * 0.7f;
        if (!Locked && !Converter) return;   // still launching
        _hold -= dt;
        if (_hold > 0f) return;

        if (rpm > up && Gear < top)
        {
            // what the speed will be once the gear is in: an AMT's half second with no drive costs a
            // loaded truck on a hill most of its speed, and a shift that lands below the down-shift
            // point only hunts. Opticruise holds the gear instead.
            float lost = Converter ? 0f : 9.81f * (Mathf.Max(d.Grade, 0f) + 0.008f) * _s.ShiftTime;
            float after = speed - Mathf.Sign(speed) * lost;
            // light and gentle: skip a gear, as Opticruise does
            int to = Gear + 1;
            if (!Converter && throttle < 0.6f && Gear + 2 <= top && RpmAt(Gear + 2, after) > down + 250f) to = Gear + 2;
            if (RpmAt(to, after) > down + (Converter ? 0f : 100f)) { Engage(to, speed); _hold = 1.5f; }
        }
        // never down while still gaining speed: a truck pulling at the bottom of its band is fine
        else if (rpm < down && Gear > 1 && _gaining < 0.05f)
        {
            int to = Gear - 1;
            if (!Converter && Gear - 2 >= 1 && RpmAt(Gear - 1, speed) < down && RpmAt(Gear - 2, speed) < up) to = Gear - 2;
            if (RpmAt(to, speed) < red * 0.95f) { Engage(to, speed); _hold = 1f; }
        }
    }

    /// <summary>Seconds before the automatic will shift again: no hunting between two gears.</summary>
    private float _hold;
    /// <summary>Smoothed acceleration, m/s², and last frame's speed.</summary>
    private float _gaining, _lastSpeed;

    /// <summary>
    /// Set by the owner in the automatic mode: the brake pedal held at a standstill asks for
    /// reverse, the throttle for forward again.
    /// </summary>
    public bool WantsReverse { get; set; }

    /// <summary>
    /// The gear to pull away in, as Opticruise picks it: the highest of the lower third that still
    /// gives half as much again as it takes to climb this grade with this load and accelerate.
    /// A converter bus always starts in first.
    /// </summary>
    public int StartGear(in DriveDemand d)
    {
        if (Converter) return 1;
        float need = d.Mass * (9.81f * (0.006f + Mathf.Max(d.Grade, 0f)) + 0.35f);
        int best = 1;
        int limit = Mathf.Max(1, _s.Gears.Length / 3);
        for (int g = 1; g <= limit; g++)
        {
            float force = _s.TorqueAt(_s.Redline * 0.47f) * Ratio(g) * Driveline / _wheelRadius;
            if (force >= need * 1.5f) best = g;
        }
        return best;
    }

    /// <summary>The lowest gear that turns the engine no faster than three quarters of its governed speed here.</summary>
    public int GearFor(float speed)
    {
        for (int g = 1; g <= _s.Gears.Length; g++)
            if (RpmAt(g, Mathf.Abs(speed)) <= _s.Redline * 0.75f) return g;
        return _s.Gears.Length;
    }

    /// <summary>H-pattern without the splitter lever: the box moves the splitter inside the gate.</summary>
    private void AutoSplit(float throttle, float speed)
    {
        if (Gear <= 0 || ClutchPedal > 0.5f) return;
        float rpm = RpmAt(Gear, speed);
        if (Splitter == 0 && rpm > _s.Redline * Mathf.Lerp(0.62f, 0.84f, throttle))
        {
            Gear++;
            Splitter = 1;
            Locked = false;
            _powershiftCut = 0.3f;
        }
        else if (Splitter == 1 && rpm < _s.Redline * 0.45f && Gear > 1)
        {
            Gear--;
            Splitter = 0;
            Locked = false;
            _powershiftCut = 0.3f;
        }
    }

    // ---- air ------------------------------------------------------------------------------

    private void UpdateAir(in DriveDemand d, float dt)
    {
        float demand = Mathf.Clamp(d.Brake, 0f, 1f);
        float before = BrakePressure;
        // Game, or hydraulic brakes (a pickup): no chambers to fill, no tank to draw
        if (Arcade || !_s.AirBrakes) BrakePressure = demand;
        else
        {
            float tau = demand > BrakePressure ? 0.28f : 0.45f;
            BrakePressure += (demand - BrakePressure) * (1f - Mathf.Exp(-dt / tau));
        }
        if (!Arcade && _s.AirBrakes)
        {
            // every application draws the tank down; the compressor, turned by the engine, fills it
            if (BrakePressure > before) AirTank -= (BrakePressure - before) * 0.65f;
            if (d.EngineOn) AirTank += 0.45f * Mathf.Clamp(EngineRpm / 1400f, 0.3f, 1.2f) * dt;
            AirTank = Mathf.Clamp(AirTank, 0f, AirMax);
        }
        else AirTank = AirMax;

        bool springs = d.Handbrake || AirTank < AirSprings;
        if (springs != SpringBrakes)
        {
            SpringBrakes = springs;
            // a hydraulic handbrake makes no hiss
            if (!_s.AirBrakes) return;
            Event = springs ? (d.Handbrake ? "spring" : "air") : "release";
            AirPuffs++;
        }
    }

    /// <summary>The service brakes' effect: the chamber pressure, as much as the tank can still supply.</summary>
    public float ServiceBrake => BrakePressure * Mathf.Clamp((AirTank - 2f) / 4.5f, 0f, 1f);

    /// <summary>A fresh start: engine at idle, a gear for the mode and the speed, a full tank, pedal up.</summary>
    public void Reset(bool engineOn, float speed = 0f)
    {
        EngineRpm = engineOn ? _s.IdleRpm : 0f;
        Gear = Mode == HeavyShift.Automatic || Converter ? GearFor(speed) : 0;
        if (Gear > 0 && Mathf.Abs(speed) > 1f) EngineRpm = RpmAt(Gear, speed);
        Locked = Gear > 0 && Mathf.Abs(speed) > 1f;
        ClutchPedal = 0f;
        _shift = 0f;
        AirTank = AirMax;
        BrakePressure = 0f;
        Splitter = 1;
    }
}
