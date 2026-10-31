using Godot;

namespace UnitSport.Core;

/// <summary>Which kind of device the player last touched. Drives prompts and pad-only behaviour.</summary>
public enum InputDevice
{
    KeyboardMouse,
    Gamepad,
    /// <summary>
    /// The VR controllers (#435). Never <see cref="PlayerInput.LastDevice"/> (they replay as a pad,
    /// so pad-only behaviour holds); only <see cref="PlayerInput.HintDevice"/>, so prompts name them.
    /// </summary>
    VR,
    /// <summary>
    /// The phone's touch controls (#1054). Never <see cref="PlayerInput.LastDevice"/> (the overlay
    /// plays a pad); only <see cref="PlayerInput.HintDevice"/>, so hints name the on-screen controls.
    /// </summary>
    Touch,
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
    /// <summary>In an open car: soft top down/up (<see cref="Player.Car.RoofOpen"/>); in one with hydraulics, bounce on/off (<see cref="Player.Car.Bouncing"/>).</summary>
    public const string RoofToggle = "roof_toggle";
    /// <summary>In a car, truck or bus: the next / previous live radio station, through off (#179).</summary>
    public const string RadioNext = "radio_next";
    public const string RadioPrev = "radio_prev";
    /// <summary>In a car, truck or bus (driver or passenger): the radio panel, stations and CDs (#211).</summary>
    public const string RadioPanel = "radio_panel";
    // --- trucks and buses (#70) ---
    /// <summary>Couple or uncouple a trailer (<see cref="Player.Truck.Couple"/>).</summary>
    public const string Couple = "couple";
    /// <summary>A steamer's whistle (#303), held: it blows as long as it is held. H, like coupling a truck.</summary>
    public const string Horn = "horn";
    /// <summary>A UFO's tractor beam on or off (#861). H, like coupling a truck: "take hold of it".</summary>
    public const string TractorBeam = "tractor_beam";
    /// <summary>A passenger moves into the free driver's seat (#158).</summary>
    public const string TakeWheel = "take_wheel";
    /// <summary>A bus kneels (lowers its door side) or rises.</summary>
    public const string Kneel = "kneel";
    /// <summary>The next destination on a bus's display.</summary>
    public const string Destination = "destination";
    /// <summary>
    /// The guiding gust (#1226, <c>Adventure.Navigation.GuidingWind</c>): N on foot and mounted (in a bus, a tipper or a
    /// mixer N stays <see cref="Destination"/>'s). A pad has no free button on foot: the quick menu's Guide wedge; VR the
    /// open left hand palm up, or the wrist menu.
    /// </summary>
    public const string Guide = "guide";
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
    // --- the excavator (#611): a drive / dig toggle, then the arm on the two sticks, ISO pattern ---
    /// <summary>The excavator: dig mode on / off. In it the tracks hold and the sticks work the arm.</summary>
    public const string DigMode = "dig_mode";
    /// <summary>Dig mode, the left stick: slew the house (X), run the stick out and in (Y).</summary>
    public const string ArmSlewLeft = "arm_slew_left";
    public const string ArmSlewRight = "arm_slew_right";
    /// <summary>Bowling (#1336): turn your line on the lane while aiming (the spot moves on the move keys).</summary>
    public const string BowlAimLeft = "bowl_aim_left";
    public const string BowlAimRight = "bowl_aim_right";
    public const string ArmStickOut = "arm_stick_out";
    public const string ArmStickIn = "arm_stick_in";
    /// <summary>Dig mode, the right stick: raise and lower the boom (Y, back is up), curl and dump the bucket (X, left curls).</summary>
    public const string ArmBoomUp = "arm_boom_up";
    public const string ArmBoomDown = "arm_boom_down";
    public const string ArmBucketCurl = "arm_bucket_curl";
    public const string ArmBucketDump = "arm_bucket_dump";
    /// <summary>A mini excavator's dozer blade up / down (#614), held, driving or digging: the gear paddles, which a crawler has no use for.</summary>
    public const string BladeRaise = "blade_raise";
    public const string BladeLower = "blade_lower";
    /// <summary>An airliner's flap lever a notch down / up, its speedbrake, its parking brake (#414). The gear is <see cref="CarDoor"/> in the air.</summary>
    public const string FlapsDown = "flaps_down";
    public const string FlapsUp = "flaps_up";
    public const string Speedbrake = "speedbrake";
    public const string ParkingBrake = "parking_brake";
    /// <summary>An airliner's autopilot and autothrust on / off, and its pitch trim held (Light sim, #415).</summary>
    public const string Autopilot = "autopilot";
    /// <summary>A car's, truck's, farm machine's or motorbike's speed regulator (#494): the autopilot's key.</summary>
    public const string Cruise = "cruise";
    public const string TrimNoseDown = "trim_nose_down";
    public const string TrimNoseUp = "trim_nose_up";

    // --- free-fly camera ---
    public const string FlyUp = "fly_up";
    public const string FlyDown = "fly_down";
    public const string FlyBoost = "fly_boost";

    // --- game shortcuts ---
    public const string InteractMount = "interact_mount";
    public const string EngineToggle = "engine_toggle";
    public const string CameraToggle = "camera_toggle";
    /// <summary>Operating a concert's camera (#885): zoom in and out, and cut this camera to air.</summary>
    public const string CamZoomIn = "cam_zoom_in";
    public const string CamZoomOut = "cam_zoom_out";
    public const string CamCut = "cam_cut";
    /// <summary>On foot: the camera over the other shoulder (#460). A pad swaps with R3 while aiming a gun.</summary>
    public const string SwapShoulder = "swap_shoulder";
    /// <summary>
    /// On foot in third person: the camera arm in and out (#1149), from far off down to the face.
    /// The plain wheel (Alt + wheel is the hotbar, #1369), = / -; a pad holds R3 and pushes the right stick.
    /// </summary>
    public const string ViewZoomIn = "view_zoom_in";
    public const string ViewZoomOut = "view_zoom_out";
    public const string ToggleMode = "toggle_mode";
    /// <summary>The spectator fly camera and back (#1002): Shift+T; a pad holds D-pad down (<c>ClientWorld</c>), VR the wrist menu.</summary>
    public const string Spectator = "spectator";
    public const string Teleport = "teleport";
    /// <summary>Battle Royale squads (#469): mark the point under the crosshair for your team-mates.</summary>
    public const string Ping = "ping";
    public const string Menu = "menu";
    /// <summary>In a stopped car at a garage: open the tuning menu (<see cref="Vehicles.GarageUi"/>).</summary>
    public const string Tune = "tune";
    /// <summary>Open or shut a parked car's door without getting in (on foot beside it; never from the seat).</summary>
    public const string CarDoor = "car_door";
    /// <summary>The travel picker (<see cref="Player.RideUi"/>): mounts, equipment and, for an admin, vehicles.</summary>
    public const string RideMenu = "ride_menu";
    /// <summary>
    /// A home-trainer training (#901): back to the GPX course from a road off it, turn round, ERG on/off and its
    /// target ±10 W. Keys only: a pad and VR have the same as buttons in the phone's Training app (no pad
    /// button is free on a bike); the turn at a junction is the steering.
    /// </summary>
    public const string RideRejoin = "ride_rejoin", RideUTurn = "ride_uturn", RideErg = "ride_erg",
        RidePowerUp = "ride_power_up", RidePowerDown = "ride_power_down";
    /// <summary>
    /// The pad's analog triggers alone, 0..1, for code that wants the pull itself (flight's lever,
    /// the GPX camera). Through the input map rather than <c>Input.GetJoyAxis(0, …)</c>, so any
    /// pad works, and so do the VR controllers, which replay as a pad (#436).
    /// </summary>
    public const string TriggerRight = "trigger_right", TriggerLeft = "trigger_left";
    /// <summary>The controls overlay (<see cref="ControlsHelp"/>), built from the live bindings.</summary>
    public const string Help = "help";
    /// <summary>The debug menu (<see cref="DebugMenu"/>): overlays, terrain layers, view modes. Offline or as an admin.</summary>
    public const string DebugMenu = "debug_menu";
    /// <summary>The playtest panel (<c>Playtest/PlaytestPanel</c>, #751): Debug builds run with <c>--playtest</c> only; a pad uses both stick clicks.</summary>
    public const string PlaytestPanel = "playtest_panel";
    /// <summary>
    /// The playtest recorder's note (#844, <c>Playtest/Recording/NoteTaker</c>): F12 asks "what's wrong?" and
    /// stamps it with the time, the place and a screenshot. A pad and VR note from the playtest panel's Note button.
    /// </summary>
    public const string PlaytestNote = "playtest_note";
    /// <summary>Shift+F12: the playtest top view (#844, <c>Playtest/TopView/TopViewOverlay</c>); a pad and VR from the panel's Top view button.</summary>
    public const string PlaytestTopView = "playtest_topview";
    /// <summary>Ctrl+F12: the top view stops on the last minute and scrubs it (Ctrl + wheel); keyboard only, a tuning aid.</summary>
    public const string PlaytestTopViewScrub = "playtest_topview_scrub";

    // --- items (on foot) ---
    public const string UseItem = "use_item";
    /// <summary>Hold on foot outdoors to collect stone, water or firewood (<see cref="Loot.Gathering"/>).</summary>
    public const string Gather = "gather";
    public const string AimItem = "aim_item";
    public const string Inventory = "inventory";
    public const string QuickWheel = "quick_wheel";
    /// <summary>
    /// The quick menu (#840, <see cref="Ui.QuickMenu"/>): hold F2, or hold Back on a pad (a tap of Back is still the
    /// inventory), for a ring of Activities, Invites, Inventory, Vehicles, Map, Emotes, Journal, Controls.
    /// A tap of F2 while an invite is up accepts it.
    /// </summary>
    public const string QuickMenu = "quick_menu";
    /// <summary>Shift+F2 (#840): declines the newest invite. A pad declines from the quick menu's Invites or the Start menu.</summary>
    public const string InviteDecline = "invite_decline";
    /// <summary>
    /// The phone (#889): on foot, takes it out, and again opens it (<see cref="UnitSport.Phone.PhoneController"/>).
    /// Up arrow, free on foot (only the excavator's boom in a cab); a pad reaches it from the quick menu.
    /// </summary>
    public const string Phone = "phone";
    /// <summary>Hold on foot for the emote wheel (<see cref="Player.EmoteWheel"/>, #404): dances and gestures, any time.</summary>
    public const string EmoteWheel = "emote_wheel";
    // --- pinball (#1325): read only while standing at a machine (Interiors.Pinball.PinballPlay) ---
    /// <summary>The left and right flipper buttons: held, the flipper is up. On a pad the shoulders and triggers, as on a cabinet's sides.</summary>
    public const string PinballLeft = "pinball_left";
    public const string PinballRight = "pinball_right";
    /// <summary>Held: the plunger pulled back (the longer, the harder); let go, it launches. Pad A, or the right stick pulled down.</summary>
    public const string PinballPlunge = "pinball_plunge";
    /// <summary>A shove on the cabinet, forward or to a side. Too hard or too often: tilt.</summary>
    public const string PinballNudge = "pinball_nudge";
    public const string PinballNudgeLeft = "pinball_nudge_left";
    public const string PinballNudgeRight = "pinball_nudge_right";
    /// <summary>
    /// The replay buffer's last minutes into the movie studio (#638). Keyboard only: a pad and VR do it
    /// from the pause menu (Save clip), which they reach with Start / the menu button.
    /// </summary>
    public const string SaveClip = "save_clip";
    /// <summary>
    /// Tap Alt: the mouse is let go in game, MMO style (#654, <see cref="CursorToggle"/>, which reads the key
    /// itself to tell a tap from Alt+Enter). Bound for the hints and the help only. No pad or VR way: a pad
    /// has no cursor, VR points with the laser.
    /// </summary>
    public const string FreeCursor = "free_cursor";
    /// <summary>In a fist fight only (#495): the fight is its own context, items and wheels are off, so these share their buttons.</summary>
    public const string FightPunch = "fight_punch";
    public const string FightKick = "fight_kick";
    /// <summary>Held: the guard (high standing, low crouching).</summary>
    public const string FightBlock = "fight_block";
    /// <summary>
    /// With a sword in hand only (#1371, <c>Combat/SwordRules</c>): the dodge. A press is the dodge, held on it carries on as the
    /// sprint it also is. Attacking is <see cref="UseItem"/> (LMB, RB; VR's right trigger).
    /// </summary>
    public const string SwordDodge = "sword_dodge";
    /// <summary>With a sword in hand only (#1371): locks onto the target nearest the view, again lets it go.</summary>
    public const string SwordLock = "sword_lock";
    /// <summary>
    /// In an aura battle only (#907): its own context like the fight, the walk and the items are off.
    /// Next cuts to the following move of the combo being performed (timed on the downbeat); the
    /// planner's lock in, take back the last move, and suggest.
    /// </summary>
    public const string AuraNext = "aura_next";
    public const string AuraLock = "aura_lock";
    public const string AuraRemove = "aura_remove";
    public const string AuraSuggest = "aura_suggest";
    /// <summary>Drops one of the item in hand on the ground; with Ctrl, the whole stack (#206).</summary>
    public const string DropItem = "drop_item";
    /// <summary>Next hotbar slot: Alt + wheel down (the plain wheel zooms the camera, #1369), D-pad right on a pad.</summary>
    public const string NextItem = "next_item";
    /// <summary>Puts the item in hand away, or takes it out again (#1369): comma on the keyboard (selection kept); a pad holds D-pad right, VR holds the grip at the right hip.</summary>
    public const string Holster = "holster_item";
    /// <summary>Hotbar slots 1-6 (#391): select on foot, swap the hovered slot in the inventory, pick in the quick wheel. Same keys as the truck's gates, never at the same time.</summary>
    public static readonly StringName[] Slots = { "slot_1", "slot_2", "slot_3", "slot_4", "slot_5", "slot_6" };
    public const string PrevItem = "prev_item";
    /// <summary>
    /// Opens and closes the Journal (<see cref="Ui.AdventureLog"/>, #1223): quests, birds, fish and the
    /// Adventure plans' tabs. The id stays "bird_journal" so players' rebinds of J carry over.
    /// </summary>
    public const string BirdJournal = "bird_journal";

    /// <summary>Opens the Character menu, the figure editor (<see cref="Ui.CharacterScreen"/>, #1127). On foot only: P is the radio's previous station in a vehicle.</summary>
    public const string CharacterMenu = "character_menu";
    /// <summary>The hammer in hand (#359): turns the piece; with Aim, changes its material. R, or D-pad up on a pad.</summary>
    public const string BuildTurn = "build_turn";

    // The map screen (#515). Menu actions, so they may share pad buttons with world ones: no world
    // is listening while a page is up. VR is deferred — the map renders on the XrPad panel but is
    // not pointable yet (docs/notes/xr/vr-action-map.md).
    /// <summary>Map screen: zoom in a step.</summary>
    public const string MapZoomIn = "map_zoom_in";
    /// <summary>Map screen: zoom out a step.</summary>
    public const string MapZoomOut = "map_zoom_out";
    /// <summary>Map screen: cycle the drawing tool (rectangle, brush, erase).</summary>
    public const string MapTool = "map_tool";
    /// <summary>Map screen: jump to the place search box.</summary>
    public const string MapSearch = "map_search";

    /// <summary>Which hotbar slot action <paramref name="e"/> presses (0-based), or -1.</summary>
    public static int SlotPressed(InputEvent e)
    {
        if (e.IsEcho()) return -1;
        for (int s = 0; s < Slots.Length; s++)
            if (e.IsActionPressed(Slots[s])) return s;
        return -1;
    }

    /// <summary>
    /// Whether a mouse motion turns the camera: the captured mouse, or a touch-look drag
    /// (<see cref="TouchControls"/>, #63). Never the mouse Godot makes up from a touch, which would
    /// turn it a second time.
    /// </summary>
    public static bool IsLookMotion(InputEventMouseMotion m) =>
        m.Device == TouchControls.Device
        || MouseCapture.Mode == Input.MouseModeEnum.Captured && m.Device != InputEvent.DeviceIdEmulation;

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

    /// <summary>
    /// Raised when the player switches between keyboard and pad, or when what a VR control is
    /// called changes (<see cref="HintsChanged"/>), so on-screen key hints can follow.
    /// </summary>
    public static event Action? DeviceChanged;

    /// <summary>
    /// The device prompts name (#435): <see cref="InputDevice.VR"/> while the headset is on, where
    /// <see cref="LastDevice"/> stays Gamepad because the controllers replay as a pad.
    /// </summary>
    public static InputDevice HintDevice => XR.XrSession.Active ? InputDevice.VR
        : TouchControls.Hinting ? InputDevice.Touch : _lastDevice;

    /// <summary>
    /// What a control is called changed without a device switch (the VR controller was recognised,
    /// the VR triggers changed role on mounting): forget the hints and tell whoever shows them.
    /// </summary>
    public static void HintsChanged()
    {
        InputHints.Invalidate();
        DeviceChanged?.Invoke();
    }

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
        : MoveIsHeldOff ? MoveHeldOff()
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
        !Blocked && (Input.IsActionPressed(ActionName(action)) && !HeldOff(action) || SteeringWheel.Strength(action) > 0.5f);

    /// <summary>
    /// Held on a key or a button, leaving out a wheel's pedal: for a control that takes the pedal's
    /// travel separately (<see cref="WheelPedal"/>), as the clutch does (#290).
    /// </summary>
    public static bool HeldButton(string action) => !Blocked && Input.IsActionPressed(ActionName(action)) && !HeldOff(action);

    /// <summary>A steering wheel's pedal behind <paramref name="action"/>, 0..1; 0 without one.</summary>
    public static float WheelPedal(string action) => Blocked ? 0f : SteeringWheel.Strength(action);

    /// <summary>0..1 — a trigger's or a pedal's travel, or 1 for a pressed key.</summary>
    public static float Strength(string action) =>
        Blocked ? 0f : Mathf.Max(HeldOff(action) ? 0f : Input.GetActionStrength(ActionName(action)), SteeringWheel.Strength(action));

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

    /// <summary>
    /// On foot only: the pedals walk, brake back minus throttle forward (y back, as <see cref="Move"/>);
    /// 0 without a wheel. Never in a vehicle, where the pedals drive.
    /// </summary>
    public static float WheelWalk => Blocked || !SteeringWheel.Active ? 0f : SteeringWheel.Brake - SteeringWheel.Throttle;

    /// <summary>
    /// On foot only: the wheel turned ±<see cref="SteeringWheel.PlainSpanDeg"/> turns the view like a
    /// fully pushed right stick, x right, rad/s; a few degrees around centre do nothing. 0 without a
    /// wheel. Never in a vehicle, where the wheel steers.
    /// </summary>
    public static float WheelLookRate
    {
        get
        {
            if (Blocked || !SteeringWheel.Active) return 0f;
            float a = Mathf.Clamp(SteeringWheel.Angle / Mathf.DegToRad(SteeringWheel.PlainSpanDeg), -1f, 1f);
            float m = Mathf.Max(0f, (Mathf.Abs(a) - 0.05f) / 0.95f);
            return Mathf.Sign(a) * m * m * StickTurnRate * GameSettings.Current.StickSensitivity;
        }
    }

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
        // the phone itself (#63): one motor, so the stronger of the two
        if (TouchControls.Shown) Input.VibrateHandheld((int)(seconds * 1000), Mathf.Clamp(Math.Max(weak, strong), 0, 1));
        foreach (int pad in Input.GetConnectedJoypads())
            // never the steering wheel: Godot rumbles a force-feedback wheel through its own SDL,
            // which takes the wheel's forces away from SteeringWheel (#68); its knocks are its own
            if (!_ignoredPads.Contains(pad))
                Input.StartJoyVibration(pad, Mathf.Clamp(weak, 0, 1), Mathf.Clamp(strong, 0, 1), seconds);
    }

    /// <summary>Adds the tracker node and the steering wheel reader, and registers the default bindings. Idempotent.</summary>
    public static void Install(Node root)
    {
        RegisterActions();
        // RegisterActions puts back any every-device pad binding it does not find, and a wheel
        // claimed on the title screen has had those replaced by per-pad copies: without this, loading
        // a world handed the wheel back to the pad bindings, and past the stick deadzone (180° of an
        // 1800° wheel) it steered as a stick — the car's wheel snapping back to centre (#68)
        if (_ignoredPads.Count > 0) RetargetPads();
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
        if (e is InputEventKey key) TrackKey(key);
        // the steering wheel is read through SDL; Godot's copy of it is not a pad
        if (e is InputEventJoypadButton or InputEventJoypadMotion && _ignoredPads.Contains(e.Device)) return;
        // VR replays the controllers as a pad, and points at the UI panel with mouse events: it
        // stays a pad for pad-only behaviour either way (#186); prompts ask HintDevice (#435)
        if (XR.XrSession.Active)
        {
            LastDevice = InputDevice.Gamepad;
            return;
        }
        // the touch overlay plays a pad (#63): its buttons and the touches behind it are the pad,
        // and the mouse Godot makes up from a finger is not a mouse
        if (e.Device == TouchControls.Device || e.Device == InputEvent.DeviceIdEmulation || e is InputEventScreenTouch or InputEventScreenDrag)
        {
            if (TouchControls.Shown) LastDevice = InputDevice.Gamepad;
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
        // a steamer has nothing to couple: its whistle takes the same key
        Bind(Horn, Keys(Key.H), Button(JoyButton.DpadLeft));
        // and a UFO's tractor beam (#861): it takes hold of what is under it
        Bind(TractorBeam, Keys(Key.H), Button(JoyButton.DpadLeft));
        // a passenger never does tricks: the trick keys are free in a seat
        Bind(TakeWheel, Keys(Key.F), Button(JoyButton.RightShoulder));
        // L3 means nothing in a cab: a bus kneels and a farm machine lowers its implement or header (#494)
        Bind(Kneel, Keys(Key.K), Button(JoyButton.LeftStick));
        Bind(Destination, Keys(Key.N));
        Bind(Guide, Keys(Key.N));
        Bind(ShiftUp, Keys(Key.Shift), Button(JoyButton.RightShoulder));
        Bind(ShiftDown, Keys(Key.Ctrl), Button(JoyButton.LeftShoulder));
        Bind(Clutch, Keys(Key.C), Button(JoyButton.B));
        for (int g = 0; g < Gates.Length; g++) Bind(Gates[g], Keys(Key.Key1 + g));
        Bind(GearReverse, Keys(Key.Quoteleft));
        Bind(GearNeutral, Keys(Key.Key0));
        Bind(RetarderUp, Keys(Key.Apostrophe));
        Bind(RetarderDown, Keys(Key.Semicolon));
        // The excavator (#611): C / pad B toggles dig mode (the clutch's and the crouch's, which mean
        // nothing in a crawler). In dig mode the sticks are the arm's, ISO pattern, as on a real
        // one: the left slews (X) and runs the stick out and in (Y), the right raises the boom (Y,
        // back is up) and curls the bucket (X, left curls). On the keyboard WASD are the left
        // stick and the arrows the right; the tracks ignore WASD while it digs.
        Bind(DigMode, Keys(Key.C), Button(JoyButton.B));
        Bind(ArmSlewLeft, Keys(Key.A), Axis(JoyAxis.LeftX, -1));
        Bind(ArmSlewRight, Keys(Key.D), Axis(JoyAxis.LeftX, 1));
        Bind(BowlAimLeft, Keys(Key.Left), Axis(JoyAxis.RightX, -1));
        Bind(BowlAimRight, Keys(Key.Right), Axis(JoyAxis.RightX, 1));
        Bind(ArmStickOut, Keys(Key.W), Axis(JoyAxis.LeftY, -1));
        Bind(ArmStickIn, Keys(Key.S), Axis(JoyAxis.LeftY, 1));
        Bind(ArmBoomUp, Keys(Key.Up), Axis(JoyAxis.RightY, 1));
        Bind(ArmBoomDown, Keys(Key.Down), Axis(JoyAxis.RightY, -1));
        Bind(ArmBucketCurl, Keys(Key.Left), Axis(JoyAxis.RightX, -1));
        Bind(ArmBucketDump, Keys(Key.Right), Axis(JoyAxis.RightX, 1));
        Bind(BladeRaise, Keys(Key.Shift), Button(JoyButton.RightShoulder));
        Bind(BladeLower, Keys(Key.Ctrl), Button(JoyButton.LeftShoulder));
        Bind(FlapsDown, Keys(Key.F7), Button(JoyButton.RightShoulder));
        Bind(FlapsUp, Keys(Key.F6), Button(JoyButton.LeftShoulder));
        Bind(Speedbrake, Keys(Key.Slash), Button(JoyButton.DpadLeft));
        Bind(ParkingBrake, Keys(Key.Period));
        Bind(Autopilot, Keys(Key.Y));
        // on the ground the autopilot's key holds a speed; on a pad, as the autopilot, D-pad → held (FootPlayer.Cruise.cs)
        Bind(Cruise, Keys(Key.Y));
        Bind(TrimNoseDown, Keys(Key.Home));
        Bind(TrimNoseUp, Keys(Key.End));

        Bind(FlyUp, Keys(Key.Space, Key.E), Button(JoyButton.A), Axis(JoyAxis.TriggerRight, 1));
        Bind(FlyDown, Keys(Key.Shift, Key.Q), Button(JoyButton.B), Axis(JoyAxis.TriggerLeft, 1));
        Bind(FlyBoost, Keys(Key.Ctrl), Button(JoyButton.LeftStick));
        Bind(TriggerRight, Axis(JoyAxis.TriggerRight, 1));
        Bind(TriggerLeft, Axis(JoyAxis.TriggerLeft, 1));

        // E only ever acts on what is in front of you (get in or out, search, a door). The
        // travel picker has its own key: sharing E made the picker pop up whenever you pressed
        // it one step too far from a car. A pad has no spare face button, so there Y still opens
        // the picker when there is nothing to interact with (ClientWorld).
        Bind(InteractMount, Keys(Key.E), Button(JoyButton.Y));
        Bind(RideMenu, Keys(Key.R));
        // every letter is taken: the keys beside Enter and the page keys, which no ride uses
        Bind(RideRejoin, Keys(Key.Pageup));
        Bind(RideUTurn, Keys(Key.Pagedown));
        Bind(RideErg, Keys(Key.Backslash));
        Bind(RidePowerUp, Keys(Key.Bracketright));
        Bind(RidePowerDown, Keys(Key.Bracketleft));
        Bind(EngineToggle, Keys(Key.Z), Button(JoyButton.DpadUp));
        Bind(CameraToggle, Keys(Key.V), Button(JoyButton.RightStick));
        // a concert's camera (#885): the operator's zoom on the wheel and the shoulders (nothing else
        // is held while a camera is driven), the cut on Enter / X
        Bind(CamZoomIn, Keys(Key.Equal), Mouse(MouseButton.WheelUp), Button(JoyButton.RightShoulder));
        Bind(CamZoomOut, Keys(Key.Minus), Mouse(MouseButton.WheelDown), Button(JoyButton.LeftShoulder));
        Bind(CamCut, Keys(Key.Enter), Button(JoyButton.X));
        // H only couples or sounds a horn in a vehicle; on foot it is the other shoulder (#460)
        Bind(SwapShoulder, Keys(Key.H), Mouse(MouseButton.Middle));
        // the on-foot camera's zoom (#1149, #1369): the plain wheel, whatever is held (Shift is sprint);
        // the hotbar is on Alt + wheel
        Bind(ViewZoomIn, Keys(Key.Equal), Mouse(MouseButton.WheelUp));
        Bind(ViewZoomOut, Keys(Key.Minus), Mouse(MouseButton.WheelDown));
        Bind(ToggleMode, Keys(Key.T), Button(JoyButton.DpadDown));
        Bind(Spectator, new InputEvent[] { new InputEventKey { PhysicalKeycode = Key.T, ShiftPressed = true } });
        // The place search is a map in all but drawing, so it sits on M. A pad can open it but
        // not type in it, so it stays keyboard-only rather than trapping a controller player.
        Bind(Teleport, Keys(Key.M));
        // mouse only: a pad has no button left that is free in a match
        Bind(Ping, Mouse(MouseButton.Middle));
        Bind(Menu, Keys(Key.Escape), Button(JoyButton.Start));
        // Both share a key with something that cannot happen at the same moment: T drops to the
        // fly camera except in a stopped car at a garage, and G / X gathers only as a HOLD, where
        // a door is a tap beside a car.
        Bind(Tune, Keys(Key.T), Button(JoyButton.DpadDown));
        Bind(CarDoor, Keys(Key.G), Button(JoyButton.X));
        Bind(Help, Keys(Key.F1));
        Bind(DebugMenu, Keys(Key.F9));
        // F5 is free everywhere: the quick-save key of other games, and this keeps the last minutes
        Bind(SaveClip, Keys(Key.F5));
        Bind(FreeCursor, Keys(Key.Alt));
#if PLAYTEST
        // a pad opens it with L3+R3 together (PlaytestPanel reads the chord): no single button is free
        Bind(PlaytestPanel, Keys(Key.F10));
        // F12 is free; Shift/Ctrl+F12 are told apart with exact matching (Playtest/Recording)
        Bind(PlaytestNote, Keys(Key.F12));
        Bind(PlaytestTopView, new InputEvent[] { new InputEventKey { PhysicalKeycode = Key.F12, ShiftPressed = true } });
        Bind(PlaytestTopViewScrub, new InputEvent[] { new InputEventKey { PhysicalKeycode = Key.F12, CtrlPressed = true } });
#endif

        // Items are an on-foot thing, so they reuse the shoulders that only mean something
        // mounted (RB trick, LB boost). The inventory is on the two keys players try first, I
        // and Tab; E is taken by interacting, which is also why it is not Minecraft's E.
        Bind(UseItem, Mouse(MouseButton.Left), Button(JoyButton.RightShoulder));
        Bind(AimItem, Mouse(MouseButton.Right), Button(JoyButton.LeftShoulder));
        Bind(Inventory, Keys(Key.I, Key.Tab), Button(JoyButton.Back));
        Bind(QuickWheel, Keys(Key.X), Button(JoyButton.DpadLeft));
        // B only looks behind when mounted; D-pad up is the engine in a vehicle and turns the
        // hammer's piece, so the emote wheel does not open with the hammer in hand
        Bind(EmoteWheel, Keys(Key.B), Button(JoyButton.DpadUp));
        // pinball (#1325): the flippers on A / D, the arrows and Left / Right Shift (each Shift its side), the pad's
        // shoulders and triggers; the plunger on Enter too (PinballPlay keeps Enter from the chat while at a machine);
        // Space / S / down, pad A or the right stick pulled back; nudges W (forward), Q and R (sides), pad X with
        // the left stick for its side. Read only at a machine (PinballPlay), where walking is off.
        Bind(PinballLeft, Keys(Key.A, Key.Left), KeyAt(Key.Shift, KeyLocation.Left), Button(JoyButton.LeftShoulder), Axis(JoyAxis.TriggerLeft, 1));
        Bind(PinballRight, Keys(Key.D, Key.Right), KeyAt(Key.Shift, KeyLocation.Right), Button(JoyButton.RightShoulder), Axis(JoyAxis.TriggerRight, 1));
        Bind(PinballPlunge, Keys(Key.Space, Key.S, Key.Down, Key.Enter), Button(JoyButton.A), Axis(JoyAxis.RightY, 1));
        Bind(PinballNudge, Keys(Key.W, Key.Up), Button(JoyButton.X));
        Bind(PinballNudgeLeft, Keys(Key.Q));
        Bind(PinballNudgeRight, Keys(Key.R));
        // the phone (#889): Up, as in the games players know it from; no pad button is free on foot
        // (D-pad up is the emote wheel), so the pad takes it out from the quick menu
        Bind(Phone, Keys(Key.Up));
        // F2 is free everywhere (F1 help, F3 F4 the perf tools); a pad has no free button, so the
        // quick menu is a hold of Back, whose tap stays the inventory (Ui/QuickMenu tells them apart)
        Bind(QuickMenu, Keys(Key.F2), Button(JoyButton.Back));
        Bind(InviteDecline, new InputEvent[] { new InputEventKey { PhysicalKeycode = Key.F2, ShiftPressed = true } });
        // a fist fight (#495): its own context, with items and wheels off, so the item buttons
        // punch and kick (VR's triggers send RB / LB on foot) and B, the crouch, guards (the stick crouches);
        // J K L for an arcade row under the right hand
        Bind(FightPunch, Mouse(MouseButton.Left), Keys(Key.J), Button(JoyButton.X), Button(JoyButton.RightShoulder));
        Bind(FightKick, Mouse(MouseButton.Right), Keys(Key.K), Button(JoyButton.Y), Button(JoyButton.LeftShoulder));
        Bind(FightBlock, Keys(Key.Shift, Key.L), Button(JoyButton.B));
        // the sword (#1371): its own context while it is in the hand. The dodge shares Shift (it is also the sprint: a press
        // dodges, holding on runs) and the pad's B (VR's B too; the pad's crouch gives way to it); the lock shares the middle
        // button and R3 (the sword in hand takes them first, FootPlayer.Sword) and Z, free on foot
        Bind(SwordDodge, Keys(Key.Shift), Button(JoyButton.B));
        Bind(SwordLock, Mouse(MouseButton.Middle), Keys(Key.Z), Button(JoyButton.RightStick));
        // an aura battle (#907): its own context too, the walk locked and the items off, so Next is
        // the jump's Space / A, or the left click, or RB (which VR's right trigger sends on foot); the
        // planner's lock in, take back and suggest are F / Y, Backspace / X, G / R3 (VR: the same buttons)
        Bind(AuraNext, Mouse(MouseButton.Left), Keys(Key.Space), Button(JoyButton.A), Button(JoyButton.RightShoulder));
        Bind(AuraLock, Keys(Key.F), Button(JoyButton.Y));
        Bind(AuraRemove, Keys(Key.Backspace), Button(JoyButton.X));
        Bind(AuraSuggest, Keys(Key.G), Button(JoyButton.RightStick));
        // Minecraft's key: Q only means "down" in the fly camera and in the air, never on foot
        Bind(DropItem, Keys(Key.Q));
        // pad X is tuck/sprint only when mounted, so on foot it is free, as RB/LB are for items
        Bind(Gather, Keys(Key.G), Button(JoyButton.X));
        Bind(NextItem, AltWheel(MouseButton.WheelDown), Button(JoyButton.DpadRight));
        Bind(Holster, Keys(Key.Comma));
        for (int s = 0; s < Slots.Length; s++) Bind(Slots[s], Keys(Key.Key1 + s));
        Bind(PrevItem, AltWheel(MouseButton.WheelUp));
        Bind(BirdJournal, Keys(Key.J));
        // the Character menu (#1127): P on foot (in a vehicle it is the radio's previous station, so the world reads it
        // only on foot); no pad button is free on foot, so a pad and VR open it from the quick menu or the pause menu
        Bind(CharacterMenu, Keys(Key.P));
        // R is the travel picker on foot, D-pad up the engine in a vehicle: with the hammer in hand
        // the item controller takes either first (Build.BuildTool)
        Bind(BuildTurn, Keys(Key.R), Button(JoyButton.DpadUp));

        // The map screen. The shoulders zoom because they are the one pair of pad controls no menu
        // uses, and the stick already pans through ui_left/right/up/down.
        // Letters, not punctuation: these are physical keycodes, so the symbol keys are labelled
        // differently on every layout ("/" is where "-" is on a Swiss keyboard) and the hint line
        // ends up reading nonsense. Letters keep the same name everywhere.
        Bind(MapZoomIn, Keys(Key.Equal), Button(JoyButton.RightShoulder));
        Bind(MapZoomOut, Keys(Key.Minus), Button(JoyButton.LeftShoulder));
        Bind(MapTool, Keys(Key.T), Button(JoyButton.Y));
        Bind(MapSearch, Keys(Key.F), Button(JoyButton.X));

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

    /// <summary>One side's key of a pair (Left Shift, Right Shift): only that key, not the other.</summary>
    private static InputEvent[] KeyAt(Key key, KeyLocation location) =>
        new InputEvent[] { new InputEventKey { PhysicalKeycode = key, Location = location } };

    private static InputEvent[] Mouse(MouseButton b) =>
        new InputEvent[] { new InputEventMouseButton { ButtonIndex = b } };

    private static InputEvent[] AltWheel(MouseButton b) =>
        new InputEvent[] { new InputEventMouseButton { ButtonIndex = b, AltPressed = true } };

    /// <summary>
    /// Alt + wheel: the hotbar's step (#1369). A modifier-less binding matches whatever is held, so the
    /// plain wheel's zoom asks first: Alt is the only modifier that moves the wheel to the hotbar.
    /// </summary>
    public static bool IsHotbarWheel(InputEvent e) =>
        e is InputEventMouseButton { AltPressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown };

    private static InputEvent[] Button(JoyButton b) =>
        new InputEvent[] { new InputEventJoypadButton { ButtonIndex = b, Device = -1 } };

    private static InputEvent[] Axis(JoyAxis axis, float sign) =>
        new InputEvent[] { new InputEventJoypadMotion { Axis = axis, AxisValue = sign, Device = -1 } };

    /// <summary>
    /// Creates the action if absent and adds any binding it does not already have. A binding the
    /// player (or a future rebind screen) has already changed is left alone.
    /// </summary>
    private static void Bind(StringName action, params InputEvent[][] groups)
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
