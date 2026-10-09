using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--tippercheck tipper|mixer[,shots] --world flat --systems physics,ui</c> (#613): the site's
/// four-axle tipper and concrete mixer, their work on the <b>real binding</b> (<c>destination</c>,
/// held through <see cref="XrPad.Press"/>):
/// <list type="bullet">
/// <item>both drive, loaded;</item>
/// <item>tipper: stopped, the binding tips the body up over its time (and the tailgate hangs open),
/// again brings it down; moving, it refuses; tipped and driven off, the body drops; the parked
/// flags keep it up;</item>
/// <item>mixer: the drum turns while the engine runs and stops with it; the binding reverses it to
/// discharge, faster, and swings the chute out; again stops it and brings the chute back.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/tipper/*.png</c>.
/// </summary>
public partial class TipperCheck : Node
{
    public static bool Requested => CmdArgs.Has("--tippercheck");
    private static string[] Args => (CmdArgs.Value("--tippercheck") ?? "").Split(',');
    private static bool Shots => Args.Contains("shots");
    private static bool MixerRun => Args.Contains("mixer");
    private static RideKind Kind => (RideKind)(MixerRun ? 105 : 104);

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public TipperCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[tipper] {(ok ? "ok  " : "FAIL")} {what}");
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/tipper");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{(MixerRun ? "mixer" : "tipper")}-{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[tipper] wrote {path}");
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

    /// <summary>The camera beside the truck: a side view a little behind, so the body and the drum show.</summary>
    private static Vector3 Beside(FootPlayer me, float side, float back, float up) =>
        me.GlobalPosition + me.GlobalTransform.Basis.X * side + me.GlobalTransform.Basis.Z * back + Vector3.Up * up;

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.SetRide(Kind) && me.Heavy is { } t0 && t0.Spec.Body == (MixerRun ? TruckBody.Mixer : TruckBody.Tipper),
            $"mounted the {HeavyCatalog.For(Kind)?.Label}");
        if (me.Heavy is not { } truck || me.Visual is not HeavyRig rig) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(1);

        // ---- it drives -------------------------------------------------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(5);
        float went = Flat(me.GlobalPosition, start);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(4);
        // let go once stopped: a truck's brake held at a standstill engages reverse
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        Expect(went > 4f && me.GroundSpeed < 0.5f, $"five seconds of throttle covers {went:F1} m, and it stops");

        if (MixerRun) await Mixer(me, truck, rig);
        else await Tipper(me, truck, rig);
        Finish();
    }

    private async Task Tipper(FootPlayer me, Truck truck, HeavyRig rig)
    {
        await Shot("down", Beside(me, 9f, 3f, 3f), me.GlobalPosition + Vector3.Up * 2f);
        await Tap(PlayerInput.Destination);
        Expect(truck.Tipped, "stopped, the destination binding tips the body");
        await Seconds(3.5);
        Expect(Mathf.IsEqualApprox(rig.TippedShown, 1f), $"it rises over its time ({rig.TippedShown:F2} up)");
        var gate = rig.GetNodeOrNull<Node3D>("Body/Tip/Tailgate") ?? rig.FindChild("Tailgate", true, false) as Node3D;
        Expect(gate != null && gate.Rotation.X < -0.5f, $"and the tailgate hangs open ({gate?.Rotation.X:F2} rad)");
        await Shot("tipped", Beside(me, 9f, 4f, 3f), me.GlobalPosition + Vector3.Up * 3f);
        int flags = truck.PackFlags();
        var parked = new Truck(truck.Spec, 0, 0.5f);
        parked.UnpackFlags(flags);
        Expect(parked.Tipped, "the parked flags keep it up");
        await Tap(PlayerInput.Destination);
        await Seconds(3.5);
        Expect(!truck.Tipped && rig.TippedShown == 0f, $"again, and it comes down ({rig.TippedShown:F2})");

        // moving, it refuses; tipped and driven off, it drops
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(2.5);
        await Tap(PlayerInput.Destination);
        Expect(!truck.Tipped, $"at {me.GroundSpeed * 3.6f:F0} km/h it does not tip");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(4);
        await Tap(PlayerInput.Destination);
        Expect(truck.Tipped, "stopped again, it tips");
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(2);
        Expect(!truck.Tipped, "pulling away drops the body");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        await Seconds(3);
    }

    private async Task Mixer(FootPlayer me, Truck truck, HeavyRig rig)
    {
        float turned0 = rig.DrumTurned;
        await Seconds(1);
        Expect(rig.DrumSpeed > 0f && Mathf.Abs(rig.DrumTurned - turned0) > 0.05f,
            $"the engine running, the drum turns ({rig.DrumSpeed * 60f / Mathf.Tau:F1} rpm)");
        await Shot("charging", Beside(me, 9f, 3f, 3f), me.GlobalPosition + Vector3.Up * 2.5f);
        await Tap(PlayerInput.EngineToggle);
        await Seconds(1);
        Expect(!me.EngineOn && rig.DrumSpeed == 0f, "the engine off, the drum stops");
        await Tap(PlayerInput.EngineToggle);
        await Seconds(2);
        Expect(me.EngineOn && rig.DrumSpeed > 0f, "on again, it turns");

        float charging = rig.DrumSpeed;
        await Tap(PlayerInput.Destination);
        await Seconds(2.5);
        Expect(truck.Discharging && rig.DrumSpeed < -charging, $"stopped, the destination binding reverses it to discharge, faster ({rig.DrumSpeed * 60f / Mathf.Tau:F1} rpm)");
        Expect(Mathf.IsEqualApprox(rig.ChuteOut, 1f), $"and swings the chute out ({rig.ChuteOut:F2})");
        await Shot("discharging", Beside(me, 6f, 9f, 3f), me.GlobalPosition + Vector3.Up * 2f + me.GlobalTransform.Basis.Z * 4f);
        await Tap(PlayerInput.Destination);
        await Seconds(2.5);
        Expect(!truck.Discharging && rig.DrumSpeed > 0f && rig.ChuteOut == 0f, $"again, it charges and the chute comes back ({rig.ChuteOut:F2})");
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? $"[tipper] RESULT: ok — the {(MixerRun ? "mixer" : "tipper")} drives and works on the real binding"
            : $"[tipper] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
