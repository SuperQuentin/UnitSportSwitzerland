using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --menucheck</c>: boots to the title screen, then walks the
/// menus with real input events — Esc on the title (consumed, title stays), Settings and back,
/// Play solo and back with pad B — starts Explore behind the loading screen, opens and closes the
/// pause menu with Esc and pad Start / B, and leaves to the title again. Each step waits for its
/// condition (with a timeout); non-zero exit on the first that fails.
/// </summary>
public partial class MenuCheck : Node
{
    private readonly GameShell _shell;
    private int _step;
    private double _wait;
    private bool _acted;

    public MenuCheck(GameShell shell)
    {
        _shell = shell;
        Name = "MenuCheck";
    }

    public static bool Requested() => Array.IndexOf(OS.GetCmdlineUserArgs(), "--menucheck") >= 0;

    private bool Top<T>() where T : Screen => _shell.Top is T;

    // (what to do, the condition to reach, how long it may take, name)
    private (Action Act, Func<bool> Ok, double Timeout, string Name)[] Steps => new (Action, Func<bool>, double, string)[]
    {
        (() => { }, Top<TitleScreen>, 5, "title at boot"),
        (Esc, () => Top<TitleScreen>() && _shell.Top!.Visible, 0.5, "Esc on the title is consumed, the title stays"),
        (() => _shell.Push(SettingsScreen.Create()), Top<SettingsScreen>, 1, "settings open"),
        (() => Pad(JoyButton.RightShoulder), Top<SettingsScreen>, 0.5, "RB changes tab"),
        (Esc, Top<TitleScreen>, 1, "Esc from settings goes back to the title"),
        (() => _shell.Push(SoloScreen.Create()), Top<SoloScreen>, 1, "play solo opens"),
        (() => Pad(JoyButton.B), Top<TitleScreen>, 1, "pad B goes back"),
        (() => _shell.Launch(new WorldLaunch { Mode = GameMode.Explore }), () => _shell.InWorld, 120, "Explore loads behind the loading screen"),
        (() => { }, () => _shell.Top == null && !_shell.MenuOpen, 1, "no menu once in the world"),
        (Esc, Top<PauseScreen>, 1, "Esc opens the pause menu"),
        (Esc, () => _shell.Top == null, 1, "Esc closes it"),
        (() => { }, () => _shell.Top == null, 1.5, "and it stays closed"),
        (() => Pad(JoyButton.Start), Top<PauseScreen>, 1, "pad Start opens it"),
        (() => Pad(JoyButton.B), () => _shell.Top == null, 1, "pad B closes it"),
        (Esc, Top<PauseScreen>, 1, "Esc opens it again"),
        (() => _shell.LeaveWorld(null), () => Top<TitleScreen>() && GetTree().Root.GetNodeOrNull("Main/World") == null, 30, "leave to the title, the world is gone"),
    };

    public override void _Process(double delta)
    {
        var steps = Steps;
        if (_step >= steps.Length) return;
        var (act, ok, timeout, name) = steps[_step];
        if (!_acted) { act(); _acted = true; _wait = 0; return; }
        _wait += delta;
        // a step with no action waits the whole time for a "stays" check, else passes as soon as it holds
        bool holds = ok();
        bool settleStep = name.Contains("stays");
        if (holds && (!settleStep || _wait >= timeout))
        {
            GD.Print($"[menucheck] ok   {name} ({_wait:F1} s)");
            Next(steps.Length);
        }
        else if (!holds && settleStep || _wait > timeout)
        {
            GD.Print($"[menucheck] FAIL {name} (top={_shell.Top?.Name ?? "none"}, world={_shell.World != null})");
            GD.Print("[menucheck] RESULT: FAILED");
            GetTree().Quit(1);
            _step = steps.Length;
        }
    }

    private void Next(int count)
    {
        _acted = false;
        if (++_step == count)
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
