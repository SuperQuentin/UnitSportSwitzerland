using Godot;

namespace UnitSport.Core;

/// <summary>What the player is doing, as far as the phone's controls care (#1043).</summary>
public enum TouchMode
{
    Foot,
    Spectator,
    CreativeFly,
    Drive,
    Ride,
    Snow,
    Dig,
    Crane,
    Fly,
    Hover,
    Glide,
    Passenger,
    Fight,
    Aura,
    /// <summary>Stepped up to bowl (#1353): the stick walks the spot and hooks, Bowl held swings.</summary>
    Bowl,
}

/// <summary>What else changes the layout within a mode.</summary>
[Flags]
public enum TouchFlags
{
    None = 0,
    ItemInHand = 1 << 0,
    /// <summary>A gearbox shifted by hand: Gear + and −.</summary>
    Gears = 1 << 1,
    /// <summary>A clutch pedal to work (a manual car, a truck's clutch box).</summary>
    Clutch = 1 << 2,
    /// <summary>A craft with guns.</summary>
    Armed = 1 << 3,
    Pigeon = 1 << 4,
    Wingsuit = 1 << 5,
    Ufo = 1 << 6,
    Airliner = 1 << 7,
    /// <summary>A machine with a work mode (excavator, loader, telehandler) while it drives.</summary>
    WorkMode = 1 << 8,
    /// <summary>Setting: a Run button instead of pushing the stick past its ring.</summary>
    RunButton = 1 << 9,
    /// <summary>Setting: a tap on the world uses the item, so there is no Use button.</summary>
    TapToUse = 1 << 10,
}

/// <summary>The mode and its flags, and the ride's type name (it picks the More sheet's groups).</summary>
public readonly record struct TouchState(TouchMode Mode, TouchFlags Flags, string? Ride = null);

public enum TouchShape { Round, Pedal, Rocker, Stick, Top }

/// <summary>The corner a control is placed from. Mirrored for the left-handed.</summary>
public enum TouchCorner { BottomRight, TopRight }

public enum TouchSendKind
{
    /// <summary>A pad button on the virtual pad.</summary>
    Button,
    /// <summary>A trigger, fully in while held.</summary>
    Trigger,
    /// <summary>A rocker: up pushes <see cref="TouchSend.Axis"/>, down <see cref="TouchSend.Axis2"/>, springing back.</summary>
    Rocker,
    /// <summary>The pad's right stick.</summary>
    RightStick,
    /// <summary>An input action with no pad button (the map, the bag).</summary>
    Action,
    /// <summary>Opens the More sheet.</summary>
    More,
    /// <summary>Enter, which opens the chat.</summary>
    Chat,
}

/// <summary>What pressing a control sends.</summary>
public readonly record struct TouchSend(TouchSendKind Kind, JoyButton Button = JoyButton.Invalid,
    JoyAxis Axis = JoyAxis.Invalid, JoyAxis Axis2 = JoyAxis.Invalid, string Action = "")
{
    public static TouchSend Pad(JoyButton b) => new(TouchSendKind.Button, Button: b);
    public static TouchSend Trigger(JoyAxis a) => new(TouchSendKind.Trigger, Axis: a);
    public static TouchSend Rocker => new(TouchSendKind.Rocker, Axis: JoyAxis.TriggerRight, Axis2: JoyAxis.TriggerLeft);
    public static TouchSend RightStick => new(TouchSendKind.RightStick);
    public static TouchSend Act(string action) => new(TouchSendKind.Action, Action: action);
    public static TouchSend More => new(TouchSendKind.More);
    public static TouchSend Chat => new(TouchSendKind.Chat);
}

[Flags]
public enum TouchFeel
{
    None = 0,
    /// <summary>Drawn bigger and brighter: the mode's main control.</summary>
    Primary = 1,
    /// <summary>The finger's drag also turns the camera: aim while firing.</summary>
    DragLook = 2,
    /// <summary>Slid off its top and let go, it stays down (the gas).</summary>
    Lockable = 4,
    /// <summary>Hidden while the hotbar (and its own bag) is on screen.</summary>
    NotWithHotbar = 8,
    /// <summary>Releases a <see cref="Lockable"/> control that is locked down (the brake).</summary>
    Unlocks = 16,
}

/// <summary>
/// A player's change to one control (#1048): where it sits (mm from its corner, as
/// <see cref="TouchKey.Mm"/>), how big against its own size, or hidden (still in More).
/// </summary>
public sealed class TouchOverride
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Size { get; set; } = 1f;
    public bool Hidden { get; set; }

    public TouchOverride Copy() => new() { X = X, Y = Y, Size = Size, Hidden = Hidden };
}

/// <summary>
/// One on-screen control: where it sits, in millimetres from its corner (x inwards, y away from the
/// edge), and how big it is, before the size setting.
/// </summary>
public sealed record TouchKey(string Id, string Label, TouchShape Shape, TouchSend Send, TouchCorner Corner,
    Vector2 Mm, Vector2 SizeMm, TouchFeel Feel = TouchFeel.None)
{
    public bool Has(TouchFeel f) => (Feel & f) != 0;
}

/// <summary>
/// The phone's controls per mode (#1043, docs/notes/core/touch-controls.md): a pure table, so the
/// tier-0 tests can lay every mode out on every screen and look for overlaps. Each mode keeps 2 to 4
/// controls under the right thumb, in the same places for as long as the mode lasts; the rest of
/// what applies comes from the prompts (pills) or the More sheet. No new actions: every control
/// sends a pad button, a trigger, a stick, or an action the game already has.
/// </summary>
public static class TouchLayout
{
    /// <summary>Millimetres per typical 6-inch phone screen height, landscape: the --mobile preview's scale.</summary>
    public const float PhoneHeightMm = 68f;
    /// <summary>The floating stick's ring radius, mm.</summary>
    public const float StickRadiusMm = 12f;
    /// <summary>Where the stick waits until a thumb lands, mm from the bottom left (mirrored: right).</summary>
    public static readonly Vector2 StickHomeMm = new(22f, 20f);
    /// <summary>A pill's height and the gap between pills, mm.</summary>
    public const float PillHeightMm = 7f, PillGapMm = 1.5f;
    /// <summary>Extra touch margin round every control, mm.</summary>
    public const float HitMarginMm = 2f;

    /// <summary>The phone app's finger (#1253): a touch moving less than 2 mm is a tap, a sideways stroke of 10 mm is a back swipe, targets are 7 mm (9 mm primary).</summary>
    public const float TapSlopMm = 2f, BackSwipeMm = 10f, MinTargetMm = 7f, PrimaryTargetMm = 9f;

    /// <summary>Whether a press that has moved <paramref name="moved"/> pixels is a drag rather than a tap (slop in pixels).</summary>
    public static bool Beyond(Vector2 moved, float slopPx) => moved.Length() > slopPx;

    /// <summary>A mostly sideways stroke of at least <paramref name="minPx"/>: 1 to the right, -1 to the left, 0 when it is neither.</summary>
    public static int SideSwipe(Vector2 moved, float minPx) =>
        Mathf.Abs(moved.X) > Mathf.Abs(moved.Y) * 1.5f && Mathf.Abs(moved.X) >= minPx ? (moved.X > 0f ? 1 : -1) : 0;

    /// <summary>A mostly vertical stroke of at least <paramref name="minPx"/>: 1 down, -1 up, 0 when it is neither.</summary>
    public static int DownSwipe(Vector2 moved, float minPx) =>
        Mathf.Abs(moved.Y) > Mathf.Abs(moved.X) * 1.5f && Mathf.Abs(moved.Y) >= minPx ? (moved.Y > 0f ? 1 : -1) : 0;

    /// <summary>The largest size setting: past it a 16:9 phone has no room for the hotbar beside the buttons.</summary>
    public const float MaxScale = 1.2f;
    /// <summary>A hotbar cell on a phone, and the gap between cells, mm.</summary>
    public const float HotbarSlotMm = 7.5f, HotbarGapMm = 0.6f;
    /// <summary>How far the hotbar keeps from the right edge (the buttons' side), mm before the size setting.</summary>
    public const float HotbarClearMm = 49f;

    // the right thumb's arc, mm from the bottom right corner; Aim and Run stand above the hotbar's row
    private static readonly Vector2 Main = new(13.5f, 14.5f), Second = new(28f, 8f), Third = new(29f, 24f),
        Fourth = new(42f, 23f), RunAt = new(14f, 29f);
    private const float MainD = 13f, SecondD = 9.5f, ThirdD = 12f, FourthD = 9.5f;

    /// <summary>Every control of this mode, the top bar included.</summary>
    public static List<TouchKey> For(TouchMode mode, TouchFlags flags)
    {
        bool has(TouchFlags f) => (flags & f) != 0;
        var k = new List<TouchKey>();
        void round(string id, string label, TouchSend send, Vector2 at, float d, TouchFeel feel = TouchFeel.None) =>
            k.Add(new TouchKey(id, label, TouchShape.Round, send, TouchCorner.BottomRight, at, new Vector2(d, d), feel));
        void pedals(string gas)
        {
            k.Add(new TouchKey("gas", gas, TouchShape.Pedal, TouchSend.Trigger(JoyAxis.TriggerRight), TouchCorner.BottomRight,
                new Vector2(10f, 19f), new Vector2(12f, 26f), TouchFeel.Primary | TouchFeel.Lockable));
            k.Add(new TouchKey("brake", "Brake", TouchShape.Pedal, TouchSend.Trigger(JoyAxis.TriggerLeft), TouchCorner.BottomRight,
                new Vector2(24.5f, 14f), new Vector2(12f, 17f), TouchFeel.Unlocks));
        }
        void rocker(string label) =>
            k.Add(new TouchKey("rocker", label, TouchShape.Rocker, TouchSend.Rocker, TouchCorner.BottomRight,
                new Vector2(10.5f, 21f), new Vector2(11f, 30f), TouchFeel.Primary));
        void rightStick(string label) =>
            k.Add(new TouchKey("rstick", label, TouchShape.Stick, TouchSend.RightStick, TouchCorner.BottomRight,
                new Vector2(22f, 20f), new Vector2(2 * StickRadiusMm, 2 * StickRadiusMm)));

        switch (mode)
        {
            case TouchMode.Foot:
                round("jump", "Jump", TouchSend.Pad(JoyButton.A), Main, MainD, TouchFeel.Primary);
                round("crouch", "Crouch", TouchSend.Pad(JoyButton.B), Second, SecondD);
                if (has(TouchFlags.ItemInHand))
                {
                    if (!has(TouchFlags.TapToUse))
                        round("use", "Use", TouchSend.Pad(JoyButton.RightShoulder), Third, ThirdD, TouchFeel.Primary | TouchFeel.DragLook);
                    round("aim", "Aim", TouchSend.Pad(JoyButton.LeftShoulder), Fourth, FourthD, TouchFeel.DragLook);
                }
                if (has(TouchFlags.RunButton)) round("run", "Run", TouchSend.Pad(JoyButton.LeftStick), RunAt, 9f);
                break;
            case TouchMode.Spectator:
            case TouchMode.CreativeFly:
                round("up", "Up", TouchSend.Pad(JoyButton.A), Main, MainD, TouchFeel.Primary);
                round("down", "Down", TouchSend.Pad(JoyButton.B), Second, SecondD);
                if (mode == TouchMode.Spectator) round("walk", "Walk", TouchSend.Pad(JoyButton.DpadDown), Fourth, FourthD);
                if (has(TouchFlags.RunButton)) round("run", "Boost", TouchSend.Pad(JoyButton.LeftStick), RunAt, 9f);
                break;
            case TouchMode.Drive:
                pedals("Gas");
                round("horn", "Horn", TouchSend.Pad(JoyButton.DpadLeft), new Vector2(37f, 9f), 8f);
                if (has(TouchFlags.Gears))
                {
                    round("gearup", "Gear +", TouchSend.Pad(JoyButton.RightShoulder), new Vector2(37f, 30f), 8f);
                    round("geardown", "Gear −", TouchSend.Pad(JoyButton.LeftShoulder), new Vector2(37f, 20f), 8f);
                }
                // the cards' "Handbrake: A" (#1054): a car's slide, a stop on a hill
                round("handbrake", "Handbrake", TouchSend.Pad(JoyButton.A), new Vector2(49f, 20f), 8f);
                if (has(TouchFlags.Clutch)) round("clutch", "Clutch", TouchSend.Pad(JoyButton.B), new Vector2(49f, 9f), 8f);
                else if (has(TouchFlags.WorkMode)) round("work", "Work", TouchSend.Pad(JoyButton.B), new Vector2(49f, 9f), 8f);
                break;
            case TouchMode.Ride:
                pedals("Pedal");
                round("tuck", "Tuck", TouchSend.Pad(JoyButton.X), new Vector2(37f, 9f), 8f);
                round("hop", "Hop", TouchSend.Pad(JoyButton.A), new Vector2(37f, 20f), 8f);
                break;
            case TouchMode.Snow:
                round("jump", "Jump", TouchSend.Pad(JoyButton.A), Main, MainD, TouchFeel.Primary);
                round("brake", "Brake", TouchSend.Trigger(JoyAxis.TriggerLeft), Second, 10f);
                round("schuss", "Schuss", TouchSend.Pad(JoyButton.X), Third, 11f);
                break;
            case TouchMode.Dig:
                rightStick("Boom / bucket");
                round("drive", "Drive", TouchSend.Pad(JoyButton.B), new Vector2(45f, 8.5f), 8f);
                break;
            case TouchMode.Crane:
                rightStick("Hoist");
                break;
            case TouchMode.Fly:
                rocker("Throttle");
                if (has(TouchFlags.Armed)) round("fire", "Fire", TouchSend.Pad(JoyButton.RightShoulder), new Vector2(25f, 12f), 12f, TouchFeel.Primary | TouchFeel.DragLook);
                else if (has(TouchFlags.Airliner))
                {
                    round("flapsdown", "Flaps +", TouchSend.Pad(JoyButton.RightShoulder), new Vector2(25f, 21f), 8f);
                    round("flapsup", "Flaps −", TouchSend.Pad(JoyButton.LeftShoulder), new Vector2(25f, 10f), 8f);
                }
                break;
            case TouchMode.Hover:
                rocker("Climb");
                if (has(TouchFlags.Armed)) round("fire", "Fire", TouchSend.Pad(JoyButton.RightShoulder), new Vector2(25f, 12f), 12f, TouchFeel.Primary | TouchFeel.DragLook);
                else if (has(TouchFlags.Ufo)) round("boost", "Boost", TouchSend.Pad(JoyButton.LeftStick), new Vector2(25f, 12f), 9.5f);
                break;
            case TouchMode.Glide:
                round("action", has(TouchFlags.Pigeon) ? "Flap" : has(TouchFlags.Wingsuit) ? "Open canopy" : "Flare",
                    TouchSend.Pad(JoyButton.A), Main, MainD, TouchFeel.Primary);
                if (has(TouchFlags.Pigeon))
                {
                    round("dive", "Dive", TouchSend.Pad(JoyButton.B), Second, SecondD);
                    round("drop", "Drop", TouchSend.Pad(JoyButton.RightShoulder), Third, 10f);
                }
                break;
            case TouchMode.Fight:
                round("punch", "Punch", TouchSend.Pad(JoyButton.X), Main, MainD, TouchFeel.Primary);
                round("kick", "Kick", TouchSend.Pad(JoyButton.Y), Third, 11.5f, TouchFeel.Primary);
                round("block", "Block", TouchSend.Pad(JoyButton.B), Second, SecondD);
                round("jump", "Jump", TouchSend.Pad(JoyButton.A), Fourth, 8f);
                break;
            case TouchMode.Aura:
                round("next", "Next move", TouchSend.Pad(JoyButton.A), new Vector2(21f, 21f), 20f, TouchFeel.Primary);
                break;
            case TouchMode.Bowl:
                // held through the swing; the thumb sliding off it sideways is the hook (a look drag)
                round("bowl", "Bowl", TouchSend.Pad(JoyButton.A), Main, MainD, TouchFeel.Primary | TouchFeel.DragLook);
                round("back", "Back", TouchSend.Pad(JoyButton.B), Second, SecondD);
                round("pins", "Pins", TouchSend.Pad(JoyButton.LeftStick), Fourth, FourthD);
                break;
        }

        // the top bar: rare and deliberate, a stretch away on purpose (action names as PlayerInput
        // has them: this file is pure, for the tier-0 tests)
        void top(string id, string label, TouchSend send, float x, float w = 7f, TouchFeel feel = TouchFeel.None) =>
            k.Add(new TouchKey(id, label, TouchShape.Top, send, TouchCorner.TopRight, new Vector2(x, 6f), new Vector2(w, 6f), feel));
        top("pause", "II", TouchSend.Pad(JoyButton.Start), 6f);
        top("more", "•••", TouchSend.More, 14.5f);
        top("map", "MAP", TouchSend.Act("teleport"), 23f);
        top("chat", "CHAT", TouchSend.Chat, 31.5f);
        top("cam", "CAM", TouchSend.Pad(JoyButton.RightStick), 40f);
        if (mode is TouchMode.Drive or TouchMode.Ride or TouchMode.Dig or TouchMode.Fly or TouchMode.Hover)
            top("exit", "Get out", TouchSend.Pad(JoyButton.Y), 51.5f, 12f);
        else if (mode is TouchMode.Foot or TouchMode.Passenger)
            top("bag", "BAG", TouchSend.Act("inventory"), 49f, 8f, TouchFeel.NotWithHotbar);
        if (mode == TouchMode.Foot) top("phone", "PHONE", TouchSend.Act("phone"), 60f, 9f);
        return k;
    }

    /// <summary>Smallest and largest a control can be made in the editor, against its own size.</summary>
    public const float MinOverrideSize = 0.6f, MaxOverrideSize = 1.6f;

    /// <summary>The flag sets that change which controls a mode shows: every layout a mode can have.</summary>
    public static readonly TouchFlags[] FlagSets =
    {
        TouchFlags.None, TouchFlags.ItemInHand, TouchFlags.ItemInHand | TouchFlags.RunButton,
        TouchFlags.ItemInHand | TouchFlags.TapToUse, TouchFlags.Gears | TouchFlags.Clutch,
        TouchFlags.Gears | TouchFlags.WorkMode, TouchFlags.Armed, TouchFlags.Airliner, TouchFlags.Ufo,
        TouchFlags.Pigeon, TouchFlags.Wingsuit, TouchFlags.RunButton,
    };

    /// <summary>The flags that show the most of a mode's controls at once, for the editor's still view.</summary>
    public static TouchFlags Fullest(TouchMode mode) => mode switch
    {
        TouchMode.Foot => TouchFlags.ItemInHand,
        TouchMode.Drive => TouchFlags.Gears | TouchFlags.Clutch,
        TouchMode.Fly or TouchMode.Hover => TouchFlags.Armed,
        TouchMode.Glide => TouchFlags.Pigeon,
        _ => TouchFlags.None,
    };

    /// <summary>An override for this control in this mode only.</summary>
    public static string ModeKey(TouchMode mode, TouchKey key) => $"{mode}:{key.Id}";

    /// <summary>
    /// An override shared by every mode with the same control (same id and shape: Jump on foot and
    /// in a fight, the top bar, the pedals), unless a mode has its own.
    /// </summary>
    public static string SharedKey(TouchKey key) => $"*:{key.Id}:{key.Shape}";

    /// <summary>The override that applies to a control here: this mode's own, else the shared one, else null.</summary>
    public static TouchOverride? OverrideFor(TouchMode mode, TouchKey key, IReadOnlyDictionary<string, TouchOverride>? overrides) =>
        overrides == null ? null
        : overrides.TryGetValue(ModeKey(mode, key), out var own) ? own
        : overrides.TryGetValue(SharedKey(key), out var shared) ? shared : null;

    /// <summary>The controls with the player's overrides applied: moved, resized, hidden ones left out.</summary>
    public static List<TouchKey> Apply(List<TouchKey> keys, TouchMode mode, IReadOnlyDictionary<string, TouchOverride>? overrides)
    {
        if (overrides == null || overrides.Count == 0) return keys;
        var result = new List<TouchKey>(keys.Count);
        foreach (var key in keys)
        {
            if (OverrideFor(mode, key, overrides) is not { } o) result.Add(key);
            else if (!o.Hidden)
                result.Add(key with { Mm = new Vector2(o.X, o.Y), SizeMm = key.SizeMm * Math.Clamp(o.Size, MinOverrideSize, MaxOverrideSize) });
        }
        return result;
    }

    /// <summary>
    /// The first thing these overrides break on this screen, or null: in every mode and layout
    /// variant that has the control <paramref name="id"/> (all of them when null), two controls
    /// overlapping, one leaving the screen, or the hotbar running under one on foot.
    /// </summary>
    public static string? Conflict(IReadOnlyDictionary<string, TouchOverride> overrides, Rect2 safe, float pxPerMm, float scale,
        bool leftHanded, string? id = null)
    {
        foreach (var mode in Enum.GetValues<TouchMode>())
            foreach (var flags in FlagSets)
            {
                var all = For(mode, flags);
                if (id != null && !all.Exists(k => k.Id == id)) continue;
                var keys = Apply(all, mode, overrides);
                var rects = keys.Select(k => Place(k, safe, pxPerMm, scale, leftHanded)).ToList();
                var bar = mode == TouchMode.Foot ? Hotbar(safe, pxPerMm, scale, leftHanded, 6) : (Rect2?)null;
                for (int i = 0; i < keys.Count; i++)
                {
                    if (id != null && keys[i].Id != id) continue;
                    if (!safe.Encloses(rects[i])) return $"{keys[i].Label} would leave the screen";
                    if (bar is { } b && b.Intersects(rects[i])) return $"{keys[i].Label} would cover the hotbar";
                    for (int j = 0; j < keys.Count; j++)
                        if (j != i && Overlap(keys[i], rects[i], keys[j], rects[j], gap: 0.5f * pxPerMm))
                            return $"{keys[i].Label} would cover {keys[j].Label} ({ModeName(mode)})";
                }
            }
        return null;
    }

    /// <summary>What the player calls a mode, for the editor.</summary>
    public static string ModeName(TouchMode mode) => mode switch
    {
        TouchMode.Foot => "on foot",
        TouchMode.Spectator => "fly camera",
        TouchMode.CreativeFly => "creative flight",
        TouchMode.Drive => "driving",
        TouchMode.Ride => "bike",
        TouchMode.Snow => "skis",
        TouchMode.Dig => "digging",
        TouchMode.Crane => "crane",
        TouchMode.Fly => "plane",
        TouchMode.Hover => "helicopter",
        TouchMode.Glide => "gliding",
        TouchMode.Passenger => "passenger",
        TouchMode.Fight => "fist fight",
        TouchMode.Aura => "aura battle",
        TouchMode.Bowl => "bowling",
        _ => mode.ToString(),
    };

    /// <summary>Whether the left stick is on screen (a passenger and an aura battle only look).</summary>
    public static bool HasStick(TouchMode mode) => mode is not (TouchMode.Passenger or TouchMode.Aura);

    /// <summary>The word under the left stick.</summary>
    public static string StickLabel(TouchMode mode) => mode switch
    {
        TouchMode.Drive or TouchMode.Ride or TouchMode.Glide => "Steer",
        TouchMode.Snow => "Lean",
        TouchMode.Dig => "Slew / stick",
        TouchMode.Crane => "Slew / trolley",
        TouchMode.Fly => "Pitch / roll",
        TouchMode.Fight => "Step",
        TouchMode.Bowl => "Spot / hook",
        TouchMode.Spectator or TouchMode.CreativeFly => "Fly",
        _ => "Move",
    };

    /// <summary>Whether pushing the stick past its ring presses L3 (run, fly boost) in this mode.</summary>
    public static bool StickRuns(TouchMode mode) => mode is TouchMode.Foot or TouchMode.Spectator or TouchMode.CreativeFly or TouchMode.Fight;

    /// <summary>
    /// Where a control is drawn, in canvas pixels: <paramref name="safe"/> is the screen's safe
    /// area, <paramref name="pxPerMm"/> the canvas pixels in a millimetre, <paramref name="scale"/>
    /// the size setting, which grows the controls and their distances from the corner together.
    /// </summary>
    public static Rect2 Place(TouchKey key, Rect2 safe, float pxPerMm, float scale, bool leftHanded)
    {
        float k = pxPerMm * scale;
        float x = leftHanded ? safe.Position.X + key.Mm.X * k : safe.End.X - key.Mm.X * k;
        float y = key.Corner == TouchCorner.TopRight ? safe.Position.Y + key.Mm.Y * k : safe.End.Y - key.Mm.Y * k;
        var size = key.SizeMm * k;
        return new Rect2(new Vector2(x, y) - size / 2, size);
    }

    /// <summary>
    /// The hotbar on a phone, the bag's cell first: bottom centre, pushed off the buttons' side so it
    /// stays clear of them, and inside the screen.
    /// </summary>
    public static Rect2 Hotbar(Rect2 safe, float pxPerMm, float scale, bool leftHanded, int slots)
    {
        int cells = slots + 1;
        float s = HotbarSlotMm * pxPerMm, gap = HotbarGapMm * pxPerMm, w = cells * s + (cells - 1) * gap;
        float clear = HotbarClearMm * pxPerMm * scale, centre = safe.GetCenter().X;
        float x = leftHanded ? Mathf.Max(centre - w / 2, safe.Position.X + clear) : Mathf.Min(centre + w / 2, safe.End.X - clear) - w;
        x = Mathf.Clamp(x, safe.Position.X, safe.End.X - w);
        return new Rect2(x, safe.End.Y - pxPerMm - s, w, s);
    }

    /// <summary>The stick's waiting place, canvas pixels.</summary>
    public static Vector2 StickHome(Rect2 safe, float pxPerMm, float scale, bool leftHanded)
    {
        float k = pxPerMm * scale;
        return new Vector2(leftHanded ? safe.End.X - StickHomeMm.X * k : safe.Position.X + StickHomeMm.X * k,
            safe.End.Y - StickHomeMm.Y * k);
    }

    /// <summary>Whether two placed controls overlap (a round one by its circle).</summary>
    public static bool Overlap(TouchKey a, Rect2 ra, TouchKey b, Rect2 rb, float gap = 0f)
    {
        bool roundA = a.Shape is TouchShape.Round or TouchShape.Stick, roundB = b.Shape is TouchShape.Round or TouchShape.Stick;
        if (roundA && roundB)
            return ra.GetCenter().DistanceTo(rb.GetCenter()) < ra.Size.X / 2 + rb.Size.X / 2 + gap;
        if (roundA || roundB)
        {
            var (circle, box) = roundA ? (ra, rb) : (rb, ra);
            var c = circle.GetCenter();
            var nearest = new Vector2(Mathf.Clamp(c.X, box.Position.X, box.End.X), Mathf.Clamp(c.Y, box.Position.Y, box.End.Y));
            return nearest.DistanceTo(c) < circle.Size.X / 2 + gap;
        }
        return ra.Grow(gap / 2).Intersects(rb.Grow(gap / 2));
    }
}
