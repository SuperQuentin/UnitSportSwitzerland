using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// The Touch controllers replayed as a gamepad, so every <see cref="Core.PlayerInput"/> action
/// (all bound for "any pad") works in VR without a second input path through the game.
///
/// <para>
/// Layout (docs/notes/xr/controls.md):
/// left stick = left stick, walking where the head looks on foot; A B X Y = A B X Y;
/// left stick click = L3 (sprint); right stick left/right = snap turn (<see cref="XrRig"/>),
/// up = D-pad up, down = D-pad right; right stick click = R3, held = recentre.
/// Triggers are the triggers when mounted (throttle, brake) and the shoulders on foot (use and
/// aim an item, the way a hand would); the grips are the shoulders. The left menu button is
/// Start, held it is Back (the inventory).
/// </para>
/// </summary>
internal sealed class XrPad
{
    /// <summary>A device number no real pad gets; the bindings listen to every device.</summary>
    private const int Device = 7;
    private const float MenuHold = 0.5f;

    private readonly XRController3D _left, _right;
    private readonly Dictionary<JoyAxis, float> _axes = new();
    private readonly Dictionary<JoyButton, bool> _buttons = new();
    private readonly HashSet<JoyButton> _pulses = new();

    private float _menuHeld;
    private bool _menuLong;
    private bool _r3Was;
    private bool _dpadUpWas, _dpadDownWas;

    /// <summary>Set by the rig while the right stick is held for a recentre: R3 is not sent then.</summary>
    public bool RecentreHeld { get; set; }

    /// <summary>Set by the hands (#243) while a grip holds the wheel or worked a door: it is not a shoulder then.</summary>
    public bool LeftGripBusy { get; set; }
    public bool RightGripBusy { get; set; }

    public XrPad(XRController3D left, XRController3D right)
    {
        _left = left;
        _right = right;
    }

    /// <param name="calibrated">The head in the anchor's frame, for walking where you look.</param>
    /// <param name="uiActive">The right hand is pointing at the UI panel: its trigger is a click, not a pull.</param>
    public void Update(FootPlayer? player, Transform3D calibrated, bool uiActive, float dt)
    {
        // pulses (a tap of R3, a long menu press) go up the frame after they went down, so the
        // action is seen pressed for at least one frame
        foreach (var b in _pulses) Button(b, false);
        _pulses.Clear();

        bool onFoot = player == null || player.Ride == RideKind.OnFoot && player.RidingWith == 0;
        // the pigeon (#217) drops on the right trigger, like using an item on foot; it flaps on A
        bool shoulders = onFoot || player?.Ride == RideKind.Pigeon;
        // the triggers change role on mounting: the prompts name them again (#435)
        if (shoulders != TriggersAsShoulders)
        {
            TriggersAsShoulders = shoulders;
            Core.PlayerInput.HintsChanged();
        }

        // --- left stick: on foot, forward is where the head looks, not where the body faces ---
        var stick = _left.GetVector2("primary");
        if (onFoot && player != null)
        {
            float yaw = XrRig.YawOf(calibrated.Basis);
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            stick = new Vector2(stick.X * c - stick.Y * s, stick.X * s + stick.Y * c);
        }
        Axis(JoyAxis.LeftX, stick.X);
        Axis(JoyAxis.LeftY, -stick.Y);

        // --- triggers and grips ---
        float lt = _left.GetFloat("trigger"), rt = uiActive ? 0f : _right.GetFloat("trigger");
        float lg = LeftGripBusy ? 0f : _left.GetFloat("grip"), rg = RightGripBusy ? 0f : _right.GetFloat("grip");
        if (shoulders)
        {
            Axis(JoyAxis.TriggerLeft, 0f);
            Axis(JoyAxis.TriggerRight, 0f);
            Button(JoyButton.LeftShoulder, lt > 0.6f || lg > 0.6f);
            Button(JoyButton.RightShoulder, rt > 0.6f || rg > 0.6f);
        }
        else
        {
            Axis(JoyAxis.TriggerLeft, lt);
            Axis(JoyAxis.TriggerRight, rt);
            Button(JoyButton.LeftShoulder, lg > 0.6f);
            Button(JoyButton.RightShoulder, rg > 0.6f);
        }

        // --- face buttons ---
        Button(JoyButton.A, _right.IsButtonPressed("ax_button"));
        Button(JoyButton.B, _right.IsButtonPressed("by_button"));
        Button(JoyButton.X, _left.IsButtonPressed("ax_button"));
        Button(JoyButton.Y, _left.IsButtonPressed("by_button"));
        Button(JoyButton.LeftStick, _left.IsButtonPressed("primary_click"));

        // --- right stick up / down: the two D-pad directions that matter most ---
        var r = _right.GetVector2("primary");
        bool up = Hysteresis(r.Y, ref _dpadUpWas), down = Hysteresis(-r.Y, ref _dpadDownWas);
        Button(JoyButton.DpadUp, up);
        Button(JoyButton.DpadRight, down);

        // --- R3: a tap is the view switch; a hold belongs to the rig's recentre ---
        bool r3 = _right.IsButtonPressed("primary_click");
        if (_r3Was && !r3 && !RecentreHeld) Pulse(JoyButton.RightStick);
        _r3Was = r3;

        // --- menu: tap = Start, hold = Back ---
        if (_left.IsButtonPressed("menu_button"))
        {
            _menuHeld += dt;
            if (_menuHeld > MenuHold && !_menuLong)
            {
                Pulse(JoyButton.Back);
                _menuLong = true;
            }
        }
        else
        {
            if (_menuHeld > 0f && !_menuLong) Pulse(JoyButton.Start);
            _menuHeld = 0f;
            _menuLong = false;
        }
    }

    /// <summary>
    /// The triggers act as the shoulders (on foot, the pigeon) rather than as the triggers
    /// (mounted). Read by <see cref="Control"/>, which names what the prompts show.
    /// </summary>
    public static bool TriggersAsShoulders { get; private set; } = true;

    /// <summary>
    /// Names controls as in another context for a moment (the controls overlay lists the vehicle
    /// groups as mounted while you stand): sets <see cref="TriggersAsShoulders"/>, returns what it was.
    /// </summary>
    public static bool AssumeShoulders(bool shoulders)
    {
        bool was = TriggersAsShoulders;
        TriggersAsShoulders = shoulders;
        return was;
    }

    /// <summary>
    /// The controller input that <see cref="Update"/> replays as this pad event, right now; null
    /// when none does (D-pad ← / ↓, Guide, the triggers' axes on foot). The reverse of the layout
    /// above (#435): change the two together.
    /// </summary>
    public static XrControl? Control(InputEvent e) => e switch
    {
        InputEventJoypadButton b => b.ButtonIndex switch
        {
            JoyButton.A => XrControl.A,
            JoyButton.B => XrControl.B,
            JoyButton.X => XrControl.X,
            JoyButton.Y => XrControl.Y,
            // on foot the trigger and the grip both press the shoulder; the trigger is the one to name
            JoyButton.LeftShoulder => TriggersAsShoulders ? XrControl.LeftTrigger : XrControl.LeftGrip,
            JoyButton.RightShoulder => TriggersAsShoulders ? XrControl.RightTrigger : XrControl.RightGrip,
            JoyButton.LeftStick => XrControl.LeftStickClick,
            JoyButton.RightStick => XrControl.RightStickClick,
            JoyButton.DpadUp => XrControl.RightStickUp,
            JoyButton.DpadRight => XrControl.RightStickDown,
            JoyButton.Start => XrControl.Menu,
            JoyButton.Back => XrControl.MenuHold,
            _ => null,
        },
        InputEventJoypadMotion m => m.Axis switch
        {
            JoyAxis.LeftX or JoyAxis.LeftY => XrControl.LeftStick,
            JoyAxis.RightX or JoyAxis.RightY => XrControl.RightStick,
            JoyAxis.TriggerLeft when !TriggersAsShoulders => XrControl.LeftTrigger,
            JoyAxis.TriggerRight when !TriggersAsShoulders => XrControl.RightTrigger,
            _ => null,
        },
        _ => null,
    };

    private static bool Hysteresis(float v, ref bool was) => was = was ? v > 0.4f : v > 0.75f;

    private void Pulse(JoyButton b)
    {
        Button(b, true);
        _pulses.Add(b);
    }

    private void Axis(JoyAxis axis, float value)
    {
        value = Mathf.Clamp(value, -1f, 1f);
        if (_axes.TryGetValue(axis, out float was) && Mathf.Abs(was - value) < 0.004f) return;
        _axes[axis] = value;
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = Device, Axis = axis, AxisValue = value });
    }

    private void Button(JoyButton button, bool pressed)
    {
        if (_buttons.TryGetValue(button, out bool was) && was == pressed) return;
        _buttons[button] = pressed;
        Input.ParseInputEvent(new InputEventJoypadButton
        {
            Device = Device,
            ButtonIndex = button,
            Pressed = pressed,
        });
    }
}
