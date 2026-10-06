using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--loadercheck [shots] --world flat</c> (#612): the wheel loader driven, steered by its
/// articulation, and its arm and bucket worked through the <b>real bindings</b>
/// (<see cref="XrPad.Press"/>), as the excavator's check does.
///
/// <list type="bullet">
/// <item>driven, it accelerates like a 17 t machine;</item>
/// <item>on full lock it turns on the circle its articulated kinematics say
/// (<see cref="WheelLoaderLayout.TurnRadius"/>), the right way round, and the drawn front frame is
/// swung by the articulation;</item>
/// <item>in work mode it <b>still drives</b> (a loader works on the move), and the arm and the bucket
/// run at their rates while held, stop where let go, and stop at their limits;</item>
/// <item>the drawn front, arm and bucket are at the ride's angles, and the parked flags round-trip them.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/loader/*.png</c>.
/// </summary>
public partial class LoaderCheck : Node
{
    public static bool Requested => CmdArgs.Has("--loadercheck");
    private static bool Shots => CmdArgs.Value("--loadercheck") == "shots";

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public LoaderCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[loader] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Hold(string action, double seconds)
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/loader");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[loader] wrote {path}");
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
        Expect(me.SetRide(RideKind.WheelLoader) && me.Vehicle is WheelLoader, "mounted the wheel loader");
        if (me.Vehicle is not WheelLoader loader) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(10);
        await Shot("parked", me.GlobalPosition + new Vector3(8f, 4f, 8f), me.GlobalPosition + Vector3.Up * 1.5f);

        // ---- driven: a heavy machine's pull -------------------------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Frames(240);
        float went = Flat(me.GlobalPosition, start);
        Expect(went > 6f && went < 20f, $"four seconds on the throttle covers {went:F1} m");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Frames(240);

        // ---- full lock: the circle its kinematics say, the right way round -------------------
        me.RideControls = () => new RideInput(0f, 0f, 1f, false);
        await Frames(90);
        Expect(Mathf.Abs(loader.Articulation + WheelLoaderLayout.MaxArticulation) < 0.01f,
            $"steered right, the front frame swings to its stop ({Mathf.RadToDeg(loader.Articulation):F0}°)");
        var front = WheelLoaderMeshBuilder.FrontOf(me.Visual);
        Expect(front == null && DisplayServer.GetName() == "headless" || front != null && Mathf.Abs(front.Drawn.X - loader.Articulation) < 0.01f,
            $"and it is drawn swung ({front?.Drawn.X:F2})");
        await Shot("full-lock", me.GlobalPosition + new Vector3(0.5f, 14f, 0.5f), me.GlobalPosition);
        start = me.GlobalPosition;
        float yaw0 = me.Rotation.Y;
        me.RideControls = () => new RideInput(me.GroundSpeed < 2f ? 0.4f : 0f, me.GroundSpeed > 2.5f ? 0.3f : 0f, 1f, false);
        while (Mathf.Abs(Mathf.AngleDifference(yaw0, me.Rotation.Y)) < Mathf.Pi * 0.5f && Flat(me.GlobalPosition, start) < 30f)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        float turned = Mathf.AngleDifference(yaw0, me.Rotation.Y);
        // a quarter circle's chord is the radius times the square root of two
        float radius = Flat(me.GlobalPosition, start) / Mathf.Sqrt2;
        float want = WheelLoaderLayout.TurnRadius(WheelLoaderLayout.MaxArticulation);
        Expect(turned < 0f && Mathf.Abs(radius - want) < want * 0.3f,
            $"a quarter turn on full right lock turns right on a {radius:F1} m circle ({want:F1} m from the kinematics)");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Frames(180);

        // ---- work mode: it still drives, and the arm and the bucket run ---------------------
        XrPad.Press(PlayerInput.DigMode, true);
        await Frames(2);
        XrPad.Press(PlayerInput.DigMode, false);
        await Frames(2);
        Expect(loader.Working, "C / pad B puts it in work mode");
        start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Frames(150);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        Expect(Flat(me.GlobalPosition, start) > 2f, $"in work mode it still drives ({Flat(me.GlobalPosition, start):F1} m in 2.5 s)");
        await Frames(120);
        float lift0 = loader.Lift;
        await Hold(PlayerInput.ArmBoomUp, 1.0);
        float rose = loader.Lift - lift0;
        Expect(Mathf.Abs(rose - WheelLoaderLayout.LiftRate) < WheelLoaderLayout.LiftRate * 0.35f,
            $"a second of lift raises the arm by its rate ({rose:F2} rad)");
        await Frames(30);
        Expect(Mathf.Abs(loader.Lift - lift0 - rose) < 0.01f, "let go, the arm stays where it is");
        await Hold(PlayerInput.ArmBoomUp, (WheelLoaderLayout.LiftMax - loader.Lift) / WheelLoaderLayout.LiftRate + 1.0);
        await Hold(PlayerInput.ArmBucketDump, (loader.Tilt - WheelLoaderLayout.TiltMin) / WheelLoaderLayout.TiltRate + 1.0);
        Expect(Mathf.IsEqualApprox(loader.Lift, WheelLoaderLayout.LiftMax) && Mathf.IsEqualApprox(loader.Tilt, WheelLoaderLayout.TiltMin),
            $"arm up and bucket dumped reach their stops ({loader.Lift:F2}, {loader.Tilt:F2}; the pin {WheelLoaderLayout.PinHeight(loader.Lift):F1} m up)");
        Expect(WheelLoaderLayout.PinHeight(loader.Lift) > 3.8f, "high enough to tip into a lorry");
        await Shot("dumping", me.GlobalPosition + new Vector3(9f, 4f, 9f), me.GlobalPosition + Vector3.Up * 2.5f);
        await Hold(PlayerInput.ArmBoomDown, (loader.Lift - WheelLoaderLayout.LiftMin) / WheelLoaderLayout.LiftRate + 1.0);
        await Hold(PlayerInput.ArmBucketCurl, (WheelLoaderLayout.TiltMax - loader.Tilt) / WheelLoaderLayout.TiltRate + 1.0);
        Expect(Mathf.IsEqualApprox(loader.Lift, WheelLoaderLayout.LiftMin) && Mathf.IsEqualApprox(loader.Tilt, WheelLoaderLayout.TiltMax),
            $"and down and rolled back to the other stops ({loader.Lift:F2}, {loader.Tilt:F2})");

        await Frames(3);
        front = WheelLoaderMeshBuilder.FrontOf(me.Visual);
        Expect(front == null && DisplayServer.GetName() == "headless" || front != null
            && (front.Drawn - new Vector3(loader.Articulation, loader.Lift, loader.Tilt)).Length() < 0.01f,
            $"the drawn front, arm and bucket are at the ride's angles ({front?.Drawn})");
        var packed = new WheelLoader();
        packed.UnpackFlags(loader.PackFlags());
        float err = Mathf.Max(Mathf.Abs(packed.Lift - loader.Lift), Mathf.Max(Mathf.Abs(packed.Tilt - loader.Tilt), Mathf.Abs(packed.Articulation - loader.Articulation)));
        Expect(err < 0.012f, $"parked flags round-trip the arm, the bucket and the bend (worst {err:F3} rad)");
        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[loader] RESULT: ok — drives, bends to steer, lifts and tilts on the real bindings"
            : $"[loader] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
