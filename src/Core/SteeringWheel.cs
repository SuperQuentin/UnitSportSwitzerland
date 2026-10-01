using Godot;
using SDL;
using static SDL.SDL3;

namespace UnitSport.Core;

/// <summary>
/// A sim-racing wheel and its pedals, read through SDL3 (issue #68). Godot's joypad API caps a
/// device at ten axes, knows nothing of which end a pedal rests at, and has no force feedback, so
/// the wheel is read here instead and handed to the game through <see cref="PlayerInput"/>:
///
/// <list type="bullet">
/// <item><see cref="Angle"/>: the real wheel's rotation, radians, from the range set in its driver
/// (<see cref="WheelSettings.RangeDeg"/>). A car steers from it 1:1 (<see cref="PlayerInput.WheelAngle"/>);
/// every other mount gets a plain −1..1 over ±<see cref="PlainSpanDeg"/>.</item>
/// <item>pedals: <see cref="Strength"/> of the throttle, brake and clutch actions, and <see cref="Handbrake"/>;</item>
/// <item>buttons: raised as ordinary <see cref="InputEventAction"/>s, so a wheel button is the key
/// it is bound to everywhere a key would work.</item>
/// </list>
///
/// <para>
/// Godot sees the same device as a joypad and would feed its axes into the pad bindings (the
/// wheel strafing, a released pedal read as a stick held back), so the claimed wheel's Godot
/// device is taken out of every pad binding (<see cref="PlayerInput.SetIgnoredJoypads"/>).
/// Local only: nothing here is replicated, the car's steering already is.
/// </para>
/// </summary>
public partial class SteeringWheel : Node
{
    /// <summary>Virtual button index of hat 0 up; right, down, left follow, then hat 1…</summary>
    public const int HatBase = 1000;
    /// <summary>A mount that is not a car turns fully at this much wheel either way, degrees.</summary>
    public const float PlainSpanDeg = 90f;

    private static SteeringWheel? _instance;

    /// <summary>A wheel is claimed, enabled and not being assigned in the settings.</summary>
    public static bool Active => _instance is { _claimed: true } && (Settings.Enabled || Simulated) && !Assigning;

    /// <summary>The claimed device's SDL name, or null.</summary>
    public static string? DeviceName => _instance is { _claimed: true } w ? w._name : null;

    /// <summary>Every joystick SDL can see, by name, for the device picker.</summary>
    public static IReadOnlyList<string> Devices => _instance?._devices ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>SDL could not start (no native library, a headless run): there is no wheel support.</summary>
    public static bool Unavailable => !Simulated && _instance is not { _sdl: true };

    /// <summary>The wheel's rotation from centre, radians, + right, as far as the real one turns.</summary>
    public static float Angle { get; private set; }
    public static float Throttle { get; private set; }
    public static float Brake { get; private set; }
    public static float Clutch { get; private set; }
    public static float Handbrake { get; private set; }

    /// <summary>Raw axes −1..1 and buttons (hats after <see cref="HatBase"/>), for the settings panel.</summary>
    public static IReadOnlyList<float> RawAxes => _instance?._axes ?? Array.Empty<float>();
    public static bool RawButton(int index) => _instance?._pressed.Contains(index) == true;
    public static IReadOnlyCollection<int> PressedButtons => _instance?._pressed ?? (IReadOnlyCollection<int>)Array.Empty<int>();

    /// <summary>The settings panel is waiting for a control to move: the wheel drives nothing meanwhile.</summary>
    public static bool Assigning { get; set; }

    /// <summary>
    /// <c>--fakewheel</c>: no device, a wheel swept ±<see cref="FakeSweepDeg"/> every
    /// <see cref="FakePeriod"/> s with a steady throttle, so the wheel path can be checked on a
    /// machine without one (<c>--wheelwatch</c>).
    /// </summary>
    public static readonly bool Simulated = Array.IndexOf(OS.GetCmdlineUserArgs(), "--fakewheel") >= 0;
    public const float FakeSweepDeg = 180f, FakePeriod = 4f, FakeThrottle = 0.35f;
    private double _fakeTime;

    private static WheelSettings Settings => GameSettings.Current.Wheel;

    private bool _sdl;
    private bool _claimed;
    private unsafe SDL_Joystick* _joy;
    private string _name = "";
    private float[] _axes = Array.Empty<float>();
    /// <summary>
    /// Axes that have read anything but 0. Until a wheel sends its first report every axis reads 0 —
    /// the HORI's pedals rest at −1, so 0 would be gas and brake held half way.
    /// </summary>
    private bool[] _reported = Array.Empty<bool>();
    private readonly HashSet<int> _pressed = new();
    /// <summary>Actions this wheel is holding down, released when the button is or the wheel goes.</summary>
    private readonly HashSet<string> _held = new();
    private string[] _devices = Array.Empty<string>();
    private double _scanTimer;
    private List<int> _ignoredLogged = new();

    /// <summary>Adds the reader under <paramref name="root"/>; nothing on a headless run. Idempotent.</summary>
    public static void Install(Node root)
    {
        if (DisplayServer.GetName() == "headless" || root.GetNodeOrNull("SteeringWheel") != null) return;
        root.AddChild(new SteeringWheel { Name = "SteeringWheel" });
    }

    /// <summary>
    /// The in-game steering-wheel angle for a vehicle whose wheel turns <paramref name="lockToLock"/>
    /// radians from lock to lock. 1:1 with the real wheel when its range covers the vehicle's lock;
    /// when it does not (a 900° wheel in a 1300° truck), the real range is stretched over the lock so
    /// full lock can still be reached.
    /// </summary>
    public static float GameAngle(float lockToLock) => GameAngle(Angle, Mathf.DegToRad(Settings.RangeDeg), lockToLock);

    /// <summary><see cref="GameAngle(float)"/> for a given wheel angle and range, radians.</summary>
    public static float GameAngle(float angle, float range, float lockToLock) =>
        range >= lockToLock ? angle : angle * lockToLock / range;

    /// <summary>The pedal behind an action, 0..1; 0 for anything else or without a wheel.</summary>
    public static float Strength(string action)
    {
        if (!Active) return 0f;
        return action switch
        {
            PlayerInput.Throttle => Throttle,
            PlayerInput.Brake => Brake,
            // the trucks' clutch action (#70)
            "clutch" => Clutch,
            _ => 0f,
        };
    }

    /// <summary>Claims <paramref name="name"/> from now on, with its preset if its bindings were for another device.</summary>
    public static void Select(string name)
    {
        if (name != Settings.Device) WheelPresets.Reset(Settings, name);
        GameSettings.Current.Commit();
        _instance?.Release();
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _instance = this;
        if (Simulated)
        {
            (_claimed, _name) = (true, "Simulated wheel");
            GD.Print("[wheel] simulated wheel (--fakewheel)");
            return;
        }
        try
        {
            // no SDL window ever has focus here, so without this SDL would drop the wheel's input
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            _sdl = SDL_Init(SDL_InitFlags.SDL_INIT_JOYSTICK);
            if (!_sdl) GD.PushWarning($"[wheel] SDL_Init failed: {SDL_GetError()}");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            GD.PushWarning($"[wheel] SDL3 not available, no steering wheel support: {e.Message}");
        }
        Input.JoyConnectionChanged += OnGodotJoypads;
    }

    public override void _ExitTree()
    {
        if (!Simulated) Input.JoyConnectionChanged -= OnGodotJoypads;
        Release();
        if (_sdl) SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_JOYSTICK);
        _sdl = false;
        if (_instance == this) _instance = null;
    }

    public override void _Process(double delta)
    {
        if (Simulated)
        {
            _fakeTime += delta;
            Angle = Mathf.DegToRad(FakeSweepDeg) * Mathf.Sin((float)(_fakeTime * Mathf.Tau / FakePeriod));
            Throttle = FakeThrottle;
            return;
        }
        if (!_sdl) return;
        SDL_UpdateJoysticks();

        _scanTimer -= delta;
        if (_scanTimer <= 0)
        {
            _scanTimer = 1.0;
            Scan();
        }
        var s = Settings;
        // off, or unplugged: Godot gets the device back as a plain joypad
        if (_claimed && (!s.Enabled || !Connected())) Release();
        if (!_claimed) return;

        Read();
        if (Assigning)
        {
            Neutral();
            return;
        }

        float steer = s.SteerAxis < _axes.Length ? _axes[s.SteerAxis] : 0f;
        Angle = (s.SteerInvert ? -steer : steer) * Mathf.DegToRad(s.RangeDeg) * 0.5f;
        Throttle = Pedal(s.Throttle);
        Brake = Pedal(s.Brake);
        Clutch = Pedal(s.Clutch);
        float lever = Pedal(s.Handbrake);
        foreach (var (button, target) in s.Buttons)
            if (target == WheelSettings.HandbrakeButton && _pressed.Contains(button)) lever = 1f;
        Handbrake = lever;
        FeedButtons(s);
    }

    private float Pedal(WheelAxis a) =>
        a.Bound && a.Axis < _axes.Length && _reported[a.Axis] ? a.Read(_axes[a.Axis]) : 0f;

    private unsafe bool Connected() => _joy != null && SDL_JoystickConnected(_joy);

    // ---------------------------------------------------------------------------------------
    // devices
    // ---------------------------------------------------------------------------------------

    /// <summary>Lists what SDL sees and, with nothing claimed, claims the configured wheel or the first one.</summary>
    private unsafe void Scan()
    {
        int count;
        var ids = SDL_GetJoysticks(&count);
        if (ids == null) return;
        var names = new List<string>();
        SDL_JoystickID pick = default;
        bool found = false;
        var s = Settings;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var id = ids[i];
                string name = SDL_GetJoystickNameForID(id) ?? $"Joystick {i}";
                names.Add(name);
                if (found || _claimed || !s.Enabled) continue;
                bool wanted = s.Device.Length > 0
                    ? name == s.Device
                    : SDL_GetJoystickTypeForID(id) == SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL || WheelPresets.For(name) != null;
                if (wanted) { pick = id; found = true; }
            }
        }
        finally
        {
            SDL_free(ids);
        }
        _devices = names.ToArray();
        if (found) Claim(pick);
    }

    private unsafe void Claim(SDL_JoystickID id)
    {
        _joy = SDL_OpenJoystick(id);
        if (_joy == null)
        {
            GD.PushWarning($"[wheel] could not open {SDL_GetJoystickNameForID(id)}: {SDL_GetError()}");
            return;
        }
        _name = SDL_GetJoystickName(_joy) ?? "wheel";
        _claimed = true;
        var s = Settings;
        if (s.Device != _name)
        {
            WheelPresets.Reset(s, _name);
            GameSettings.Current.Commit();
        }
        FixRestEnds(s);
        GD.Print($"[wheel] {_name}: {SDL_GetNumJoystickAxes(_joy)} axes, {SDL_GetNumJoystickButtons(_joy)} buttons, "
            + $"{SDL_GetNumJoystickHats(_joy)} hats, range {s.RangeDeg:F0}°"
            + (s.Preset.Length > 0 ? $", preset {s.Preset}" : ""));
        IgnoreInGodot();
    }

    /// <summary>
    /// A preset's pedal that SDL says rests at its To end is the other way round on this driver:
    /// swap it, or the car would start with the brake floored.
    /// </summary>
    private unsafe void FixRestEnds(WheelSettings s)
    {
        foreach (var a in new[] { s.Throttle, s.Brake, s.Clutch, s.Handbrake })
        {
            short rest;
            if (!a.Bound || !SDL_GetJoystickAxisInitialState(_joy, a.Axis, &rest)) continue;
            float r = rest / 32767f;
            if (MathF.Abs(r - a.To) < 0.4f && MathF.Abs(r - a.From) > 0.6f) (a.From, a.To) = (a.To, a.From);
        }
    }

    private unsafe void Release()
    {
        if (_joy != null) SDL_CloseJoystick(_joy);
        _joy = null;
        if (_claimed) GD.Print($"[wheel] {_name} released");
        _claimed = false;
        _axes = Array.Empty<float>();
        _reported = Array.Empty<bool>();
        _pressed.Clear();
        Neutral();
        PlayerInput.SetIgnoredJoypads(Array.Empty<int>());
    }

    private void Neutral()
    {
        Angle = Throttle = Brake = Clutch = Handbrake = 0f;
        foreach (string action in _held) Raise(action, false);
        _held.Clear();
    }

    private unsafe void Read()
    {
        int axes = Math.Max(0, SDL_GetNumJoystickAxes(_joy));
        if (_axes.Length != axes) (_axes, _reported) = (new float[axes], new bool[axes]);
        for (int i = 0; i < axes; i++)
        {
            _axes[i] = Math.Clamp(SDL_GetJoystickAxis(_joy, i) / 32767f, -1f, 1f);
            _reported[i] |= _axes[i] != 0f;
        }

        _pressed.Clear();
        int buttons = SDL_GetNumJoystickButtons(_joy);
        for (int i = 0; i < buttons; i++)
            if (SDL_GetJoystickButton(_joy, i)) _pressed.Add(i);
        int hats = SDL_GetNumJoystickHats(_joy);
        for (int h = 0; h < hats; h++)
        {
            byte v = SDL_GetJoystickHat(_joy, h);
            // SDL_HAT_UP 1, RIGHT 2, DOWN 4, LEFT 8
            for (int bit = 0; bit < 4; bit++)
                if ((v & (1 << bit)) != 0) _pressed.Add(HatBase + h * 4 + bit);
        }
    }

    // ---------------------------------------------------------------------------------------
    // buttons → actions
    // ---------------------------------------------------------------------------------------

    /// <summary>Presses and releases actions on the button edges, as a key would.</summary>
    private void FeedButtons(WheelSettings s)
    {
        var down = new HashSet<string>();
        foreach (var (button, action) in s.Buttons)
            if (_pressed.Contains(button) && action != WheelSettings.HandbrakeButton && InputMap.HasAction(action))
                down.Add(action);
        foreach (string action in down)
            if (_held.Add(action)) Raise(action, true);
        _held.RemoveWhere(action =>
        {
            if (down.Contains(action)) return false;
            Raise(action, false);
            return true;
        });
    }

    private static void Raise(string action, bool pressed) =>
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed, Strength = pressed ? 1f : 0f });

    // ---------------------------------------------------------------------------------------
    // Godot's view of the same device
    // ---------------------------------------------------------------------------------------

    private void OnGodotJoypads(long device, bool connected)
    {
        if (_claimed) IgnoreInGodot();
    }

    /// <summary>Takes the claimed wheel out of Godot's pad bindings: matched by USB ids, else by name.</summary>
    private unsafe void IgnoreInGodot()
    {
        ushort vendor = SDL_GetJoystickVendor(_joy), product = SDL_GetJoystickProduct(_joy);
        var ignored = new List<int>();
        foreach (int pad in Input.GetConnectedJoypads())
        {
            var info = Input.GetJoyInfo(pad);
            bool byIds = vendor != 0
                && info.TryGetValue("vendor_id", out var v) && info.TryGetValue("product_id", out var p)
                && v.AsInt32() == vendor && p.AsInt32() == product;
            string godotName = Input.GetJoyName(pad);
            bool byName = godotName.Length > 0
                && (godotName.Contains(_name, StringComparison.OrdinalIgnoreCase) || _name.Contains(godotName, StringComparison.OrdinalIgnoreCase));
            if (byIds || byName) ignored.Add(pad);
        }
        if (!ignored.SequenceEqual(_ignoredLogged))
        {
            _ignoredLogged = ignored;
            GD.Print(ignored.Count == 0
                ? $"[wheel] Godot has no joypad matching {_name} ({vendor:x4}:{product:x4})"
                : $"[wheel] Godot joypad {string.Join(", ", ignored.Select(p => $"{p} '{Input.GetJoyName(p)}'"))} left to SDL");
        }
        PlayerInput.SetIgnoredJoypads(ignored);
    }
}
