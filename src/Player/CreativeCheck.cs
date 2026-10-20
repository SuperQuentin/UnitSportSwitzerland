using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--creativecheck --world flat</c> (#1002): creative mode on a body read from the keys. Damage
/// hurts in survival and not in creative; a double jump takes off, Jump held rises, let go it
/// hovers, the view looks straight down, forward flies level, Shift sinks and lands (the flight ends,
/// the pitch held again); Shift+T reads as a chord; a 30 m fall costs nothing;
/// <see cref="GameSettings.CreativeNoclip"/> sinks through the ground. Prints <c>[creativecheck] RESULT: ok</c>.
/// </summary>
public partial class CreativeCheck : Node3D
{
    public static bool Requested => CmdArgs.Has("--creativecheck");

    private FootPlayer _me = null!;
    private int _failed;

    public override void _Ready()
    {
        _me = new FootPlayer { Name = "Player" };
        AddChild(_me);
        _me.DebugLaunch(new Vector3(0, 0.1f, 0), Vector3.Zero);
        _ = Run();
    }

    private static void Log(string what) => GD.Print($"[creativecheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[creativecheck] FAIL {what}");
        }
    }

    private async System.Threading.Tasks.Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async System.Threading.Tasks.Task Tap(string action)
    {
        Input.ActionPress(action);
        await Wait(0.05);
        Input.ActionRelease(action);
        await Wait(0.05);
    }

    private async System.Threading.Tasks.Task Run()
    {
        bool noclip = GameSettings.Current.CreativeNoclip;
        GameSettings.Current.CreativeNoclip = false;
        try
        {
            await Wait(1.0);
            Expect(_me.IsOnFloor(), "standing on the plane");

            float before = _me.Health;
            _me.TakeDamage(10f);
            Expect(_me.Health < before - 9f, $"survival: a hit hurts ({before:F0} -> {_me.Health:F0})");

            _me.Creative = true;
            before = _me.Health;
            _me.TakeDamage(50f);
            _me.ShotHit(50f, 7);
            Expect(_me.Health >= before, $"creative: nothing hurts ({before:F0} -> {_me.Health:F0})");

            // one jump is a jump, not a take-off
            await Tap(PlayerInput.Jump);
            Expect(!_me.CreativeFlying, "one jump does not fly");
            await Wait(1.5);

            // the double tap: the second press held, it rises
            await Tap(PlayerInput.Jump);
            Input.ActionPress(PlayerInput.Jump);
            await Wait(0.05);
            Expect(_me.CreativeFlying, "a double jump takes off");
            await Wait(1.5);
            Input.ActionRelease(PlayerInput.Jump);
            float up = _me.GlobalPosition.Y;
            Expect(up > 6f, $"Jump held rises ({up:F1} m)");

            await Wait(0.5);
            float hover = _me.GlobalPosition.Y;
            await Wait(1.0);
            Expect(Mathf.Abs(_me.GlobalPosition.Y - hover) < 0.3f, $"let go, it hovers ({hover:F1} -> {_me.GlobalPosition.Y:F1} m)");

            // flying, the camera itself looks straight down at what is under you and up at the sky,
            // in first and in third person (first person once stayed level: its eye is set in the walk step)
            foreach (bool third in new[] { false, true })
            {
                _me.DebugThirdPerson(third);
                string view = third ? "third person" : "first person";
                _me.LookPitch = -1.5f;
                await Wait(0.2);
                float lookDown = -_me.Camera.GlobalBasis.Z.Y;
                _me.LookPitch = 1.3f;
                await Wait(0.2);
                float lookUp = -_me.Camera.GlobalBasis.Z.Y;
                Expect(_me.LookPitch > 1.25f && lookDown < -0.95f && lookUp > 0.9f,
                    $"flying, {view}: the camera looks down and up (forward y {lookDown:F2} / {lookUp:F2})");
            }
            _me.LookPitch = 0f;

            var from = _me.GlobalPosition;
            Input.ActionPress(PlayerInput.MoveForward);
            await Wait(1.5);
            Input.ActionRelease(PlayerInput.MoveForward);
            var moved = _me.GlobalPosition - from;
            Expect(MathX.FlatLength(moved) > 10f && Mathf.Abs(moved.Y) < 0.5f,
                $"forward flies level ({MathX.FlatLength(moved):F1} m on, {moved.Y:+0.0;-0.0} m up)");

            Input.ActionPress(PlayerInput.Sprint);
            for (int i = 0; i < 300 && _me.CreativeFlying; i++) await Wait(0.05);
            Input.ActionRelease(PlayerInput.Sprint);
            Expect(!_me.CreativeFlying && _me.IsOnFloor(), $"Shift sinks and lands ({_me.GlobalPosition.Y:F1} m)");
            // third person keeps the lens off the ground (first person always looked straight down)
            _me.DebugThirdPerson(true);
            _me.LookPitch = -1.5f;
            Expect(_me.LookPitch > -1.3f, $"on the ground, third person, the pitch is held again ({_me.LookPitch:F2})");
            _me.LookPitch = 0f;
            string spectator = InputHints.Label(PlayerInput.Spectator, InputDevice.KeyboardMouse);
            Expect(spectator == "Shift+T", $"the spectator key reads as a chord ({spectator})");

            // a fall from 30 m, creative and not flying
            before = _me.Health;
            _me.DebugLaunch(_me.GlobalPosition with { Y = 30f }, Vector3.Zero);
            for (int i = 0; i < 200 && !_me.IsOnFloor(); i++) await Wait(0.05);
            await Wait(0.3);
            Expect(_me.IsOnFloor() && _me.Health >= before, $"a 30 m fall costs nothing ({before:F0} -> {_me.Health:F0})");

            // through the ground
            GameSettings.Current.CreativeNoclip = true;
            await Wait(0.2);
            await Tap(PlayerInput.Jump);
            await Tap(PlayerInput.Jump);
            Expect(_me.CreativeFlying, "takes off again");
            Input.ActionPress(PlayerInput.Sprint);
            await Wait(0.8);
            float low = _me.GlobalPosition.Y;
            Input.ActionRelease(PlayerInput.Sprint);
            Expect(low < -0.5f, $"through walls: it sinks under the ground ({low:F1} m)");

            // survival again: the flight ends with it
            _me.Creative = false;
            Expect(!_me.CreativeFlying, "survival ends the flight");
        }
        finally
        {
            foreach (var a in new[] { PlayerInput.Jump, PlayerInput.Sprint, PlayerInput.MoveForward }) Input.ActionRelease(a);
            GameSettings.Current.CreativeNoclip = noclip;
        }

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
