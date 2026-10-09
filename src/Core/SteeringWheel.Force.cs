using Godot;
using SDL;
using static SDL.SDL3;

namespace UnitSport.Core;

/// <summary>
/// Force feedback (issue #68, part 2), through SDL3 haptics on the claimed wheel. Godot only does
/// joypad rumble, so the forces are SDL effects played on the wheel's own controller:
///
/// <list type="bullet">
/// <item>a <b>constant</b> force, updated every frame: the vehicle's self-aligning torque
/// (<see cref="WheelFeel.Torque"/>) plus the <b>soft lock</b> past the vehicle's lock;</item>
/// <item>a <b>sine</b> for the road (<see cref="WheelFeel.Road"/>), one for the engine
/// (<see cref="WheelFeel.Engine"/>, at the crankshaft's rate), and a short one for knocks
/// (<see cref="Knock"/>: crashes, hard landings);</item>
/// <item><b>damper</b> and <b>friction</b> conditions, which the wheel runs itself at its own rate:
/// the weight of the steering at a standstill (<see cref="WheelFeel.Weight"/>).</item>
/// </list>
///
/// <para>
/// The game drives it from the local player's feel layer once a frame (<see cref="Drive"/>); a feel
/// older than <see cref="StaleSeconds"/> (a menu, on foot, someone else's camera) leaves only a light
/// damper. The wheel's own autocentre spring is switched off where it can be, or it would fight the
/// aligning torque. Signs: + pushes the wheel right everywhere in the game; SDL's levels give the
/// side a force comes from, so it is negated on the way out, and <see cref="WheelSettings.FfbInvert"/>
/// flips it again for a device that still reads the other way.
/// </para>
/// </summary>
public partial class SteeringWheel
{
    /// <summary>A feel this old means nobody is driving with this wheel any more.</summary>
    public const float StaleSeconds = 0.25f;
    /// <summary>
    /// Past the lock, the soft lock is at full force within this much more rotation, radians (20°):
    /// the default, now per wheel (<see cref="WheelSettings.SoftLockRampDeg"/>, #290). On the HORI 8°
    /// made a stiff spring that, updated at the frame rate, bounced a fast rim off the lock and back
    /// (88° → 40° → 77°) instead of stopping it. The G29 holds a 6° ramp without bouncing, and at 20°
    /// a kart's lock (±99°) was not felt at all: 7° past it the force was only half.
    /// </summary>
    public const float SoftLockRamp = 0.35f;
    /// <summary>Past the lock, force per rad/s of rim speed (either way): it soaks up the bounce.</summary>
    public const float SoftLockDamping = 0.07f;
    /// <summary>The wheel's own damper near and past the lock, 0..1 of full: it runs at the device's rate, the frame cannot.</summary>
    public const float SoftLockDamper = 0.7f;

    /// <summary>The claimed wheel has force feedback and it is open.</summary>
    public static bool HasForceFeedback => _instance is { _hapticOpen: true };

    /// <summary>What the force-feedback panel shows: the forces sent last frame, −1..1 / 0..1.</summary>
    public static (float Constant, float Road, float Damper, float Friction) LastForces =>
        _instance is { } w ? (w._sentConstant, w._sentRoad, w._sentDamper, w._sentFriction) : default;

    private unsafe SDL_Haptic* _haptic;
    private bool _hapticSdl, _hapticOpen;
    private uint _features;
    private SDL_HapticEffectID _engine = (SDL_HapticEffectID)(-1);
    private SDL_HapticEffectID _constant = (SDL_HapticEffectID)(-1), _road = (SDL_HapticEffectID)(-1),
        _knock = (SDL_HapticEffectID)(-1), _damper = (SDL_HapticEffectID)(-1), _friction = (SDL_HapticEffectID)(-1);

    private WheelFeel _feel;
    private float _lock;
    private double _feelAge = double.MaxValue;
    private float _testLevel, _testTimer;
    private bool _softLogged;
    /// <summary><c>--ffblog</c>: a line every 2 s of what the wheel is being given.</summary>
    private static readonly bool TraceForces = CmdArgs.Has("--ffblog");
    private float _traceIn;
    private float _softDeepest, _softHardest;
    private float _lastAngle, _rate;
    private float _sentConstant = float.NaN, _sentRoad = float.NaN, _sentRoadHz, _sentDamper = float.NaN, _sentFriction = float.NaN;
    private float _sentEngine = float.NaN, _sentEngineHz;

    /// <summary>
    /// The vehicle being driven with this wheel this frame: what its steering feels and how far its
    /// wheel turns lock to lock, radians (the soft lock).
    /// </summary>
    public static void Drive(in WheelFeel feel, float lockToLock)
    {
        if (_instance is not { } w) return;
        w._feel = feel;
        w._lock = lockToLock;
        w._feelAge = 0;
    }

    /// <summary>
    /// <c>--wheellock deg</c>: every vehicle's lock to lock instead of its own (<c>Rideable.WheelLock</c>,
    /// and with it the steering ratio and the cockpit wheel), to feel the soft lock close to centre
    /// (180: the road wheels at their stop and a wall 90° either side).
    /// </summary>
    public static readonly float? LockOverride = ParseLockOverride();

    private static float? ParseLockOverride() => CmdArgs.Float("--wheellock") is float deg ? Mathf.DegToRad(deg) : null;

    /// <summary>A knock through the rim, 0..1: a crash, a kerb, a hard landing.</summary>
    public static unsafe void Knock(float strength)
    {
        if (_instance is not { _hapticOpen: true } w || (int)w._knock < 0 || w._feelAge > StaleSeconds) return;
        float level = Math.Clamp(strength * Settings.FfbKnocks * Settings.FfbStrength, 0f, 1f);
        if (level < 0.02f) return;
        var e = Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, 70, level * (Settings.FfbInvert ? -1f : 1f), 160);
        e.periodic.fade_length = 120;
        if (w.Send(w._knock, &e)) SDL_RunHapticEffect(w._haptic, w._knock, 1);
    }

    /// <summary>The settings panel's test: push the wheel one way, + right, for a moment.</summary>
    public static void Test(float level, float seconds)
    {
        if (_instance is not { } w) return;
        (w._testLevel, w._testTimer) = (Math.Clamp(level, -1f, 1f), seconds);
    }

    /// <summary>
    /// The soft lock's push, + right: none inside <paramref name="halfLock"/> (radians of wheel either
    /// side of centre), then back toward centre, full within <see cref="SoftLockRamp"/> more.
    /// </summary>
    public static float SoftLock(float angle, float halfLock, float ramp = SoftLockRamp)
    {
        float excess = Mathf.Abs(angle) - halfLock;
        return excess <= 0f ? 0f : -Mathf.Sign(angle) * Mathf.Clamp(excess / Mathf.Max(ramp, 0.01f), 0f, 1f);
    }

    /// <summary>
    /// Where the soft lock starts on the real wheel, radians either side: the vehicle's lock when the
    /// wheel's range covers it, else nowhere — a stretched range reaches the lock at the wheel's own stop.
    /// </summary>
    public static float SoftLockAt(float lockToLock, float range) =>
        lockToLock > 0f && range > lockToLock + 0.01f ? lockToLock * 0.5f : float.PositiveInfinity;

    /// <summary>
    /// The forces for one frame, as the constant level and the three other channels.
    /// <paramref name="rate"/> is the rim's speed, rad/s, + right.
    /// </summary>
    public static (float Constant, float Road, float Damper, float Friction, float Engine) Compose(
        in WheelFeel feel, float lockToLock, float angle, float rate, WheelSettings s)
    {
        float softAt = SoftLockAt(lockToLock, Mathf.DegToRad(s.RangeDeg));
        float master = s.FfbStrength;
        // The soft lock is a wall, so it takes the device's whole force whatever the strength: capped
        // at 70% it was pushed straight through on the HORI (121° past a 90° lock, force maxed out).
        float wall = SoftLock(angle, softAt, Mathf.DegToRad(s.SoftLockRampDeg));
        if (Mathf.Abs(angle) > softAt) wall -= Math.Clamp(rate * SoftLockDamping, -0.5f, 0.5f);
        float constant = feel.Torque * s.FfbAligning * master + wall;
        // the wheel's damper takes over from 6° short of the lock, fully at it
        float nearLock = Math.Clamp((Mathf.Abs(angle) - (softAt - 0.1f)) / 0.1f, 0f, 1f);
        // a little damping always, so the rim does not oscillate on the aligning torque; more parked
        float damper = Math.Max(Math.Clamp((0.12f + 0.5f * feel.Weight) * s.FfbWeight, 0f, 1f) * master, SoftLockDamper * nearLock);
        return (Math.Clamp(constant, -1f, 1f),
            Math.Clamp(feel.Road * s.FfbRoad, 0f, 1f) * master,
            damper,
            Math.Clamp(feel.Weight * s.FfbWeight, 0f, 1f) * master,
            Math.Clamp(feel.Engine * s.FfbEngine, 0f, 1f) * master);
    }

    // ---------------------------------------------------------------------------------------
    // per frame
    // ---------------------------------------------------------------------------------------

    private unsafe void UpdateForces(float dt)
    {
        var s = Settings;
        // the forces only while the game has the focus: another window gets the wheel back at once
        // (#290). --ffbcheck keeps them: launched from a terminal its window may never get the focus
        bool want = s.ForceFeedback && _claimed && (_focused || Player.WheelProbe.ForceCheckRequested);
        if (want && !_hapticOpen && _hapticSdl && !_hapticFailed)
        {
            OpenHaptic();
            // G HUB switches its profile a moment after the game comes to the front, which can leave
            // the fresh effects silent: made once more a little later
            if (_hapticOpen) _remakeAt = Time.GetTicksMsec() / 1000.0 + 1.5;
        }
        else if (!want && _hapticOpen)
        {
            GD.Print("[wheel] force feedback released: the game window lost the focus");
            CloseHaptic();
        }
        if (!_hapticOpen) return;
        RecoverHaptic();
        if (!_hapticOpen) return;

        // a drive starting after a pause (the first one, or after a menu or a walk) gets its effects
        // made afresh: anything that reset the wheel meanwhile would have left them silent (#290)
        bool starting = _feelAge <= StaleSeconds && _wasIdle;
        _wasIdle = _feelAge > RefreshAfterIdle;
        bool late = _remakeAt > 0 && Time.GetTicksMsec() / 1000.0 >= _remakeAt;
        if (starting || late)
        {
            if (late) _remakeAt = 0;
            Refresh(starting ? "a drive starts" : "the game came to the front a moment ago");
            if (!_hapticOpen) return;
        }

        _feelAge += dt;
        float constant, road, damper, friction, engine;
        float hz = _feel.RoadHz;
        bool driving = _feelAge <= StaleSeconds && !Assigning;
        // the rim's speed, smoothed over a few frames: a 1° step of the axis in one frame is noise, not 60°/s
        if (dt > 0f) _rate = Mathf.Lerp(_rate, (Angle - _lastAngle) / dt, Mathf.Clamp(dt * 10f, 0f, 1f));
        _lastAngle = Angle;
        if (driving)
            (constant, road, damper, friction, engine) = Compose(_feel, _lock, Angle, _rate, s);
        else
            (constant, road, damper, friction, engine) = (0f, 0f, 0.1f * s.FfbStrength, 0f, 0f);
        // a test push rides on top of whatever is being driven (so --ffbcheck can push into the soft lock)
        if (_testTimer > 0f)
        {
            _testTimer -= dt;
            constant = Math.Clamp(constant + _testLevel * s.FfbStrength, -1f, 1f);
            if (!driving) damper = 0f;
        }
        // say when the soft lock takes hold and, on the way out, how deep and how hard it was
        float softAt = SoftLockAt(_lock, Mathf.DegToRad(s.RangeDeg));
        bool locked = driving && Mathf.Abs(Angle) > softAt;
        if (locked && !_softLogged)
        {
            GD.Print($"[wheel] soft lock at {Mathf.RadToDeg(softAt):F0}° (vehicle {Mathf.RadToDeg(_lock):F0}° lock to lock, range {s.RangeDeg:F0}°)");
            (_softDeepest, _softHardest) = (0f, 0f);
        }
        if (locked)
            (_softDeepest, _softHardest) = (Mathf.Max(_softDeepest, Mathf.Abs(Angle) - softAt), Mathf.Max(_softHardest, Mathf.Abs(constant)));
        bool stillIn = locked || (_softLogged && Mathf.Abs(Angle) > softAt - 0.2f);
        if (_softLogged && !stillIn)
            GD.Print($"[wheel] soft lock left: {Mathf.RadToDeg(_softDeepest):F0}° deep at most, force up to {_softHardest:F2} (strength {s.FfbStrength:F2})");
        _softLogged = stillIn;
        if (s.FfbInvert) constant = -constant;
        if (TraceForces && (_traceIn -= dt) <= 0f)
        {
            _traceIn = 2f;
            GD.Print($"[ffb] driving {driving} (feel {Math.Min(_feelAge, 99):F2} s old)  torque {_feel.Torque:+0.00;-0.00}  road {_feel.Road:F2}  engine {_feel.Engine:F2} at {_feel.EngineHz:F0} Hz  "
                + $"weight {_feel.Weight:F2}  wheel {Mathf.RadToDeg(Angle):+0;-0}°  sent {constant:+0.00;-0.00}  refused {_sendFailures}");
        }

        if ((int)_constant >= 0 && (MathF.Abs(constant - _sentConstant) > 1f / 512f || float.IsNaN(_sentConstant)))
        {
            _sentConstant = constant;
            var e = new SDL_HapticEffect();
            e.constant.type = SDL_HapticEffectType.SDL_HAPTIC_CONSTANT;
            e.constant.direction.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
            e.constant.length = SDL_HAPTIC_INFINITY;
            // SDL (as DirectInput) gives the direction a force comes FROM: a positive level comes
            // from the right and pushes left, so + right is sent negative (measured with --ffbcheck)
            e.constant.level = Level(-constant);
            Send(_constant, &e);
        }
        if ((int)_road >= 0 && (MathF.Abs(road - _sentRoad) > 0.01f || MathF.Abs(hz - _sentRoadHz) > 1f || float.IsNaN(_sentRoad)))
        {
            (_sentRoad, _sentRoadHz) = (road, hz);
            var e = Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, (ushort)Math.Clamp(1000f / Math.Max(hz, 1f), 20f, 500f), road, SDL_HAPTIC_INFINITY);
            Send(_road, &e);
        }
        float engineHz = _feel.EngineHz;
        if ((int)_engine >= 0 && (MathF.Abs(engine - _sentEngine) > 0.01f || MathF.Abs(engineHz - _sentEngineHz) > 1f || float.IsNaN(_sentEngine)))
        {
            (_sentEngine, _sentEngineHz) = (engine, engineHz);
            var e = Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, (ushort)Math.Clamp(1000f / Math.Max(engineHz, 1f), 16f, 125f), engine, SDL_HAPTIC_INFINITY);
            Send(_engine, &e);
        }
        if ((int)_damper >= 0 && (MathF.Abs(damper - _sentDamper) > 0.01f || float.IsNaN(_sentDamper)))
        {
            _sentDamper = damper;
            var e = Condition(SDL_HapticEffectType.SDL_HAPTIC_DAMPER, damper);
            Send(_damper, &e);
        }
        if ((int)_friction >= 0 && (MathF.Abs(friction - _sentFriction) > 0.01f || float.IsNaN(_sentFriction)))
        {
            _sentFriction = friction;
            var e = Condition(SDL_HapticEffectType.SDL_HAPTIC_FRICTION, friction);
            Send(_friction, &e);
        }
    }

    // ---------------------------------------------------------------------------------------
    // the device
    // ---------------------------------------------------------------------------------------

    private int _sendFailures;
    private double _reopenAt;

    /// <summary>
    /// Updates a running effect. Windows can take the forces away from the game (DirectInput hands
    /// them to one program at a time): the update then fails, silently unless checked — the wheel
    /// went dead while the log showed full force. A failed update is logged once, the effect is
    /// started again, and if that fails too the device is closed and reopened a second later.
    /// </summary>
    private unsafe bool Send(SDL_HapticEffectID id, SDL_HapticEffect* e)
    {
        if (SDL_UpdateHapticEffect(_haptic, id, e)) return true;
        if (_sendFailures++ == 0) GD.PushWarning($"[wheel] force feedback update refused: {SDL_GetError()}");
        // a one-shot (the knock) is started by its caller; the others run for ever
        if ((int)id != (int)_knock && SDL_RunHapticEffect(_haptic, id, SDL_HAPTIC_INFINITY) && SDL_UpdateHapticEffect(_haptic, id, e))
            return true;
        if (_reopenAt <= 0) _reopenAt = Time.GetTicksMsec() / 1000.0 + 1.0;
        return false;
    }

    /// <summary>Closes and reopens the forces once a second while updates keep failing.</summary>
    private void RecoverHaptic()
    {
        if (_reopenAt <= 0 || Time.GetTicksMsec() / 1000.0 < _reopenAt) return;
        _reopenAt = 0;
        GD.Print($"[wheel] force feedback lost ({_sendFailures} refused updates): reopening");
        CloseHaptic();
        _hapticFailed = false;
        OpenHaptic();
        _sendFailures = 0;
    }

    private bool _hapticFailed;

    /// <summary>Seconds without a vehicle's feel after which the next drive makes the effects afresh.</summary>
    private const float RefreshAfterIdle = 2f;
    private bool _wasIdle = true;
    /// <summary>The game window has the focus; the forces are only held while it does.</summary>
    private bool _focused = true;
    /// <summary>When the effects are made once more after the game came to the front (0: not pending).</summary>
    private double _remakeAt;

    /// <summary>
    /// The effects made afresh on the open device. Something can reset the wheel behind the game's
    /// back after the effects are made: Logitech G HUB switching profiles as the game window comes to
    /// the front, or another SDL (Godot's own joypad layer) opening the device while the world loads.
    /// The effects then go silent while every update still succeeds, so <see cref="Send"/> sees
    /// nothing to recover: forces off at launch until toggled (seen on a G29, #290). Done when a drive
    /// starts and 1.5 s after the device is opened (the game came to the front). The device stays open:
    /// closed and reopened at once, Windows refuses the reopen (the G29 then had no forces at all).
    /// </summary>
    private unsafe void Refresh(string why)
    {
        if (!_hapticOpen) return;
        GD.Print($"[wheel] force feedback made afresh: {why}");
        SDL_StopHapticEffects(_haptic);
        foreach (var id in new[] { _constant, _road, _engine, _knock, _damper, _friction })
            if ((int)id >= 0) SDL_DestroyHapticEffect(_haptic, id);
        MakeEffects();
    }

    /// <summary>The focus decides whether the forces are held (<see cref="UpdateForces"/>): released on the way out, reopened on the way in.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusIn) _focused = true;
        else if (what == NotificationApplicationFocusOut) _focused = false;
    }

    /// <summary>Opens that failed in a row; after <see cref="OpenTries"/> the wheel is left without forces.</summary>
    private int _openFailures;
    private double _openRetryAt;
    private const int OpenTries = 5;

    private unsafe void OpenHaptic()
    {
        if (_joy == null || !SDL_IsJoystickHaptic(_joy)) { _hapticFailed = true; return; }
        if (Time.GetTicksMsec() / 1000.0 < _openRetryAt) return;
        _haptic = SDL_OpenHapticFromJoystick(_joy);
        if (_haptic == null)
        {
            // Windows refuses a device closed a moment ago (the recovery's reopen, a quick toggle):
            // try again a second later, a few times, before giving up for the session
            _openFailures++;
            GD.PushWarning($"[wheel] force feedback: could not open {_name} (try {_openFailures} of {OpenTries}): {SDL_GetError()}");
            _hapticFailed = _openFailures >= OpenTries;
            _openRetryAt = Time.GetTicksMsec() / 1000.0 + 1.0;
            return;
        }
        _openFailures = 0;
        _hapticOpen = true;
        MakeEffects();
    }

    /// <summary>The device's gain and autocentre set, and every effect it supports made and started.</summary>
    private unsafe void MakeEffects()
    {
        _features = SDL_GetHapticFeatures(_haptic);
        if ((_features & SDL_HAPTIC_GAIN) != 0) SDL_SetHapticGain(_haptic, 100);
        // the wheel's own centring spring would fight the aligning torque
        if ((_features & SDL_HAPTIC_AUTOCENTER) != 0) SDL_SetHapticAutocenter(_haptic, 0);

        _constant = Start(SDL_HAPTIC_CONSTANT, () =>
        {
            var e = new SDL_HapticEffect();
            e.constant.type = SDL_HapticEffectType.SDL_HAPTIC_CONSTANT;
            e.constant.direction.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
            e.constant.length = SDL_HAPTIC_INFINITY;
            return e;
        }, run: true);
        _road = Start(SDL_HAPTIC_SINE, () => Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, 50, 0f, SDL_HAPTIC_INFINITY), run: true);
        _engine = Start(SDL_HAPTIC_SINE, () => Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, 60, 0f, SDL_HAPTIC_INFINITY), run: true);
        _knock = Start(SDL_HAPTIC_SINE, () => Periodic(SDL_HapticEffectType.SDL_HAPTIC_SINE, 70, 0f, 160), run: false);
        _damper = Start(SDL_HAPTIC_DAMPER, () => Condition(SDL_HapticEffectType.SDL_HAPTIC_DAMPER, 0f), run: true);
        _friction = Start(SDL_HAPTIC_FRICTION, () => Condition(SDL_HapticEffectType.SDL_HAPTIC_FRICTION, 0f), run: true);
        _sentConstant = _sentRoad = _sentDamper = _sentFriction = _sentEngine = float.NaN;
        GD.Print($"[wheel] force feedback on {SDL_GetHapticName(_haptic)}: features 0x{_features:x}, "
            + $"constant {(int)_constant >= 0}, road {(int)_road >= 0}, engine {(int)_engine >= 0}, knock {(int)_knock >= 0}, "
            + $"damper {(int)_damper >= 0}, friction {(int)_friction >= 0}");
    }

    /// <summary>Creates (and with <paramref name="run"/>, starts) an effect the device supports; −1 otherwise.</summary>
    private unsafe SDL_HapticEffectID Start(uint feature, Func<SDL_HapticEffect> make, bool run)
    {
        if ((_features & feature) == 0) return (SDL_HapticEffectID)(-1);
        var e = make();
        var id = SDL_CreateHapticEffect(_haptic, &e);
        if ((int)id < 0)
        {
            GD.PushWarning($"[wheel] force feedback: effect 0x{feature:x} refused: {SDL_GetError()}");
            return id;
        }
        if (run && !SDL_RunHapticEffect(_haptic, id, SDL_HAPTIC_INFINITY))
            GD.PushWarning($"[wheel] force feedback: effect 0x{feature:x} would not run: {SDL_GetError()}");
        return id;
    }

    private unsafe void CloseHaptic()
    {
        if (_haptic != null)
        {
            SDL_StopHapticEffects(_haptic);
            SDL_CloseHaptic(_haptic);
        }
        _haptic = null;
        _hapticOpen = false;
        _constant = _road = _engine = _knock = _damper = _friction = (SDL_HapticEffectID)(-1);
        _sentConstant = _sentRoad = _sentDamper = _sentFriction = _sentEngine = float.NaN;
    }

    private static short Level(float v) => (short)Math.Clamp(MathF.Round(v * 32767f), -32767f, 32767f);

    private static SDL_HapticEffect Periodic(SDL_HapticEffectType wave, ushort periodMs, float magnitude, uint length)
    {
        var e = new SDL_HapticEffect();
        e.periodic.type = wave;
        e.periodic.direction.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
        e.periodic.length = length;
        e.periodic.period = periodMs;
        e.periodic.magnitude = Level(magnitude);
        return e;
    }

    /// <summary>A damper or friction on the steering axis, the same both ways, at <paramref name="strength"/> 0..1.</summary>
    private static SDL_HapticEffect Condition(SDL_HapticEffectType kind, float strength)
    {
        var e = new SDL_HapticEffect();
        e.condition.type = kind;
        e.condition.direction.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
        e.condition.length = SDL_HAPTIC_INFINITY;
        short coeff = Level(Math.Clamp(strength, 0f, 1f));
        e.condition.right_coeff[0] = coeff;
        e.condition.left_coeff[0] = coeff;
        e.condition.right_sat[0] = 0xFFFF;
        e.condition.left_sat[0] = 0xFFFF;
        return e;
    }
}
