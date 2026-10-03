using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--stairscheck [shots] --world fixture</c> offline (#417): airstairs at a parked A320, on the real
/// deck and docking code (<c>docs/notes/vehicles/airstairs.md</c>).
/// <list type="bullet">
/// <item>an A320 parked ahead with L1 and L2 open; stairs placed docked at L1 by
/// <see cref="AirstairsDock.PlaceAt"/> (#422's API): docked, the platform at the sill;</item>
/// <item>a player walks from the ground behind the stairs up the flight, over the platform and the
/// bridge plate, through L1 into the cabin: aboard the A320, on its floor;</item>
/// <item>a second airstairs truck driven at L2 from 8 m out, askew, let go: it lines up by itself
/// and raises its platform; parked, it stays docked;</item>
/// <item>the A320 taken and taxied away: both stairs are shoved clear, the aircraft is not held.</item>
/// </list>
/// Windowed with <c>shots</c> (and <c>--style cartoon</c>, real tiles): <c>test_output/stairs/*.png</c>
/// from a camera of its own. RESULT line at the end.
/// </summary>
public partial class AirstairsCheck : Node
{
    public static bool Requested => CmdArgs.Has("--stairscheck");
    private static bool Shots => CmdArgs.Value("--stairscheck") == "shots";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures;
    private int _shot;

    public AirstairsCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[stairs] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    /// <summary>Windowed: a picture from <paramref name="eye"/> looking at <paramref name="at"/>, by a camera of its own.</summary>
    private async Task Shot(string name, Vector3 eye, Vector3 at)
    {
        if (!Shots || DisplayServer.GetName() == "headless") return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 55f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/stairs");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[stairs] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    private static VehicleBody? Body(string name) =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Name == name && !v.IsQueuedForDeletion());

    private static IEnumerable<VehicleBody> Stairs() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Where(v => v.Ride is Airstairs && !v.IsQueuedForDeletion())
        ?? Enumerable.Empty<VehicleBody>();

    /// <summary>Walks toward a world point (level) until within 0.4 m.</summary>
    private async Task<bool> WalkTo(FootPlayer me, System.Func<Vector3> target, double seconds)
    {
        me.WalkControls = () =>
        {
            var to = (target() - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.15f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => ((target() - me.GlobalPosition) with { Y = 0 }).Length() < 0.4f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.3);
        return there;
    }

    public override async void _Ready()
    {
        await Seconds(2);
        for (int i = 0; i < 1800 && (_player() is not { } ready || !ready.IsOnFloor()); i++)
        {
            if (_player() == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor() || VehicleManager.Instance is not { } vehicles) { Finish("no player"); return; }
        await Seconds(1);

        // ---- an A320 parked 40 m ahead (north), nose north; L1 and L2 open ----------------------
        var start = me.GlobalPosition;
        // the scene turned to --stairsheading (degrees from north, clockwise): along a real runway or apron
        float yaw = -Mathf.DegToRad(float.TryParse(CmdArgs.Value("--stairsheading"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float heading) ? heading : 0f);
        var turn = new Basis(Vector3.Up, yaw);
        var planeAt = start + turn * new Vector3(0, 0, -45f);
        var origin = vehicles.Origin;
        vehicles.Place(new VehicleState(RideKind.A320, origin.ToGlobal(planeAt), yaw, Vector3.Zero, 400f, true, false, 0f, VehicleState.Now,
            // parked as a pilot leaves one: gear down and the rest in its flags (flags 0 read as "fresh")
            Flags: Airliner.For(RideKind.A320)!.PackFlags()), "a320_check");
        bool posed = await Until(() => Body("a320_check") is { Posed: true }, 10);
        Expect(posed, "an A320 parked and posed");
        if (Body("a320_check") is not { } plane) { Finish("no A320"); return; }
        await Seconds(2);
        plane.ToggleDoor(1 | 4);
        await Seconds(2);
        var deck = plane.Ride.Decks[0];
        Transform3D Frame() => (plane.Visual ?? (Node3D)plane).GlobalTransform.Orthonormalized();
        Vector3 Cabin(float x, float z) => Frame() * AircraftMeshBuilder.Flip(new Vector3(x, A320Layout.FloorY + 0.05f, z));
        float sillY = (Frame() * AirstairsDock.LocalSill(deck, 0)!.Value.Edge).Y;
        GD.Print($"[stairs] A320 at {plane.GlobalPosition}, doors {plane.DoorsOpen}, L1 sill {sillY - plane.GlobalPosition.Y:F3} m over its ground");

        // ---- stairs placed docked at L1 (#422's API) ----------------------------------------
        int placed = AirstairsDock.PlaceAt(vehicles, plane.Capture(), "stairs", 0);
        bool docked = await Until(() => Body("stairs_0") is { StairsDockedAt: not null } s && s.Ride is Airstairs a
            && Mathf.Abs(a.Height - (sillY - s.GlobalPosition.Y + AirstairsLayout.DockAbove)) < 0.02f, 10);
        var l1 = Body("stairs_0");
        float h1 = (l1?.Ride as Airstairs)?.Height ?? 0f;
        Expect(placed == 1 && docked, $"stairs placed docked at L1 (placed {placed}, docked {l1?.StairsDockedAt?.Door}, platform {h1:F3} m, sill {sillY - (l1?.GlobalPosition.Y ?? 0f):F3} m)");
        if (l1 == null) { Finish("no stairs"); return; }
        // settled on the ground (placed a hand's breadth up), the platform at the sill over it
        await Seconds(3);
        h1 = (l1.Ride as Airstairs)?.Height ?? 0f;
        var stairsXf = l1.GlobalTransform.Orthonormalized();
        await Shot("docked-l1", stairsXf * new Vector3(-9f, 4.5f, 7f), stairsXf * new Vector3(0, 2.2f, -1.5f));
        await Shot("a320-with-stairs", planeAt + turn * new Vector3(-38f, 12f, 22f), planeAt + turn * new Vector3(-3f, 3f, -2f));

        // ---- walked from the ground up the stairs, through L1, into the cabin ----------------
        me.DebugLaunch(stairsXf * new Vector3(0, 0.2f, 8f), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(1);
        bool foot = await WalkTo(me, () => stairsXf * new Vector3(0, 0, 5.4f), 15);
        Expect(foot && !me.Aboard, $"on the ground at the stairs' foot ({me.GlobalPosition.Y - l1.GlobalPosition.Y:F2} m up)");
        bool up = await WalkTo(me, () => stairsXf * new Vector3(0, 0, -3.0f), 20);
        float onPlatform = me.GlobalPosition.Y - l1.GlobalPosition.Y;
        Expect(up && Mathf.Abs(onPlatform - h1) < 0.15f, $"walked up the flight onto the platform ({onPlatform:F2} m up, platform {h1:F2})");
        await Shot("walker-on-platform", stairsXf * new Vector3(-6f, h1 + 2.5f, 3f), me.GlobalPosition + Vector3.Up);
        bool inside = await WalkTo(me, () => Cabin(0.9f, A320Layout.ForwardDoorZ), 15) && await WalkTo(me, () => Cabin(0f, A320Layout.ForwardDoorZ), 10);
        var local = AircraftMeshBuilder.Flip(Frame().AffineInverse() * me.GlobalPosition);
        Expect(inside && me.Aboard && me.DeckOn == "v:" + plane.Name && Mathf.Abs(local.Y - A320Layout.FloorY) < 0.3f,
            $"through L1 into the cabin (aboard {me.DeckOn}, at {local.X:F2}, {local.Y - A320Layout.FloorY:F2} over the floor, {local.Z:F2})");
        await Shot("in-the-door", stairsXf * new Vector3(-2.2f, h1 + 1.7f, -1.2f), me.GlobalPosition + Vector3.Up * 1.1f);
        // and back down to the ground
        bool out1 = await WalkTo(me, () => stairsXf * new Vector3(0, 0, -3.0f), 15);
        GD.Print($"[stairs] out on the platform {out1}: at {stairsXf.AffineInverse() * me.GlobalPosition}, aboard '{me.DeckOn}', floor {me.IsOnFloor()}");
        bool down = out1 && await WalkTo(me, () => stairsXf * new Vector3(0, 0, 6.5f), 20);
        if (!down) GD.Print($"[stairs] stuck at {stairsXf.AffineInverse() * me.GlobalPosition}, aboard '{me.DeckOn}', floor {me.IsOnFloor()}, {me.WalkState}");
        Expect(down && !me.Aboard && me.GlobalPosition.Y - l1.GlobalPosition.Y < 0.3f, $"back down the stairs to the ground ({me.GlobalPosition.Y - l1.GlobalPosition.Y:F2} m up)");

        // ---- a second truck driven to L2 from 8 m out, askew, let go: it docks ----------------
        var l2Local = AirstairsDock.LocalSill(deck, 2)!.Value;
        var f = Frame();
        var (dockAt, dockYaw, _) = AirstairsDock.Pose(f * l2Local.Edge, (f.Basis * l2Local.Out) with { Y = 0 }, plane.GlobalPosition.Y);
        // the platform's height is over the ground the truck stands on (a sloping apron: not the aircraft's)
        float DockH() => (f * l2Local.Edge).Y - me.GlobalPosition.Y + AirstairsLayout.DockAbove;
        var outward = new Vector3(Mathf.Sin(dockYaw), 0, Mathf.Cos(dockYaw));
        var side = new Vector3(outward.Z, 0, -outward.X);
        me.DebugLaunch(dockAt + outward * 8f + side * 1.2f + Vector3.Up * 0.3f, Vector3.Zero);
        await Until(() => me.IsOnFloor() && !me.Aboard, 5);
        await Seconds(0.5);
        // headed at the door, a little askew (the ride takes the body's heading as it is set)
        me.Rotation = new Vector3(0, dockYaw + 0.25f, 0);
        Expect(me.SetRide(RideKind.Airstairs) && me.Vehicle is Airstairs, "at the wheel of airstairs");
        if (me.Vehicle is not Airstairs driving) { Finish("not driving airstairs"); return; }
        await Seconds(1);
        Vector3 Want() => dockAt;
        bool letGo = false;
        me.RideControls = () =>
        {
            if (letGo) return new RideInput(0f, 0f, 0f, false);
            var lip = AirstairsDock.Lip(me.GlobalTransform);
            var target = AirstairsDock.Lip(new Transform3D(new Basis(Vector3.Up, dockYaw), Want()));
            var to = (target - lip) with { Y = 0 };
            // near enough: hands off, the truck lines itself up
            if (to.Length() < 2.5f) { letGo = true; return new RideInput(0f, 0f, 0f, false); }
            float yawTo = Mathf.Atan2(-to.X, -to.Z);
            float e = MathX.WrapAngle(yawTo - me.Rotation.Y);
            return new RideInput(me.GroundSpeed < 1.2f ? 0.8f : 0f, 0f, Mathf.Clamp(-e * 2.5f, -1f, 1f), false);
        };
        double nextLog = 0;
        var near = new List<AirstairsDock.Sill>();
        bool lined = await Until(() =>
        {
            if (Time.GetTicksMsec() / 1000.0 > nextLog)
            {
                nextLog = Time.GetTicksMsec() / 1000.0 + 2;
                AirstairsDock.SillsNear(GetTree(), me.GlobalPosition, 15f, near);
                var ap = AirstairsDock.Approach(me.GlobalTransform.Orthonormalized(), near);
                GD.Print($"[stairs]   driving: at {me.GlobalPosition} yaw {me.Rotation.Y:F2} (dock {dockAt}, {dockYaw:F2}), speed {me.GroundSpeed:F2}, sills {near.Count}, approach {ap?.Door}, hits [{string.Join(" ", Enumerable.Range(0, me.GetSlideCollisionCount()).Select(k => $"{(me.GetSlideCollision(k).GetCollider() as Node)?.Name} n{me.GetSlideCollision(k).GetNormal()}"))}], lip off {(AirstairsDock.Lip(me.GlobalTransform) - AirstairsDock.Lip(new Transform3D(new Basis(Vector3.Up, dockYaw), dockAt))).Length():F2}, target {driving.TargetHeight:F2}");
            }
            return driving.Docked != null && Mathf.Abs(driving.Height - DockH()) < 0.01f;
        }, 40);
        var err = (me.GlobalPosition - dockAt) with { Y = 0 };
        Expect(lined && err.Length() < 0.05f && Mathf.Abs(MathX.WrapAngle(me.Rotation.Y - dockYaw)) < 0.02f,
            $"driven to L2 and let go: docked (door {driving.Docked?.Door}, off by {err.Length():F3} m, {Mathf.RadToDeg(MathX.WrapAngle(me.Rotation.Y - dockYaw)):F1}°, platform {driving.Height:F3} for {DockH():F3})");
        await Shot("driven-docked-l2", me.GlobalPosition + outward * 10f + side * 8f + Vector3.Up * 6f, me.GlobalPosition + Vector3.Up * 2.5f);
        me.RideControls = null;
        Expect(me.TryInteract() && await Until(() => me.Ride == RideKind.OnFoot, 5), "got out: parked");
        bool parked = await Until(() => Stairs().Any(s => s.StairsDockedAt is { Door: 2 }), 6);
        Expect(parked, $"parked, it stays docked at L2 ({string.Join(", ", Stairs().Select(s => $"{s.Name}: {s.StairsDockedAt?.Door}"))})");
        await Shot("both-docked", planeAt + turn * new Vector3(-30f, 9f, 14f), planeAt + turn * new Vector3(-3f, 3f, 0f));

        // ---- the A320 taken and taxied away: the stairs are shoved clear ----------------------
        var before = Stairs().ToDictionary(s => s.Name.ToString(), s => s.GlobalPosition);
        var seat = plane.Ride.StandSpot(0)!.Value;
        bool aboard = false;
        for (int tries = 0; tries < 3 && !aboard; tries++)
        {
            await Seconds(1);
            me.DebugLaunch(Frame() * seat + Vector3.Up * 0.05f, Vector3.Zero);
            aboard = await Until(() => me.Aboard, 3);
            if (!aboard) GD.Print($"[stairs]   not aboard at {AircraftMeshBuilder.Flip(Frame().AffineInverse() * me.GlobalPosition)} (decks {me.DeckSetsBuilt}, floor {me.IsOnFloor()}, ride {me.Ride}, {me.WalkState})");
        }
        await Seconds(0.5);
        bool took = aboard && me.TryInteract() && await Until(() => me.Vehicle is Airliner, 6);
        Expect(took, $"at the A320's controls (aboard {aboard}, ride {me.Ride})");
        if (me.Vehicle is not Airliner jet) { Finish("not at the controls"); return; }
        if ((jet.DoorsOpen & 1) != 0) jet.ToggleDoor(0);
        if ((jet.DoorsOpen & 4) != 0) jet.ToggleDoor(2);
        if (jet.State.ParkingBrake) jet.Command(AirlinerCommand.ParkingBrake);
        var from = me.GlobalPosition;
        GD.Print($"[stairs] taxiing from {from}, yaw {me.Rotation.Y:F2}, plane frame yaw {jet.State.Yaw:F2}, parking {jet.State.ParkingBrake}, stairs {string.Join(", ", Stairs().Select(s => $"{s.Name} {s.GlobalPosition}"))}");
        Input.ActionPress(PlayerInput.Sprint);
        for (int i = 0; i < 4; i++)
        {
            await Seconds(2);
            var hits = string.Join(" ", Enumerable.Range(0, me.GetSlideCollisionCount()).Select(k => (me.GetSlideCollision(k).GetCollider() as Node)?.Name.ToString() ?? "?"));
            GD.Print($"[stairs]   thrust: spool {jet.State.Spool:F2} lever {jet.State.Lever:F2} braking {jet.State.Braking:F2} v {jet.State.Velocity} on ground {jet.State.OnGround} at {me.GlobalPosition} hits [{hits}]");
        }
        await Until(() => MathX.FlatLength(jet.State.Velocity) > 4f, 40);
        Input.ActionRelease(PlayerInput.Sprint);
        bool rolled = await Until(() => me.GlobalPosition.DistanceTo(from) > 45f, 60);
        GD.Print($"[stairs] taxied to {me.GlobalPosition} at {me.GroundSpeed:F1} m/s, lever {jet.State.Lever:F2}");
        Input.ActionPress(PlayerInput.Jump);
        await Seconds(1);
        float moved = me.GlobalPosition.DistanceTo(from);
        var planeXf = me.GlobalTransform.Orthonormalized();
        var shoved = Stairs().Select(s =>
        {
            var l = planeXf.AffineInverse() * s.GlobalPosition;
            float pushed = before.TryGetValue(s.Name, out var b) ? (s.GlobalPosition - b).Length() : 0f;
            // clear: out past the wingtip, or behind the tail
            return (s.Name, Side: Mathf.Abs(l.X), Aft: l.Z, Pushed: pushed, Clear: Mathf.Abs(l.X) > A320Layout.WingTipX || l.Z > -A320Layout.TailZ + 1f);
        }).ToList();
        Expect(rolled && shoved.All(s => s.Pushed > 2f && s.Clear),
            $"taxied {moved:F0} m away, the stairs shoved clear ({string.Join(", ", shoved.Select(s => $"{s.Name} pushed {s.Pushed:F1} m, now {s.Side:F1} m off the centreline, {s.Aft:F1} m aft"))})");
        await Shot("taxied-away", from + turn * new Vector3(-35f, 14f, 10f), from + turn * new Vector3(0, 2f, -20f));
        await Until(() => me.GroundSpeed < 0.5f, 30);
        Input.ActionRelease(PlayerInput.Jump);

        // ---- parked stairs being raised with a player on the platform, then walked down -------
        me.ExitVehicle();
        await Until(() => me.Ride == RideKind.OnFoot, 5);
        await Seconds(1);
        var lift = Body("stairs_0");
        if (lift?.Ride is not Airstairs raised) { Finish("no stairs to raise"); return; }
        var lx = lift.GlobalTransform.Orthonormalized();
        me.DebugLaunch(lx * new Vector3(0, raised.Height + 0.1f, -3f), Vector3.Zero);
        bool onIt = await Until(() => me.IsOnFloor() && me.Aboard, 5);
        float wasH = raised.Height;
        raised.TargetHeight = 4.6f;
        float worst = 0f;
        bool risen = await Until(() =>
        {
            worst = Mathf.Max(worst, Mathf.Abs(me.GlobalPosition.Y - lift.GlobalPosition.Y - raised.Height));
            return Mathf.Abs(raised.Height - 4.6f) < 0.001f;
        }, 15);
        await Seconds(1);
        float stood = me.GlobalPosition.Y - lift.GlobalPosition.Y;
        Expect(onIt && risen && me.Aboard && Mathf.Abs(stood - raised.Height) < 0.06f && worst < 0.12f,
            $"parked stairs raised {wasH:F2} -> {raised.Height:F2} m with a player on the platform: carried up (stands {stood:F2} m, worst {worst * 100:F0} cm off)");
        await Shot("raised-with-player", lx * new Vector3(-7f, 5.5f, 2f), me.GlobalPosition + Vector3.Up);
        bool walkedDown = await WalkTo(me, () => lx * new Vector3(0, 0, 6.5f), 30);
        Expect(walkedDown && !me.Aboard && me.GlobalPosition.Y - lift.GlobalPosition.Y < 0.3f,
            $"walked down the raised flight to the ground ({me.GlobalPosition.Y - lift.GlobalPosition.Y:F2} m up)");
        Finish(null);
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) { p.WalkControls = null; p.RideControls = null; }
        GD.Print(_failures == 0 ? "[stairs] RESULT: ok" : $"[stairs] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
