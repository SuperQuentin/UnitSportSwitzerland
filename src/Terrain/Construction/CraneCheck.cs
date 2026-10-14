using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.XR;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>--cranecheck [shots] --world flat --systems physics,ui</c> (#618, tier 1): a building site's
/// tower crane worked by a player, offline, on the real bindings. A hand-made site (a block two
/// storeys into four, so it has a tower crane) is planned, shelled and craned by the game's own
/// builders under the player, its collision built as a tile builds it.
/// <list type="bullet">
/// <item>E at the ladder's foot: on the ladder; up it to the cab, where the controls are granted;</item>
/// <item>a pallet set down in the yard; the crane slewed, the trolley run and the hook lowered over
/// it on the levers; <c>couple</c> hooks it on, and it waits on the ground until the slings are taut;</item>
/// <item>hoisted, slewed over the top slab, lowered until it stands, and set down there with its load;</item>
/// <item>the hook camera on and off;</item>
/// <item>let go: it holds where it was left, then eases back into its schedule with no jump (the
/// simulation clock run fast), and the hold is forgotten;</item>
/// <item>down the ladder onto the ground, then the radio remote at the mast's foot: the crane slews
/// from the ground, and is let go.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/crane/*.png</c>.
/// </summary>
public partial class CraneCheck : Node
{
    public static bool Requested => CmdArgs.Has("--cranecheck");
    private static bool Shots => (CmdArgs.Value("--cranecheck") ?? "").Split(',').Contains("shots");

    /// <summary>The pallet's load: a narrow deck of cement sacks.</summary>
    private const byte Load = 100;

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private SiteCranes.Live? _crane;
    private ConstructionSite? _site;
    private ShellPlan? _shell;
    private Node3D? _tile;
    private int _failures, _shot;
    private bool _started;

    public CraneCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[crane] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Until(Func<bool> done, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!done() && GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
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
        string dir = ProjectSettings.GlobalizePath("res://test_output/crane");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[crane] wrote {path}");
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
        if (PalletService.Instance == null) PalletService.Create(this, _origin, server: false);
        if (CraneService.Instance == null) CraneService.Create(this, server: false);
        if (!BuildSite(new Vector3(at.X, ground, at.Z)))
        {
            Finish();
            return;
        }
        _player = new FootPlayer { Name = "Probe", Origin = _origin };
        AddChild(_player);
        _player.GlobalPosition = _crane!.LadderFoot + Vector3.Up * 1f;
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = Run();
    }

    /// <summary>
    /// A block of four floors with two built, its tile's frame put so that the site stands round
    /// <paramref name="at"/>: the shell and yard collide as a tile's would, and the crane is drawn.
    /// </summary>
    private bool BuildSite(Vector3 at)
    {
        var centre = new Vector2(500f, 500f);
        var tile = new BuildingTile { Id = new TileId(2600, 1200), Buildings = new List<Building> { Prism(centre, 24f, 15f, 6.3f, 4) } };
        var sites = SitePlans.For(tile, null);
        Expect(sites.Count == 1 && sites[0].Cranes.Count > 0 && sites[0].Cranes[0].Kind == CraneKind.Tower,
            $"a hand-made site with a tower crane ({sites.Count} site, {(sites.Count > 0 ? sites[0].Cranes.Count : 0)} crane)");
        if (sites.Count != 1 || sites[0].Cranes.Count == 0 || sites[0].Cranes[0].Kind != CraneKind.Tower) return false;
        _site = sites[0];
        _shell = SiteShellBuilder.Shell(tile, _site, null);
        if (SiteShellBuilder.Build(tile, sites, null, mesh: true, collision: true) is not { } built || built.Mesh == null) return false;
        var material = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        // tile-local (500, 0, 500) is the site's middle: put it where the player is
        _tile = new Node3D { Name = "Tile", Position = new Vector3(at.X - centre.X, at.Y, at.Z - centre.Y) };
        AddChild(_tile);
        var body = new StaticBody3D { Name = "Site" };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = built.Faces, BackfaceCollision = true } });
        _tile.AddChild(body);
        _tile.AddChild(new MeshInstance3D { Name = "SiteMesh", Mesh = ChunkNode.ToPropMesh(built.Mesh, material) });
        _tile.AddChild(new SiteCranes(built.Cranes.Select(c => CraneRig.Make(c, material)).ToArray(), material));
        _crane = SiteCranes.All.GetValueOrDefault(built.Cranes[0].Job.Key);
        Expect(_crane != null, $"the crane {built.Cranes[0].Job.Key} is in the tree, its jib {built.Cranes[0].Spot.JibLength:F0} m");
        return _crane != null;
    }

    /// <summary>A flat-topped prism, as swissBUILDINGS3D draws an <c>Im Bau</c> volume.</summary>
    private static Building Prism(Vector2 c, float w, float d, float h, byte floors)
    {
        var corners = new[] { c + new Vector2(-w / 2, -d / 2), c + new Vector2(w / 2, -d / 2), c + new Vector2(w / 2, d / 2), c + new Vector2(-w / 2, d / 2) };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(P(a, 0), P(b, 0), P(b, h));
            Tri(P(a, 0), P(b, h), P(a, h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    private CraneService.Hold? Hold => CraneService.Instance?.MyHold;

    /// <summary>The hook over <paramref name="target"/> on the real levers and lowered onto it (<see cref="CraneDriver"/>).</summary>
    private async Task SteerTo(Vector3 target, string what)
    {
        var crane = _crane!;
        float off = await CraneDriver.SteerTo(this, crane, target);
        Expect(off < 0.4f, $"{what}: the hook was over it ({off:F2} m off, yaw {crane.Last.Yaw:F3}, trolley {crane.Last.Trolley:F2})");
    }

    private async Task Run()
    {
        var me = _player!;
        var crane = _crane!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await Frames(1);
        await Shot("site", crane.Foot + new Vector3(30f, 18f, 30f), crane.Foot + Vector3.Up * 8f);

        // ---- up the ladder into the cab ------------------------------------------------------------
        Expect(me.CranePrompt()?.Contains("Climb") == true, $"at the ladder's foot, the prompt: {me.CranePrompt()}");
        Expect(me.TryInteract() && me.OnCraneLadder, "E puts the player on the ladder");
        me.ForceCraneClimb = 1f;
        float top = CranePlans.LadderTop(crane.Data.Spot);
        double t0 = GameClock.Now;
        await Until(() => me.CraneWorking, top / 2.2f + 5f);
        me.ForceCraneClimb = null;
        Expect(me.CraneInCab && CraneService.Instance?.Mine == crane.Key,
            $"climbed {top:F1} m in {GameClock.Now - t0:F1} s, into the cab with the controls");
        await Frames(10);
        var (cab, _) = crane.InCab(crane.Last);
        Expect(me.GlobalPosition.DistanceTo(cab) < 0.3f, $"the operator stands in the cab ({me.GlobalPosition.DistanceTo(cab):F2} m off)");

        // ---- a pallet in the yard -------------------------------------------------------------------
        var spot = ClearGround(crane);
        Expect(spot != null, "clear ground in the yard within the jib's reach");
        if (spot is not { } ground) { Finish(); return; }
        bool put = false;
        PalletService.Instance!.RequestDrop(Load, _origin.ToGlobal(ground), crane.Last.Yaw, ok => put = ok);
        await Frames(5);
        var pallet = PalletNode.All.Values.FirstOrDefault(p => !p.Taken && p.Load == Load);
        Expect(put && pallet != null, $"a pallet of {Pallets.Goods(Load)} stands in the yard");
        if (pallet == null) { Finish(); return; }

        // ---- over it, down, hooked on ---------------------------------------------------------------
        double slewed = crane.Last.Yaw;
        await SteerTo(pallet.GlobalPosition, "the pallet");
        Expect(Mathf.Abs(Mathf.AngleDifference((float)slewed, crane.Last.Yaw)) > 0.05f, $"the levers slewed the jib ({slewed:F2} -> {crane.Last.Yaw:F2})");
        var hookAt = crane.HookWorld(crane.Last);
        float gap = hookAt.Y - CraneControl.HookBottom - (pallet.GlobalPosition.Y + Pallets.Height);
        Expect(gap is > -0.05f and < 0.1f, $"the hook comes down onto the pallet's top and stops ({gap:F2} m over it)");
        Expect(me.HookAction().Take == pallet.Id, $"the hook can take it ({me.HookAction().Take ?? "nothing"})");
        await Shot("hook-down", hookAt + new Vector3(5f, 3f, 5f), hookAt);
        await Tap(PlayerInput.Couple);
        await Until(() => Hold?.Carrying != 0, 3);
        Expect(Pallets.LoadCarried(Hold?.Carrying ?? 0) == Load && PalletService.Instance.Loose.Count == 0,
            $"couple hooks it on: its load on the hook ({Hold?.Carrying}), gone from the ground");
        await Frames(5);
        var hung = crane.Pivot.GetNodeOrNull<Node3D>("HookPallet");
        Expect(hung != null && Mathf.Abs(hung.GlobalPosition.Y - Pallets.Seat - ground.Y) < 0.05f,
            $"drawn in its slings, still on the ground while they are slack ({(hung == null ? "none" : (hung.GlobalPosition.Y - Pallets.Seat - ground.Y).ToString("F2"))} m up)");

        // ---- lifted, carried over the top slab, set down --------------------------------------------
        XrPad.Press(PlayerInput.ArmBoomUp, true);
        await Until(() => hung != null && hung.GlobalPosition.Y - Pallets.Seat > ground.Y + 3f, 10);
        XrPad.Press(PlayerInput.ArmBoomUp, false);
        Expect(hung != null && hung.GlobalPosition.Y - Pallets.Seat > ground.Y + 2.9f && float.IsNaN(Hold?.Rest ?? 0),
            $"hoisted, it hangs a sling's length under the hook ({(hung == null ? 0 : hung.GlobalPosition.Y - Pallets.Seat - ground.Y):F1} m up)");
        float slab = _shell!.Levels[^1];
        var drop = crane.ToWorld(CraneDriver.DropOnSlab(crane) ?? crane.Job.Drops[0]);
        await SteerTo(drop, "the top slab");
        await Shot("over-the-slab", drop + new Vector3(8f, 6f, 8f), drop);
        Expect(me.HookAction().SetAt is { } setAt && Mathf.Abs(setAt.Y - crane.ToWorld(new Vector3(0, slab, 0)).Y) < 0.2f,
            $"lowered until it stands, it can be set down on the slab ({me.HookAction().SetAt?.Y:F2} at slab {crane.ToWorld(new Vector3(0, slab, 0)).Y:F2})");
        await Tap(PlayerInput.Couple);
        await Until(() => Hold?.Carrying == 0, 3);
        var set = PalletService.Instance.Loose.Values.FirstOrDefault();
        var setNode = set == null ? null : PalletNode.All.GetValueOrDefault(Pallets.LooseId(set.Id));
        Expect(Hold?.Carrying == 0 && set?.Load == Load && setNode != null
               && Mathf.Abs(setNode.GlobalPosition.Y - crane.ToWorld(new Vector3(0, slab, 0)).Y) < 0.2f,
            $"couple sets it down on the top slab with its load ({(setNode == null ? "none" : setNode.GlobalPosition.Y.ToString("F2"))})");
        await Shot("on-the-slab", drop + new Vector3(6f, 5f, -6f), drop);

        // ---- the hook camera --------------------------------------------------------------------------
        await Tap(PlayerInput.CameraToggle);
        Expect(me.CraneHookView && (crane.Camera?.Current == true || XrSession.Active), "the view toggle looks through the trolley's camera");
        await Tap(PlayerInput.CameraToggle);
        Expect(!me.CraneHookView && crane.Camera?.Current != true, "and back to the player's own");

        // ---- let go: it holds, then eases back into its schedule -------------------------------------
        var left = crane.Last;
        Expect(me.TryInteract() && me.OnCraneLadder && CraneService.Instance!.Mine == null, "E lets go of the controls, back on the ladder's top");
        var hold = CraneService.Instance.Holds.GetValueOrDefault(crane.Key);
        Expect(hold is { Operator: 0 } && !double.IsNaN(hold.LeftAt), "the crane is let go, stamped");
        await Frames(30);
        Expect(Mathf.Abs(Mathf.AngleDifference(left.Yaw, crane.Last.Yaw)) < 0.001f && Mathf.Abs(left.HookY - crane.Last.HookY) < 0.01f,
            "it holds where it was left");
        // the simulation clock run fast: to the next cycle and through the ease
        double sim = SimClock.SimAt(Net.ClockSync.ServerNow);
        double next = CraneMotion.NextCycle(crane.Key, hold!.LeftAt);
        SimClock.Rebase(Net.ClockSync.ServerNow, 20);
        var last = crane.Last;
        float worstYaw = 0, worstHook = 0;
        while (SimClock.SimAt(Net.ClockSync.ServerNow) < next + CraneMotion.Cycle * CraneMotion.ResumeShare + 3 && GameClock.Now - t0 < 600)
        {
            await Frames(1);
            worstYaw = Mathf.Max(worstYaw, Mathf.Abs(Mathf.AngleDifference(last.Yaw, crane.Last.Yaw)));
            worstHook = Mathf.Max(worstHook, Mathf.Abs(last.HookY - crane.Last.HookY));
            last = crane.Last;
        }
        SimClock.Rebase(Net.ClockSync.ServerNow, SimClock.Normal);
        double nowSim = SimClock.SimAt(Net.ClockSync.ServerNow);
        double hour = World.WorldClock.CurrentHour;
        long day = (long)Math.Floor((World.WorldClock.EnvNow + World.WorldClock.HourShift) / 86400.0);
        await Frames(2);
        var scheduled = CraneMotion.Pose(crane.Job, SimClock.SimAt(Net.ClockSync.ServerNow), hour, day);
        // 20x a 60 fps frame: a third of a simulated second a frame
        Expect(worstYaw < 0.12f && worstHook < 1.5f, $"from {next - sim:F0} simulated s ahead, it eased back with no jump (worst a frame at 20x: {worstYaw:F3} rad, {worstHook:F2} m)");
        Expect(!CraneService.Instance.Holds.ContainsKey(crane.Key) && Mathf.Abs(Mathf.AngleDifference(scheduled.Yaw, crane.Last.Yaw)) < 0.02f,
            $"and is back on its schedule, the hold forgotten (sim {nowSim:F0})");

        // ---- down the ladder; the remote from the ground ---------------------------------------------
        me.ForceCraneClimb = -1f;
        await Until(() => !me.OnCraneLadder, top / 2.2f + 5f);
        me.ForceCraneClimb = null;
        await Frames(30);
        Expect(!me.OnCraneLadder && me.CraneKey == null && me.GlobalPosition.DistanceTo(crane.LadderFoot) < 1.5f,
            $"down the ladder, standing at its foot ({me.GlobalPosition.DistanceTo(crane.LadderFoot):F2} m off)");
        me.GlobalPosition = crane.AtRemote.At + Vector3.Up * 0.5f;
        await Frames(30);
        Expect(me.CranePrompt()?.Contains("remote") == true, $"by the remote's box, the prompt: {me.CranePrompt()}");
        Expect(me.TryInteract(), "E at the box");
        await Until(() => me.CraneWorking, 2);
        Expect(me.CraneWorking && !me.CraneInCab, "the remote's controls are the player's, on the ground");
        float before = crane.Last.Yaw;
        XrPad.Press(PlayerInput.ArmSlewRight, true);
        await Until(() => false, 2);
        XrPad.Press(PlayerInput.ArmSlewRight, false);
        await Frames(10);
        Expect(Mathf.AngleDifference(before, crane.Last.Yaw) < -0.1f, $"slewed right from the ground ({before:F2} -> {crane.Last.Yaw:F2})");
        Expect(me.TryInteract() && !me.CraneWorking && CraneService.Instance.Mine == null, "E hangs the remote back up and lets go");
        await Frames(10);
        Expect(me.GlobalPosition.DistanceTo(crane.AtRemote.At) < 1f, "standing by the box");
        Finish();
    }

    /// <summary>A spot in the jib's reach whose ground is clear for a pallet: nothing above the flat world within its corners.</summary>
    private static Vector3? ClearGround(SiteCranes.Live crane)
    {
        var foot = crane.Foot;
        for (float r = 10f; r < crane.Job.JibLength - 3f; r += 2f)
            for (int k = 0; k < 24; k++)
            {
                float a = k * Mathf.Tau / 24;
                var at = foot + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * r;
                bool clear = true;
                foreach (var (dx, dz) in new[] { (0f, 0f), (-0.9f, -0.9f), (0.9f, -0.9f), (0.9f, 0.9f), (-0.9f, 0.9f) })
                    clear &= crane.FloorUnder(at + new Vector3(dx, 40f, dz)) < foot.Y + 0.05f;
                if (clear) return at with { Y = crane.FloorUnder(at + Vector3.Up * 40f) };
            }
        return null;
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[crane] RESULT: ok — climbed, worked, hooked a pallet onto the top slab, let go and resumed, and the remote"
            : $"[crane] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
