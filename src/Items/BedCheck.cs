using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--bedcheck tipper|dumper[,bucket][,shots] --world flat --systems physics,ui</c> (#615, tier 1): a pallet
/// set into a parked tipper's body (or mini dumper's skip), carried off in it and tipped out, offline
/// on the real bindings:
/// <list type="bullet">
/// <item>a telehandler with a pallet on its forks, lifted over the parked body and lowered (with
/// <c>bucket</c>, a wheel loader's bucket raised over it and dumped): the
/// pallet is in the body (the parked vehicle's replicated <c>BedLoad</c>, drawn on its floor), the
/// forks empty, nothing on the ground;</item>
/// <item>got into, the body still holds it, and driven off it rides along;</item>
/// <item>stopped and tipped (the real <c>destination</c>), it slides to the open end and is set down
/// on the ground past it, with its load, the body empty;</item>
/// <item>the parked flags and the pose keep any pallet.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/bed/*.png</c>.
/// </summary>
public partial class BedCheck : Node
{
    public static bool Requested => CmdArgs.Has("--bedcheck");
    private static string[] Args => (CmdArgs.Value("--bedcheck") ?? "").Split(',');
    private static bool Shots => Args.Contains("shots");
    private static bool Dumper => Args.Contains("dumper");
    /// <summary>A wheel loader's bucket dumps the pallet into the body, not a telehandler's forks.</summary>
    private static bool Bucket => Args.Contains("bucket");
    private static RideKind Host => Dumper ? RideKind.MiniDumper : (RideKind)104;

    /// <summary>The pallet's load: a narrow deck of cement sacks.</summary>
    private const byte Load = 40;

    private readonly WorldOrigin _origin;
    private PalletService _pallets = null!;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public BedCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[bed] {(ok ? "ok  " : "FAIL")} {what}");
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

    private async Task<bool> Until(System.Func<bool> done, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!done() && GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        return done();
    }

    private async Task Hold(string action, double seconds, System.Func<bool>? done = null)
    {
        XrPad.Press(action, true);
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end && done?.Invoke() != true) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/bed");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{(Dumper ? "dumper" : "tipper")}{(Bucket ? "-bucket" : "")}-{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[bed] wrote {path}");
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
        _pallets = PalletService.Instance ?? PalletService.Create(this, _origin, server: false);
        // with the world's origin: taking over a parked vehicle places the body from its LV95
        _player = new FootPlayer { Name = "Probe", Origin = _origin };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + 1.0f, at.Z);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = RunCaught();
    }

    private async Task RunCaught()
    {
        try { await Run(); }
        catch (System.Exception e)
        {
            Expect(false, $"the run threw: {e}");
            Finish();
        }
    }

    private static VehicleBody? Parked() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .FirstOrDefault(v => v.Kind == Host && !v.IsQueuedForDeletion());

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        // the flat world has no vehicles of its own: a manager to park in, as the client world has
        var vehicles = VehicleManager.Instance ?? VehicleManager.Create(this, null, _origin);

        // ---- a machine holding a pallet up: a telehandler's forks, or a wheel loader's bucket ---------
        // (the flat world has no terrain under a teleport, so the body is parked under the pallet
        // rather than the machine driven or moved to it)
        int carrying = Pallets.Carried(Load);
        var shape = Dumper ? MiniDumperLayout.Bed : TruckMeshBuilder.TipperBed(HeavyCatalog.For(Host)!, 0f);
        float hostHalf = Dumper ? MiniDumperLayout.HalfWidth : HeavyCatalog.For(Host)!.Sections[0].Width * 0.5f;
        // over the body's sides, not through them: the skip's rim, the tipper's top rail
        float sides = Dumper ? MiniDumperLayout.SkipTop + 0.2f : 3.05f;
        System.Func<int> held;
        System.Func<float> heldUp;
        System.Func<Vector3> goes;
        System.Func<Task> lower;
        string how;
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        if (Bucket)
        {
            Expect(me.SetRide(RideKind.WheelLoader) && me.Vehicle is WheelLoader { HasBucket: true }, "mounted a wheel loader with its bucket");
            if (me.Vehicle is not WheelLoader loader) { Finish(); return; }
            await Frames(10);
            await Tap(PlayerInput.DigMode);
            await Hold(PlayerInput.ArmBucketCurl, 3, () => loader.BucketPitch > Pallets.CurlCarry + 0.15f);
            await Hold(PlayerInput.ArmBoomUp, 12, () => (loader.BucketFrame * loader.BucketFloor).Y > sides);
            loader.Carrying = carrying;
            held = () => loader.Carrying;
            heldUp = () => (loader.BucketFrame * loader.BucketFloor).Y;
            // where it tips out (PalletService.TendBucket): past the lip, the bucket turned about its
            // pin down to where it dumps
            goes = () =>
            {
                var b = loader.BucketFrame;
                var dumped = new Transform3D(b.Basis * new Basis(Vector3.Right, Pallets.DumpDrop - loader.BucketPitch), b.Origin);
                return me.GlobalTransform * (dumped * new Vector3(0f, loader.BucketFloor.Y, -(loader.BucketReach + Pallets.Length * 0.5f)));
            };
            lower = () => Hold(PlayerInput.ArmBucketDump, 8, () => loader.Carrying == 0);
            how = "dumped";
        }
        else
        {
            Expect(me.SetRide(RideKind.Telehandler) && me.Vehicle is Telehandler, "mounted a telehandler");
            if (me.Vehicle is not Telehandler boom) { Finish(); return; }
            await Frames(10);
            await Tap(PlayerInput.DigMode);
            await Hold(PlayerInput.ArmBoomUp, 6.0, () => boom.ForkHeight > shape.Floor.Y + 0.8f);
            // run out until the pallet rides past the machine's nose by the body's half width, and a
            // margin: the machine stays clear of the body's side
            var (box, size) = boom.ParkedBox;
            float clear = -(box.Z - size.Z * 0.5f) + hostHalf + 0.5f;
            await Hold(PlayerInput.ShiftUp, 6.0, () => -(boom.TinesFrame * new Vector3(0f, 0f, -Pallets.LoadAhead)).Z > clear);
            await Hold(PlayerInput.ArmBoomUp, 6.0, () => boom.ForkHeight > shape.Floor.Y + 0.8f);
            // given its pallet up there: lowered tines with one on them would set it down at once
            boom.Carrying = carrying;
            held = () => boom.Carrying;
            heldUp = () => boom.ForkHeight;
            goes = () => me.GlobalTransform * (boom.TinesFrame * new Vector3(0f, 0f, -Pallets.LoadAhead));
            lower = () => Hold(PlayerInput.ArmBoomDown, 10.0, () => boom.Carrying == 0);
            how = "lowered";
        }
        await Frames(10);

        // ---- the parked body, empty, square across the machine, its floor under where the pallet goes --
        float hostYaw = me.GlobalRotation.Y + Mathf.Pi * 0.5f;
        var spot = goes() - new Basis(Vector3.Up, hostYaw) * shape.Floor;
        spot.Y = me.GlobalPosition.Y;
        vehicles.Park(new VehicleState(Host, _origin.ToGlobal(spot), hostYaw, Vector3.Zero, 400f, false, false, 0f, VehicleState.Now, Load: 0f));
        Expect(await Until(() => Parked() is { Visual: not null } || Parked() != null && DisplayServer.GetName() == "headless", 5), $"a {Host} parked under the pallet");
        await Seconds(2);
        if (Parked() is not { Ride: IBed { HasBed: true } bed } host) { Finish(); return; }
        Expect(host.BedLoad == 0, "its body is empty");
        var floor = host.GlobalTransform * bed.Bed.Floor;
        var over = host.GlobalTransform.AffineInverse() * goes();
        float floorUp = floor.Y - me.GlobalPosition.Y;
        Expect(bed.Bed.Over(over) && heldUp() > floorUp && Pallets.LoadCarried(held()) == Load,
            $"the pallet is held over the body ({over.X - bed.Bed.Floor.X:F2} across, {over.Z - bed.Bed.Floor.Z:F2} along its floor's middle, {heldUp() - floorUp:F2} m above it)");
        await Shot("over", me.GlobalPosition + me.GlobalTransform.Basis.X * 9f + Vector3.Up * 5f, floor);

        // ---- lowered or dumped: into the body ----------------------------------------------------------
        await lower();
        await Frames(10);
        Expect(held() == 0 && host.BedLoad != 0 && Pallets.LoadCarried(host.BedLoad) == Load && _pallets.Loose.Count == 0,
            $"{how}, it goes into the body: the machine empty, the body's load {host.BedLoad}, nothing on the ground ({_pallets.Loose.Count} loose)");
        // runners along the machine's way, so across the body it stands square to
        int inBody = host.BedLoad;
        Expect(Pallets.CarriedAcross(inBody), "it lies across the body, as it went into it");
        Expect(heldUp() > floorUp - 0.5f, $"it went in from above the floor, {heldUp():F2} m up");
        await Frames(5);
        var drawn = host.Visual?.FindChild("BedPallet", true, false) as Node3D;
        // headless, a parked truck is drawn as a bare node: nothing to look for
        Expect(drawn != null || DisplayServer.GetName() == "headless", "it is drawn in the body");
        await Shot("in-body", floor - me.GlobalTransform.Basis.Z * 5f + host.GlobalTransform.Basis.Z * 3f + Vector3.Up * 6f, floor);

        // ---- got into: it holds it, and carries it off ------------------------------------------------
        // taken over straight from the machine's seat (getting off waits for terrain the flat
        // world has not got), and put down by hand as the probe was
        me.RideControls = null;
        bool took = false;
        vehicles.Claim(host, state => { me.TakeVehicle(state, 0); took = true; });
        await Until(() => took, 5);
        me.DebugLaunch(me.GlobalPosition, Vector3.Zero);
        await Seconds(1);
        Expect(took && me.Vehicle is IBed { HasBed: true } taken && taken.BedLoad == inBody, $"got into, its body still holds the pallet ({(me.Vehicle as IBed)?.BedLoad})");
        if (me.Vehicle is not IBed carrier) { Finish(); return; }
        if (!me.EngineOn) await Tap(PlayerInput.EngineToggle);
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(Dumper ? 4 : 3);
        me.RideControls = () => new RideInput(0f, Dumper ? 0f : 1f, 0f, false);
        await Seconds(4);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);
        float went = new Vector2(me.GlobalPosition.X - start.X, me.GlobalPosition.Z - start.Z).Length();
        Expect(went > 3f && carrier.BedLoad == inBody, $"driven {went:F1} m, the pallet rides along");

        // ---- tipped: it slides out and is set down past the open end ----------------------------------
        await Tap(PlayerInput.Destination);
        Expect(carrier.BedUp, "stopped, the destination binding tips the body");
        var rig = me.Visual as HeavyRig;
        if (rig != null)
        {
            Expect(await Until(() => rig.BedSlid > 0.3f, 5), $"as it rises the pallet slides toward the open end ({rig.BedSlid:F2})");
            await Shot("sliding", me.GlobalTransform * (carrier.Bed.Spill + new Vector3(7f, 5f, 4f)), me.GlobalTransform * carrier.Bed.Hinge);
        }
        var spill = me.GlobalTransform * carrier.Bed.Spill;
        Expect(await Until(() => carrier.BedLoad == 0, 8), "at the edge it leaves the body");
        await Frames(10);
        var loose = _pallets.Loose.Values.FirstOrDefault();
        var down = loose == null ? null : PalletNode.All.GetValueOrDefault(Pallets.LooseId(loose.Id));
        float off = down == null ? float.NaN : new Vector2(down.GlobalPosition.X - spill.X, down.GlobalPosition.Z - spill.Z).Length();
        Expect(_pallets.Loose.Count == 1 && loose?.Load == Load && off < 0.3f,
            $"set down past the open end with its load ({_pallets.Loose.Count} loose, {off:F2} m from where it falls)");
        Expect(rig == null || rig.FindChild("BedPallet", true, false) == null, "and the body is drawn empty");
        await Shot("tipped-out", me.GlobalTransform * (carrier.Bed.Spill + new Vector3(7f, 4f, 5f)), spill);

        // ---- the flags and the pose keep any pallet ----------------------------------------------------
        bool all = true;
        foreach (int l in new[] { 0, 1, 127, 128, 255 })
            foreach (bool across in new[] { false, true })
            {
                int c = Pallets.Carried((byte)l, across);
                int flags = carrier.FlagsWithBed(Dumper ? MiniDumperLayout.Pack(true) : 0, c);
                if (Dumper)
                {
                    var d = new MiniDumper();
                    d.UnpackFlags(flags);
                    all &= d.BedLoad == c && d.Tipped;
                    d.BedLoad = c;
                    all &= MiniDumper.BedInPose(d.WritePose(null!, default, default)) == c;
                }
                else
                {
                    all &= Truck.BedInFlags(flags) == c;
                    var t = (Truck)me.Vehicle!;
                    t.BedLoad = c;
                    all &= Truck.BedInFlags(t.PackFlags()) == c;
                }
            }
        Expect(all, "the parked flags and the pose keep every load byte and which way it sits");
        me.RideControls = null;
        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? $"[bed] RESULT: ok — a pallet set into the {(Dumper ? "mini dumper's skip" : "tipper's body")}, carried off and tipped out"
            : $"[bed] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
