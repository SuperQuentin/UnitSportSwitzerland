using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Held keys that should not count (#1002, docs/notes/core/held-keys.md):
/// <list type="bullet">
/// <item><see cref="HoldOff"/>: what is held at a switch (Shift of Shift+T into the spectator
/// camera) counts only once let go and pressed again, or the camera sinks on the Shift that chose it.</item>
/// <item>Stuck keys: Godot knows a key is up only from its key-up event, which a long hitch can lose
/// (Windows' "not responding" ghost window takes the input), so a key let go then stays held.
/// On Windows the keys believed down are checked against the OS's live state a few times a second,
/// and a key up there twice running is released.</item>
/// </list>
/// </summary>
public partial class PlayerInput
{
    /// <summary>Actions held at the last <see cref="HoldOff"/>, not yet let go: read as released.</summary>
    private static readonly HashSet<string> HeldOffActions = new();

    /// <summary>
    /// Every action held now reads as released (<see cref="Held"/>, <see cref="Strength"/>,
    /// <see cref="Move"/>) until it is let go: for a switch between controls, so the keys that made
    /// it do not act on what it switched to.
    /// </summary>
    public static void HoldOff()
    {
        foreach (var action in InputMap.GetActions())
            if (Input.IsActionPressed(action)) HeldOffActions.Add(action.ToString());
    }

    private static bool HeldOff(string action) => HeldOffActions.Count > 0 && HeldOffActions.Contains(action);

    /// <summary><see cref="Move"/> while some of it is held off: the four keys one by one.</summary>
    private static Vector2 MoveHeldOff()
    {
        var v = new Vector2(Part(NRight, MoveRight) - Part(NLeft, MoveLeft), Part(NBack, MoveBack) - Part(NForward, MoveForward));
        return v.LengthSquared() > 1f ? v.Normalized() : v;

        static float Part(StringName name, string action) => HeldOff(action) ? 0f : Input.GetActionStrength(name);
    }

    private static bool MoveIsHeldOff => HeldOffActions.Count > 0
        && (HeldOffActions.Contains(MoveLeft) || HeldOffActions.Contains(MoveRight) || HeldOffActions.Contains(MoveForward) || HeldOffActions.Contains(MoveBack));

    /// <summary>Let go since the hold-off: counts again from its next press.</summary>
    private static void SweepHeldOff()
    {
        if (HeldOffActions.Count > 0) HeldOffActions.RemoveWhere(static a => !Input.IsActionPressed(ActionName(a)));
    }

    // ---- stuck keys (Windows) ----

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    private const uint MapScanToVk = 1;   // MAPVK_VSC_TO_VK

    /// <summary>Physical keys Godot believes down, with the key-down event that pressed them.</summary>
    private static readonly Dictionary<Key, InputEventKey> DownKeys = new();
    /// <summary>Keys the OS said were up at the last look: released if it says so again.</summary>
    private static readonly HashSet<Key> UpOnce = new();
    private static readonly List<Key> ToRelease = new();
    private static double _nextStuckCheck;
    private const double StuckCheckSeconds = 0.1;

    private static readonly bool StuckCheckOn = OS.GetName() == "Windows" && DisplayServer.GetName() != "headless";

    private static void TrackKey(InputEventKey k)
    {
        if (k.Echo) return;
        var key = k.PhysicalKeycode;
        if (key == Key.None) return;
        if (k.Pressed) DownKeys[key] = k;
        else
        {
            DownKeys.Remove(key);
            UpOnce.Remove(key);
        }
    }

    /// <summary>A few times a second: keys down for Godot and up for Windows, twice running, are let go.</summary>
    private static void ReleaseStuckKeys()
    {
        if (!StuckCheckOn || DownKeys.Count == 0) return;
        double now = RealClock.Now;
        if (now < _nextStuckCheck) return;
        _nextStuckCheck = now + StuckCheckSeconds;
        // unfocused, the OS state is someone else's typing; a check run in a window drives the keys itself
        if (!DisplayServer.WindowIsFocused() || MouseCapture.Disabled) { UpOnce.Clear(); return; }

        ToRelease.Clear();
        foreach (var key in DownKeys.Keys)
        {
            int vk = VirtualKey(key);
            if (vk == 0) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) { UpOnce.Remove(key); continue; }
            if (!UpOnce.Add(key)) ToRelease.Add(key);
        }
        foreach (var key in ToRelease)
        {
            GD.Print($"[input] {key} was let go during a hitch: released");
            // through Godot's own input, so the action and its "just released" edge follow as for a real key-up
            Input.ParseInputEvent(KeyUp(DownKeys[key]));
            DownKeys.Remove(key);
            UpOnce.Remove(key);
        }
    }

    /// <summary>
    /// The key-up for a key-down, on its device and with its keycode and label: Godot keeps an
    /// action pressed per device (real keys come as <see cref="InputEvent.DeviceIdKeyboard"/>), and
    /// <c>Input.IsKeyPressed</c> goes by the keycode, so a bare physical key-up on device 0 let go of
    /// neither and the key stayed held (#1283).
    /// </summary>
    internal static InputEventKey KeyUp(InputEventKey down) => new()
    {
        Device = down.Device,
        PhysicalKeycode = down.PhysicalKeycode,
        Keycode = down.Keycode,
        KeyLabel = down.KeyLabel,
        Location = down.Location,
        Pressed = false,
    };

    /// <summary>
    /// The Windows virtual key for a physical key, 0 for one not checked. Letters and digits go by
    /// their scan code (their US position), through the layout: on AZERTY the physical W is VK 'Z'.
    /// </summary>
    internal static int VirtualKey(Key key)
    {
        int scan = key switch
        {
            Key.Q => 0x10, Key.W => 0x11, Key.E => 0x12, Key.R => 0x13, Key.T => 0x14, Key.Y => 0x15,
            Key.U => 0x16, Key.I => 0x17, Key.O => 0x18, Key.P => 0x19,
            Key.A => 0x1E, Key.S => 0x1F, Key.D => 0x20, Key.F => 0x21, Key.G => 0x22, Key.H => 0x23,
            Key.J => 0x24, Key.K => 0x25, Key.L => 0x26,
            Key.Z => 0x2C, Key.X => 0x2D, Key.C => 0x2E, Key.V => 0x2F, Key.B => 0x30, Key.N => 0x31, Key.M => 0x32,
            >= Key.Key1 and <= Key.Key9 => 0x02 + (int)(key - Key.Key1), Key.Key0 => 0x0B,
            _ => 0,
        };
        if (scan != 0) return (int)MapVirtualKeyW((uint)scan, MapScanToVk);
        return key switch
        {
            Key.Shift => 0x10, Key.Ctrl => 0x11, Key.Alt => 0x12,
            Key.Space => 0x20, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Escape => 0x1B, Key.Backspace => 0x08,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28, Key.Home => 0x24, Key.End => 0x23,
            >= Key.F1 and <= Key.F12 => 0x70 + (int)(key - Key.F1),
            _ => 0,
        };
    }

    public override void _Process(double delta)
    {
        SweepHeldOff();
        ReleaseStuckKeys();
    }
}
