using Godot;

namespace UnitSport.Core;

/// <summary>
/// <c>godot --headless --path . -- --menucheck</c>: boots normally (the mode menu opens), then
/// drives the menu with real input events — Esc, pad Start and B, and Esc out of the settings
/// panel — and checks after each that it opened or closed, and that a closed menu stays closed.
/// Non-zero exit on the first step that fails.
/// </summary>
public partial class MenuCheck : Node
{
    private readonly MainMenu _menu;
    private int _step;
    private double _wait;

    public MenuCheck(MainMenu menu) => _menu = menu;

    public static bool Requested() => System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--menucheck") >= 0;

    // (what to do, what IsOpen must then be, seconds to wait before checking)
    private (System.Action Act, bool Open, double Wait, string Name)[] Steps => new (System.Action, bool, double, string)[]
    {
        (() => { }, true, 0.5, "open at boot"),
        (Esc, false, 0.3, "Esc closes the boot menu"),
        (() => { }, false, 2.0, "and it stays closed"),
        (Esc, true, 0.3, "Esc opens it again"),
        (Esc, false, 0.3, "Esc closes it"),
        (() => Pad(JoyButton.Start), true, 0.3, "pad Start opens it"),
        (() => Pad(JoyButton.B), false, 0.3, "pad B closes it"),
        (() => _menu.OpenSettings(), true, 0.3, "settings open"),
        (Esc, true, 0.3, "Esc from settings goes back to the menu"),
        (Esc, false, 0.3, "Esc closes the menu"),
    };

    public override void _Process(double delta)
    {
        var steps = Steps;
        if (_step >= steps.Length) return;
        if (_wait == 0) steps[_step].Act();
        _wait += delta;
        if (_wait < steps[_step].Wait) return;

        bool ok = _menu.IsOpen == steps[_step].Open;
        GD.Print($"[menucheck] {(ok ? "ok  " : "FAIL")} {steps[_step].Name} (open={_menu.IsOpen})");
        if (!ok) { GetTree().Quit(1); _step = steps.Length; return; }
        _wait = 0;
        if (++_step == steps.Length)
        {
            GD.Print("[menucheck] RESULT: ok");
            GetTree().Quit(0);
        }
    }

    private static void Esc()
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = down });
    }

    private static void Pad(JoyButton button)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = down });
    }
}
