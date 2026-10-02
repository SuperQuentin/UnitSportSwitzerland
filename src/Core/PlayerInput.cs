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
    /// <summary>Guns, in an armed craft (plane, helicopter): <see cref="Combat.CombatManager"/>.</summary>
    public const string Fire = "fire";
    /// <summary>In a car: headlights on/off, raising or folding pop-ups (<see cref="Player.Car.Headlights"/>).</summary>
    public const string LightsToggle = "lights_toggle";
    /// <summary>In an open car: soft top down/up (<see cref="Player.Car.RoofOpen"/>).</summary>
    public const string RoofToggle = "roof_toggle";
    /// <summary>In a car, truck or bus: the next / previous live radio station, through off (#179).</summary>
    public const string RadioNext = "radio_next";
    public const string RadioPrev = "radio_prev";
    /// <summary>In a car, truck or bus (driver or passenger): the radio panel, stations and CDs (#211).</summary>
    public const string RadioPanel = "radio_panel";
    // --- trucks and buses (#70) ---
    /// <summary>Couple or uncouple a trailer (<see cref="Player.Truck.Couple"/>).</summary>
    public const string Couple = "couple";
    /// <summary>A passenger moves into the free driver's seat (#158).</summary>
    public const string TakeWheel = "take_wheel";
    /// <summary>A bus kneels (lowers its door side) or rises.</summary>
    public const string Kneel = "kneel";
    /// <summary>The next destination on a bus's display.</summary>
    public const string Destination = "destination";
    /// <summary>Sequential shift up / down; in the H-pattern, the splitter high / low.</summary>
    public const string ShiftUp = "shift_up";
    public const string ShiftDown = "shift_down";
    /// <summary>Held: the clutch pedal down.</summary>
    public const string Clutch = "clutch";
    /// <summary>The H-pattern's gates, reverse and neutral.</summary>
    public static readonly string[] Gates = { "gear_1", "gear_2", "gear_3", "gear_4", "gear_5", "gear_6" };
    public const string GearReverse = "gear_r";
    public const string GearNeutral = "gear_n";
    /// <summary>The retarder stalk: 0 off, 1 exhaust brake, 2-4 the retarder.</summary>
    public const string RetarderUp = "retarder_up";
    public const string RetarderDown = "retarder_down";

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
    /// <summary>In a stopped car at a garage: open the tuning menu (<see cref="Vehicles.GarageUi"/>).</summary>
    public const string Tune = "tune";
    /// <summary>Open or shut a parked car's door without getting in (on foot beside it; never from the seat).</summary>
    public const string CarDoor = "car_door";
    /// <summary>The travel picker (<see cref="Player.RideUi"/>): mounts, equipment and, for an admin, vehicles.</summary>
    public const string RideMenu = "ride_menu";
    /// <summary>The controls overlay (<see cref="ControlsHelp"/>), built from the live bindings.</summary>
    public const string Help = "help";

    // --- items (on foot) ---
    public const string UseItem = "use_item";
    /// <summary>Hold on foot outdoors to collect stone, water or firewood (<see cref="Loot.Gathering"/>).</summary>
    public const string Gather = "gather";
    public const string AimItem = "aim_item";
    public const string Inventory = "inventory";
    public const string QuickWheel = "quick_wheel";
    /// <summary>Drops one of the item in hand on the ground; with Ctrl, the whole stack (#206).</summary>
    public const string DropItem = "drop_item";
    public const string NextItem = "next_item";
    public const string PrevItem = "prev_item";
    /// <summary>Opens the field journal of birds seen and bagged (<see cref="Birds.BirdJournal"/>).</summary>
    public const string BirdJournal = "bird_journal";

    /// <summary>Right-stick turn rate at full deflection and sensitivity 1, radians per second.</summary>
    public const float StickTurnRate = 3.0f;

    /// <summary>What the player touched last. Updated from raw events; a tiny stick drift does not count.</summary>
    public static InputDevice LastDevice
    {
        get => _lastDevice;
        private set
        {
            if (_lastDevice == value) return;
            _lastDevice = value;
            DeviceChanged?.Invoke();
        }
    }

    private static InputDevice _lastDevice = InputDevice.KeyboardMouse;

    /// <summary>Raised when the player switches between keyboard and pad, so on-screen key hints can follow.</summary>
    public static event Action? DeviceChanged;

    // Per-frame reads: a string action converts to a new StringName on every call (#221).
    private static readonly StringName NLeft = MoveLeft, NRight = MoveRight, NForward = MoveForward, NBack = MoveBack,
        NLookLeft = LookLeft, NLookRight = LookRight, NLookUp = LookUp, NLookDown = LookDown;
    private static readonly Dictionary<string, StringName> Names = new();

    private static StringName ActionName(string action)
    {
        if (!Names.TryGetValue(action, out var name)) Names[action] = name = action;
        return name;
    }

    /// <summary>True when the player cannot be steering, because a text field has the keyboard.</summary>
    private static bool Blocked => UiFocus.TextEntryActive;

    /// <summary>
    /// Left stick / WASD as a vector, x right, y <b>back</b> (so forward is −y, the convention the
    /// controllers already used for <c>input.Y -= 1</c> on W). Length up to 1: a half-tilted stick
    /// is a half-speed request, which is what makes a pad walk rather than only sprint.
    /// </summary>
    public static Vector2 Move => Blocked
        ? Vector2.Zero
        : Input.GetVector(NLeft, NRight, NForward, NBack);

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
            var v = Input.GetVector(NLookLeft, NLookRight, NLookUp, NLookDown);
            float m = v.Length();
            if (m < 1e-4f) return Vector2.Zero;
            var s = GameSettings.Current;
            v = v / m * (m * m) * StickTurnRate * s.StickSensitivity;
            if (s.InvertY) v.Y = -v.Y;
            return v;
        }
    }

    /// <summary>Held, for buttons. For an axis-bound action it means past the deadzone (a wheel's pedal: half way).</summary>
    public static bool Held(string action) =>
        !Blocked && (Input.IsActionPressed(ActionName(action)) || SteeringWheel.Strength(action) > 0.5f);

    /// <summary>0..1 — a trigger's or a pedal's travel, or 1 for a pressed key.</summary>
    public static float Strength(string action) =>
        Blocked ? 0f : Mathf.Max(Input.GetActionStrength(ActionName(action)), SteeringWheel.Strength(action));

    /// <summary>
    /// Steering axis −1 left .. +1 right, from the left stick or A/D, else a steering wheel turned
    /// ±<see cref="SteeringWheel.PlainSpanDeg"/>. Kept separate from <see cref="Move"/> because a
    /// vehicle only wants the one axis, unnormalised.
    /// </summary>
    public static float Steer
    {
        get
        {
            if (Blocked) return 0f;
            float keys = Input.GetAxis(NLeft, NRight);
            if (keys != 0f || !SteeringWheel.Active) return keys;
            return Mathf.Clamp(SteeringWheel.Angle / Mathf.DegToRad(SteeringWheel.PlainSpanDeg), -1f, 1f);
        }
    }

    /// <summary>
    /// The steering wheel's angle for a vehicle whose wheel turns <paramref name="lockToLock"/>
    /// radians lock to lock (<see cref="SteeringWheel.GameAngle"/>), + right; NaN without a wheel, or
    /// while the keys or the stick are steering, so they still can.
    /// </summary>
    public static float WheelAngle(float lockToLock) =>
        Blocked || !SteeringWheel.Active || Input.GetAxis(NLeft, NRight) != 0f
            ? float.NaN
            : SteeringWheel.GameAngle(lockToLock);

    /// <summary>The wheel's handbrake lever (or the button bound to it), 0..1.</summary>
    public static float WheelHandbrake => Blocked || !SteeringWheel.Active ? 0f : SteeringWheel.Handbrake;

    /// <summary>
    /// Rumble on every connected pad. Silently nothing on keyboard, or when vibration is off in
    /// the settings. <paramref name="weak"/> is the high-frequency motor, <paramref name="strong"/>
    /// the heavy one.
    /// </summary>
    public static void Rumble(float weak, float strong, float seconds)
    {
        // in VR the hands are the pad (#186)
        if (XR.XrSession.Active)
        {
            XR.XrSession.Rumble(weak, strong, seconds);
            return;
        }
        if (!GameSettings.Current.Vibration || LastDevice != InputDevice.Gamepad) return;
        foreach (int pad in Input.GetConnectedJoypads())
            Input.StartJoyVibration(pad, Mathf.Clamp(weak, 0, 1), Mathf.Clamp(strong, 0, 1), seconds);
    }

    /// <summary>Adds the tracker node and the steering wheel reader, and registers the default bindings. Idempotent.</summary>
    public static void Install(Node root)
    {
        RegisterActions();
        if (root.GetNodeOrNull("PlayerInput") == null)
            root.AddChild(new PlayerInput { Name = "PlayerInput" });
        SteeringWheel.Install(root);
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
        // the steering wheel is read through SDL; Godot's copy of it is not a pad
        if (e is InputEventJoypadButton or InputEventJoypadMotion && _ignoredPads.Contains(e.Device)) return;
        // VR replays the controllers as a pad, and points at the UI panel with mouse events:
        // the prompts stay on pad glyphs either way (#186)
        if (XR.XrSession.Active)
        {
            LastDevice = InputDevice.Gamepad;
            return;
        }
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
    // joypads left to SDL
    // ------------------------------------------------------------------------------------

    private static HashSet<int> _ignoredPads = new();
    /// <summary>Each action's pad bindings as registered (device −1, every pad), kept while they are retargeted.</summary>
    private static Dictionary<string, InputEvent[]>? _padBindings;

    /// <summary>
    /// Takes these Godot joypads out of every pad binding: the steering wheel, which
    /// <see cref="SteeringWheel"/> reads itself. Godot binds a pad event to one device or to all,
    /// so while any is ignored each pad binding is copied once per other connected pad; an empty
    /// set puts the all-devices bindings back. Called again when a pad connects, so it gets its copies.
    /// </summary>
    public static void SetIgnoredJoypads(IReadOnlyCollection<int> pads)
    {
        // with a wheel ignored the per-pad copies follow every connection, so it always rebuilds
        if (pads.Count == 0 && _ignoredPads.Count == 0) return;
        _ignoredPads = new HashSet<int>(pads);
        RetargetPads();
    }

    private static void RetargetPads()
    {
        if (_padBindings == null)
        {
            if (_ignoredPads.Count == 0) return;
            _padBindings = new();
            foreach (var action in InputMap.GetActions())
            {
                var pads = InputMap.ActionGetEvents(action)
                    .Where(e => e is InputEventJoypadButton or InputEventJoypadMotion && e.Device == -1).ToArray();
                if (pads.Length > 0) _padBindings[action] = pads;
            }
        }

        int[] devices = _ignoredPads.Count == 0
            ? new[] { -1 }
            : Input.GetConnectedJoypads().Where(d => !_ignoredPads.Contains(d)).ToArray();
        foreach (var (action, templates) in _padBindings)
        {
            if (!InputMap.HasAction(action)) continue;
            foreach (var e in InputMap.ActionGetEvents(action))
                if (e is InputEventJoypadButton or InputEventJoypadMotion) InputMap.ActionEraseEvent(action, e);
            foreach (var template in templates)
                foreach (int device in devices)
                {
                    var copy = (InputEvent)template.Duplicate();
                    copy.Device = device;
                    InputMap.ActionAddEvent(action, copy);
                }
        }
        if (_ignoredPads.Count == 0) _padBindings = null;
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
        // Flying, the mouse button and RB mean nothing else: items are on foot only, and tricks
        // and boost belong to the ground mounts.
        Bind(Fire, Mouse(MouseButton.Left), Button(JoyButton.RightShoulder));
        // The car's switches borrow the D-pad sides, which only mean something on foot (items).
        Bind(LightsToggle, Keys(Key.L), Button(JoyButton.DpadRight));
        Bind(RoofToggle, Keys(Key.O), Button(JoyButton.DpadLeft));
        // no pad button is free in a car: the radio is keyboard only. Not physical Y: that is the
        // key printed Z on a Swiss keyboard, next to the engine's physical Z printed Y.
        Bind(RadioNext, Keys(Key.U));
        Bind(RadioPrev, Keys(Key.P));
        // R, shared with the travel picker: in a vehicle with a stereo R is the radio (RadioUi takes it
        // first and ClientWorld leaves it alone), on foot it is the picker. Keyboard only, like U / P.
        Bind(RadioPanel, Keys(Key.R));
        // A truck has no tricks, boost or hop: its shift paddles take the shoulders (and Shift / Ctrl,
        // which only mean tuck and slide elsewhere), the clutch takes C / B, and the H-pattern's
        // gates the number keys, which only pick hotbar slots on foot.
        Bind(Couple, Keys(Key.H), Button(JoyButton.DpadLeft));
        // a passenger never does tricks: the trick keys are free in a seat
        Bind(TakeWheel, Keys(Key.F), Button(JoyButton.RightShoulder));
        Bind(Kneel, Keys(Key.K));
        Bind(Destination, Keys(Key.N));
        Bind(ShiftUp, Keys(Key.Shift), Button(JoyButton.RightShoulder));
        Bind(ShiftDown, Keys(Key.Ctrl), Button(JoyButton.LeftShoulder));
        Bind(Clutch, Keys(Key.C), Button(JoyButton.B));
        for (int g = 0; g < Gates.Length; g++) Bind(Gates[g], Keys(Key.Key1 + g));
        Bind(GearReverse, Keys(Key.Quoteleft));
        Bind(GearNeutral, Keys(Key.Key0));
        Bind(RetarderUp, Keys(Key.Apostrophe));
        Bind(RetarderDown, Keys(Key.Semicolon));

        Bind(FlyUp, Keys(Key.Space, Key.E), Button(JoyButton.A), Axis(JoyAxis.TriggerRight, 1));
        Bind(FlyDown, Keys(Key.Shift, Key.Q), Button(JoyButton.B), Axis(JoyAxis.TriggerLeft, 1));
        Bind(FlyBoost, Keys(Key.Ctrl), Button(JoyButton.LeftStick));

        // E only ever acts on what is in front of you (get in or out, search, a door). The
        // travel picker has its own key: sharing E made the picker pop up whenever you pressed
        // it one step too far from a car. A pad has no spare face button, so there Y still opens
        // the picker when there is nothing to interact with (ClientWorld).
        Bind(InteractMount, Keys(Key.E), Button(JoyButton.Y));
        Bind(RideMenu, Keys(Key.R));
        Bind(EngineToggle, Keys(Key.Z), Button(JoyButton.DpadUp));
        Bind(CameraToggle, Keys(Key.V), Button(JoyButton.RightStick));
        Bind(ToggleMode, Keys(Key.T), Button(JoyButton.DpadDown));
        // The place search is a map in all but drawing, so it sits on M. A pad can open it but
        // not type in it, so it stays keyboard-only rather than trapping a controller player.
        Bind(Teleport, Keys(Key.M));
        Bind(Menu, Keys(Key.Escape), Button(JoyButton.Start));
        // Both share a key with something that cannot happen at the same moment: T drops to the
        // fly camera except in a stopped car at a garage, and G / X gathers only as a HOLD, where
        // a door is a tap beside a car.
        Bind(Tune, Keys(Key.T), Button(JoyButton.DpadDown));
        Bind(CarDoor, Keys(Key.G), Button(JoyButton.X));
        Bind(Help, Keys(Key.F1));

        // Items are an on-foot thing, so they reuse the shoulders that only mean something
        // mounted (RB trick, LB boost). The inventory is on the two keys players try first, I
        // and Tab; E is taken by interacting, which is also why it is not Minecraft's E.
        Bind(UseItem, Mouse(MouseButton.Left), Button(JoyButton.RightShoulder));
        Bind(AimItem, Mouse(MouseButton.Right), Button(JoyButton.LeftShoulder));
        Bind(Inventory, Keys(Key.I, Key.Tab), Button(JoyButton.Back));
        Bind(QuickWheel, Keys(Key.X), Button(JoyButton.DpadLeft));
        // Minecraft's key: Q only means "down" in the fly camera and in the air, never on foot
        Bind(DropItem, Keys(Key.Q));
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
        InputHints.Invalidate();
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
