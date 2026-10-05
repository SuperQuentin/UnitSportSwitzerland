using System.Text.RegularExpressions;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// What to <i>print</i> for a control: the key or button an action is bound to, named for the
/// device the player is actually holding.
///
/// <para>
/// Every hint is read from the live <see cref="InputMap"/>, never typed into a string, so a
/// rebind or a change of default cannot leave a menu advertising the old key. Keyboard bindings
/// are physical keycodes, and a physical key is printed through
/// <see cref="DisplayServer.KeyboardGetLabelFromPhysical"/>: the key bound as "Z" is the one
/// labelled W on an AZERTY keyboard and Y on a Swiss QWERTZ one, and the hint says so.
/// </para>
///
/// <para>
/// Strings shown to the player can carry <c>{action}</c> placeholders (<see cref="Format"/>), so
/// data like item blurbs names the controls without knowing them. Anything that keeps a hint on
/// screen should re-format on <see cref="PlayerInput.DeviceChanged"/>.
/// </para>
/// </summary>
public static class InputHints
{
    /// <summary>The binding's name for the current device, e.g. "E", "LMB", "Y", "D-pad ↑", "R trigger". "?" when unbound.</summary>
    public static string Label(string action) =>
        Label(action, PlayerInput.HintDevice);

    // Memoised (#221): HUD prompts ask every frame, and each lookup marshals the action's events
    // and builds a list. Bindings only change in PlayerInput.Bind, which calls Invalidate.
    // In VR a control's name also depends on whether the triggers are the shoulders (on foot) or
    // the triggers (mounted), XR.XrPad.TriggersAsShoulders: part of the key, false elsewhere.
    private static readonly Dictionary<(string, InputDevice, bool), string> Labels = new(), Formatted = new();

    private static bool Context(InputDevice device) => device == InputDevice.VR && XR.XrPad.TriggersAsShoulders;

    /// <summary>Forget the memoised labels: call after any change to the input map.</summary>
    public static void Invalidate()
    {
        Labels.Clear();
        Formatted.Clear();
        Buttons.Clear();
        Keys.Clear();
    }

    public static string Label(string action, InputDevice device)
    {
        var key = (action, device, Context(device));
        if (Labels.TryGetValue(key, out var label)) return label;
        return Labels[key] = Lookup(action, device);
    }

    private static string Lookup(string action, InputDevice device)
    {
        if (!InputMap.HasAction(action)) return "?";
        var events = InputMap.ActionGetEvents(action);

        if (device == InputDevice.VR)
        {
            // the control XR.XrPad replays as this pad event, by the controller's own name (#435)
            foreach (var e in events)
                if (XR.XrPad.Control(e) is { } control && XR.XrProfile.Name(control) is { } name) return name;
            // nothing on the controllers: the wrist menu, else a keyboard within reach (#437)
            if (XR.XrHands.Gesture(action) is { } gesture) return gesture;
            if (XR.XrWristMenu.Reaches(action)) return "Wrist";
        }
        else if (device == InputDevice.Gamepad)
        {
            foreach (var e in events)
                if (PadName(e) is { } pad) return pad;
            // no pad binding: the keyboard key is still better than nothing (the place search)
        }

        var keys = new List<string>();
        foreach (var e in events)
            if (KeyName(e) is { } key && !keys.Contains(key)) keys.Add(key);
        return keys.Count > 0 ? string.Join(" / ", keys.Take(2)) : "?";
    }

    /// <summary>True when hints name buttons rather than keys: a pad, or the VR controllers.</summary>
    public static bool Pad => PlayerInput.HintDevice != InputDevice.KeyboardMouse;

    /// <summary>True when the VR controllers are what hints name.</summary>
    public static bool Vr => PlayerInput.HintDevice == InputDevice.VR;

    private static readonly Dictionary<(JoyButton, InputDevice, bool), string> Buttons = new();

    /// <summary>
    /// A pad button some screen reads directly instead of through an action (a menu's tab switch),
    /// named for the device in hand: "RB" on a pad, "R grip" on VR controllers.
    /// </summary>
    public static string Button(JoyButton button)
    {
        var device = PlayerInput.HintDevice;
        var key = (button, device, Context(device));
        if (Buttons.TryGetValue(key, out var name)) return name;
        var e = new InputEventJoypadButton { ButtonIndex = button };
        name = device == InputDevice.VR && XR.XrPad.Control(e) is { } c && XR.XrProfile.Name(c) is { } vr ? vr
            : PadName(e) ?? "?";
        return Buttons[key] = name;
    }

    /// <summary>A physical key some screen reads directly, as printed on the player's keyboard.</summary>
    public static string Keyboard(Key physical)
    {
        if (Keys.TryGetValue(physical, out var name)) return name;
        return Keys[physical] = PhysicalLabel(physical);
    }

    private static readonly Dictionary<Key, string> Keys = new();

    /// <summary>The binding in brackets, "[E]", the form every prompt uses.</summary>
    public static string Tag(string action) => $"[{Label(action)}]";

    /// <summary>"[E] Get in": a prompt line.</summary>
    public static string Prompt(string action, string text) => $"{Tag(action)} {text}";

    private static readonly Regex Placeholder = new(@"\{([a-z_0-9]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces each <c>{action}</c> by that action's binding, bare ("Hold {aim_item} to look"
    /// becomes "Hold RMB to look"). Braces that name no action are left as they are.
    /// </summary>
    public static string Format(string text) => Format(text, PlayerInput.HintDevice);

    public static string Format(string text, InputDevice device)
    {
        var key = (text, device, Context(device));
        if (Formatted.TryGetValue(key, out var done)) return done;
        // ponytail: some texts carry numbers (loot toasts), so the cache is simply dropped when it grows
        if (Formatted.Count > 256) Formatted.Clear();
        return Formatted[key] =
            Placeholder.Replace(text, m => InputMap.HasAction(m.Groups[1].Value) ? Label(m.Groups[1].Value, device) : m.Value);
    }

    // ------------------------------------------------------------------------------------

    private static string? KeyName(InputEvent e) => e switch
    {
        InputEventKey k when k.PhysicalKeycode != Key.None => PhysicalLabel(k.PhysicalKeycode),
        InputEventKey k when k.Keycode != Key.None => OS.GetKeycodeString(k.Keycode),
        InputEventMouseButton m => m.ButtonIndex switch
        {
            MouseButton.Left => "LMB",
            MouseButton.Right => "RMB",
            MouseButton.Middle => "MMB",
            MouseButton.WheelUp => "Wheel ↑",
            MouseButton.WheelDown => "Wheel ↓",
            _ => $"Mouse {(int)m.ButtonIndex}",
        },
        _ => null,
    };

    /// <summary>The letter printed on this physical key in the active layout, else its QWERTY name.</summary>
    private static string PhysicalLabel(Key physical)
    {
        // Named keys have no printed letter to look up, and some layouts report nothing for them.
        switch (physical)
        {
            case Key.Space: return "Space";
            case Key.Shift: return "Shift";
            case Key.Ctrl: return "Ctrl";
            case Key.Alt: return "Alt";
            case Key.Tab: return "Tab";
            case Key.Escape: return "Esc";
            case Key.Enter: return "Enter";
            case Key.Backspace: return "Backspace";
        }
        if (physical is >= Key.F1 and <= Key.F12) return OS.GetKeycodeString(physical);

        var label = DisplayServer.GetName() == "headless" ? Key.None : DisplayServer.KeyboardGetLabelFromPhysical(physical);
        string name = OS.GetKeycodeString(label != Key.None ? label : physical);
        return name.Length == 1 ? name.ToUpperInvariant() : name;
    }

    /// <summary>Xbox names: the layout most PC pads use, and what Godot's JoyButton enum follows.</summary>
    private static string? PadName(InputEvent e) => e switch
    {
        InputEventJoypadButton b => b.ButtonIndex switch
        {
            JoyButton.A => "A",
            JoyButton.B => "B",
            JoyButton.X => "X",
            JoyButton.Y => "Y",
            JoyButton.LeftShoulder => "LB",
            JoyButton.RightShoulder => "RB",
            JoyButton.LeftStick => "L3",
            JoyButton.RightStick => "R3",
            JoyButton.Back => "Back",
            JoyButton.Start => "Start",
            JoyButton.Guide => "Guide",
            JoyButton.DpadUp => "D-pad ↑",
            JoyButton.DpadDown => "D-pad ↓",
            JoyButton.DpadLeft => "D-pad ←",
            JoyButton.DpadRight => "D-pad →",
            _ => $"Button {(int)b.ButtonIndex}",
        },
        InputEventJoypadMotion m => m.Axis switch
        {
            JoyAxis.TriggerLeft => "LT",
            JoyAxis.TriggerRight => "RT",
            JoyAxis.LeftX or JoyAxis.LeftY => "Left stick",
            JoyAxis.RightX or JoyAxis.RightY => "Right stick",
            _ => null,
        },
        _ => null,
    };
}
