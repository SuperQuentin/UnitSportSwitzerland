using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--forkliftcheck [shots] --world flat</c> (#583): the forklift driven and its mast worked
/// through the <b>real paddle bindings</b> — the check holds <c>shift_up</c> / <c>shift_down</c>
/// down with <see cref="XrPad.Press"/> rather than writing <c>TargetLift</c>, so what it proves is
/// the control a player (or a VR hand on the cab lever) actually has.
///
/// <list type="bullet">
/// <item>mounted, the forks start on the ground;</item>
/// <item>held up, they run at the mast's rate and <b>stop where the paddle is let go</b>, which is
/// the whole of the control's design; held on, they reach the top stop and go no further;</item>
/// <item>the inner stage has extended by the free lift's worth, and the drawn carriage is at the
/// height the ride says — the mast is moved, never rebuilt, so this is the one thing that could
/// silently drift;</item>
/// <item>held down, they come back to the bottom stop;</item>
/// <item>the parked flags round-trip the height and what is on the forks;</item>
/// <item>driven on full lock it turns inside <see cref="TurnRadius"/>, and the right way round: a
/// lock big enough to turn in its own length is the whole of what makes it a forklift, and a
/// car-sized one still drives perfectly well — it just drives like a car.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/forklift/*.png</c> from a camera of its own.
/// </summary>
public partial class ForkliftCheck : Node
{
    public static bool Requested => CmdArgs.Has("--forkliftcheck");
    private static bool Shots => CmdArgs.Value("--forkliftcheck") == "shots";

    /// <summary>A counterbalance truck turns inside this, measured on the body's own path, m.</summary>
    private const float TurnRadius = 3.2f;

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public ForkliftCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[forklift] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>Holds a paddle for <paramref name="seconds"/> of game time, then lets it go.</summary>
    private async Task Paddle(string action, double seconds)
    {
        XrPad.Press(action, true);
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        XrPad.Press(action, false);
        await Frames(2);
    }

    private async Task Shot(string name, Vector3 eye, Vector3 at)
    {
        if (!Shots || DisplayServer.GetName() == "headless") return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 55f, Near = 0.05f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/forklift");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[forklift] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_started) return;
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        if (!TestWorld.TryGround(null, at, out float ground)) return;
        _started = true;
        _player = new FootPlayer { Name = "Probe" };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + 1.0f, at.Z);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = Run();
    }

    private async Task Run()
    {
        var me = _player!;
        // a mount is refused while airborne: let it settle on the flat world's box first
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.SetRide(RideKind.Forklift) && me.Vehicle is Forklift, "mounted the forklift");
        if (me.Vehicle is not Forklift fork) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Frames(5);
        Expect(Mathf.Abs(fork.Lift - ForkliftLayout.MinLift) < 0.02f, $"forks start on the ground ({fork.Lift:F2} m)");
        await Shot("forks-down", me.GlobalPosition + new Vector3(4.5f, 2.2f, 4.5f), me.GlobalPosition + Vector3.Up * 0.9f);

        // ---- held up for a second, let go: it runs at the mast's rate and STOPS there ----------
        float before = fork.Lift;
        await Paddle(PlayerInput.ShiftUp, 1.0);
        float after = fork.Lift;
        float rose = after - before;
        Expect(Mathf.Abs(rose - ForkliftLayout.LiftRate) < ForkliftLayout.LiftRate * 0.35f,
            $"a second on the paddle raises the forks by the mast's rate ({rose:F2} m, rate {ForkliftLayout.LiftRate:F2} m/s)");
        await Frames(30);
        Expect(Mathf.Abs(fork.Lift - after) < 0.01f, $"let go, the forks stay where they are ({fork.Lift:F2} m)");

        // ---- held on to the top stop ----------------------------------------------------------
        await Paddle(PlayerInput.ShiftUp, (ForkliftLayout.MaxLift - fork.Lift) / ForkliftLayout.LiftRate + 1.0);
        Expect(Mathf.Abs(fork.Lift - ForkliftLayout.MaxLift) < 0.02f, $"the forks reach the top stop ({fork.Lift:F2} m)");
        Expect(Mathf.Abs(ForkliftLayout.Stage(fork.Lift) - (ForkliftLayout.MaxLift - ForkliftLayout.FreeLift)) < 0.01f,
            $"the inner stage has extended past the free lift ({ForkliftLayout.Stage(fork.Lift):F2} m)");
        var mast = ForkliftMeshBuilder.MastOf(me.Visual);
        Expect(mast != null && Mathf.Abs(mast.Lift - fork.Lift) < 0.01f,
            $"the drawn carriage is where the ride says ({mast?.Lift ?? float.NaN:F2} m)");
        // the fork box rides with them, which is what phase 2 asks a pallet about
        Expect(Mathf.Abs(ForkliftLayout.ForkBox(fork.Lift).Position.Y + 0.05f - fork.Lift) < 0.001f,
            "the fork box is at the forks");
        await Shot("forks-up", me.GlobalPosition + new Vector3(4.5f, 2.6f, 4.5f), me.GlobalPosition + Vector3.Up * 1.8f);

        // ---- the flags a parked one keeps ------------------------------------------------------
        fork.Carrying = 5;
        var packed = new Forklift();
        packed.UnpackFlags(fork.PackFlags());
        Expect(Mathf.Abs(packed.Lift - fork.Lift) < 0.011f && packed.Carrying == 5,
            $"parked flags round-trip the height and the load ({packed.Lift:F2} m, carrying {packed.Carrying})");
        fork.Carrying = 0;

        // ---- held down to the bottom stop ------------------------------------------------------
        await Paddle(PlayerInput.ShiftDown, ForkliftLayout.MaxLift / ForkliftLayout.LiftRate + 1.0);
        Expect(Mathf.Abs(fork.Lift - ForkliftLayout.MinLift) < 0.02f, $"the forks come back to the ground ({fork.Lift:F2} m)");

        // ---- full lock: it turns inside its own length -------------------------------------------
        me.RideControls = () => new RideInput(1f, 0f, 1f, false);
        await Frames(20);
        var start = me.GlobalPosition;
        float startYaw = me.Rotation.Y;
        float far = 0f;
        // a quarter turn's worth, measuring how far the body ever gets from where it set off
        while (Mathf.Abs(Mathf.Wrap(me.Rotation.Y - startYaw, -Mathf.Pi, Mathf.Pi)) < Mathf.Pi * 0.5f)
        {
            far = Mathf.Max(far, (me.GlobalPosition - start with { Y = 0 }).Length());
            if (far > TurnRadius * 4f) break;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        far = Mathf.Max(far, (me.GlobalPosition - start with { Y = 0 }).Length());
        Expect(far < TurnRadius, $"a quarter turn on full lock stays inside {TurnRadius:F1} m ({far:F2} m)");
        await Shot("turning", start + new Vector3(6f, 4f, 6f), start);

        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[forklift] RESULT: ok — drives, the mast runs on the paddles and stops where it is let go"
            : $"[forklift] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
