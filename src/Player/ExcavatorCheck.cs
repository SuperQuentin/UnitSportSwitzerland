using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--excavatorcheck [shots] --world flat</c> (#611): the excavator driven, turned on the spot,
/// switched into dig mode and its arm worked through the <b>real bindings</b> (held with
/// <see cref="XrPad.Press"/>, as the forklift's check holds its paddles), so what it proves is the
/// control a player, a pad or a VR hand on the cab's levers actually has.
///
/// <list type="bullet">
/// <item>driven, it goes forward along its tracks at a crawl;</item>
/// <item>steered with no throttle it <b>turns on the spot</b>: its yaw changes and it hardly moves —
/// the thing that makes it a crawler;</item>
/// <item>in dig mode the drive keys move nothing, and a slew turns the house (the vehicle's yaw)
/// while the tracks keep their heading and the drawn undercarriage turns back by as much;</item>
/// <item>each joint runs at its rate while held and stops at its limit;</item>
/// <item>the drawn arm is at the angles the ride says;</item>
/// <item>the parked flags round-trip the arm;</item>
/// <item>out of dig mode with the house slewed a quarter turn it travels <b>along its tracks</b>, at
/// right angles to the cab.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/excavator/*.png</c> from a camera of its own.
/// </summary>
public partial class ExcavatorCheck : Node
{
    public static bool Requested => CmdArgs.Has("--excavatorcheck");
    private static bool Shots => CmdArgs.Value("--excavatorcheck") == "shots";

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public ExcavatorCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[excavator] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>Holds a binding for <paramref name="seconds"/> of game time, then lets it go.</summary>
    private async Task Hold(string action, double seconds)
    {
        XrPad.Press(action, true);
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/excavator");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[excavator] wrote {path}");
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
        Expect(me.SetRide(RideKind.Excavator) && me.Vehicle is Excavator, "mounted the excavator");
        if (me.Vehicle is not Excavator ex) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(10);
        await Shot("parked", me.GlobalPosition + new Vector3(8f, 4f, 8f), me.GlobalPosition + Vector3.Up * 1.5f);

        // ---- driven: forward along its tracks at a crawl ---------------------------------------
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Frames(240);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(90);
        float went = Flat(me.GlobalPosition, start);
        Expect(went > 3f && went < 8f, $"four seconds on the tracks moves it at a crawl ({went:F1} m)");

        // ---- steered alone: it turns on the spot ---------------------------------------------
        start = me.GlobalPosition;
        float yaw0 = me.Rotation.Y;
        me.RideControls = () => new RideInput(0f, 0f, 1f, false);
        await Frames(180);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(30);
        float turned = Mathf.Abs(Mathf.AngleDifference(yaw0, me.Rotation.Y));
        float drift = Flat(me.GlobalPosition, start);
        Expect(turned > 0.8f && drift < 0.3f, $"steered with no throttle it turns on the spot ({Mathf.RadToDeg(turned):F0}°, moving {drift:F2} m)");

        // ---- dig mode: the drive keys move nothing; the house slews on its tracks ---------------
        await Tap(PlayerInput.DigMode);
        Expect(ex.Digging, "C / pad B puts it in dig mode");
        start = me.GlobalPosition;
        float track0 = ex.TrackYaw;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Frames(90);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        Expect(Flat(me.GlobalPosition, start) < 0.1f, $"digging, the throttle moves it nowhere ({Flat(me.GlobalPosition, start):F2} m)");
        yaw0 = me.Rotation.Y;
        await Hold(PlayerInput.ArmSlewLeft, 2.0);
        float slewed = Mathf.AngleDifference(yaw0, me.Rotation.Y);
        Expect(Mathf.Abs(slewed - ExcavatorLayout.SlewRate * 2f) < ExcavatorLayout.SlewRate * 0.4f,
            $"two seconds slewing left turns the house by the slew's rate ({Mathf.RadToDeg(slewed):F0}°)");
        Expect(Mathf.Abs(Mathf.AngleDifference(track0, ex.TrackYaw)) < 0.01f, "the tracks keep their heading while the house slews");
        Expect(Flat(me.GlobalPosition, start) < 0.1f, "and the machine stays where it stands");

        // ---- each joint at its rate, to its limits ------------------------------------------------
        await Hold(PlayerInput.ArmBoomUp, (ExcavatorLayout.BoomMax - ex.Boom) / ExcavatorLayout.BoomRate + 1.0);
        await Hold(PlayerInput.ArmStickOut, (ExcavatorLayout.StickMax - ex.Stick) / ExcavatorLayout.StickRate + 1.0);
        await Hold(PlayerInput.ArmBucketDump, (ExcavatorLayout.BucketMax - ex.Bucket) / ExcavatorLayout.BucketRate + 1.0);
        Expect(Mathf.IsEqualApprox(ex.Boom, ExcavatorLayout.BoomMax) && Mathf.IsEqualApprox(ex.Stick, ExcavatorLayout.StickMax)
            && Mathf.IsEqualApprox(ex.Bucket, ExcavatorLayout.BucketMax),
            $"boom up, stick out and bucket open reach their stops ({ex.Boom:F2}, {ex.Stick:F2}, {ex.Bucket:F2})");
        await Shot("reaching", me.GlobalPosition + new Vector3(12f, 5f, 12f), me.GlobalPosition + Vector3.Up * 3f);
        float boomBefore = ex.Boom;
        await Hold(PlayerInput.ArmBoomDown, 1.0);
        float lowered = boomBefore - ex.Boom;
        Expect(Mathf.Abs(lowered - ExcavatorLayout.BoomRate) < ExcavatorLayout.BoomRate * 0.35f,
            $"a second of boom down lowers it by its rate ({lowered:F2} rad)");
        await Frames(30);
        Expect(Mathf.Abs(boomBefore - lowered - ex.Boom) < 0.01f, "let go, the boom stays where it is");
        await Hold(PlayerInput.ArmBoomDown, (ex.Boom - ExcavatorLayout.BoomMin) / ExcavatorLayout.BoomRate + 1.0);
        await Hold(PlayerInput.ArmStickIn, (ex.Stick - ExcavatorLayout.StickMin) / ExcavatorLayout.StickRate + 1.0);
        await Hold(PlayerInput.ArmBucketCurl, (ex.Bucket - ExcavatorLayout.BucketMin) / ExcavatorLayout.BucketRate + 1.0);
        Expect(Mathf.IsEqualApprox(ex.Boom, ExcavatorLayout.BoomMin) && Mathf.IsEqualApprox(ex.Stick, ExcavatorLayout.StickMin)
            && Mathf.IsEqualApprox(ex.Bucket, ExcavatorLayout.BucketMin),
            $"and down, in and curled to the other stops ({ex.Boom:F2}, {ex.Stick:F2}, {ex.Bucket:F2})");

        // ---- the drawn arm is the ride's ----------------------------------------------------------
        await Frames(3);
        var arm = ExcavatorMeshBuilder.ArmOf(me.Visual);
        Expect(arm == null && DisplayServer.GetName() == "headless" || arm != null
            && (arm.Drawn - new Vector4(ex.Slew, ex.Boom, ex.Stick, ex.Bucket)).Length() < 0.01f,
            $"the drawn arm is at the ride's angles ({arm?.Drawn})");

        // ---- the parked flags ----------------------------------------------------------------------
        var packed = new Excavator();
        packed.UnpackFlags(ex.PackFlags());
        float err = Mathf.Max(Mathf.Max(Mathf.Abs(Mathf.AngleDifference(packed.Slew, ex.Slew)), Mathf.Abs(packed.Boom - ex.Boom)),
            Mathf.Max(Mathf.Abs(packed.Stick - ex.Stick), Mathf.Abs(packed.Bucket - ex.Bucket)));
        Expect(err < 0.03f, $"parked flags round-trip the arm (worst {err:F3} rad)");

        // ---- slewed a quarter turn, out of dig mode: it travels along its tracks -------------------
        await Hold(PlayerInput.ArmBoomUp, 1.5);
        float toQuarter = Mathf.Pi * 0.5f - ex.Slew;
        await Hold(toQuarter > 0 ? PlayerInput.ArmSlewLeft : PlayerInput.ArmSlewRight, Mathf.Abs(toQuarter) / ExcavatorLayout.SlewRate);
        await Tap(PlayerInput.DigMode);
        Expect(!ex.Digging, "C / pad B back to driving");
        start = me.GlobalPosition;
        var nose = -me.GlobalTransform.Basis.Z with { Y = 0 };
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Frames(180);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(60);
        var moved = (me.GlobalPosition - start) with { Y = 0 };
        float across = moved.Length() > 0.5f ? Mathf.Abs(moved.Normalized().Dot(nose.Normalized())) : 1f;
        Expect(moved.Length() > 2f && across < 0.25f,
            $"slewed {Mathf.RadToDeg(ex.Slew):F0}°, it drives along its tracks, across its cab ({moved.Length():F1} m, cos {across:F2})");
        await Shot("slewed", me.GlobalPosition + new Vector3(10f, 6f, 10f), me.GlobalPosition + Vector3.Up * 2f);
        // from nearly overhead: the tracks across the house, a quarter turn from it
        await Shot("slewed-above", me.GlobalPosition + new Vector3(0.5f, 16f, 0.5f), me.GlobalPosition);

        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[excavator] RESULT: ok — tracks, turns on the spot, digs on the real bindings"
            : $"[excavator] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
