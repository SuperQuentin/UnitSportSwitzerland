using System.Threading.Tasks;
using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--touchcheck --mobile --world fixture</c> (#63, #1043): the touch overlay drives the player
/// through the same screen events a phone sends, mode by mode. From the fly camera Walk lands on
/// foot; the stick walks, a drag turns the camera, Jump jumps; More opens and closes; on a bike and
/// in a car the Gas pedal moves it; Get out leaves the car, and the "get in" pill the prompts put up
/// beside it takes the seat again; in a plane the rocker pushes the throttle trigger. Prints
/// <c>[touchcheck] RESULT: ok</c> and quits 0, or FAILED and 1. Touches are sent in window pixels,
/// as a phone sends them, so any <c>--resolution</c> works.
/// </summary>
public partial class TouchCheck : Node
{
    private const double Timeout = 120;
    private readonly Func<FootPlayer?> _local;
    private readonly TouchControls _touch;
    private readonly List<string> _fails = new();
    private double _t;
    private bool _done;

    public static bool Requested => CmdArgs.Has("--touchcheck");

    /// <summary><c>--touchshots &lt;dir&gt;</c>: a picture of each mode's controls on the way (a windowed run).</summary>
    private static readonly string? ShotDir = CmdArgs.Value("--touchshots");

    public TouchCheck(Func<FootPlayer?> local, TouchControls touch)
    {
        Name = "TouchCheck";
        _local = local;
        _touch = touch;
    }

    public override void _Ready() => _ = Run();

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > Timeout && !_done) Finish("timed out");
    }

    private async Task Run()
    {
        try
        {
            await Steps();
            Finish(null);
        }
        catch (Exception e)
        {
            Finish($"{e.GetType().Name}: {e.Message}");
        }
    }

    private async Task Steps()
    {
        // the fixture world starts in the fly camera: the spectator's Walk key lands on foot
        await Until(() => TouchControls.Shown && _touch.Mode == TouchMode.Spectator ? this : null, 20, "the overlay in the fly camera");
        Expect(_touch.Has("walk") && _touch.Has("up") && _touch.Has("down"), "the fly camera has no Walk, Up or Down");
        await Shot("1-spectator");
        // the world settles under the camera first, or Walk drops the body where nothing is built yet
        await Seconds(3);
        await TapAt(_touch.CentreOf("walk"));
        var me = await Until(() => _local() is { } p && p.IsOnFloor() && _touch.Mode == TouchMode.Foot ? p : null, 30, "on foot");
        Expect(_touch.Has("jump") && _touch.Has("crouch") && !_touch.Has("walk"), "on foot: not Jump and Crouch without Walk");
        await Seconds(1.5);
        await Shot("2-foot");
        // hints name the on-screen controls (#1054)
        Names("on foot", (PlayerInput.MoveForward, "the stick"), (PlayerInput.LookRight, "a drag"), (PlayerInput.Jump, "[Jump]"),
            (PlayerInput.Teleport, "[MAP]"), (PlayerInput.RideMenu, "[More › Travel menu]"));
        string walk = InputHints.Format("Walk with {move_forward} {move_left} {move_back} {move_right}.");
        GD.Print($"[touchcheck] hint: {walk}");
        Expect(walk == "Walk with the stick.", $"a WASD hint reads \"{walk}\" on touch");

        // the stick: a thumb pushed up walks forward. Far left, clear of the buttons even on the
        // square screen a headless run has (the stick's resting place is under them there)
        var size = GetViewport().GetVisibleRect().Size;
        var stick = new Vector2(size.X * 0.08f, size.Y * 0.8f);
        var from = me.GlobalPosition;
        Touch(0, stick, true);
        var push = new Vector2(0, -size.Y * 0.16f);   // most of the ring (12 mm)
        for (double t = 0; t < 2; t += await Frame()) Drag(0, stick + push, new Vector2(0, -1));
        Touch(0, stick + push, false);
        float walked = Flat(me.GlobalPosition - from);
        GD.Print($"[touchcheck] stick: walked {walked:F2} m");
        Expect(walked >= 1.5f, $"the stick moved the player only {walked:F2} m");

        // a drag on the right turns the camera
        var cam = GetViewport().GetCamera3D()!;
        var look = Flat2(-cam.GlobalBasis.Z);
        var at = new Vector2(size.X * 0.6f, size.Y * 0.4f);
        Touch(1, at, true);
        Drag(1, at + new Vector2(240, 0), new Vector2(240, 0));
        Touch(1, at + new Vector2(240, 0), false);
        await Seconds(0.5);
        float turned = Mathf.RadToDeg(look.AngleTo(Flat2(-cam.GlobalBasis.Z)));
        GD.Print($"[touchcheck] look drag: turned {turned:F1} deg");
        Expect(Mathf.Abs(turned) >= 10, $"a 240 px drag turned the camera only {turned:F1} deg");

        // Jump
        float startY = me.GlobalPosition.Y, peakY = startY;
        var jump = _touch.CentreOf("jump");
        Touch(2, jump, true);
        for (double t = 0; t < 1.2; t += await Frame())
        {
            peakY = Math.Max(peakY, me.GlobalPosition.Y);
            if (t > 0.15) Touch(2, jump, false);
        }
        GD.Print($"[touchcheck] Jump: rose {peakY - startY:F2} m");
        Expect(peakY - startY >= 0.3f, $"Jump lifted the player only {peakY - startY:F2} m");

        // More: the sheet opens on the top bar's key, lists Travel, and closes on it again
        await TapAt(_touch.CentreOf("more"));
        Expect(_touch.MoreOpen, "More did not open");
        await Shot("3-more");
        Expect(_touch.MoreEntryOf(PlayerInput.RideMenu) != null, "More has no Travel");
        Expect(_touch.MoreEntryOf(PlayerInput.Jump) == null, "More lists Jump, which has its own key");
        await TapAt(_touch.CentreOf("more"));
        Expect(!_touch.MoreOpen, "More did not close");
        await Seconds(1);

        // a bike: Pedal moves it
        await Ride(me, RideKind.RoadBike, TouchMode.Ride, 1f);
        // a car: Gas drives it, Get out leaves it
        var car = RideProbe.KindNamed("car");
        await Ride(me, car, TouchMode.Drive, 1.5f);
        await Until(() => me.Velocity.Length() < 0.5f ? me : null, 15, "the car stopped");
        await TapAt(_touch.CentreOf("exit"));
        await Until(() => _touch.Mode == TouchMode.Foot ? me : null, 10, "out of the car");
        // beside it, the prompts' "get in" becomes a pill, and the pill gets in
        var pill = await UntilAt(() => _touch.PillOf(PlayerInput.InteractMount), 10, "a get-in pill beside the car");
        GD.Print($"[touchcheck] pills by the car: {string.Join(" | ", _touch.PillTexts)}");
        await Shot("6-pill");
        await TapAt(pill);
        await Until(() => _touch.Mode == TouchMode.Drive ? me : null, 10, "back in the car through the pill");
        await TapAt(_touch.CentreOf("exit"));
        await Until(() => _touch.Mode == TouchMode.Foot ? me : null, 10, "out of the car again");
        await Seconds(1);

        // a plane: the rocker is the throttle trigger, springing back
        Expect(me.SetRide(RideKind.Plane), "could not board the plane");
        await Until(() => _touch.Mode == TouchMode.Fly ? me : null, 5, "the plane's controls");
        Expect(_touch.Has("rocker"), "the plane has no rocker");
        await Shot("7-plane");
        var rocker = _touch.CentreOf("rocker");
        Touch(3, rocker, true);
        Drag(3, rocker + new Vector2(0, -400), new Vector2(0, -400));
        await Frame();
        float rt = Input.GetJoyAxis(TouchControls.Device, JoyAxis.TriggerRight);
        Touch(3, rocker + new Vector2(0, -400), false);
        await Frame();
        float after = Input.GetJoyAxis(TouchControls.Device, JoyAxis.TriggerRight);
        GD.Print($"[touchcheck] rocker: throttle trigger {rt:F2}, let go {after:F2}");
        Expect(rt > 0.9f && after < 0.05f, $"the rocker gave the trigger {rt:F2}, and {after:F2} let go");
    }

    /// <summary>Into a ride, Gas held for 2.5 s: it moved at least <paramref name="metres"/>; then the brake.</summary>
    private async Task Ride(FootPlayer me, RideKind kind, TouchMode mode, float metres)
    {
        await Until(() => me.IsOnFloor() && me.Velocity.Length() < 0.5f ? me : null, 10, "standing still");
        Expect(me.SetRide(kind), $"could not get on the {kind}");
        await Until(() => _touch.Mode == mode ? me : null, 5, $"the {kind}'s controls ({mode})");
        Expect(_touch.Has("gas") && _touch.Has("brake"), $"the {kind} has no pedals");
        await Seconds(1);
        await Shot(mode == TouchMode.Ride ? "4-bike" : "5-car");
        if (mode == TouchMode.Ride) Names("on the bike", (PlayerInput.Throttle, "[Pedal]"), (PlayerInput.Jump, "[Hop]"));
        else Names("in the car", (PlayerInput.Throttle, "[Gas]"), (PlayerInput.Brake, "[Brake]"), (PlayerInput.Jump, "[Handbrake]"),
            (PlayerInput.MoveLeft, "the stick"));
        var from = me.GlobalPosition;
        var gas = _touch.CentreOf("gas");
        Touch(4, gas, true);
        await Seconds(2.5);
        Touch(4, gas, false);
        float moved = Flat(me.GlobalPosition - from);
        GD.Print($"[touchcheck] {kind}: Gas moved it {moved:F2} m");
        Expect(moved >= metres, $"Gas moved the {kind} only {moved:F2} m");
        var brake = _touch.CentreOf("brake");
        Touch(4, brake, true);
        await Seconds(2);
        Touch(4, brake, false);
        if (mode == TouchMode.Ride)
        {
            await Until(() => me.Velocity.Length() < 0.5f ? me : null, 10, $"the {kind} stopped");
            Expect(me.SetRide(RideKind.OnFoot), $"could not get off the {kind}");
            await Until(() => _touch.Mode == TouchMode.Foot ? me : null, 5, "back on foot");
        }
    }

    private async Task Shot(string name)
    {
        if (ShotDir == null || DisplayServer.GetName() == "headless") return;
        await Seconds(0.3);
        DirAccess.MakeDirRecursiveAbsolute(ShotDir);
        string path = $"{ShotDir}/{name}.png";
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[touchcheck] shot {path}");
    }

    // ---- touches and time ----------------------------------------------------------------

    private async Task TapAt(Vector2 at)
    {
        Touch(5, at, true);
        await Frame();
        await Frame();
        Touch(5, at, false);
        await Seconds(0.4);
    }

    private async Task<double> Frame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return GetProcessDeltaTime();
    }

    private async Task Seconds(double s)
    {
        for (double t = 0; t < s;) t += await Frame();
    }

    private async Task<T> Until<T>(Func<T?> get, double within, string what) where T : class
    {
        double said = 0;
        for (double t = 0; t < within; t += await Frame())
        {
            if (get() is { } v) return v;
            if (t - said > 5)
            {
                said = t;
                GD.Print($"[touchcheck] waiting for {what}: {Where()}");
            }
        }
        throw new TimeoutException($"never {what} ({Where()})");
    }

    private async Task<Vector2> UntilAt(Func<Vector2?> get, double within, string what)
    {
        for (double t = 0; t < within; t += await Frame())
            if (get() is { } v) return v;
        throw new TimeoutException($"never {what} (mode {_touch.Mode}, pills: {string.Join(" | ", _touch.PillTexts)})");
    }

    private string Where() => $"mode {_touch.Mode}, overlay {TouchControls.Shown}, mouse {MouseCapture.Mode}, "
        + $"player at {_local()?.GlobalPosition}, on the floor {_local()?.IsOnFloor()}, moving {_local()?.Velocity}";

    /// <summary>What hints call these actions here, against what the overlay shows.</summary>
    private void Names(string where, params (string Action, string Expected)[] names)
    {
        foreach (var (action, expected) in names)
        {
            string label = InputHints.Label(action);
            GD.Print($"[touchcheck] {where}: {action} is {label}");
            Expect(label == expected, $"{where}: {action} is called \"{label}\", not \"{expected}\"");
        }
    }

    private void Expect(bool ok, string failure)
    {
        if (!ok) _fails.Add(failure);
    }

    // a phone's touches come in window pixels, which the viewport scales to the UI's canvas: send
    // the overlay's canvas positions back through that scale, as a finger on them would arrive
    private Vector2 Window(Vector2 canvas) => GetViewport().GetFinalTransform() * canvas;

    private void Touch(int finger, Vector2 at, bool pressed) =>
        Input.ParseInputEvent(new InputEventScreenTouch { Index = finger, Position = Window(at), Pressed = pressed });

    private void Drag(int finger, Vector2 at, Vector2 relative) =>
        Input.ParseInputEvent(new InputEventScreenDrag { Index = finger, Position = Window(at), Relative = GetViewport().GetFinalTransform().BasisXform(relative) });

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
    private static Vector2 Flat2(Vector3 v) => new Vector2(v.X, v.Z).Normalized();

    private void Finish(string? error)
    {
        if (_done) return;
        _done = true;
        if (error != null) _fails.Add(error);
        foreach (var f in _fails) GD.PrintErr($"[touchcheck] {f}");
        bool ok = _fails.Count == 0;
        GD.Print(ok ? "[touchcheck] RESULT: ok" : "[touchcheck] RESULT: FAILED");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
