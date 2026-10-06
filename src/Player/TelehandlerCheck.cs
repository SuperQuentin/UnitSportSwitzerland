using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--telehandlercheck [shots] --world flat</c> (#614): the telehandler driven on the flat, its
/// steering modes and its boom worked through the <b>real bindings</b> (<see cref="XrPad.Press"/>):
/// <list type="bullet">
/// <item>the throttle takes it to a road pace;</item>
/// <item>on full lock in front steering it turns on the circle its kinematics give; O / D-pad left
/// to four-wheel steering, on half that circle; again to crab, and on full lock it travels at the
/// wheels' angle without turning; again, back to front steering. The drawn rear wheels follow;</item>
/// <item>in work mode it still drives, and the lift, the extension and the tilt each run at their
/// rates to both stops, drawn at the ride's angles, the carriage as high as the geometry says;</item>
/// <item>the parked flags keep the boom and the mode.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/telehandler/*.png</c>.
/// </summary>
public partial class TelehandlerCheck : Node
{
    public static bool Requested => CmdArgs.Has("--telehandlercheck");
    private static bool Shots => CmdArgs.Value("--telehandlercheck") == "shots";

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public TelehandlerCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[telehandler] {(ok ? "ok  " : "FAIL")} {what}");
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

    private async Task Hold(string action, double seconds)
    {
        XrPad.Press(action, true);
        await Seconds(seconds);
        XrPad.Press(action, false);
        await Frames(2);
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/telehandler");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[telehandler] wrote {path}");
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

    /// <summary>A quarter turn on full left lock at a crawl: the circle it drove (its chord over √2) and which way it turned.</summary>
    private async Task<(float Radius, float Turned)> QuarterTurn(FootPlayer me)
    {
        me.RideControls = () => new RideInput(0f, 0f, -1f, false);
        await Seconds(1.0);
        var start = me.GlobalPosition;
        float yaw0 = me.Rotation.Y;
        me.RideControls = () => new RideInput(me.GroundSpeed < 1.5f ? 0.5f : 0f, me.GroundSpeed > 2f ? 0.3f : 0f, -1f, false);
        double t0 = GameClock.Now;
        while (Mathf.Abs(Mathf.AngleDifference(yaw0, me.Rotation.Y)) < Mathf.Pi * 0.5f && GameClock.Now - t0 < 25)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        float turned = Mathf.AngleDifference(yaw0, me.Rotation.Y);
        float radius = Flat(me.GlobalPosition, start) / Mathf.Sqrt2;
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(1.5);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1.2);
        return (radius, turned);
    }

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.SetRide(RideKind.Telehandler) && me.Vehicle is Telehandler, "mounted the telehandler");
        if (me.Vehicle is not Telehandler th) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(10);
        await Shot("parked", me.GlobalPosition + new Vector3(7f, 3.5f, 7f), me.GlobalPosition + Vector3.Up * 1.2f);

        // ---- driven: a road pace ---------------------------------------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(4);
        float went = Flat(me.GlobalPosition, start);
        Expect(went > 6f && went < 20f, $"four seconds on the throttle covers {went:F1} m");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(3);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);

        // ---- the three steering modes -------------------------------------------------------------
        Expect(th.Mode == SteerMode.Front, "it starts in front steering");
        var (front, turnedFront) = await QuarterTurn(me);
        float wantFront = TelehandlerLayout.TurnRadius(SteerMode.Front);
        Expect(turnedFront > 0f && Mathf.Abs(front - wantFront) < wantFront * 0.3f,
            $"front steering, full left lock: a left turn on a {front:F1} m circle ({wantFront:F1} m from the kinematics)");

        await Tap(PlayerInput.RoofToggle);
        Expect(th.Mode == SteerMode.FourWheel, $"O / D-pad left: four-wheel steering ({th.Mode})");
        me.RideControls = () => new RideInput(0f, 0f, -1f, false);
        await Seconds(1.0);
        var boom = TelehandlerMeshBuilder.BoomOf(me.Visual);
        Expect(boom == null && DisplayServer.GetName() == "headless" || boom != null && Mathf.Abs(boom.Drawn.W + th.Steer) < 0.01f,
            $"the rear wheels are drawn the other way to the front ({boom?.Drawn.W:F2} for {th.Steer:F2})");
        await Shot("four-wheel", me.GlobalPosition + new Vector3(0.3f, 11f, 0.3f), me.GlobalPosition);
        var (four, turnedFour) = await QuarterTurn(me);
        float wantFour = TelehandlerLayout.TurnRadius(SteerMode.FourWheel);
        Expect(turnedFour > 0f && Mathf.Abs(four - wantFour) < wantFour * 0.3f && four < front * 0.7f,
            $"four-wheel steering turns on a {four:F1} m circle ({wantFour:F1} m from the kinematics, front {front:F1} m)");

        await Tap(PlayerInput.RoofToggle);
        Expect(th.Mode == SteerMode.Crab, $"again: crab steering ({th.Mode})");
        me.RideControls = () => new RideInput(0f, 0f, -1f, false);
        await Seconds(1.0);
        start = me.GlobalPosition;
        float yaw0 = me.Rotation.Y;
        var nose = -me.GlobalTransform.Basis.Z with { Y = 0 };
        me.RideControls = () => new RideInput(me.GroundSpeed < 2f ? 0.5f : 0f, 0f, -1f, false);
        await Seconds(4);
        var moved = (me.GlobalPosition - start) with { Y = 0 };
        float travel = moved.Length() > 0.5f ? nose.Normalized().SignedAngleTo(moved.Normalized(), Vector3.Up) : 0f;
        float turnedCrab = Mathf.AngleDifference(yaw0, me.Rotation.Y);
        Expect(moved.Length() > 3f && Mathf.Abs(turnedCrab) < 0.05f && Mathf.Abs(travel - TelehandlerLayout.MaxSteer) < 0.12f,
            $"crabbing on full left lock it moves {moved.Length():F1} m at {Mathf.RadToDeg(travel):F0}° to its nose, turning {Mathf.RadToDeg(turnedCrab):F1}°");
        await Shot("crab", me.GlobalPosition + new Vector3(0.3f, 11f, 0.3f), me.GlobalPosition);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(2);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Tap(PlayerInput.RoofToggle);
        Expect(th.Mode == SteerMode.Front, $"and again: front steering ({th.Mode})");
        await Seconds(1.0);

        // ---- work mode: it still drives, and the boom runs ----------------------------------------
        await Tap(PlayerInput.DigMode);
        Expect(th.Working, "C / pad B puts it in work mode");
        start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(2);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        Expect(Flat(me.GlobalPosition, start) > 1f, $"in work mode it still drives, from a standstill ({Flat(me.GlobalPosition, start):F1} m in 2 s)");
        await Seconds(2);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);

        float lift0 = th.Lift;
        await Hold(PlayerInput.ArmBoomUp, 1.0);
        Expect(Mathf.Abs(th.Lift - lift0 - TelehandlerLayout.LiftRate) < TelehandlerLayout.LiftRate * 0.35f,
            $"a second of lift raises the boom by its rate ({th.Lift - lift0:F2} rad)");
        await Hold(PlayerInput.ArmBoomUp, (TelehandlerLayout.LiftMax - th.Lift) / TelehandlerLayout.LiftRate + 0.5);
        await Hold(PlayerInput.ShiftUp, (TelehandlerLayout.ExtendMax - th.Extend) / TelehandlerLayout.ExtendRate + 0.5);
        await Hold(PlayerInput.ArmBucketCurl, (TelehandlerLayout.TiltMax - th.Tilt) / TelehandlerLayout.TiltRate + 0.5);
        var top = TelehandlerLayout.Carriage(th.Lift, th.Extend);
        Expect(Mathf.IsEqualApprox(th.Lift, TelehandlerLayout.LiftMax) && Mathf.IsEqualApprox(th.Extend, TelehandlerLayout.ExtendMax)
            && Mathf.IsEqualApprox(th.Tilt, TelehandlerLayout.TiltMax),
            $"lifted, run out and tilted back to their stops: the carriage {top.Y:F1} m up, {top.X:F1} m out");
        await Frames(3);
        boom = TelehandlerMeshBuilder.BoomOf(me.Visual);
        Expect(boom == null && DisplayServer.GetName() == "headless" || boom != null
            && (new Vector3(boom.Drawn.X, boom.Drawn.Y, boom.Drawn.Z) - new Vector3(th.Lift, th.Extend, th.Tilt)).Length() < 0.01f,
            $"the drawn boom is at the ride's ({boom?.Drawn})");
        await Shot("boom-up", me.GlobalPosition + new Vector3(18f, 7f, 4f), me.GlobalPosition + Vector3.Up * 5f);
        await Hold(PlayerInput.ShiftDown, 2.0);
        Expect(Mathf.Abs(TelehandlerLayout.ExtendMax - th.Extend - TelehandlerLayout.ExtendRate * 2f) < 0.3f,
            $"two seconds of retract bring it in by its rate ({TelehandlerLayout.ExtendMax - th.Extend:F2} m)");

        // ---- the parked flags ----------------------------------------------------------------------
        await Tap(PlayerInput.RoofToggle);
        var packed = new Telehandler();
        packed.UnpackFlags(th.PackFlags());
        Expect(Mathf.Abs(packed.Lift - th.Lift) < 0.01f && Mathf.Abs(packed.Extend - th.Extend) < 0.02f
            && Mathf.Abs(packed.Tilt - th.Tilt) < 0.02f && packed.Mode == th.Mode,
            $"parked flags keep the boom and the mode ({packed.Lift:F2} {packed.Extend:F2} {packed.Tilt:F2} {packed.Mode})");

        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[telehandler] RESULT: ok — drives, three steering modes, the boom on the real bindings"
            : $"[telehandler] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
