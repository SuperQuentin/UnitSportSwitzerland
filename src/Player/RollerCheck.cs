using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--rollercheck [shots] --world flat</c> (#614): the compact tandem roller driven on the flat,
/// its vibration worked through the <b>real binding</b> (<see cref="XrPad.Press"/>, as every machine
/// check does):
/// <list type="bullet">
/// <item>the throttle takes it to a walking pace, and let go its hydrostatic drive stops it;</item>
/// <item>on full lock its front frame swings to its stop, is drawn swung, and it turns on the circle
/// its kinematics give;</item>
/// <item>C / pad B sets the drums vibrating: the vibration comes up, the driver's view trembles by
/// as much, the drums hum (where anything is drawn); it drives the same; again, and it all dies away;</item>
/// <item>the parked flags keep the bend.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/roller/*.png</c>.
/// </summary>
public partial class RollerCheck : Node
{
    public static bool Requested => CmdArgs.Has("--rollercheck");
    private static bool Shots => CmdArgs.Value("--rollercheck") == "shots";

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public RollerCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[roller] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Seconds(double s)
    {
        double end = GameClock.Now + s;
        while (GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Tap(string action)
    {
        XrPad.Press(action, true);
        await Frames(2);
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/roller");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[roller] wrote {path}");
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

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.SetRide(RideKind.CompactRoller) && me.Vehicle is CompactRoller, "mounted the compact roller");
        if (me.Vehicle is not CompactRoller roller) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(10);
        await Shot("parked", me.GlobalPosition + new Vector3(4.5f, 2.5f, 4.5f), me.GlobalPosition + Vector3.Up * 0.8f);

        // ---- driven: a walking pace, and stopped by letting go ---------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(4);
        float went = Flat(me.GlobalPosition, start);
        float top = me.GroundSpeed;
        Expect(went > 4f && went < 10f && top < CompactRollerLayout.TopSpeed + 0.3f,
            $"four seconds on the throttle covers {went:F1} m, at {top * 3.6f:F1} km/h");
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        var letGo = me.GlobalPosition;
        await Seconds(2.5);
        Expect(me.GroundSpeed < 0.1f && Flat(me.GlobalPosition, letGo) < 2.5f,
            $"let go, the hydrostatic drive stops it in {Flat(me.GlobalPosition, letGo):F1} m");

        // ---- full lock: the circle its kinematics say ---------------------------------------------
        me.RideControls = () => new RideInput(0f, 0f, -1f, false);
        await Seconds(1.2);
        Expect(Mathf.Abs(roller.Articulation - CompactRollerLayout.MaxArticulation) < 0.01f,
            $"steered left, the front frame swings to its stop ({Mathf.RadToDeg(roller.Articulation):F0}°)");
        var front = CompactRollerMeshBuilder.FrontOf(me.Visual);
        Expect(front == null && DisplayServer.GetName() == "headless" || front != null && Mathf.Abs(front.Drawn - roller.Articulation) < 0.01f,
            $"and it is drawn swung ({front?.Drawn:F2})");
        start = me.GlobalPosition;
        float yaw0 = me.Rotation.Y;
        me.RideControls = () => new RideInput(me.GroundSpeed < 1.2f ? 0.5f : 0f, 0f, -1f, false);
        double t0 = GameClock.Now;
        while (Mathf.Abs(Mathf.AngleDifference(yaw0, me.Rotation.Y)) < Mathf.Pi * 0.5f && GameClock.Now - t0 < 20)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        float turned = Mathf.AngleDifference(yaw0, me.Rotation.Y);
        float radius = Flat(me.GlobalPosition, start) / Mathf.Sqrt2;
        float want = CompactRollerLayout.TurnRadius(CompactRollerLayout.MaxArticulation);
        Expect(turned > 0f && Mathf.Abs(radius - want) < want * 0.3f,
            $"a quarter turn on full left lock turns left on a {radius:F1} m circle ({want:F1} m from the kinematics)");
        await Shot("full-lock", me.GlobalPosition + new Vector3(0.3f, 8f, 0.3f), me.GlobalPosition);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(2);

        // ---- the drums vibrate on the work toggle --------------------------------------------------
        await Tap(PlayerInput.DigMode);
        Expect(roller.Vibrating, "C / pad B sets the drums vibrating");
        await Seconds(CompactRollerLayout.VibrationRamp + 0.3);
        float setting = GameSettings.Current.ScreenShake;
        Expect(Mathf.IsEqualApprox(roller.Vibration, 1f), $"the vibration comes up in {CompactRollerLayout.VibrationRamp} s ({roller.Vibration:F2})");
        Expect(Mathf.Abs(me.RideShake - CompactRollerLayout.ShakeAmplitude * setting) < 1e-5f,
            $"the driver's view trembles by {me.RideShake:F4} rad (the setting at {setting:F2})");
        var drums = CompactRollerMeshBuilder.DrumsOf(me.Visual);
        Expect(drums == null && DisplayServer.GetName() == "headless" || drums is { Playing: true },
            $"and the drums hum ({(drums == null ? "nothing drawn" : drums.Playing ? $"{drums.VolumeDb:F1} dB" : "silent")})");
        start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(4);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        float vibrated = Flat(me.GlobalPosition, start);
        Expect(Mathf.Abs(vibrated - went) < 1.0f, $"vibrating, it drives the same ({vibrated:F1} m in four seconds)");
        await Shot("vibrating", me.GlobalPosition + new Vector3(-4f, 2.2f, 3.5f), me.GlobalPosition + Vector3.Up * 0.8f);
        await Seconds(2);
        await Tap(PlayerInput.DigMode);
        await Seconds(CompactRollerLayout.VibrationRamp + 0.3);
        Expect(!roller.Vibrating && roller.Vibration == 0f && me.RideShake == 0f && drums is not { Playing: true },
            $"again, and the vibration, the tremble and the hum die away ({roller.Vibration:F2}, {me.RideShake:F4})");

        // ---- the parked flags ----------------------------------------------------------------------
        me.RideControls = () => new RideInput(0f, 0f, 0.6f, false);
        await Seconds(1);
        var packed = new CompactRoller();
        packed.UnpackFlags(roller.PackFlags());
        Expect(Mathf.Abs(packed.Articulation - roller.Articulation) < 0.005f && !packed.Vibrating,
            $"parked flags keep the bend ({packed.Articulation:F3} for {roller.Articulation:F3}), and a parked one is still");

        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[roller] RESULT: ok — drives, steers on its hinge, vibrates on the real binding"
            : $"[roller] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
