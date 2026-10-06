namespace UnitSport.Core;

/// <summary>The kinds of ride that each get their own short intro the first time (#517).</summary>
public enum VehicleIntroKind
{
    RoadBike, Skis, Car, Motorbike, Truck, Boat, Steamer,
    Helicopter, Plane, Canopy, Wingsuit, Pigeon, Airliner,
    Forklift, Excavator, MiniExcavator, WheelLoader, CompactRoller, Telehandler,
    /// <summary>Not driven: shown on walking up to a lone trailer, and done from a truck.</summary>
    Trailer,
}

/// <summary>
/// One control of an intro: what it says (keyboard words, and pad words where a trigger or a stick
/// replaces a key, as in <see cref="TutorialStep"/>) and the actions any of which ticks it off.
/// With <see cref="While"/>, it ticks only while driving that kind, and with no actions it ticks by
/// being in it ("get into a truck").
/// </summary>
public sealed record IntroRow(string Keys, string[] Actions, string? Pad = null, VehicleIntroKind? While = null);

/// <summary>
/// A ride's intro: its name, the 3-4 controls that make it go, and a last hint line. <see cref="Near"/>:
/// shown on walking up to one (a trailer), not on driving it.
/// </summary>
public sealed record VehicleIntro(VehicleIntroKind Kind, string Title, IntroRow[] Rows, string Footer, bool Near = false);

/// <summary>
/// The mini tutorials shown the first time the player takes each kind of ride (#517): a card of
/// its few essential controls, each ticked off as it is used, gone once all are. The full list
/// stays on F1. Plain data, unit-tested; the card is <see cref="VehicleIntroCard"/>.
/// The action names are <see cref="PlayerInput"/>'s, written out so this file needs no Godot.
/// </summary>
public static class VehicleIntros
{
    private static readonly string[] Throttle = { "throttle", "move_forward" };
    private static readonly string[] Brake = { "brake", "move_back" };
    private static readonly string[] Steer = { "move_left", "move_right" };
    private static readonly string[] Stick = { "move_forward", "move_back", "move_left", "move_right" };
    private const string Wasd = "{move_forward} {move_left} {move_back} {move_right}";
    private const string Arrows = "{arm_boom_up} {arm_boom_down} {arm_bucket_curl} {arm_bucket_dump}";
    private static readonly string[] Tracks = { "throttle", "brake", "move_forward", "move_back" };
    private static readonly string[] LeftArm = { "arm_slew_left", "arm_slew_right", "arm_stick_out", "arm_stick_in" };
    private static readonly string[] RightArm = { "arm_boom_up", "arm_boom_down", "arm_bucket_curl", "arm_bucket_dump" };
    private static readonly string[] Lift = { "arm_boom_up", "arm_boom_down" };

    private static readonly VehicleIntro[] All =
    {
        new(VehicleIntroKind.RoadBike, "Road bike", new IntroRow[]
        {
            new("Pedal with {throttle}", Throttle),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Brake with {brake}", Brake),
            new("Hop with {jump}", new[] { "jump" }),
        }, "Hold {tuck_boost} to sprint · {ride_menu} takes it off when you stand still"),

        new(VehicleIntroKind.Skis, "Skis", new IntroRow[]
        {
            new("Push with the poles: {throttle}", Throttle),
            new("Turn with {move_left} {move_right}: turning across the slope is how you slow down", Steer,
                Pad: "Turn with {move_forward}: turning across the slope is how you slow down"),
            new("Tuck for speed: hold {tuck_boost}", new[] { "tuck_boost" }),
            new("Jump with {jump}", new[] { "jump" }),
        }, "{ride_menu} takes them off when you stand still"),

        new(VehicleIntroKind.Car, "Car", new IntroRow[]
        {
            new("Accelerate with {throttle}", Throttle),
            new("Brake, then reverse, with {brake}", Brake),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Handbrake: {jump}", new[] { "jump" }),
        }, "{camera_toggle} cockpit view · {lights_toggle} lights · {interact_mount} gets out"),

        new(VehicleIntroKind.Motorbike, "Motorbike", new IntroRow[]
        {
            new("Throttle with {throttle}", Throttle),
            new("Brake with {brake}", Brake),
            new("Lean into turns with {move_left} {move_right}", Steer, Pad: "Lean into turns with {move_forward}"),
            new("Wheelie: hold {tuck_boost}", new[] { "tuck_boost" }),
        }, "{camera_toggle} camera · {interact_mount} gets off"),

        new(VehicleIntroKind.Truck, "Truck, bus or pickup", new IntroRow[]
        {
            new("Accelerate with {throttle}", Throttle),
            new("Brake with {brake}", Brake),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Shift up / down: {shift_up} / {shift_down}", new[] { "shift_up", "shift_down" }),
        }, "{couple} couples a trailer · {car_door} bus doors · {interact_mount} gets out"),

        new(VehicleIntroKind.Boat, "Boat", new IntroRow[]
        {
            new("Throttle with {throttle}", Throttle),
            new("Slow down and reverse with {brake}", Brake),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
        }, "{interact_mount} gets out by the shore"),

        new(VehicleIntroKind.Steamer, "Paddle steamer", new IntroRow[]
        {
            new("Telegraph ahead / astern: {move_forward} / {move_back}", new[] { "move_forward", "move_back" },
                Pad: "Telegraph ahead / astern: {move_forward} up / down"),
            new("Wheel with {move_left} {move_right} (she needs way on to turn)", Steer,
                Pad: "Wheel with {move_forward} (she needs way on to turn)"),
            new("Whistle: hold {horn}", new[] { "horn" }),
        }, "{car_door} the gangways when stopped · {interact_mount} leaves the wheel"),

        new(VehicleIntroKind.Helicopter, "Helicopter", new IntroRow[]
        {
            new("Fly where you look with " + Wasd, Stick, Pad: "Fly where you look with {move_forward}"),
            new("Climb: hold {jump}", new[] { "jump", "trigger_right" }, Pad: "Climb: hold {throttle}"),
            new("Descend: hold {crouch_slide}", new[] { "crouch_slide", "trigger_left" }, Pad: "Descend: hold {brake}"),
        }, "Let go and it holds its height · {interact_mount} gets out on the ground"),

        new(VehicleIntroKind.Plane, "Plane", new IntroRow[]
        {
            new("Throttle lever forward: {sprint}", new[] { "sprint", "trigger_right" }, Pad: "Throttle lever forward: {throttle}"),
            new("Pull back to climb, sideways to bank: " + Wasd, Stick, Pad: "Pull back to climb, sideways to bank: {move_forward}"),
            new("Throttle lever back: {crouch_slide}", new[] { "crouch_slide", "trigger_left" }, Pad: "Throttle lever back: {brake}"),
        }, "Below about 80 km/h it stalls · {interact_mount} gets out on the ground"),

        new(VehicleIntroKind.Canopy, "Paraglider or parachute", new IntroRow[]
        {
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Brakes, slower and flatter: {move_back}", new[] { "move_back" }),
            new("Speed bar, faster and steeper: {move_forward}", new[] { "move_forward" }),
        }, "Find rising air over sunny slopes · {ride_menu} packs a paraglider away on the ground"),

        new(VehicleIntroKind.Wingsuit, "Wingsuit", new IntroRow[]
        {
            new("Bank and turn with {move_left} {move_right}", Steer, Pad: "Bank and turn with {move_forward}"),
            new("Dive for speed {move_forward}, flare {move_back}", new[] { "move_forward", "move_back" }),
            new("Open the parachute: {jump}", new[] { "jump" }),
        }, "Open it well above the ground"),

        new(VehicleIntroKind.Pigeon, "Pigeon", new IntroRow[]
        {
            new("Flap with {jump} (hold {tuck_boost} to flap harder)", new[] { "jump", "trigger_right" }, Pad: "Flap with {throttle}"),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Dive: hold {crouch_slide}", new[] { "crouch_slide", "trigger_left" }, Pad: "Dive: hold {brake}"),
            new("Drop something on someone: {fire}", new[] { "fire" }),
        }, "Land on roofs and ledges · {ride_menu} turns you back"),

        new(VehicleIntroKind.Airliner, "Airliner", new IntroRow[]
        {
            new("Thrust levers forward / back: {sprint} / {crouch_slide}", new[] { "sprint", "crouch_slide", "trigger_right", "trigger_left" },
                Pad: "Thrust levers forward / back: {throttle} / {brake}"),
            new("Steer the nose wheel, then pitch and roll: " + Wasd, Stick, Pad: "Steer the nose wheel, then pitch and roll: {move_forward}"),
            new("Flaps a notch down / up: {flaps_down} / {flaps_up}", new[] { "flaps_down", "flaps_up" }),
            new("Gear up once flying: {car_door}", new[] { "car_door" }),
        }, "Rotate at about 260 km/h · {help} lists every cockpit switch"),

        new(VehicleIntroKind.Forklift, "Forklift", new IntroRow[]
        {
            new("Drive with {throttle}", Throttle),
            new("Brake, then reverse, with {brake}", Brake),
            new("Steer with {move_left} {move_right}", Steer, Pad: "Steer with {move_forward}"),
            new("Raise / lower the forks: hold {shift_up} / {shift_down}", new[] { "shift_up", "shift_down" }),
        }, "Run the forks in under a pallet and raise them to lift it · {interact_mount} gets out"),

        new(VehicleIntroKind.Excavator, "Excavator", new IntroRow[]
        {
            new("Tracks forward / back: {move_forward} / {move_back}, turn on the spot: {move_left} {move_right}", Tracks,
                Pad: "Tracks forward / back: {throttle} / {brake}, turn on the spot: {move_left}"),
            new("Dig mode on / off: {dig_mode}", new[] { "dig_mode" }),
            new("Dig: slew and stick with " + Wasd, LeftArm, Pad: "Dig: slew and stick with {arm_slew_left}"),
            new("Dig: boom and bucket with " + Arrows, RightArm, Pad: "Dig: boom and bucket with {arm_boom_up}"),
        }, "{interact_mount} gets out"),

        new(VehicleIntroKind.MiniExcavator, "Mini excavator", new IntroRow[]
        {
            new("Tracks forward / back: {move_forward} / {move_back}, turn on the spot: {move_left} {move_right}", Tracks,
                Pad: "Tracks forward / back: {throttle} / {brake}, turn on the spot: {move_left}"),
            new("Dig mode on / off: {dig_mode}", new[] { "dig_mode" }),
            new("Dig: the arm on " + Wasd + " and " + Arrows, RightArm,
                Pad: "Dig: the arm on both sticks, {arm_slew_left} and {arm_boom_up}"),
            new("Blade up / down: hold {blade_raise} / {blade_lower}", new[] { "blade_raise", "blade_lower" }),
        }, "The blade works driving or digging · {interact_mount} gets out"),

        new(VehicleIntroKind.WheelLoader, "Wheel loader", new IntroRow[]
        {
            new("Drive with {throttle}, brake and reverse with {brake}", Throttle),
            new("Steer with {move_left} {move_right}: it bends in the middle", Steer, Pad: "Steer with {move_forward}: it bends in the middle"),
            new("Work mode on / off: {dig_mode} (it still drives)", new[] { "dig_mode" }),
            new("Work: lift and tilt the bucket with " + Arrows, RightArm, Pad: "Work: lift and tilt the bucket with {arm_boom_up}"),
        }, "With forks: run them in under a pallet and lift · {interact_mount} gets out"),

        new(VehicleIntroKind.CompactRoller, "Compact roller", new IntroRow[]
        {
            new("Drive with {throttle}", Throttle),
            new("Brake, then reverse, with {brake}", Brake),
            new("Steer with {move_left} {move_right}: it bends in the middle", Steer, Pad: "Steer with {move_forward}: it bends in the middle"),
            new("Vibrate the drums: {dig_mode}", new[] { "dig_mode" }),
        }, "{interact_mount} gets out"),

        new(VehicleIntroKind.Telehandler, "Telehandler", new IntroRow[]
        {
            new("Drive with {throttle}, brake and reverse with {brake}", Throttle),
            new("Steering front / four-wheel / crab: {roof_toggle}", new[] { "roof_toggle" }),
            new("Work mode {dig_mode}, then lift the boom with {arm_boom_up} {arm_boom_down}", Lift,
                Pad: "Work mode {dig_mode}, then lift the boom with {arm_boom_up}"),
            new("Run the boom out / in: hold {shift_up} / {shift_down}", new[] { "shift_up", "shift_down" }),
        }, "{arm_bucket_curl} {arm_bucket_dump} tilt the forks · {interact_mount} gets out"),

        new(VehicleIntroKind.Trailer, "Trailer", new IntroRow[]
        {
            // a pickup (the F-150's tow ball, #463) is a Truck too: same rows for a boat trailer
            new("Get into a truck or a pickup: {ride_menu}, Trucks and buses", Array.Empty<string>(),
                Pad: "Get into a truck or a pickup: {interact_mount} with nothing near, Trucks and buses", While: VehicleIntroKind.Truck),
            new("Back up slowly towards it with {brake}", Brake, While: VehicleIntroKind.Truck),
            new("Couple with {couple} once the hitch lines up", new[] { "couple" }, While: VehicleIntroKind.Truck),
        }, "{couple} again uncouples it · a boat trailer: {car_door}, stopped, launches or winches the boat", Near: true),
    };

    public static IReadOnlyList<VehicleIntro> Every => All;

    public static VehicleIntro For(VehicleIntroKind kind) => All.First(i => i.Kind == kind);

    /// <summary>The row's words for the device in hand: the pad's for a pad or VR, else the keyboard's.</summary>
    public static string Text(IntroRow row, bool pad) => (pad ? row.Pad : null) ?? row.Keys;

    /// <summary>Not seen yet: the saved list names the kinds already done (unknown names are ignored).</summary>
    public static bool Seen(IEnumerable<string> seen, VehicleIntroKind kind) =>
        seen.Any(s => string.Equals(s, kind.ToString(), StringComparison.OrdinalIgnoreCase));
}
