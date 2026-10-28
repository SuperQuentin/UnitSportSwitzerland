using System.Threading.Tasks;
using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--at E,N --enterspectatorcheck [venue]</c> (#1244): leaving the fly camera puts the body on the first surface under
/// the camera with room to stand. Outdoors, from 25 m up: on the ground. In a building (the fly camera taken in by
/// <c>/enter</c>, default BowlinGland): on its floor, inside it; it used to be dropped on the street over the building.
/// Needs a real map near the venue.
/// </summary>
public partial class EnterSpectatorCheck : Node
{
    public static bool Requested => CmdArgs.Has("--enterspectatorcheck");

    private readonly System.Func<FootPlayer?> _local;
    private readonly System.Func<Camera3D?> _spectator;
    private int _failed;

    public EnterSpectatorCheck(System.Func<FootPlayer?> local, System.Func<Camera3D?> spectator)
    {
        Name = "EnterSpectatorCheck";
        _local = local;
        _spectator = spectator;
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[enterspectatorcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (!ok) _failed++;
        Log($"{(ok ? "ok  " : "FAIL")} {what}");
    }

    private async Task Wait(double seconds)
    {
        double until = RealClock.Now + seconds;
        while (RealClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>
    /// Shift down, then T, as a hand presses them (frames apart, as <see cref="SpectatorSwitchCheck"/> does): into the fly
    /// camera. Back is a plain T (<see cref="T"/>): a synthetic Shift reads as "down" a moment and sinks the camera.
    /// </summary>
    private async Task ShiftT()
    {
        Input.ParseInputEvent(Key(Godot.Key.Shift, true, true));
        await Wait(0.1);
        Input.ParseInputEvent(Key(Godot.Key.T, true, true));
        await Wait(0.05);
        Input.ParseInputEvent(Key(Godot.Key.T, false, true));
        Input.ParseInputEvent(Key(Godot.Key.Shift, false, false));
    }

    private async Task T()
    {
        Input.ParseInputEvent(Key(Godot.Key.T, true, false));
        await Wait(0.05);
        Input.ParseInputEvent(Key(Godot.Key.T, false, false));
    }

    private static InputEventKey Key(Key key, bool down, bool shift) => new()
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key,
        Pressed = down, ShiftPressed = shift,
    };

    private async Task Run()
    {
        string target = CmdArgs.Value("--enterspectatorcheck", notFlag: true) ?? "bowlingland";
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.Camera.Current && me.IsOnFloor()) break;
            // a probe run starts on the fly camera: T goes into the body
            if (i % 50 == 25)
            {
                Input.ParseInputEvent(Key(Godot.Key.T, true, false));
                await Wait(0.05);
                Input.ParseInputEvent(Key(Godot.Key.T, false, false));
            }
            await Wait(0.1);
        }
        if (me == null || _spectator() is not { } cam || Interiors.InteriorManager.Instance is not { } interiors)
        {
            Finish("no body on the ground");
            return;
        }
        await Wait(1.0);
        var ground = me.GlobalPosition;

        // outside: the camera 25 m up, back into the body: down on the ground under it
        await ShiftT();
        await Wait(0.3);
        Expect(cam.Current, "Shift+T: the fly camera");
        await Wait(1.0);
        cam.GlobalPosition = me.GlobalPosition + Vector3.Up * 25f;
        await T();
        await Wait(1.0);
        Expect(me.Camera.Current && !me.Indoors, "T: back in the body, outdoors");
        // the first surface with room under the camera: the ground, or a canopy or roof over the spot
        Expect(me.IsOnFloor() && me.GlobalPosition.Y >= ground.Y - 0.5f && me.GlobalPosition.Y < ground.Y + 20f,
            $"standing on the first thing under the camera ({ground.Y:F1} m ground -> {me.GlobalPosition.Y:F1} m)");

        // the fly camera into a building (/enter while flying), moved up and along, back into the body: on its floor
        await ShiftT();
        await Wait(0.3);
        interiors.Enter(target, null);
        for (int i = 0; i < 600 && (interiors.Entering || interiors.Entered == null); i++) await Wait(0.1);
        if (interiors.Entered is not { } plan) { Finish($"/enter {target} did not take the camera in"); return; }
        await Wait(0.5);
        var flat = -cam.GlobalBasis.Z with { Y = 0 };
        var to = cam.GlobalPosition + Vector3.Up * 0.5f + (flat.LengthSquared() > 1e-4f ? flat.Normalized() : Vector3.Forward) * 2f;
        cam.GlobalPosition = to;
        await T();
        await Wait(1.0);
        Expect(me.Camera.Current, "T: back in the body");
        Expect(me.Indoors && me.InteriorKey == plan, $"inside {plan} ({me.InteriorKey ?? "outside"})");
        var at = me.GlobalPosition;
        Expect(Interiors.InteriorManager.InInteriorSpace(at), $"in the interiors' space, not up on the street ({at.Y:F1} m)");
        Expect(Mathf.Abs(at.Y - (to.Y - 1.6f - 0.5f)) < 0.7f, $"on the floor under the camera ({to.Y:F2} -> {at.Y:F2} m)");
        Expect(new Vector2(at.X - to.X, at.Z - to.Z).Length() < 0.5f, "right under where the camera was");
        Expect(me.IsOnFloor(), "standing");

        // flown through the floor into the void under it: onto the building's lowest floor. The fly camera is refused
        // indoors, so the body steps back out first (as the door would put it) and the camera goes in by /enter again.
        interiors.SetBodySpace(me, ground);
        await Wait(0.5);
        await ShiftT();
        await Wait(0.3);
        interiors.Enter(target, null);
        for (int i = 0; i < 600 && interiors.Entering; i++) await Wait(0.1);
        await Wait(0.5);
        var under = cam.GlobalPosition + Vector3.Down * 15f;
        cam.GlobalPosition = under;
        await T();
        await Wait(1.0);
        Expect(me.Indoors && Interiors.InteriorManager.InInteriorSpace(me.GlobalPosition) && me.GlobalPosition.Y > under.Y + 5f,
            $"from under the floor: onto the building's floor, not the street ({under.Y:F1} -> {me.GlobalPosition.Y:F1} m)");
        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { _failed++; GD.PrintErr($"[enterspectatorcheck] FAIL {error}"); }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
