using Godot;

namespace UnitSport.Player;

/// <summary>How a car is shifted (#290), Settings → Vehicles → "Car gearbox".</summary>
public enum CarGearbox
{
    /// <summary>The car's own box: up near the redline, down when it bogs; the brake at a standstill is reverse.</summary>
    Automatic,
    /// <summary>The driver shifts up and down (paddles, Shift / Ctrl, the shoulders); the clutch is automatic and it never stalls.</summary>
    Sequential,
    /// <summary>The gates (1-6, R, N, or a wheel's H-shifter) and the clutch pedal: it grinds without the clutch, and stalls.</summary>
    Manual,
}

/// <summary>
/// The car's sequential and manual boxes (#290). The automatic car's engine is simply the wheels'
/// speed times the gearing, floored at idle, which is also how the sequential one drives (its
/// automated clutch slips from a standstill as the floor at idle stands for). The manual car's
/// engine is a flywheel of its own against a dry clutch, as the trucks' is (<see cref="HeavyDriveline"/>):
/// it slips while the pedal bites, it revs in neutral, it brakes the car with the throttle shut,
/// and it stalls when the clutch is let out with too little throttle or in too high a gear.
/// </summary>
public sealed partial class Car
{
    /// <summary>The box this car is shifted with; the driver sets it (<see cref="SetGearbox"/>), everyone else's car stays automatic.</summary>
    public CarGearbox Gearbox { get; private set; }

    /// <summary>
    /// The automatic's selector from a wheel's H-shifter (<see cref="HeldShifter.Selector"/>), set by
    /// the driver each step; <see cref="DriveSelector.None"/>: the pedals pick reverse, as always.
    /// </summary>
    public DriveSelector Selector { get; set; }

    /// <summary>
    /// The automatic with a selector: D drives forward (the brake at a standstill no longer backs it
    /// up), R backs up on the gas once it is nearly stopped (rolling forward it is N until then),
    /// N and P drive nothing, and P holds it once it is down to walking pace (a parking pawl ratchets
    /// past faster than that).
    /// </summary>
    private void SelectGear(float u)
    {
        switch (Selector)
        {
            case DriveSelector.Drive: if (Gear <= 0) Gear = 1; break;
            case DriveSelector.Reverse: Gear = u < 1f ? -1 : 0; break;
            default: Gear = 0; break;
        }
    }

    /// <summary>
    /// An automatic's idle creep on the selector, N at the wheels: a torque converter at idle pushes
    /// about 5% of the car's weight from a standstill, nothing by 2.5 m/s (it settles at ~6.5 km/h on the
    /// flat and holds on a gentle slope), and its full push the moment the car rolls the wrong way.
    /// Only with a selector: on the pedals the brake held at a standstill is reverse, so a creeping car
    /// could never be held still. Not with the engine off.
    /// </summary>
    private float Creep(float u, bool reverse)
    {
        if (Selector is not (DriveSelector.Drive or DriveSelector.Reverse) || !EngineRunning) return 0f;
        float along = reverse ? -u : u;
        return CreepShare * Spec.Mass * Gravity * Mathf.Clamp(1f - along / CreepSpeed, 0f, 1f);
    }
    private const float CreepShare = 0.05f, CreepSpeed = 2.5f;

    /// <summary>The gear as the HUD says it: P R N D3 with a selector, else the gear (R for reverse, N for neutral).</summary>
    public string GearText => Selector switch
    {
        DriveSelector.Park => "P",
        DriveSelector.Neutral => "N",
        DriveSelector.Reverse when Gear < 0 => "R",
        DriveSelector.Drive => DriveText[Mathf.Clamp(Gear, 0, 9)],
        _ => Gear < 0 ? "R" : Gear == 0 ? "N" : GearDigits[Mathf.Clamp(Gear, 0, 9)],
    };
    // built once: the HUD asks every frame
    private static readonly string[] DriveText = { "D", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9" };
    private static readonly string[] GearDigits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    /// <summary>The clutch key or button held: the pedal goes down fast and comes back up over ~0.6 s.</summary>
    public bool ClutchHeld { get; set; }
    /// <summary>A real clutch pedal's travel (a steering wheel's), 0 up .. 1 floored, followed as it is.</summary>
    public float ClutchFoot { get; set; }
    /// <summary>The clutch pedal, 0 up .. 1 floored: for the box, the HUD and the cockpit's pedal.</summary>
    public float ClutchPedal { get; private set; }
    private float _keyClutch;

    /// <summary>The ignition, set by the driver each step: off, a manual car's engine only drags.</summary>
    public bool EngineRunning { get; set; } = true;
    /// <summary>The engine died this step (the clutch let out with too little): the owner switches it off.</summary>
    public bool Stalled { get; set; }
    /// <summary>Something for the driver to read: "grind", "overrev", "nogear", "stall"; whoever reports it clears it.</summary>
    public string? Event { get; set; }

    /// <summary>Manual: the clutch is closed and not slipping, the engine turning with the wheels.</summary>
    private bool _locked;
    /// <summary>The bigger one of the engine's curve, N·m: what the clutch is sized for.</summary>
    private float PeakTorque => _peakTorque ??= MeasurePeak();
    private float? _peakTorque;

    /// <summary>Crankshaft and flywheel of a 1.5 to 3 l engine, kg·m²: blipped, it falls from the limiter to idle in under two seconds.</summary>
    private const float EngineInertia = 0.1f;
    /// <summary>What the clutch holds fully closed, as a share of the engine's peak torque.</summary>
    private const float ClutchMargin = 1.5f;

    private float MeasurePeak()
    {
        float best = 1f;
        for (float rpm = Spec.IdleRpm; rpm <= Spec.Redline; rpm += 100f) best = Mathf.Max(best, Spec.TorqueAt(rpm));
        return best;
    }

    /// <summary>
    /// The driver's box, every step. A kart's centrifugal clutch has no other. Into the manual box at a
    /// standstill the lever goes to neutral: in gear with the clutch up, the engine would stall at once.
    /// </summary>
    public void SetGearbox(CarGearbox mode, float u)
    {
        if (IsKart) mode = CarGearbox.Automatic;
        if (mode != CarGearbox.Automatic) Selector = DriveSelector.None;
        if (mode == Gearbox) return;
        Gearbox = mode;
        _locked = false;
        if (mode == CarGearbox.Automatic) { if (Gear == 0) Gear = 1; }
        else if (mode == CarGearbox.Manual && Mathf.Abs(u) < 1f) Gear = 0;
    }

    /// <summary>The gearing into the wheels in <paramref name="gear"/>, final drive included; 0 in neutral.</summary>
    private float GearRatio(int gear) => gear switch
    {
        0 => 0f,
        < 0 => Spec.Reverse * Spec.FinalDrive,
        _ => Spec.Gears[Mathf.Clamp(gear, 1, Spec.Gears.Length) - 1] * Spec.FinalDrive,
    };

    /// <summary>Engine rpm the wheels would turn it at in <paramref name="gear"/> (− turned backwards).</summary>
    private float RpmAt(int gear, float u) =>
        gear == 0 ? 0f : u * Mathf.Sign(gear) / WheelRadius * GearRatio(gear) * 60f / Mathf.Tau;

    public void ShiftUp(float u) => Request(+1, u);
    public void ShiftDown(float u) => Request(-1, u);

    /// <summary>One step up or down: reverse, neutral, first, second… Neutral from first only nearly stopped.</summary>
    private void Request(int dir, float u)
    {
        if (Gearbox == CarGearbox.Automatic) return;
        int to = Gear == 0 ? (dir > 0 ? 1 : -1) : Gear < 0 ? (dir > 0 ? 0 : -1) : Gear + dir;
        if (to == Gear || to > Spec.Gears.Length) return;
        if (to == 0 && Gear > 0 && Mathf.Abs(u) > 1f) return;
        if (to < 0 && Gear > 0) to = 0;
        Shift(to, u);
    }

    /// <summary>The H-pattern: a gate on the number keys or the wheel's shifter, 1..6, 0 neutral, −1 reverse. True if it went in.</summary>
    public bool SelectGate(int gate, float u)
    {
        if (Gearbox != CarGearbox.Manual) return false;
        if (gate > Spec.Gears.Length) { Event = "nogear"; return false; }
        return gate == Gear || Shift(gate, u);
    }

    private bool Shift(int to, float u)
    {
        if (to != 0)
        {
            if (Gearbox == CarGearbox.Manual && ClutchPedal < 0.75f) { Event = "grind"; return false; }
            if (to < 0 && Mathf.Abs(u) > 1f) { Event = "grind"; return false; }
            // never into a gear that would throw the engine past its limiter
            if (to > 0 && RpmAt(to, u) > Spec.Redline * 1.05f) { Event = "overrev"; return false; }
        }
        // the automated clutch opens for a moment, as the automatic's does
        if (Gearbox == CarGearbox.Sequential && to > 0 && Gear > 0) _shiftTimer = 0.18f;
        Gear = to;
        _locked = false;
        return true;
    }

    /// <summary>The clutch pedal for this step: a key's goes down fast and back up at a foot's pace, a real one is where the foot has it.</summary>
    private void StepClutch(float dt)
    {
        _keyClutch = ClutchHeld ? Mathf.MoveToward(_keyClutch, 1f, 6f * dt) : Mathf.MoveToward(_keyClutch, 0f, 1.7f * dt);
        ClutchPedal = Gearbox == CarGearbox.Manual ? Mathf.Max(_keyClutch, Mathf.Clamp(ClutchFoot, 0f, 1f)) : 0f;
    }

    /// <summary>The pedal as the clutch sees it: it bites between 65% and 25% of its travel.</summary>
    private float ClutchBite => Gear == 0 ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp((0.65f - ClutchPedal) / 0.4f, 0f, 1f));

    /// <summary>
    /// The manual car's engine and clutch for one substep: the flywheel turned by the engine's torque
    /// and held by what the clutch passes. Returns the drive at the wheels, N, + forward.
    /// </summary>
    private float ManualDrive(float throttle, float u, float powerScale, float h)
    {
        float omega = Rpm * Mathf.Tau / 60f;
        float engine = ManualEngineTorque(Rpm, throttle, powerScale);
        if (Gear == 0)
        {
            _locked = false;
            Rpm = Mathf.Max(0f, (omega + engine / EngineInertia * h) * 60f / Mathf.Tau);
            return 0f;
        }
        float inOmega = RpmAt(Gear, u) * Mathf.Tau / 60f;
        float capacity = ClutchBite * ClutchMargin * PeakTorque;
        float transmitted = 0f;
        if (_locked)
        {
            if (Mathf.Abs(engine) > capacity * 1.02f || capacity < 0.05f * PeakTorque) _locked = false;
            else
            {
                omega = inOmega;
                transmitted = engine;
            }
        }
        if (!_locked)
        {
            float slip = omega - inOmega;
            float tc = capacity * Mathf.Sign(slip);
            float next = omega + (engine - tc) / EngineInertia * h;
            // caught up with the wheels: the clutch locks
            if (Mathf.Sign(next - inOmega) != Mathf.Sign(slip) && capacity > Mathf.Abs(engine))
            {
                _locked = true;
                next = inOmega;
                tc = engine;
            }
            transmitted = tc;
            omega = next;
        }
        Rpm = Mathf.Max(0f, omega * 60f / Mathf.Tau);
        return transmitted * GearRatio(Gear) * Driveline / WheelRadius * Mathf.Sign(Gear);
    }

    /// <summary>
    /// Torque at the crank: the curve on the throttle, an idle governor, and the drag of an engine
    /// whose throttle is shut (friction and pumping, which brings it from the limiter to idle in a
    /// couple of seconds and brakes the car in gear). The published curve is what it gives at full
    /// throttle, drag already paid. A throttle body opens most of the torque in its first third at
    /// low revs, so the pedal is progressive: a third of it pulls away.
    /// </summary>
    private float ManualEngineTorque(float rpm, float throttle, float powerScale)
    {
        float peak = PeakTorque;
        float drag = rpm > 5f ? peak * (0.15f + 0.2f * rpm / Spec.Redline) : 0f;
        if (!EngineRunning) return -drag;
        float open = 1f - (1f - throttle) * (1f - throttle);
        float friction = drag * (1f - open);
        float fuel = rpm >= Spec.Redline ? 0f : open * Spec.TorqueAt(rpm) * powerScale;
        // the idle governor holds idle against its own drag and the clutch biting, up to about half the peak
        float governor = rpm < Spec.IdleRpm ? Mathf.Min(0.45f * peak, friction + (Spec.IdleRpm - rpm) / Spec.IdleRpm * 4f * peak) : 0f;
        return Mathf.Max(fuel, governor) - friction;
    }

    /// <summary>After the step: an engine dragged under half its idle in gear with the clutch biting has stalled.</summary>
    private void CheckStall()
    {
        if (Gearbox != CarGearbox.Manual || !EngineRunning || Gear == 0 || ClutchBite < 0.3f || Rpm >= Spec.IdleRpm * 0.5f) return;
        Stalled = true;
        Event = "stall";
        EngineRunning = false;
        _locked = false;
    }
}
