using Godot;

namespace UnitSport.Core;

/// <summary>Which kind of device the player last touched. Drives prompts and pad-only behaviour.</summary>
public enum InputDevice
{
    KeyboardMouse,
    Gamepad,
}

/// <summary>
/// The one place gameplay reads controls from: keyboard, mouse and gamepad through named
/// actions, so a controller works everywhere a key did.
///
/// <para>
/// The actions are registered in code rather than in <c>project.godot</c>'s <c>[input]</c>
/// section. They are still ordinary <see cref="InputMap"/> actions — rebindable, and visible to
/// anything that asks for them by name — but keyboard bindings use <b>physical</b> keycodes, as
/// the controllers always have, so W is the key under the left ring finger on AZERTY too. The
/// serialised form of that in <c>project.godot</c> is an opaque object dump that is easy to get
/// subtly wrong by hand.
/// </para>
///
/// <para>
/// Everything returns neutral while <see cref="UiFocus.TextEntryActive"/>, which is the check
/// every controller used to repeat for itself. It is also the seam a VR backend plugs into
/// later: an OpenXR action map feeding these same properties, not a second input path through
/// every controller.
/// </para>
///
/// <para>
/// Registered as a node (<see cref="Install"/>) only so it can watch raw events for
/// <see cref="LastDevice"/>; the queries themselves are static.
/// </para>
/// </summary>
public partial class PlayerInput : Node
{
    // --- movement ---
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string LookLeft = "look_left";
    public const string LookRight = "look_right";
    public const string LookUp = "look_up";
    public const string LookDown = "look_down";
    public const string Jump = "jump";
    public const string Sprint = "sprint";
    public const string CrouchSlide = "crouch_slide";

    // --- mounted ---
    public const string Throttle = "throttle";
    public const string Brake = "brake";
    public const string TuckBoost = "tuck_boost";
    public const string Trick = "trick";
    public const string LookBehind = "look_behind";
    public const string Boost = "boost";

    // --- free-fly camera ---
    public const string FlyUp = "fly_up";
    public const string FlyDown = "fly_down";
    public const string FlyBoost = "fly_boost";

    // --- game shortcuts ---
    public const string InteractMount = "interact_mount";
    public const string EngineToggle = "engine_toggle";
    public const string CameraToggle = "camera_toggle";
    public const string ToggleMode = "toggle_mode";
    public const string Teleport = "teleport";
    public const string Menu = "menu";

    // --- items (on foot) ---
    public const string UseItem = "use_item";
    /// <summary>Hold on foot outdoors to collect stone, water or firewood (<see cref="Loot.Gathering"/>).</summary>
    public const string Gather = "gather";
    public const string AimItem = "aim_item";
    public const string Inventory = "inventory";
    public const string QuickWheel = "quick_wheel";
    public const string NextItem = "next_item";
    public const string PrevItem = "prev_item";
    /// <summary>Opens the field journal of birds seen and bagged (<see cref="Birds.BirdJournal"/>).</summary>
    public const string BirdJournal = "bird_journal";

    /// <summary>Right-stick turn rate at full deflection and sensitivity 1, radians per second.</summary>
    public const float StickTurnRate = 3.0f;

    /// <summary>What the player touched last. Updated from raw events; a tiny stick drift does not count.</summary>
    public static InputDevice LastDevice { get; private set; } = InputDevice.KeyboardMouse;

    /// <summary>True when the player cannot be steering, because a text field has the keyboard.</summary>
    private static bool Blocked => UiFocus.TextEntryActive;

    /// <summary>
    /// Left stick / WASD as a vector, x right, y <b>back</b> (so forward is −y, the convention the
    /// controllers already used for <c>input.Y -= 1</c> on W). Length up to 1: a half-tilted stick
    /// is a half-speed request, which is what makes a pad walk rather than only sprint.
    /// </summary>
    public static Vector2 Move => Blocked
        ? Vector2.Zero
        : Input.GetVector(MoveLeft, MoveRight, MoveForward, MoveBack);

    /// <summary>
    /// Right-stick look for this frame, in radians per second: x yaw right, y pitch down.
    /// Squared response so small corrections are fine and a full push still turns quickly;
    /// sensitivity and invert-Y come from <see cref="GameSettings"/>. The mouse is not here —
    /// it arrives as motion events, which is already a per-event delta, not a rate.
    /// </summary>
    public static Vector2 LookRate
    {
        get
        {
            if (Blocked) return Vector2.Zero;
            var v = Input.GetVector(LookLeft, LookRight, LookUp, LookDown);
            float m = v.Length();
            if (m < 1e-4f) return Vector2.Zero;
            var s = GameSettings.Current;
            v = v / m * (m * m) * StickTurnRate * s.StickSensitivity;
            if (s.InvertY) v.Y = -v.Y;
            return v;
        }
    }

    /// <summary>Held, for buttons. For an axis-bound action it means past the deadzone.</summary>
    public static bool Held(string action) => !Blocked && Input.IsActionPressed(action);

    /// <summary>0..1 — a trigger's travel, or 1 for a pressed key.</summary>
    public static float Strength(string action) => Blocked ? 0f : Input.GetActionStrength(action);

    /// <summary>
    /// Steering axis −1 left .. +1 right, from the left stick or A/D. Kept separate from
    /// <see cref="Move"/> because a vehicle only wants the one axis, unnormalised.
    /// </summary>
    public static float Steer => Blocked ? 0f : Input.GetAxis(MoveLeft, MoveRight);

    /// <summary>
    /// Rumble on every connected pad. Silently nothing on keyboard, or when vibration is off in
    /// the settings. <paramref name="weak"/> is the high-frequency motor, <paramref name="strong"/>
    /// the heavy one.
    /// </summary>
    public static void Rumble(float weak, float strong, float seconds)
    {
        if (!GameSettings.Current.Vibration || LastDevice != InputDevice.Gamepad) return;
        foreach (int pad in Input.GetConnectedJoypads())
            Input.StartJoyVibration(pad, Mathf.Clamp(weak, 0, 1), Mathf.Clamp(strong, 0, 1), seconds);
    }

    /// <summary>Adds the tracker node and registers the default bindings. Idempotent.</summary>
    public static void Install(Node root)
    {
        RegisterActions();
        if (root.GetNodeOrNull("PlayerInput") == null)
            root.AddChild(new PlayerInput { Name = "PlayerInput" });
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        GameSettings.Changed += ApplyDeadzones;
        ApplyDeadzones();
    }

    public override void _ExitTree() => GameSettings.Changed -= ApplyDeadzones;

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventJoypadButton:
                LastDevice = InputDevice.Gamepad;
                break;
            // a resting stick reports small noise forever; only a real push switches device
            case InputEventJoypadMotion m when Mathf.Abs(m.AxisValue) > 0.5f:
                LastDevice = InputDevice.Gamepad;
                break;
            case InputEventKey:
            case InputEventMouseButton:
            case InputEventMouseMotion { Relative: var r } when r.LengthSquared() > 4f:
                LastDevice = InputDevice.KeyboardMouse;
                break;
        }
    }

    private static void ApplyDeadzones()
    {
        float dz = GameSettings.Current.StickDeadzone;
        foreach (var a in new[] { MoveForward, MoveBack, MoveLeft, MoveRight, LookLeft, LookRight, LookUp, LookDown })
            if (InputMap.HasAction(a)) InputMap.ActionSetDeadzone(a, dz);
    }

    // ------------------------------------------------------------------------------------
    // default bindings
    // ------------------------------------------------------------------------------------

    private static void RegisterActions()
    {
        Bind(MoveForward, Keys(Key.W), Axis(JoyAxis.LeftY, -1));
        Bind(MoveBack, Keys(Key.S), Axis(JoyAxis.LeftY, 1));
        Bind(MoveLeft, Keys(Key.A), Axis(JoyAxis.LeftX, -1));
        Bind(MoveRight, Keys(Key.D), Axis(JoyAxis.LeftX, 1));

        Bind(LookLeft, Axis(JoyAxis.RightX, -1));
        Bind(LookRight, Axis(JoyAxis.RightX, 1));
        Bind(LookUp, Axis(JoyAxis.RightY, -1));
        Bind(LookDown, Axis(JoyAxis.RightY, 1));

        Bind(Jump, Keys(Key.Space), Button(JoyButton.A));
        Bind(Sprint, Keys(Key.Shift), Button(JoyButton.LeftStick));
        Bind(CrouchSlide, Keys(Key.Ctrl, Key.C), Button(JoyButton.B));

        // Triggers rest at 0 and travel to 1, so a small deadzone keeps a resting trigger silent
        // without eating the first third of its travel.
        Bind(Throttle, Keys(Key.W), Axis(JoyAxis.TriggerRight, 1));
        Bind(Brake, Keys(Key.S), Axis(JoyAxis.TriggerLeft, 1));
        Bind(TuckBoost, Keys(Key.Shift), Button(JoyButton.X));
        Bind(Trick, Keys(Key.F), Button(JoyButton.RightShoulder));
        Bind(LookBehind, Keys(Key.B));
        Bind(Boost, Keys(Key.Q), Button(JoyButton.LeftShoulder));

        Bind(FlyUp, Keys(Key.Space, Key.E), Button(JoyButton.A), Axis(JoyAxis.TriggerRight, 1));
        Bind(FlyDown, Keys(Key.Shift, Key.Q), Button(JoyButton.B), Axis(JoyAxis.TriggerLeft, 1));
        Bind(FlyBoost, Keys(Key.Ctrl), Button(JoyButton.LeftStick));

        Bind(InteractMount, Keys(Key.E), Button(JoyButton.Y));
        Bind(EngineToggle, Keys(Key.I), Button(JoyButton.DpadUp));
        Bind(CameraToggle, Keys(Key.V), Button(JoyButton.RightStick));
        Bind(ToggleMode, Keys(Key.T), Button(JoyButton.DpadDown));
        // The place search is a text box; a pad can open it but not type in it, so it stays
        // keyboard-only rather than trapping a controller player in a field they cannot use.
        Bind(Teleport, Keys(Key.Tab));
        Bind(Menu, Keys(Key.Escape), Button(JoyButton.Start));

        // Items are an on-foot thing, so they reuse the shoulders that only mean something
        // mounted (RB trick, LB boost). K and X are unused; E and I, the usual inventory keys,
        // already get in vehicles and start engines.
        Bind(UseItem, Mouse(MouseButton.Left), Button(JoyButton.RightShoulder));
        Bind(AimItem, Mouse(MouseButton.Right), Button(JoyButton.LeftShoulder));
        Bind(Inventory, Keys(Key.K), Button(JoyButton.Back));
        Bind(QuickWheel, Keys(Key.X), Button(JoyButton.DpadLeft));
        // pad X is tuck/sprint only when mounted, so on foot it is free, as RB/LB are for items
        Bind(Gather, Keys(Key.G), Button(JoyButton.X));
        Bind(NextItem, Mouse(MouseButton.WheelDown), Button(JoyButton.DpadRight));
        Bind(PrevItem, Mouse(MouseButton.WheelUp));
        Bind(BirdJournal, Keys(Key.J));

        // Godot's built-in UI actions map the D-pad but not the face buttons, so a pad could
        // walk a menu's focus and never press anything. A confirms and B backs out, as on
        // every console; the stick navigates too, for anyone who reaches for it first.
        Bind("ui_accept", Button(JoyButton.A));
        Bind("ui_cancel", Button(JoyButton.B));
        Bind("ui_up", Axis(JoyAxis.LeftY, -1));
        Bind("ui_down", Axis(JoyAxis.LeftY, 1));
        Bind("ui_left", Axis(JoyAxis.LeftX, -1));
        Bind("ui_right", Axis(JoyAxis.LeftX, 1));
    }

    private static InputEvent[] Keys(params Key[] keys) =>
        keys.Select(k => (InputEvent)new InputEventKey { PhysicalKeycode = k }).ToArray();

    private static InputEvent[] Mouse(MouseButton b) =>
        new InputEvent[] { new InputEventMouseButton { ButtonIndex = b } };

    private static InputEvent[] Button(JoyButton b) =>
        new InputEvent[] { new InputEventJoypadButton { ButtonIndex = b, Device = -1 } };

    private static InputEvent[] Axis(JoyAxis axis, float sign) =>
        new InputEvent[] { new InputEventJoypadMotion { Axis = axis, AxisValue = sign, Device = -1 } };

    /// <summary>
    /// Creates the action if absent and adds any binding it does not already have. A binding the
    /// player (or a future rebind screen) has already changed is left alone.
    /// </summary>
    private static void Bind(string action, params InputEvent[][] groups)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action, 0.2f);
        foreach (var group in groups)
            foreach (var e in group)
                if (!InputMap.ActionHasEvent(action, e)) InputMap.ActionAddEvent(action, e);
    }

    /// <summary>
    /// Gives keyboard/pad focus to the first usable button under <paramref name="root"/>, so a
    /// menu opened with a controller can be driven with the D-pad and A straight away. Godot's
    /// built-in <c>ui_*</c> actions already map the pad; they only need somewhere to start.
    /// </summary>
    public static void FocusFirst(Control root)
    {
        var target = FindFocusable(root);
        target?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static Control? FindFocusable(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control { Visible: false }) continue;
            if (child is BaseButton { Disabled: false } b && b.FocusMode != Control.FocusModeEnum.None)
                return b;
            if (FindFocusable(child) is { } found) return found;
        }
        return null;
    }
}
