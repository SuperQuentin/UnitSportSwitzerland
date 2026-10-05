using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--tractorcheck [shots] --chunks fixture:flat --traffic 0</c> (#494): the farm machines.
/// First the numbers on flat ground with no world (as <see cref="HeavyCheck"/>): the tractor's 0-40
/// and top speed, a lowered plough slowing it to a ploughing pace and a drill hardly, the weight of
/// a raised implement on the rear axle, the combine's 25 km/h and its threshing speed, which
/// coupling takes what, the tank in the trailer's code and the combine's flags. Then in the world,
/// with a stand-in field (<see cref="MachineWork.FakeSweep"/>): the implement lowered and raised on
/// the rig, the plough sweeping, the drill sowing only with seed in the pack and taking it, the
/// mower's bales into the pack, the combine's tank filling, the auger unloading into a parked
/// tipping trailer, a sack taken from it on foot, and the train parked with the implement down.
/// Prints <c>[tractor] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>; <c>shots</c> (windowed) writes
/// pictures to <c>test_output/</c>.
/// </summary>
public partial class TractorCheck : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--tractorcheck");
            return i >= 0 ? (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "") : null;
        }
    }

    private const float Dt = 1f / 60f;
    private readonly System.Func<FootPlayer?> _local;
    private readonly bool _shots;
    private int _failures;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TractorCheck(string role, System.Func<FootPlayer?> local)
    {
        _local = local;
        _shots = role.Contains("shots") && DisplayServer.GetName() != "headless";
        Name = "TractorCheck";
    }

    public override void _Ready()
    {
        MouseCapture.Disabled = true;
        _ = Run();
    }

    private static void Log(string what) => GD.Print($"[tractor] {what}");
    private static string F(float v, string f = "F1") => v.ToString(f, Inv);

    private void Expect(bool ok, string what)
    {
        Log($"{(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) { _failures++; GD.PrintErr($"[tractor] FAIL {what}"); }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (done()) return true;
            await Wait(0.1);
        }
        return done();
    }

    private async Task Shot(string name)
    {
        if (!_shots) return;
        await Wait(0.4);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"494-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"wrote {path}");
    }

    private static HeavySpec Tractor => HeavyCatalog.All.First(h => h.Class == HeavyClass.FarmTractor);
    private static HeavySpec Combine => HeavyCatalog.All.First(h => h.Class == HeavyClass.Combine);
    private static int Index(TrailerBody body) => System.Array.FindIndex(TrailerCatalog.All.ToArray(), t => t.Body == body);

    // ---- the numbers, no world ------------------------------------------------------------------

    /// <summary>Full throttle on flat ground for <paramref name="seconds"/>: the time to <paramref name="kmh"/> and the speed at the end.</summary>
    private static (float To, float Top) Drive(Truck t, float seconds, float kmh, Surface ground = Surface.Asphalt)
    {
        var m = new RideMotion();
        float to = float.NaN;
        for (float time = 0; time < seconds; time += Dt)
        {
            t.Step(new RideInput(1f, 0f, 0f, false), new RideGround(true, 0f, ground), Dt, ref m);
            if (float.IsNaN(to) && m.Speed * 3.6f >= kmh) to = time;
        }
        return (to, m.Speed * 3.6f);
    }

    private void Numbers()
    {
        var settings = GameSettings.Current;
        var was = settings.RideProfile;
        settings.RideProfile = RideProfile.Sim;

        var alone = new Truck(Tractor);
        var (t40, top) = Drive(alone, 60f, 39f);
        Expect(t40 is > 5f and < 40f && top is > 37f and < 41.5f, $"{Tractor.Label}: 0-39 km/h in {F(t40)} s, top {F(top)} km/h (40 km/h tractor), {alone.GearLabel}");

        int plough = Index(TrailerBody.Plough), drill = Index(TrailerBody.SeedDrill), mower = Index(TrailerBody.Mower), tipper = Index(TrailerBody.Tipper);
        var up = new Truck(Tractor, TrailerCatalog.Code(plough, 0f));
        Expect(up.Implement?.Body == TrailerBody.Plough && up.SectionCount == 2, "the plough on the linkage");
        var b0 = up.Train.Bodies[0];
        float rear = b0.StaticLoad[1], front = b0.StaticLoad[0];
        float rearAlone = alone.Train.Bodies[0].StaticLoad[1];
        Expect(rear - rearAlone > 1250f * 9.81f, $"raised, the plough's weight is on the rear axle: {F(rearAlone / 9810f)} -> {F(rear / 9810f)} t, front {F(front / 9810f)} t");
        var (_, raisedTop) = Drive(up, 40f, 99f, Surface.Grass);
        var down = new Truck(Tractor, TrailerCatalog.Code(plough, 0f)) { Lowered = true };
        var (_, ploughTop) = Drive(down, 40f, 99f, Surface.Grass);
        Expect(ploughTop > 3f && ploughTop < 0.5f * raisedTop, $"the plough down slows it to a ploughing pace: {F(ploughTop)} km/h against {F(raisedTop)} raised (draft {F(Truck.DraftOf(TrailerCatalog.All[plough], ploughTop) / 1000f)} kN)");
        Expect(down.Train.Bodies[0].StaticLoad[1] < rear - 1000f * 9.81f, "lowered, its weight is on the soil, not the linkage");
        Expect(Mathf.Abs(down.Articulation[0]) < 0.01f, $"rigid on the linkage: {F(Mathf.RadToDeg(down.Articulation[0]), "F2")}°");
        var drilling = new Truck(Tractor, TrailerCatalog.Code(drill, 0f)) { Lowered = true };
        var (_, drillTop) = Drive(drilling, 40f, 99f, Surface.Grass);
        Expect(drillTop > 0.6f * raisedTop, $"a drill down pulls a little: {F(drillTop)} km/h");
        var mowing = new Truck(Tractor, TrailerCatalog.Code(mower, 0f)) { Lowered = true };
        var (_, mowTop) = Drive(mowing, 40f, 99f, Surface.Grass);
        Expect(mowTop > ploughTop, $"a mower down: {F(mowTop)} km/h");

        // a turn with the plough up: it stays square behind
        var turning = new Truck(Tractor, TrailerCatalog.Code(plough, 0f));
        var tm = new RideMotion();
        float worst = 0f;
        for (int i = 0; i < 600; i++)
        {
            turning.Step(new RideInput(0.4f, 0f, 1f, false), new RideGround(true, 0f, Surface.Asphalt), Dt, ref tm);
            worst = Mathf.Max(worst, Mathf.Abs(turning.Articulation[0]));
        }
        Expect(worst < 0.01f && float.IsFinite(tm.Yaw), $"the implement stays square through a full-lock turn ({F(Mathf.RadToDeg(worst), "F2")}°)");

        var combine = new Truck(Combine);
        var (_, combineTop) = Drive(combine, 60f, 99f);
        Expect(combineTop is > 20f and < 26f, $"{Combine.Label}: top {F(combineTop)} km/h (hydrostatic, 25 km/h)");
        var threshing = new Truck(Combine) { Lowered = true };
        var (_, threshTop) = Drive(threshing, 40f, 99f);
        Expect(threshTop < Combine.WorkKmh + 1f, $"threshing, it holds to {F(threshTop)} km/h (limit {F(Combine.WorkKmh, "F0")})");

        // who takes what
        var scania = HeavyCatalog.All[0];
        Expect(Tractor.Accepts(TrailerCatalog.All[plough]) && Tractor.Accepts(TrailerCatalog.All[tipper]) && !Tractor.Accepts(TrailerCatalog.All[0]),
            "the tractor takes the implements and the tipping trailer, not a semi");
        Expect(!scania.Accepts(TrailerCatalog.All[plough]) && !Combine.Accepts(TrailerCatalog.All[tipper]), "a lorry takes no plough, a combine nothing");

        // the tanks in their replicated ints
        var wheat = new Tank(CropKind.Wheat, 137);
        int code = TrailerCatalog.WithTank(TrailerCatalog.Code(tipper, 0f), wheat);
        Expect(TrailerCatalog.TankOf(TrailerCatalog.Clean(code)) == wheat && Mathf.IsEqualApprox(TrailerCatalog.Load(code), 137f / 200f, 0.01f),
            $"a tipping trailer's code keeps its harvest through Clean: {TrailerCatalog.TankOf(code)}, load {F(TrailerCatalog.Load(code), "F2")}");
        var full = new Truck(Combine);
        full.SetTank(new Tank(CropKind.Barley, 140, 0.4f));
        var copy = new Truck(Combine);
        copy.UnpackFlags(full.PackFlags());
        Expect(copy.Tank == new Tank(CropKind.Barley, 140) && full.Train.Mass > copy.Train.Mass - 1f && full.Train.Mass > new Truck(Combine).Train.Mass + 6000f,
            $"the combine's tank travels in its flags and weighs: {copy.Tank}, {F(full.Train.Mass / 1000f)} t");

        settings.RideProfile = was;
    }

    // ---- in the world ---------------------------------------------------------------------------

    /// <summary>A stand-in field: every metre of bar is worked, wheat to harvest, grass to mow.</summary>
    private static FarmStroke Field(FarmTool tool, Vector3 a, Vector3 b, float width, CropKind seed)
    {
        // ten times a real field's cells: a short drive fills a tank
        float worked = new Vector2(b.X - a.X, b.Z - a.Z).Length() * width / 16f * 10f;
        int cells = worked > 0f ? Mathf.Max(1, Mathf.RoundToInt(worked)) : 0;
        return tool switch
        {
            FarmTool.Harvest => new FarmStroke(cells, CropKind.Wheat, worked * FarmTables.YieldPerCell(CropKind.Wheat)),
            FarmTool.Mow => new FarmStroke(cells, CropKind.Meadow, worked * FarmTables.YieldPerCell(CropKind.Meadow) * 4f),
            FarmTool.Sow => new FarmStroke(cells, seed, worked / FarmTables.CellsPerSeed * 4f),
            _ => new FarmStroke(cells, CropKind.None, 0f),
        };
    }

    private static int Count(ItemId id) => ItemController.Instance?.Inventory.CountPlain(id) ?? 0;

    private async Task Run()
    {
        Numbers();
        FootPlayer? me = null;
        for (int i = 0; i < 600; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                // the fly camera first: T puts the player down
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        if (VehicleManager.Instance is not { } vehicles || ItemController.Instance is not { } items) { Finish("no vehicles or items here"); return; }
        me.Announced += (text, _) => Log($"  announced: {text}");
        MachineWork.FakeSweep = Field;

        // ---- the tractor and the plough ----
        Expect(me.SetRide(Tractor.Kind), "in the tractor");
        if (me.Vehicle is not Truck tractor) { Finish("not in the tractor"); return; }
        await Wait(1.0);
        // the user's report: the implement lifted the tractor off the ground. Each one, raised and
        // lowered, driven, turned tight and reversed: the tractor stays on its wheels
        foreach (var body in new[] { TrailerBody.Plough, TrailerBody.SeedDrill, TrailerBody.Mower, TrailerBody.Tipper })
            await StaysDown(me, tractor, body);
        Expect(me.SpawnTrailer(Index(TrailerBody.Plough), 0f) && tractor.Implement?.Body == TrailerBody.Plough, "the plough on its linkage");
        await Wait(0.5);
        var lift = me.GetNodeOrNull<Node3D>("Section1/Visual/Body/Lift");
        float raisedY = lift?.Position.Y ?? -1f;
        await Shot("1-tractor-plough-up");
        me.ToggleLowered(tractor);
        await Wait(2.0);
        float loweredY = lift?.Position.Y ?? -1f;
        Expect(lift != null && raisedY > 0.3f && loweredY < 0.05f, $"{{kneel}} lowers it on the rig: {F(raisedY, "F2")} -> {F(loweredY, "F2")} m");
        Expect((tractor.PackFlags() & 8) != 0, "lowered travels in the kneel bit");
        int strokes = me.FarmStrokes;
        me.RideControls = () => new RideInput(0.6f, 0f, 0f, false);
        await Wait(6.0);
        Expect(me.FarmStrokes - strokes > 30 && me.FarmCells > 0, $"ploughing: {me.FarmStrokes - strokes} strokes, {me.FarmCells} cells, at {F(me.GroundSpeed * 3.6f)} km/h");
        await Shot("2-ploughing");
        await Stop(me);
        me.ToggleLowered(tractor);
        strokes = me.FarmStrokes;
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Wait(2.0);
        Expect(me.FarmStrokes == strokes, "raised, it works nothing");
        await Stop(me);

        // ---- the drill: seed from the pack ----
        tractor.Couple(0);   // no-op: one at a time
        Expect(me.Vehicle is Truck { Implement.Body: TrailerBody.Plough }, "one implement at a time");
        DropTrailer(me);
        await Wait(1.0);
        Expect(me.SpawnTrailer(Index(TrailerBody.SeedDrill), 0f) && tractor.Implement?.Body == TrailerBody.SeedDrill, "the drill on the linkage");
        items.Inventory.TakePlain(ItemId.WheatSeed, 999);
        me.ToggleLowered(tractor);
        strokes = me.FarmStrokes;
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Wait(3.0);
        Expect(me.FarmStrokes == strokes, "no seed in the pack: the drill sows nothing");
        // the farming core defines the seed and harvest items; until it does, nothing of them can be in a pack
        bool defs = ItemDefs.Get(ItemId.WheatSeed) != null && ItemDefs.Get(ItemId.HayBale) != null && ItemDefs.Get(ItemId.Wheat) != null;
        if (!defs) Log("note: no seed, hay or grain items defined yet: the pack checks count what the machines handed over");
        items.Give(new ItemStack(ItemId.WheatSeed, 20));
        await Wait(6.0);
        int seedLeft = Count(ItemId.WheatSeed);
        if (defs) Expect(me.FarmStrokes > strokes && seedLeft < 20 && me.FarmSeedUsed == 20 - seedLeft, $"with seed: sowing, {20 - seedLeft} of 20 bags used");
        await Stop(me);
        me.ToggleLowered(tractor);
        DropTrailer(me);
        await Wait(1.0);

        // ---- the mower: hay into the pack ----
        Expect(me.SpawnTrailer(Index(TrailerBody.Mower), 0f), "the mower on the linkage");
        int hay = Count(ItemId.HayBale);
        me.ToggleLowered(tractor);
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Wait(6.0);
        Expect(defs ? me.FarmHayCut > 0 && Count(ItemId.HayBale) > hay : me.FarmHayLeft > 0 && me.FarmHayCut == 0, $"mowing: {me.FarmHayCut} bales into the pack ({Count(ItemId.HayBale) - hay} in it), {me.FarmHayLeft} left on the field for want of room");
        Expect(!GetTree().Root.FindChildren("*", nameof(DroppedItem), true, false).Any(), "and none dropped at the driver's feet, inside the tractor");
        await Stop(me);
        await Shot("3-mowing");

        // ---- parked with the mower down: the whole train, as it was ----
        me.RideControls = null;
        me.ExitVehicle();
        await Wait(1.5);
        var parked = vehicles.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Ride is Truck { Spec.Class: HeavyClass.FarmTractor });
        var state = parked?.Capture();
        Expect(state is { } s0 && TrailerCatalog.For(s0.Train)?.Body == TrailerBody.Mower && (s0.Flags & 8) != 0,
            $"parked: the tractor with its mower, still down (flags {state?.Flags})");
        if (parked != null)
        {
            // at its door
            me.PlaceAt(parked.ToGlobal(parked.Ride.EntryPoint) + Vector3.Up * 0.5f, parked.Rotation.Y);
            await Until(() => me.IsOnFloor(), 5);
        }
        bool gotIn = me.TryGetIn();
        Expect(gotIn && await Until(() => me.Vehicle is Truck { Implement.Body: TrailerBody.Mower, Lowered: true }, 5), "back in by its door: the mower is on it, down");
        if (me.Vehicle is Truck again) { again.Lowered = false; tractor = again; }
        me.ExitVehicle();
        await Wait(1.0);
        // clear of the parked tractor, on the ground
        me.PlaceAt(me.GlobalPosition + me.GlobalTransform.Basis.X * -12f + Vector3.Up * 0.5f, me.Rotation.Y);
        await Until(() => me.IsOnFloor(), 5);

        // ---- the combine, its tank and the auger into a parked tipping trailer ----
        var start = me.GlobalPosition;
        Expect(me.SetRide(Combine.Kind), "in the combine");
        if (me.Vehicle is not Truck combine) { Finish("not in the combine"); return; }
        await Wait(1.0);
        me.ToggleLowered(combine);
        me.RideControls = () => new RideInput(0.6f, 0f, 0f, false);
        await Until(() => combine.Tank.Items >= 40, 30);
        Expect(combine.Tank.Items >= 40 && combine.Tank.Crop == CropKind.Wheat, $"harvesting: {combine.Tank.Items} sacks of {combine.Tank.Crop} in the tank at {F(me.GroundSpeed * 3.6f)} km/h");
        await Stop(me);
        await Shot("4-combine-full");
        me.ToggleLowered(combine);
        int inTank = combine.Tank.Items;
        // a tipping trailer parked beside, under the spout
        var spout = me.ToGlobal(Avatar.FarmMeshBuilder.AugerSpout(combine.Train.Bodies[0].CgAt));
        var tipperSpec = TrailerCatalog.All[Index(TrailerBody.Tipper)];
        float bodyAhead = tipperSpec.Sections[0].HitchAt - tipperSpec.Sections[1].PivotAt + tipperSpec.Sections[1].Length * 0.5f;
        var fwd = -me.GlobalTransform.Basis.Z with { Y = 0 };
        // the dolly ahead, its body under the spout
        var dollyAt = spout + fwd.Normalized() * (bodyAhead - new HeavyTrain.Body(tipperSpec.Sections[0], 0f).CgAt);
        vehicles.Park(new VehicleState(RideKind.Trailer, me.Origin!.ToGlobal(dollyAt with { Y = me.GlobalPosition.Y }), me.Rotation.Y, Vector3.Zero, 400f,
            false, false, 0f, VehicleState.Now, Train: TrailerCatalog.Code(Index(TrailerBody.Tipper), 0f)));
        await Wait(1.0);
        me.FarmAction(combine);
        Expect(combine.AugerOut, "{destination} swings the auger out");
        // the one parked under the spout (the tipping trailer dropped earlier stands elsewhere)
        VehicleBody? Tipper() => vehicles.GetChildren().OfType<VehicleBody>().Where(v => v.Trailer is { Spec.TankItems: > 0 } && !v.IsQueuedForDeletion())
            .OrderBy(v => v.GlobalPosition.DistanceTo(dollyAt)).FirstOrDefault();
        await Until(() => Tipper() is { } t && TrailerCatalog.TankOf(t.Trailer!.Code).Items >= 16, 12);
        var tipped = Tipper();
        int inTrailer = tipped != null ? TrailerCatalog.TankOf(tipped.Trailer!.Code).Items : 0;
        Expect(inTrailer >= 16 && combine.Tank.Items <= inTank - 16, $"unloading: {inTrailer} sacks in the trailer, {combine.Tank.Items} left in the tank");
        await Shot("5-unloading");
        me.FarmAction(combine);
        Expect(!combine.AugerOut && !me.CanDeliver(combine), "auger in; no co-op here to deliver to");

        // ---- on foot: a sack from the trailer ----
        me.ExitVehicle();
        await Wait(1.0);
        if (Tipper() is { } tp && tp.Trailer is { } pt)
        {
            int k = pt.Spec.Sections.Length - 1;
            var side = tp.GlobalTransform * pt.NodeLocal(k) * new Vector3(pt.Spec.Sections[k].Width * 0.5f + 0.8f, 0f, 0f);
            me.PlaceAt(side with { Y = me.GlobalPosition.Y + 0.3f }, me.Rotation.Y);
            await Until(() => me.SackSource != null, 3);
            int before = Count(ItemId.Wheat);
            int trailerBefore = TrailerCatalog.TankOf(pt.Code).Items;
            int taken = me.FarmSacksTaken;
            Expect(me.SackSource != null && me.TryInteract(), "E at the trailer");
            await Until(() => me.FarmSacksTaken > taken, 5);
            await Wait(0.5);
            int trailerAfter = Tipper() is { } t2 ? TrailerCatalog.TankOf(t2.Trailer!.Code).Items : -1;
            Expect(me.FarmSacksTaken == taken + 1 && trailerAfter == trailerBefore - 1 && (!defs || Count(ItemId.Wheat) == before + 1),
                $"a sack of wheat into the pack ({Count(ItemId.Wheat) - before} in it), the trailer {trailerBefore} -> {trailerAfter}");
        }
        else Expect(false, "the tipping trailer is gone");
        await Shot("6-on-foot");

        MachineWork.FakeSweep = null;
        Finish(null);
    }

    /// <summary>
    /// The tractor with <paramref name="body"/> coupled: standing, forward, full lock both ways and in
    /// reverse, raised then lowered; its height over the ground must stay within a few cm.
    /// </summary>
    private async Task StaysDown(FootPlayer me, Truck tractor, TrailerBody body)
    {
        if (!me.SpawnTrailer(Index(body), 0f)) { Expect(false, $"{body}: not coupled"); return; }
        await Wait(1.0);
        float Ground() => me.Terrain != null && me.Terrain.TryGetHeight(me.GlobalPosition, out float g) ? g : 0f;
        foreach (bool lowered in new[] { false, true })
        {
            if (tractor.Implement != null) tractor.Lowered = lowered;
            else if (lowered) continue;
            await Wait(1.0);
            float rest = me.GlobalPosition.Y - Ground(), worst = 0f, worstAt = 0f;
            int air = 0;
            var phases = new (string, RideInput, double)[]
            {
                ("ahead", new RideInput(0.5f, 0f, 0f, false), 3),
                ("left lock", new RideInput(0.4f, 0f, 1f, false), 4),
                ("right lock", new RideInput(0.4f, 0f, -1f, false), 4),
                ("stop", new RideInput(0f, 1f, 0f, false), 3),
                ("let go", new RideInput(0f, 0f, 0f, false), 1),
                ("reverse", new RideInput(0f, 0.5f, 0.6f, false), 4),
            };
            foreach (var (name, input, seconds) in phases)
            {
                var held = input;
                me.RideControls = () => held;
                for (double t = 0; t < seconds; t += 0.05)
                {
                    await Wait(0.05);
                    float off = me.GlobalPosition.Y - Ground() - rest;
                    if (Mathf.Abs(off) > Mathf.Abs(worst)) { worst = off; worstAt = me.GroundSpeed * 3.6f; }
                    if (!me.IsOnFloor()) air++;
                    if (CmdArgs.Has("trace") && Mathf.PosMod((float)t, 0.5f) < 0.05f)
                        Log($"    {name} t {F((float)t)} y {F(me.GlobalPosition.Y, "F2")} ground {F(Ground(), "F2")} floor {me.IsOnFloor()} {F(me.GroundSpeed * 3.6f)} km/h vy {F(me.Velocity.Y, "F2")} pos {me.GlobalPosition}");
                }
            }
            await Stop(me);
            me.RideControls = null;
            Expect(Mathf.Abs(worst) < 0.08f && air < 6 && me.Vehicle is Truck,
                $"{TrailerCatalog.All[Index(body)].Label} {(lowered ? "down" : "up")}: the tractor stays on its wheels (worst {F(worst * 100f, "F0")} cm at {F(worstAt)} km/h, {air} ticks off the floor, {me.SectionHits} section hits)");
        }
        if (tractor.Implement != null) tractor.Lowered = false;
        DropTrailer(me);
        await Wait(0.5);
        // pull clear of what was just dropped, then back straight into it: it stops the tractor, it
        // is no ramp (a dropped implement's box low down was a step the tractor rode up and flew off)
        // (past 22 m: a dropped trailer ignores the truck that left it until it has driven clear)
        var dropped = me.GlobalPosition;
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Until(() => me.GlobalPosition.DistanceTo(dropped) > 26f, 20);
        await Stop(me);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Wait(1.0);
        float rest0 = me.GlobalPosition.Y - Ground(), worstBack = 0f, slowest = 99f;
        me.RideControls = () => new RideInput(0f, 0.4f, 0f, false);
        for (int i = 0; i < 400; i++)
        {
            await Wait(0.05);
            worstBack = Mathf.Max(worstBack, Mathf.Abs(me.GlobalPosition.Y - Ground() - rest0));
            if (i > 100) slowest = Mathf.Min(slowest, me.GroundSpeed);
            if (CmdArgs.Has("trace") && (i % 8 == 0 || Mathf.Abs(me.GlobalPosition.Y - Ground() - rest0) > 0.03f)) Log($"    back {i} y {F(me.GlobalPosition.Y, "F3")} ground {F(Ground(), "F3")} floor {me.IsOnFloor()} {F(me.GroundSpeed * 3.6f)} km/h {me.Heavy?.GearLabel} pos {me.GlobalPosition} slides {me.GetSlideCollisionCount()} {(me.GetSlideCollisionCount() > 0 ? (me.GetSlideCollision(0).GetCollider() as Node)?.Name + " " + me.GetSlideCollision(0).GetNormal() : "")} floorN {me.GetFloorNormal()}");
            if (CmdArgs.Has("trace"))
                for (int c = 0; c < me.GetSlideCollisionCount(); c++)
                {
                    var hit = me.GetSlideCollision(c);
                    if (hit.GetCollider() is Node n && n is not StaticBody3D)
                        Log($"    back y {F(me.GlobalPosition.Y - Ground(), "F2")} hit {n.Name} shape {(hit.GetColliderShape() as Node)?.Name} by {(hit.GetLocalShape() as Node)?.Name} n {hit.GetNormal()} at {hit.GetPosition() - me.GlobalPosition}");
                }
        }
        await Stop(me);
        Expect(worstBack < 0.08f, $"backed 20 s into the dropped {TrailerCatalog.All[Index(body)].Label.ToLowerInvariant()}: no climb ({F(worstBack * 100f, "F0")} cm; slowest {F(slowest * 3.6f)} km/h, {F(me.GlobalPosition.DistanceTo(dropped))} m from where it was dropped)");
        // and clear of it for the next one
        me.RideControls = () => new RideInput(0.5f, 0f, 0.3f, false);
        await Wait(3.0);
        await Stop(me);
        me.RideControls = null;
    }

    /// <summary>Brakes to a standstill (in game time or real time alike), then lets the pedals go.</summary>
    private async Task Stop(FootPlayer me)
    {
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, Handbrake: true);
        bool still = await Until(() => me.GroundSpeed < 0.2f, 20);
        if (!still) Log($"  still at {F(me.GroundSpeed * 3.6f)} km/h after braking");
        await Wait(0.3);
    }

    private static void DropTrailer(FootPlayer me)
    {
        if (me.Vehicle is Truck t && t.Trailer != null) me.ToggleCouple(t);
    }

    private void Finish(string? fatal)
    {
        MachineWork.FakeSweep = null;
        if (_local() is { } me) me.RideControls = null;
        if (fatal != null) { _failures++; Log($"FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
