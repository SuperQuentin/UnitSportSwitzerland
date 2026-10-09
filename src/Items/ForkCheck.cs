using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--forkcheck telehandler|loaderforks|loaderbucket[,shots] --world flat --systems physics,ui</c> (#615, tier
/// 1): a pallet lifted, carried and set down by a machine whose forks are on an arm, not a mast —
/// the telehandler and the wheel loader with forks — through the same rule and the same service as
/// the forklift's (<see cref="IForks"/>, <see cref="PalletService.Tend"/>), offline, on the real
/// bindings. A loose pallet is set down in the open ahead of the machine, so no hall is needed.
/// <list type="bullet">
/// <item>arm down, the tines are under <see cref="ForkliftLayout.ForkEntry"/>; driven in at square,
/// the pallet is on them;</item>
/// <item>the arm lifted (the real <c>arm_boom_up</c>, in work mode): the pallet is taken, its load
/// byte on the forks, the loose one gone, drawn on the carriage;</item>
/// <item>lifted high (and on the telehandler run out) and backed away, it rides along;</item>
/// <item>lowered, it is set down where it rode, with its load, and the forks are empty;</item>
/// <item>the parked flags keep what is on the forks.</item>
/// </list>
/// <c>loaderbucket</c>: the wheel loader's bucket instead (<see cref="IBucket"/>): down and level,
/// driven in until the pallet is in it, curled back and the arm lifted (it is scooped up), carried
/// off, dumped (it is tipped out under the lip), and the parked flags keep what is in the bucket.
/// Windowed with <c>shots</c>: <c>test_output/forks/*.png</c>.
/// </summary>
public partial class ForkCheck : Node
{
    public static bool Requested => CmdArgs.Has("--forkcheck");
    private static string[] Args => (CmdArgs.Value("--forkcheck") ?? "").Split(',');
    private static bool Shots => Args.Contains("shots");
    private static RideKind Machine => Args.Contains("loaderforks") ? RideKind.WheelLoaderForks
        : Args.Contains("loaderbucket") ? RideKind.WheelLoader : RideKind.Telehandler;

    /// <summary>The pallet's load: a square deck of drums, so a wrong deck or goods shows.</summary>
    private const byte Load = 0x80 | 70;

    private readonly WorldOrigin _origin;
    private PalletService _pallets = null!;
    private FootPlayer? _player;
    private int _failures;
    private int _shot;
    private bool _started;

    public ForkCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[forks] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    // No handbrake anywhere here: on these machines the brake reverses from a standstill, so a held
    // one backs them away. Let go, their drives stop them.

    /// <summary>Holds a binding until <paramref name="done"/> or <paramref name="seconds"/> of game time.</summary>
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/forks");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{Machine}-{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[forks] wrote {path}");
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
        _player = new FootPlayer { Name = "Probe" };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + 1.0f, at.Z);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = Run();
    }

    /// <summary>A pallet's centre in the tines' frame: across them, and ahead of their heel.</summary>
    private static (float Across, float Ahead) OnTines(FootPlayer me, IForks forks, Node3D pallet)
    {
        var local = (me.GlobalTransform * forks.TinesFrame).AffineInverse() * pallet.GlobalPosition;
        return (local.X, -local.Z);
    }

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (Machine == RideKind.WheelLoader) { await RunBucket(me); Finish(); return; }
        Expect(me.SetRide(Machine) && me.Vehicle is IForks { HasTines: true }, $"mounted a {Machine}");
        if (me.Vehicle is not IForks forks) { Finish(); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(20);
        Expect(forks.ForkHeight < ForkliftLayout.ForkEntry && forks.ForkHeight >= 0f,
            $"arm down, the tines are low enough to go in under a deck ({forks.ForkHeight:F3} m)");

        // a loose pallet in the open, three metres past where the tines would hold it, runners along the way in
        var ahead = -me.GlobalTransform.Basis.Z with { Y = 0 };
        ahead = ahead.Normalized();
        // ahead of the tines, which may be off the machine's middle (the telehandler's boom is)
        var heelAt = me.GlobalTransform * forks.TinesFrame.Origin;
        var spot = heelAt + ahead * (Pallets.LoadAhead + 3f);
        spot.Y = me.GlobalPosition.Y;
        float yaw = me.GlobalRotation.Y + Mathf.Pi * 0.5f;
        bool put = false;
        _pallets.RequestDrop(Load, _origin.ToGlobal(spot), yaw, ok => put = ok);
        await Frames(5);
        var pallet = PalletNode.All.Values.FirstOrDefault(p => !p.Taken && p.Load == Load);
        Expect(put && pallet != null, $"a pallet of {Pallets.Goods(Load)} stands ahead ({_pallets.Loose.Count} loose)");
        if (pallet == null) { Finish(); return; }
        await Shot("approach", me.GlobalPosition + new Vector3(6f, 3f, -3f), spot + Vector3.Up * 0.5f);

        // work mode, then in at a crawl until the pallet sits where the tines carry one
        await Tap(PlayerInput.DigMode);
        me.RideControls = () => new RideInput(0.25f, 0f, 0f, false);
        double t0 = GameClock.Now;
        while (OnTines(me, forks, pallet).Ahead > Pallets.LoadAhead + 0.05f && GameClock.Now - t0 < 15)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        for (int i = 0; i < 240 && me.GroundSpeed > 0.05f; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var (across, inAhead) = OnTines(me, forks, pallet);
        Expect(Mathf.Abs(across) <= forks.TineHalfSpan && inAhead >= 0f && inAhead <= forks.TineLength,
            $"driven in, the pallet is on the tines ({across:F2} across, {inAhead:F2} ahead of their heel)");
        await Shot("forks-in", me.GlobalPosition + new Vector3(5f, 2.5f, 1f), spot + Vector3.Up * 0.5f);

        // ---- lifted: taken ------------------------------------------------------------------------
        await Hold(PlayerInput.ArmBoomUp, 3.0, () => forks.Carrying != 0);
        await Frames(10);
        Expect(Pallets.LoadCarried(forks.Carrying) == Load && !Pallets.CarriedAcross(forks.Carrying),
            $"lifting the arm takes it: its load byte on the forks, along its runners ({forks.Carrying})");
        Expect(_pallets.Loose.Count == 0 && (!IsInstanceValid(pallet) || pallet.IsQueuedForDeletion()), "and the loose pallet is gone from the ground");
        Node3D? drawn = me.Vehicle is Telehandler ? TelehandlerMeshBuilder.BoomOf(me.Visual)?.Load : WheelLoaderMeshBuilder.FrontOf(me.Visual)?.Load;
        Expect(drawn != null || DisplayServer.GetName() == "headless" && me.Visual == null, "it is drawn on the carriage");

        // ---- carried: up, out and away --------------------------------------------------------------
        await Hold(PlayerInput.ArmBoomUp, 2.0);
        if (me.Vehicle is Telehandler) await Hold(PlayerInput.ShiftUp, 1.5);
        float high = forks.ForkHeight;
        if (drawn?.GetNodeOrNull<Node3D>("Mesh") is { } mesh)
            Expect(Mathf.Abs(mesh.GlobalPosition.Y - me.GlobalPosition.Y - (high - Pallets.Seat)) < 0.06f,
                $"it rides up with the forks, {Pallets.Seat:F2} m under their top face ({mesh.GlobalPosition.Y - me.GlobalPosition.Y:F2} m up at {high:F2} m)");
        await Shot("lifted", me.GlobalPosition + new Vector3(9f, 4f, 4f), me.GlobalPosition + Vector3.Up * (high * 0.5f + 1f));
        var before = me.GlobalPosition;
        me.RideControls = () => new RideInput(0f, 0.5f, 0f, false);
        t0 = GameClock.Now;
        while ((me.GlobalPosition - before).Length() < 3f && GameClock.Now - t0 < 10) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        for (int i = 0; i < 240 && me.GroundSpeed > 0.05f; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect((me.GlobalPosition - before).Length() > 2.5f && Pallets.LoadCarried(forks.Carrying) == Load,
            $"backed away {(me.GlobalPosition - before).Length():F1} m with it on the forks");

        // ---- lowered: set down where it rode ----------------------------------------------------------
        if (me.Vehicle is Telehandler) await Hold(PlayerInput.ShiftDown, 3.0);
        await Hold(PlayerInput.ArmBoomDown, 12.0, () => forks.Carrying == 0);
        await Frames(10);
        var set = _pallets.Loose.Values.FirstOrDefault();
        var expected = me.GlobalTransform * forks.TinesFrame * new Vector3(0f, 0f, -Pallets.LoadAhead);
        var down = set == null ? null : PalletNode.All.GetValueOrDefault(Pallets.LooseId(set.Id));
        Expect(forks.Carrying == 0 && _pallets.Loose.Count == 1 && set?.Load == Load,
            $"lowered, it is set down: empty forks, one loose pallet with its load ({_pallets.Loose.Count})");
        Expect(down != null && new Vector2(down.GlobalPosition.X - expected.X, down.GlobalPosition.Z - expected.Z).Length() < 0.15f
               && Mathf.Abs(down.GlobalPosition.Y - me.GlobalPosition.Y) < 0.05f,
            $"where it rode, on the ground ({(down == null ? "none" : new Vector2(down.GlobalPosition.X - expected.X, down.GlobalPosition.Z - expected.Z).Length().ToString("F2"))} m off)");
        await Shot("set-down", me.GlobalPosition + new Vector3(5f, 2.5f, -4f), expected);

        // ---- a parked one keeps any pallet -----------------------------------------------------------
        bool all = true;
        foreach (int l in new[] { 0, 1, 127, 128, 255 })
            foreach (bool acrossIt in new[] { false, true })
            {
                int carrying = Pallets.Carried((byte)l, acrossIt);
                var (from, to) = Machine == RideKind.Telehandler
                    ? ((IForks)new Telehandler { Carrying = carrying }, (IForks)new Telehandler())
                    : (new WheelLoader(forks: true) { Carrying = carrying }, new WheelLoader(forks: true));
                int flags = from is Telehandler tf ? tf.PackFlags() : ((WheelLoader)from).PackFlags();
                if (to is Telehandler tt) tt.UnpackFlags(flags); else ((WheelLoader)to).UnpackFlags(flags);
                all &= to.Carrying == carrying;
            }
        Expect(all, "the parked flags keep every load byte and which way it sits");

        Finish();
    }

    /// <summary>The wheel loader's bucket: scooped up by curling it back and lifting, tipped out by dumping it.</summary>
    private async Task RunBucket(FootPlayer me)
    {
        Expect(me.SetRide(RideKind.WheelLoader) && me.Vehicle is WheelLoader { HasBucket: true }, "mounted a wheel loader with its bucket");
        if (me.Vehicle is not WheelLoader loader) return;
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(20);

        // work mode, the arm down and the bucket level on the ground, as one drives into a load
        await Tap(PlayerInput.DigMode);
        await Hold(PlayerInput.ArmBoomDown, (loader.Lift - WheelLoaderLayout.LiftMin) / WheelLoaderLayout.LiftRate + 0.3);
        float level = -loader.Lift - loader.Tilt;
        await Hold(level > 0 ? PlayerInput.ArmBucketCurl : PlayerInput.ArmBucketDump, Mathf.Abs(level) / WheelLoaderLayout.TiltRate);
        var floor = me.GlobalTransform * loader.BucketFrame * loader.BucketFloor;
        Expect(Mathf.Abs(loader.BucketPitch) < 0.05f && floor.Y - me.GlobalPosition.Y < Pallets.ScoopHeight,
            $"the bucket is down and level ({loader.BucketPitch:F2} rad, its floor {floor.Y - me.GlobalPosition.Y:F2} m up)");

        // a loose pallet three metres past the bucket's floor, runners along the way in
        var ahead = (-me.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        var spot = floor + ahead * 3f;
        spot.Y = me.GlobalPosition.Y;
        bool put = false;
        _pallets.RequestDrop(Load, _origin.ToGlobal(spot), me.GlobalRotation.Y + Mathf.Pi * 0.5f, ok => put = ok);
        await Frames(5);
        var pallet = PalletNode.All.Values.FirstOrDefault(p => !p.Taken && p.Load == Load);
        Expect(put && pallet != null, $"a pallet of {Pallets.Goods(Load)} stands ahead");
        if (pallet == null) return;

        // in at a crawl until the pallet's middle is over the bucket's floor
        float InIt() => -((me.GlobalTransform * loader.BucketFrame).AffineInverse() * pallet.GlobalPosition).Z;
        me.RideControls = () => new RideInput(0.25f, 0f, 0f, false);
        double t0 = GameClock.Now;
        while (InIt() > WheelLoaderLayout.BucketFloorZ + 0.1f && GameClock.Now - t0 < 15) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        for (int i = 0; i < 240 && me.GroundSpeed > 0.05f; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(InIt() < WheelLoaderLayout.BucketReach + 0.5f, $"driven in, the pallet is in the bucket ({InIt():F2} m ahead of the pin)");
        await Shot("in-the-bucket", me.GlobalPosition + new Vector3(5f, 2.5f, 2f), spot + Vector3.Up * 0.5f);

        // curled back: still on the ground, not yet held; then lifting rolls it back past CurlCarry
        await Hold(PlayerInput.ArmBucketCurl, (WheelLoaderLayout.TiltMax - loader.Tilt) / WheelLoaderLayout.TiltRate + 0.2);
        Expect(loader.Carrying == 0, $"curled back on the ground ({loader.BucketPitch:F2} rad), not yet carried");
        await Hold(PlayerInput.ArmBoomUp, 3.0, () => loader.Carrying != 0);
        await Frames(10);
        Expect(Pallets.LoadCarried(loader.Carrying) == Load && _pallets.Loose.Count == 0,
            $"curled and lifted ({loader.BucketPitch:F2} rad), it is scooped up: its load in the bucket, gone from the ground");
        Expect(WheelLoaderMeshBuilder.FrontOf(me.Visual)?.Load != null || me.Visual == null, "it is drawn in the bucket");

        // carried off: up and back
        await Hold(PlayerInput.ArmBoomUp, 1.5);
        await Shot("carried", me.GlobalPosition + new Vector3(8f, 3.5f, 5f), me.GlobalPosition + Vector3.Up * 2f);
        var before = me.GlobalPosition;
        me.RideControls = () => new RideInput(0f, 0.5f, 0f, false);
        t0 = GameClock.Now;
        while ((me.GlobalPosition - before).Length() < 3f && GameClock.Now - t0 < 10) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        for (int i = 0; i < 240 && me.GroundSpeed > 0.05f; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(Pallets.LoadCarried(loader.Carrying) == Load, $"backed away {(me.GlobalPosition - before).Length():F1} m with it in the bucket");

        // dumped: tipped out under the lip
        await Hold(PlayerInput.ArmBucketDump, 4.0, () => loader.Carrying == 0);
        await Frames(10);
        var set = _pallets.Loose.Values.FirstOrDefault();
        var expected = me.GlobalTransform * loader.BucketFrame
            * new Vector3(0f, loader.BucketFloor.Y, -(WheelLoaderLayout.BucketReach + Pallets.Length * 0.5f));
        var down = set == null ? null : PalletNode.All.GetValueOrDefault(Pallets.LooseId(set.Id));
        float off = down == null ? float.NaN : new Vector2(down.GlobalPosition.X - expected.X, down.GlobalPosition.Z - expected.Z).Length();
        Expect(loader.Carrying == 0 && set?.Load == Load && down != null && off < 0.4f && Mathf.Abs(down.GlobalPosition.Y - me.GlobalPosition.Y) < 0.05f,
            $"dumped ({loader.BucketPitch:F2} rad), it is tipped out under the lip with its load, on the ground ({off:F2} m off)");
        await Shot("dumped", me.GlobalPosition + new Vector3(5f, 2.5f, -4f), expected);

        // a parked one keeps what is in the bucket
        bool all = true;
        foreach (int l in new[] { 0, 1, 127, 128, 255 })
            foreach (bool across in new[] { false, true })
            {
                int carrying = Pallets.Carried((byte)l, across);
                var parked = new WheelLoader();
                parked.UnpackFlags(new WheelLoader { Carrying = carrying }.PackFlags());
                all &= parked.Carrying == carrying;
            }
        Expect(all, "the parked flags keep every load byte in the bucket and which way it sits");
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? $"[forks] RESULT: ok — the {Machine} takes, carries and sets down a pallet on the real bindings"
            : $"[forks] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
