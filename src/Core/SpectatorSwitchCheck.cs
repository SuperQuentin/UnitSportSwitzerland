using System.Threading.Tasks;
using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--spectatorcheck --world fixture</c> (#1002): Shift+T as real key events. On foot, Shift held
/// then T: the spectator camera takes over and does not sink on the Shift that chose it; Shift
/// pressed again sinks it; Shift+T again is back in the body; T alone switches creative mode.
/// Windowed on Windows, also: a W Godot believes down while Windows says up is let go (stuck keys).
/// Shift let go with an auto-repeat behind its key-up, as Windows delivers it (#1805): nothing stays
/// held, left and right, with W, after Shift+T, across a 300 ms hitch, with Q still holding Down.
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

        // the user's case (#1805): let go while a repeat is still queued; Windows hands the key-up over first
        Input.ParseInputEvent(ShiftKey(false, KeyLocation.Left));
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left, echo: true));
        await Wait(0.1);
        y = cam.GlobalPosition.Y;
        await Wait(0.5);
        Expect(Mathf.Abs(cam.GlobalPosition.Y - y) < 0.5f, $"Shift let go with a repeat behind its key-up stops sinking ({y:F1} -> {cam.GlobalPosition.Y:F1} m)");
        Key(Godot.Key.Shift, true, true);   // held again for the Shift+T below
        await Wait(0.05);

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

        await LateRepeats();

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

    /// <summary>Shift as Windows sends it: no Shift flag on Shift itself, its side, and an auto-repeat as an echo.</summary>
    private static InputEventKey ShiftKey(bool down, KeyLocation side, bool echo = false) => new()
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.Shift, Keycode = Godot.Key.Shift,
        KeyLabel = Godot.Key.Shift, Location = side, Pressed = down, Echo = echo,
    };

    private static bool ShiftActionHeld => Input.IsActionPressed(PlayerInput.Sprint) || Input.IsActionPressed(PlayerInput.FlyDown);

    /// <summary>
    /// Godot on Windows reads Shift's key-up from raw input, drained before the message queue, so a
    /// repeat queued before the release is parsed after it (#1805): Godot then holds Shift up while
    /// its actions stay pressed. Each sequence ends with nothing held on Shift.
    /// </summary>
    private async Task LateRepeats()
    {
        async Task Released(string what)
        {
            await Wait(0.1);
            Expect(!ShiftActionHeld && !Input.IsPhysicalKeyPressed(Godot.Key.Shift), $"{what}: sprint and Down let go");
        }

        // a plain press and release, either side
        foreach (var side in new[] { KeyLocation.Left, KeyLocation.Right })
        {
            Input.ParseInputEvent(ShiftKey(true, side));
            await Wait(0.05);
            bool held = Input.IsActionPressed(PlayerInput.Sprint);
            Input.ParseInputEvent(ShiftKey(false, side));
            await Released($"{side} Shift down and up (held {held})");

            Input.ParseInputEvent(ShiftKey(true, side));
            await Wait(0.05);
            Input.ParseInputEvent(ShiftKey(true, side, echo: true));
            await Wait(0.05);
            Expect(Input.IsActionPressed(PlayerInput.Sprint), $"{side} Shift held, repeating: still sprints");
            Input.ParseInputEvent(ShiftKey(false, side));
            Input.ParseInputEvent(ShiftKey(true, side, echo: true));
            await Released($"{side} Shift up with a repeat behind it");
        }

        // W held through it
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left));
        await Wait(0.05);
        Key(Godot.Key.W, true, true);
        await Wait(0.05);
        Input.ParseInputEvent(ShiftKey(false, KeyLocation.Left));
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left, echo: true));
        await Wait(0.1);
        Expect(!ShiftActionHeld && Input.IsActionPressed(PlayerInput.MoveForward), "Shift up with a repeat behind it while W is held: W still walks, sprint let go");
        Key(Godot.Key.W, false, false);
        await Released("then W up");

        // a hitch: the frame stalls 300 ms with Shift let go in it; the key-up comes first, the queued repeats after
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left));
        await Wait(0.05);
        OS.DelayMsec(300);
        Input.ParseInputEvent(ShiftKey(false, KeyLocation.Left));
        for (int i = 0; i < 3; i++) Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left, echo: true));
        await Released("Shift let go in a 300 ms hitch");

        // Q holds Down too: only Shift's part is let go
        Key(Godot.Key.Q, true, false);
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left));
        await Wait(0.05);
        Input.ParseInputEvent(ShiftKey(false, KeyLocation.Left));
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left, echo: true));
        await Wait(0.1);
        Expect(Input.IsActionPressed(PlayerInput.FlyDown) && !Input.IsActionPressed(PlayerInput.Sprint), "Q held through a late Shift repeat: Down still held by Q, sprint let go");
        Key(Godot.Key.Q, false, false);
        await Released("then Q up");

        // Alt tapped (the free cursor) while Shift is held, Shift let go with the cursor free
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left));
        await Wait(0.05);
        Key(Godot.Key.Alt, true, true);
        await Wait(0.05);
        Key(Godot.Key.Alt, false, true);
        await Wait(0.05);
        Input.ParseInputEvent(ShiftKey(false, KeyLocation.Left));
        Input.ParseInputEvent(ShiftKey(true, KeyLocation.Left, echo: true));
        await Released("Shift up with a repeat behind it after an Alt tap");
        if (CursorToggle.Free) { Key(Godot.Key.Alt, true, false); await Wait(0.05); Key(Godot.Key.Alt, false, false); }
    }

    private void Finish(string? error)
    {
        if (error != null) { _failed++; GD.PrintErr($"[spectatorcheck] FAIL {error}"); }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
