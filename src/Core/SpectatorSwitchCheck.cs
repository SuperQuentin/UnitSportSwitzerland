using System.Threading.Tasks;
using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--spectatorcheck --world fixture</c> (#1002): Shift+T as real key events. On foot, Shift held
/// then T: the spectator camera takes over and does not sink on the Shift that chose it; Shift
/// pressed again sinks it; Shift+T again is back in the body; T alone switches creative mode.
/// Windowed on Windows, also: a W Godot believes down while Windows says up is let go (stuck keys).
/// Prints <c>[spectatorcheck] RESULT: ok</c>.
/// </summary>
public partial class SpectatorSwitchCheck : Node
{
    public static bool Requested => CmdArgs.Has("--spectatorcheck");

    private readonly System.Func<FootPlayer?> _local;
    private readonly System.Func<Camera3D?> _spectator;
    private int _failed;

    public SpectatorSwitchCheck(System.Func<FootPlayer?> local, System.Func<Camera3D?> spectator)
    {
        _local = local;
        _spectator = spectator;
        Name = "SpectatorSwitchCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[spectatorcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[spectatorcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>A key as the keyboard sends it: its device, keycode and label, not a bare physical key on device 0.</summary>
    private static InputEventKey KeyEvent(Key key, bool down, bool shift) => new()
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key,
        Pressed = down, ShiftPressed = shift,
    };

    private static void Key(Key key, bool down, bool shift) => Input.ParseInputEvent(KeyEvent(key, down, shift));

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor() && me.Camera.Current) break;
            // the world starts on the fly camera: T goes into the body
            if (me == null && i % 50 == 25)
            {
                Key(Godot.Key.T, true, false);
                await Wait(0.05);
                Key(Godot.Key.T, false, false);
            }
            await Wait(0.1);
        }
        if (me == null || _spectator() is not { } cam) { Finish("no local player or no spectator camera"); return; }
        await Wait(1.0);

        // Shift down, then T: Shift+T
        Key(Godot.Key.Shift, true, true);
        await Wait(0.1);
        Key(Godot.Key.T, true, true);
        await Wait(0.05);
        Key(Godot.Key.T, false, true);
        await Wait(0.2);
        Expect(cam.Current, "Shift+T: the spectator camera");
        float y = cam.GlobalPosition.Y;
        await Wait(1.0);
        Expect(Mathf.Abs(cam.GlobalPosition.Y - y) < 0.5f, $"the Shift still held does not sink it ({y:F1} -> {cam.GlobalPosition.Y:F1} m)");

        Key(Godot.Key.Shift, false, false);
        await Wait(0.1);
        Key(Godot.Key.Shift, true, true);
        y = cam.GlobalPosition.Y;
        await Wait(0.5);
        Expect(cam.GlobalPosition.Y < y - 5f, $"Shift pressed again sinks it ({y:F1} -> {cam.GlobalPosition.Y:F1} m)");

        Key(Godot.Key.T, true, true);
        await Wait(0.05);
        Key(Godot.Key.T, false, true);
        Key(Godot.Key.Shift, false, false);
        await Wait(0.3);
        Expect(me.Camera.Current && !cam.Current, "Shift+T again: back in the body");

        Key(Godot.Key.T, true, false);
        await Wait(0.05);
        Key(Godot.Key.T, false, false);
        await Wait(0.1);
        Expect(me.Creative, "T alone: creative mode");
        Key(Godot.Key.T, true, false);
        await Wait(0.05);
        Key(Godot.Key.T, false, false);
        await Wait(0.1);
        Expect(!me.Creative, "T again: survival");

        // the key-up made for a stuck key lets go of the action and the key as a real one does (#1283):
        // one on device 0 without the keycode left both held
        var down = KeyEvent(Godot.Key.W, true, false);
        Input.ParseInputEvent(down);
        await Wait(0.05);
        bool held = Input.IsActionPressed(PlayerInput.MoveForward) && Input.IsKeyPressed(Godot.Key.W);
        Input.ParseInputEvent(PlayerInput.KeyUp(down));
        await Wait(0.05);
        Expect(held && !Input.IsActionPressed(PlayerInput.MoveForward) && !Input.IsKeyPressed(Godot.Key.W) && !Input.IsPhysicalKeyPressed(Godot.Key.W),
            "the stuck-key release lets go of the action, the keycode and the physical key");

        // windowed on Windows: a key Godot thinks is down while Windows says it is up (a key-up lost
        // in a hitch) is let go within a few checks
        if (OS.GetName() == "Windows" && DisplayServer.GetName() != "headless")
        {
            if (!DisplayServer.WindowIsFocused()) Log("skip the stuck key: the window has no focus");
            else
            {
                Key(Godot.Key.W, true, false);
                await Wait(0.05);
                bool stuck = Input.IsActionPressed(PlayerInput.MoveForward);
                double at = RealClock.Now;
                while (Input.IsActionPressed(PlayerInput.MoveForward) && RealClock.Now - at < 2.0) await Wait(0.05);
                Expect(stuck && !Input.IsActionPressed(PlayerInput.MoveForward) && !Input.IsKeyPressed(Godot.Key.W),
                    $"a key up for Windows but down for Godot is let go ({RealClock.Now - at:F2} s)");
            }
        }

        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { _failed++; GD.PrintErr($"[spectatorcheck] FAIL {error}"); }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
