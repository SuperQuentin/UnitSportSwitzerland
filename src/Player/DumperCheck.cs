using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--dumpercheck[,shots] --world flat --systems physics,ui</c> (#614): the tracked mini dumper on
/// the <b>real binding</b> (<c>destination</c>, held through <see cref="XrPad.Press"/>):
/// <list type="bullet">
/// <item>it crawls at its walking pace, and stops when let go;</item>
/// <item>its tracks turn it on the spot;</item>
/// <item>stopped, the binding tips the skip forward over its time, its back rising; again brings it
/// down; moving, it refuses; the parked flags keep it up.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/dumper/*.png</c>.
/// </summary>
public partial class DumperCheck : Node
{
    public static bool Requested => CmdArgs.Has("--dumpercheck");
    private static bool Shots => (CmdArgs.Value("--dumpercheck") ?? "").Split(',').Contains("shots");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public DumperCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[dumper] {(ok ? "ok  " : "FAIL")} {what}");
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
        var cam = new Camera3D { Fov = 50f, Near = 0.05f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/dumper");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"dumper-{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[dumper] wrote {path}");
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

    /// <summary>The camera off the dumper's front quarter, so the skip and the seat behind it show.</summary>
    private static Vector3 Quarter(FootPlayer me, float side, float ahead, float up) =>
        me.GlobalPosition + me.GlobalTransform.Basis.X * side - me.GlobalTransform.Basis.Z * ahead + Vector3.Up * up;

    /// <summary>The top of the skip's back wall, where it hangs on the tipping node: it rises as the skip tips.</summary>
    private static Vector3 SkipBackTop(HeavyRig rig)
    {
        var tip = rig.FindChild("Tip", true, false) as Node3D;
        var local = new Vector3(0f, MiniDumperLayout.SkipTop - MiniDumperLayout.Hinge.Y, -(MiniDumperLayout.SkipBack - MiniDumperLayout.Hinge.Z));
        return tip?.ToGlobal(local) ?? Vector3.Zero;
    }

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.SetRide(RideKind.MiniDumper) && me.Vehicle is MiniDumper, "mounted the mini dumper");
        if (me.Vehicle is not MiniDumper dumper || me.Visual is not HeavyRig rig) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);
        await Shot("down", Quarter(me, 3.5f, 4f, 2f), me.GlobalPosition + Vector3.Up * 0.8f);

        // ---- it crawls, and stops when let go --------------------------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(4);
        float went = Flat(me.GlobalPosition, start);
        float top = me.GroundSpeed;
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(2);
        Expect(went > 3.5f && top < MiniDumperLayout.TopSpeed + 0.1f && me.GroundSpeed < 0.1f,
            $"four seconds on the tracks covers {went:F1} m at {top * 3.6f:F1} km/h, and letting go stops it");

        // ---- the tracks turn it on the spot ----------------------------------------------------
        var spot = me.GlobalPosition;
        float yaw0 = me.GlobalRotation.Y;
        me.RideControls = () => new RideInput(0f, 0f, 1f, false);
        await Seconds(2);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);
        float turned = Mathf.Abs(Mathf.AngleDifference(yaw0, me.GlobalRotation.Y));
        Expect(turned > 1f && Flat(me.GlobalPosition, spot) < 0.3f,
            $"steering standing turns it {Mathf.RadToDeg(turned):F0}° on the spot ({Flat(me.GlobalPosition, spot):F2} m off)");

        // ---- the skip tips forward, stopped ----------------------------------------------------
        float back0 = SkipBackTop(rig).Y;
        await Tap(PlayerInput.Destination);
        Expect(dumper.Tipped, "stopped, the destination binding tips the skip");
        await Seconds(3.5);
        float rose = SkipBackTop(rig).Y - back0;
        Expect(Mathf.IsEqualApprox(rig.TippedShown, 1f) && rose > 0.6f,
            $"it tips over its time, the back of the skip {rose:F2} m higher");
        await Shot("tipped", Quarter(me, 3.5f, 4f, 2f), me.GlobalPosition + Vector3.Up * 0.8f);
        var parked = new MiniDumper();
        parked.UnpackFlags(dumper.PackFlags());
        Expect(parked.Tipped, "the parked flags keep it up");
        var never = new MiniDumper();
        never.UnpackFlags(0);
        Expect(!never.Tipped, "flags never set: the skip down");
        await Tap(PlayerInput.Destination);
        await Seconds(3.5);
        Expect(!dumper.Tipped && rig.TippedShown == 0f, $"again, and it comes down ({rig.TippedShown:F2})");

        // moving, it refuses
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(2);
        await Tap(PlayerInput.Destination);
        Expect(!dumper.Tipped, $"at {me.GroundSpeed * 3.6f:F1} km/h it does not tip");
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(2);
        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[dumper] RESULT: ok — the mini dumper crawls, turns on the spot and tips its skip on the real binding"
            : $"[dumper] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
