using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--tractorcheck [shots|slope] --chunks fixture:flat --traffic 0</c> (#494): the farm machines.
/// First the numbers on flat ground with no world (as <see cref="HeavyCheck"/>): the tractor's 0-40
/// and top speed, a lowered plough slowing it to a ploughing pace and a drill hardly, the weight of
/// a raised implement on the rear axle, the combine's 25 km/h and its threshing speed, which
/// coupling takes what, the tank in the trailer's code and the combine's flags. Then in the world,
/// with a stand-in field (<see cref="MachineWork.FakeSweep"/>): the implement lowered and raised on
/// the rig, the plough sweeping, the drill sowing only with seed in the pack and taking it, the
/// mower's bales into the pack, the combine's tank filling, the auger unloading into a parked
/// tipping trailer, a sack taken from it on foot, the train parked with the implement down, and a
/// lowered plough across a paved strip, then each implement raised and lowered driven up, down and
/// across the flat fixture's 15 % ridge. <c>slope</c>: the numbers and the ridge only.
/// Between the parked train and the strip, at a stand-in farm co-op (<see cref="FarmMarket.StandIn"/>): the server refusing a load
/// from far away and a non-harvest, the tipper's bin tipped on a field (nothing poured), then
/// driven to the co-op and tipped there (paid units × value, emptied), and the combine delivering.
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
    /// <summary><c>--tractorcheck slope</c>: the numbers, then only the ridge runs.</summary>
    private readonly bool _slope;
    private int _failures;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TractorCheck(string role, System.Func<FootPlayer?> local)
    {
        _local = local;
        _shots = role.Contains("shots") && DisplayServer.GetName() != "headless";
        _slope = role.Contains("slope");
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
        Expect(t40 is > 12f and < 24f && top is > 37f and < 41.5f, $"{Tractor.Label}: 0-39 km/h in {F(t40)} s (a Vario ~15-20 s to 40), top {F(top)} km/h (40 km/h tractor), {alone.GearLabel}");

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
        var onRoad = new Truck(Tractor, TrailerCatalog.Code(plough, 0f)) { Lowered = true, OnSoil = false };
        var (_, roadTop) = Drive(onRoad, 40f, 99f);
        Expect(roadTop > 0.95f * raisedTop && onRoad.WorkTool == FarmTool.None,
            $"lowered on tarmac it pulls and works nothing: {F(roadTop)} km/h");
        Expect(Mathf.Abs(down.Articulation[0]) < 0.01f, $"rigid on the linkage: {F(Mathf.RadToDeg(down.Articulation[0]), "F2")}°");
        var drilling = new Truck(Tractor, TrailerCatalog.Code(drill, 0f)) { Lowered = true };
        var (_, drillTop) = Drive(drilling, 40f, 99f, Surface.Grass);
        Expect(drillTop > 0.6f * raisedTop, $"a drill down pulls a little: {F(drillTop)} km/h");
        var mowing = new Truck(Tractor, TrailerCatalog.Code(mower, 0f)) { Lowered = true };
        var (_, mowTop) = Drive(mowing, 40f, 99f, Surface.Grass);
        Expect(mowTop > ploughTop, $"a mower down: {F(mowTop)} km/h");
        // a rear disc mower cuts beside the tractor, out to the right (its rear wheel's outside at 1.3 m)
        var mowBar = mowing.WorkBarNode;
        float inner = mowBar.X - TrailerCatalog.All[mower].WorkWidth * 0.5f, outer = mowBar.X + TrailerCatalog.All[mower].WorkWidth * 0.5f;
        Expect(inner > 0.2f && outer > Tractor.Sections[0].Width * 0.5f + 1.5f && Mathf.Abs(Truck.DraftOf(TrailerCatalog.All[mower], 8f)) > 0f,
            $"the mower's bar is out to the right: {F(inner, "F2")} to {F(outer, "F2")} m from the tractor's centre");

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
        me.Announced += (text, _) =>
        {
            Log($"  announced: {text}");
            if (text.Contains("on the road")) _roadToasts++;
        };
        // an empty pack lent for the check (as a match does): the saved one is neither read nor overwritten
        items.Inventory.BeginMatch();
        MachineWork.FakeSweep = Field;

        if (_slope)
        {
            await Ridge(me);
            Finish(null);
            return;
        }

        // the user's report: the implement lifted the tractor off the ground. Each one, raised and
        // lowered, driven, turned tight and reversed: the tractor stays on its wheels. Each on a lane
        // of its own south of the spawn, so what was dropped before is never in the way
        var bodies = new[] { TrailerBody.Plough, TrailerBody.SeedDrill, TrailerBody.Mower, TrailerBody.Tipper };
        for (int i = 0; i < bodies.Length; i++)
            await StaysDown(me, bodies[i], -80 + 55 * i, -120);

        // ---- the tractor and the plough, from the spawn ----
        if (await FreshTractor(me, 0, 0, North, null, false) is not { } tractor) { Finish("not in the tractor"); return; }
        Expect(me.Vehicle == tractor, "in the tractor");
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

        await CoopDelivery(me, items);

        // ---- a lowered plough across a paved strip: it rides on the tarmac, pulls and works nothing ----
        await OverTarmac(me);
        await Ridge(me);

        Finish(null);
    }

    // ---- delivering at a farm co-op (#494) ---------------------------------------------------------

    /// <summary>The server's answer to a delivery asked of it directly (<see cref="ShopService.Deliver"/>): the francs, 0 refused.</summary>
    private async Task<int> AskDelivery(ItemId item, int count, Vector3 at, string door)
    {
        int answer = -1;
        ShopService.Instance?.Deliver(item, count, at, door, francs => answer = francs);
        await Until(() => answer >= 0, 5);
        return answer;
    }

    /// <summary>
    /// A stand-in co-op ahead in a clear spot; the server refuses far away and a non-harvest; the
    /// tipping trailer tipped on the field pours nothing; driven to the co-op and tipped, it is
    /// paid units × value and emptied (the bin drawn up); the combine delivers its tank the same way.
    /// </summary>
    private async Task CoopDelivery(FootPlayer me, ItemController items)
    {
        if (me.Vehicle != null) { me.ExitVehicle(); await Wait(1.0); }
        if (ShopService.Instance == null) { Expect(false, "no shop service: nothing to deliver to"); return; }
        // well clear of everything parked and dropped so far
        var side = me.GlobalTransform.Basis.X with { Y = 0 };
        me.PlaceAt(me.GlobalPosition + side.Normalized() * -45f + Vector3.Up * 0.5f, me.Rotation.Y);
        await Until(() => me.IsOnFloor(), 5);
        var fwd = (-me.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        var door = me.GlobalPosition + fwd * 70f;
        FarmMarket.StandIn(door with { Y = me.GlobalPosition.Y - 0.3f }, -fwd);
        float wheat = ItemDefs.Get(ItemId.Wheat)?.Value ?? 0f;

        // the server's own checks, asked directly
        Expect(await AskDelivery(ItemId.Wheat, 10, door + fwd * -200f, "") == 0, "the server refuses a load 200 m from the co-op");
        Expect(await AskDelivery(ItemId.WheatSeed, 10, door - fwd * 5f, "") == 0, "and seed, which is no harvest, at its door");
        Expect(await AskDelivery(ItemId.Flour, 10, door - fwd * 5f, "") == 0, "and flour");
        int refusedClient = -1;
        FarmMarket.Deliver(me, door - fwd * 5f, ItemId.WheatSeed, 10, f => refusedClient = f);
        Expect(refusedClient == 0, "the client does not even ask for seed");

        // the tipping trailer, 60 sacks of wheat
        Expect(me.SetRide(Tractor.Kind) && me.Vehicle is Truck, "in a tractor again");
        if (me.Vehicle is not Truck tractor) return;
        await Wait(1.0);
        Expect(me.SpawnTrailer(Index(TrailerBody.Tipper), 60f / 200f) && tractor.TrailerTank == new Tank(CropKind.Wheat, 60), $"a tipping trailer with {tractor.TrailerTank}");
        await Wait(1.0);
        var bin = me.GetNodeOrNull<Node3D>($"Section{tractor.SectionCount - 1}/Visual/Body/Tip");
        int cash = items.Inventory.Cash;
        Expect(!me.CanDeliver(tractor), "on the field: no co-op to deliver to");
        me.FarmAction(tractor);
        await Wait(3.5);
        float fieldTilt = bin?.Rotation.X ?? 0f;
        Expect(tractor.Tipping && (tractor.PackFlags() & (Truck.AugerBit << 4)) != 0 && fieldTilt > 0.6f,
            $"{{destination}} tips the bin on the field: drawn up {F(Mathf.RadToDeg(fieldTilt), "F0")}°, the bit in the pose");
        Expect(tractor.TrailerTank.Items == 60 && items.Inventory.Cash == cash, $"and pours nothing: {tractor.TrailerTank.Items} sacks still in it, {items.Inventory.Cash - cash} CHF");
        await Shot("7-tipped-on-field");
        await Until(() => !tractor.Tipping, 8);
        await Wait(3.5);
        Expect(!tractor.Tipping && (bin?.Rotation.X ?? 1f) < 0.05f, "the bin comes back down");

        // to the co-op's yard
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Until(() => me.GlobalPosition.DistanceTo(door) < 18f, 30);
        await Stop(me);
        me.RideControls = null;
        Expect(me.CanDeliver(tractor), $"stopped {F(me.GlobalPosition.DistanceTo(door))} m from the co-op's door: it can deliver");
        long due = ShopTables.DeliveryPrice(ItemCategory.Produce, wheat, 60);
        me.FarmAction(tractor);
        await Until(() => tractor.TrailerTank.Items == 0, 8);
        await Wait(1.0);
        Expect(tractor.TrailerTank.Items == 0 && me.FarmFrancsPaid == 60 * (int)wheat && due == 60 * (int)wheat && items.Inventory.Cash - cash == me.FarmFrancsPaid,
            $"tipped at the co-op: paid {me.FarmFrancsPaid} CHF for 60 sacks × {F(wheat, "F0")} (pocket +{items.Inventory.Cash - cash}), the trailer {tractor.TrailerTank.Items}");
        // the rig was rebuilt for the new load: look the bin up again
        bin = me.GetNodeOrNull<Node3D>($"Section{tractor.SectionCount - 1}/Visual/Body/Tip");
        var heap = bin?.GetNodeOrNull<Node3D>("Heap");
        Expect(tractor.Tipping && bin != null && bin.Rotation.X > 0.6f && heap != null && !heap.Visible,
            $"the bin still up ({F(Mathf.RadToDeg(bin?.Rotation.X ?? 0f), "F0")}°), the heap gone from it");
        await Shot("8-tipped-at-coop");
        me.FarmAction(tractor);
        Expect(me.FarmDeliveries == 1, "a second press while it is up sells nothing more");

        // the combine, its tank delivered by the auger
        me.ExitVehicle();
        await Wait(1.0);
        me.PlaceAt(me.GlobalPosition + side.Normalized() * 14f + Vector3.Up * 0.5f, me.Rotation.Y);
        await Until(() => me.IsOnFloor(), 5);
        Expect(me.SetRide(Combine.Kind) && me.Vehicle is Truck, "in a combine by the co-op");
        if (me.Vehicle is not Truck combine) return;
        await Wait(1.0);
        combine.SetTank(new Tank(CropKind.Wheat, 40));
        cash = items.Inventory.Cash;
        int paidBefore = me.FarmDeliveries;
        Expect(me.CanDeliver(combine), $"{F(me.GlobalPosition.DistanceTo(door))} m from the door, 40 sacks: it can deliver");
        me.FarmAction(combine);
        await Until(() => combine.Tank.Items == 0, 8);
        Expect(combine.Tank.Items == 0 && !combine.AugerOut && me.FarmDeliveries == paidBefore + 1 && me.FarmFrancsPaid == 40 * (int)wheat && items.Inventory.Cash - cash == 40 * (int)wheat,
            $"the combine delivers: paid {me.FarmFrancsPaid} CHF for 40 sacks, the tank {combine.Tank.Items}");
        me.ExitVehicle();
        await Wait(1.0);
        Interiors.DoorIndex.ClearTile(FarmMarket.StandInTile);
    }

    /// <summary>The ridge: each implement raised and lowered, driven up, down and across the 15 % slope.</summary>
    private async Task Ridge(FootPlayer me)
    {
        MachineWork.FakeSweep = null;
        foreach (var body in new[] { TrailerBody.Plough, TrailerBody.SeedDrill, TrailerBody.Mower })
            foreach (bool lowered in new[] { false, true })
                await OnSlope(me, body, lowered);
    }

    private int _roadToasts;

    private static float GroundAt(FootPlayer me, Vector3 p) => me.Terrain != null && me.Terrain.TryGetHeight(p, out float g) ? g : 0f;

    /// <summary>Heading east (+X) and north (-Z) as a yaw.</summary>
    private const float East = -Mathf.Pi / 2f, North = 0f;

    /// <summary>
    /// A fresh tractor at a point of the course (metres from the start, x east, y north), facing
    /// <paramref name="yaw"/>, with <paramref name="body"/> on its linkage, lowered or not. The one
    /// driven before goes (a picker's swap): nothing is left in the way.
    /// </summary>
    private async Task<Truck?> FreshTractor(FootPlayer me, double x, double y, float yaw, TrailerBody? body, bool lowered)
    {
        if (me.Vehicle != null)
        {
            await Stop(me);
            me.RideControls = null;
            me.SetRide(RideKind.OnFoot);
            await Wait(0.5);
        }
        var at = new Vector3((float)x, 0f, (float)-y);
        at.Y = GroundAt(me, at) + 0.5f;
        me.PlaceAt(at, yaw);
        await Until(() => me.IsOnFloor(), 10);
        if (!me.SetRide(Tractor.Kind) || me.Vehicle is not Truck t) { Expect(false, $"a tractor at {F((float)x, "F0")},{F((float)y, "F0")}"); return null; }
        await Wait(1.0);
        if (body is not { } b) return t;
        if (!me.SpawnTrailer(Index(b), 0f)) { Expect(false, $"{b} coupled at {F((float)x, "F0")},{F((float)y, "F0")}"); return null; }
        await Wait(0.5);
        t.Lowered = lowered;
        await Wait(1.0);
        return t;
    }

    /// <summary>
    /// The plough lowered, driven east across the course's paved strip: over the paving its bodies
    /// pull nothing and work nothing, a toast says once to raise it; past it, it works again.
    /// </summary>
    private async Task OverTarmac(FootPlayer me)
    {
        MachineWork.FakeSweep = Field;
        const double from = Terrain.Fixture.FixtureCourse.PavedFrom, to = from + Terrain.Fixture.FixtureCourse.PavedWidth;
        if (await FreshTractor(me, from - 25, 0, East, TrailerBody.Plough, true) is not { } t) return;
        int toasts = _roadToasts, before = me.FarmStrokes, onStrip = -1, after = -1;
        float worstDraft = 0f, stripKmh = 0f, fieldKmh = 0f;
        bool sawRoad = false, workedOnStrip = false;
        me.RideControls = () => new RideInput(0.6f, 0f, 0f, false);
        for (double time = 0; time < 60; time += 0.05)
        {
            await Wait(0.05);
            float bx = me.ToGlobal(t.WorkBarNode).X;
            int strokes = me.FarmStrokes;
            // a metre inside the strip: last tick's bar may still have been on the grass
            if (bx > from + 1 && bx < to - 1)
            {
                if (onStrip < 0) onStrip = strokes;
                sawRoad |= !t.OnSoil;
                workedOnStrip |= strokes > onStrip;
                worstDraft = Mathf.Max(worstDraft, t.Train.Bodies[^1].Draft);
                stripKmh = Mathf.Max(stripKmh, me.GroundSpeed * 3.6f);
            }
            else if (bx < from - 1) fieldKmh = me.GroundSpeed * 3.6f;
            if (bx > to + 4) { after = strokes; break; }
        }
        int afterStrip = me.FarmStrokes;
        await Wait(1.5);
        Expect(onStrip > before && sawRoad && !workedOnStrip && worstDraft == 0f,
            $"the plough lowered over paving: off the soil {sawRoad}, worked there {workedOnStrip} ({onStrip - before} strokes on the grass before), draft {F(worstDraft / 1000f)} kN, {F(stripKmh)} km/h against {F(fieldKmh)} on the grass");
        Expect(_roadToasts - toasts == 1, $"one toast to raise it on the road ({_roadToasts - toasts})");
        Expect(after >= 0 && t.OnSoil && me.FarmStrokes > afterStrip, $"past the paving it works again ({me.FarmStrokes - afterStrip} strokes)");
        await Stop(me);
        me.RideControls = null;
    }

    /// <summary>
    /// The tractor with <paramref name="body"/> raised or lowered on the course's ridge: driven up
    /// it, down the far side and across its slope. It stays on its wheels: its height over the
    /// ground within 8 cm of the flat's, hardly a tick off the floor, not rolled over.
    /// </summary>
    private async Task OnSlope(FootPlayer me, TrailerBody body, bool lowered)
    {
        const double a = Terrain.Fixture.FixtureCourse.RidgeFrom, slope = Terrain.Fixture.FixtureCourse.RidgeSlope,
            plateau = Terrain.Fixture.FixtureCourse.RidgePlateau;
        string what = $"{TrailerCatalog.All[Index(body)].Label} {(lowered ? "down" : "up")}";
        float rest = float.NaN;
        // up from the flat onto the slope, down from the plateau onto the flat, across mid-slope; at a
        // working pace, braking on the way down (Stop: a tractor's driver does not coast down a 15 % hill)
        var runs = new (string Name, double X, double Y, float Yaw, System.Func<Vector3, Vector3, bool> Done, double Seconds)[]
        {
            ("up", a - 10, -40, East, (p, s) => p.X > a + 35, 60),
            ("down", a + slope + plateau - 10, -40, East, (p, s) => p.X > a + 2 * slope + plateau + 10, 60),
            ("across", a + slope / 2, -150, North, (p, s) => s.DistanceTo(p) > 40f, 40),
        };
        foreach (var run in runs)
        {
            if (await FreshTractor(me, run.X, run.Y, run.Yaw, body, lowered) is null) return;
            float off0 = me.GlobalPosition.Y - GroundAt(me, me.GlobalPosition);
            if (float.IsNaN(rest)) rest = off0;   // the first run starts on the flat
            var start = me.GlobalPosition;
            float worst = 0f, worstKmh = 0f, top = 0f, tilt = 0f;
            int air = 0;
            bool done = false;
            int hits = me.SectionHits;
            me.RideControls = () =>
            {
                float err = 15f - me.GroundSpeed * 3.6f;
                return new RideInput(Mathf.Clamp(err * 0.3f, 0f, 1f), Mathf.Clamp(-(err + 2f) * 0.2f, 0f, 1f), 0f, false);
            };
            for (double time = 0; time < run.Seconds && me.Vehicle is Truck; time += 0.05)
            {
                await Wait(0.05);
                var p = me.GlobalPosition;
                float off = p.Y - GroundAt(me, p) - rest;
                if (Mathf.Abs(off) > Mathf.Abs(worst)) { worst = off; worstKmh = me.GroundSpeed * 3.6f; }
                if (!me.IsOnFloor()) air++;
                top = Mathf.Max(top, me.GroundSpeed * 3.6f);
                tilt = Mathf.Max(tilt, Mathf.RadToDeg(me.GlobalTransform.Basis.Y.AngleTo(Vector3.Up)));
                if (CmdArgs.Has("trace") && Mathf.PosMod((float)time, 1f) < 0.05f)
                    Log($"    {run.Name} t {F((float)time)} x {F(p.X)} y {F(p.Y, "F2")} ground {F(GroundAt(me, p), "F2")} off {F(off * 100f, "F0")} cm floor {me.IsOnFloor()} {F(me.GroundSpeed * 3.6f)} km/h {me.Heavy?.GearLabel}");
                if (run.Done(p, start)) { done = true; break; }
            }
            bool truck = me.Vehicle is Truck;
            Expect(done && truck && Mathf.Abs(worst) < 0.08f && air < 6 && tilt < 5f && me.SectionHits == hits,
                $"{what}, {run.Name} the 15 % slope: on its wheels (worst {F(worst * 100f, "F0")} cm at {F(worstKmh)} km/h, start {F((off0 - rest) * 100f, "F0")} cm, {air} ticks off the floor, body tilt {F(tilt)}°, top {F(top)} km/h, {me.SectionHits - hits} section hits{(done ? "" : ", did not get there")}{(truck ? "" : ", WRECKED")})");
        }
        await Stop(me);
        me.RideControls = null;
    }

    /// <summary>
    /// A fresh tractor at (<paramref name="x"/>, <paramref name="y"/>) facing north with <paramref name="body"/>
    /// coupled: standing, forward, full lock both ways and in reverse, raised then lowered; its height
    /// over the ground must stay within a few cm, its sections hit nothing.
    /// </summary>
    private async Task StaysDown(FootPlayer me, TrailerBody body, double x, double y)
    {
        if (await FreshTractor(me, x, y, North, body, false) is not { } tractor) return;
        float Ground() => me.Terrain != null && me.Terrain.TryGetHeight(me.GlobalPosition, out float g) ? g : 0f;
        foreach (bool lowered in new[] { false, true })
        {
            if (tractor.Implement != null) tractor.Lowered = lowered;
            else if (lowered) continue;
            await Wait(1.0);
            float rest = me.GlobalPosition.Y - Ground(), worst = 0f, worstAt = 0f;
            int air = 0, tracedHits = me.SectionHits, hitsBefore = me.SectionHits;
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
                    if (CmdArgs.Has("trace") && me.SectionHits != tracedHits && me.GetNodeOrNull<CharacterBody3D>("Section1") is { } sec)
                    {
                        tracedHits = me.SectionHits;
                        for (int c = 0; c < sec.GetSlideCollisionCount(); c++)
                            Log($"    {name} section hit {(sec.GetSlideCollision(c).GetCollider() as Node)?.Name} n {sec.GetSlideCollision(c).GetNormal()} at {sec.GetSlideCollision(c).GetPosition() - sec.GlobalPosition} {F(me.GroundSpeed * 3.6f)} km/h");
                    }
                    if (CmdArgs.Has("trace") && Mathf.PosMod((float)t, 0.5f) < 0.05f)
                        Log($"    {name} t {F((float)t)} y {F(me.GlobalPosition.Y, "F2")} ground {F(Ground(), "F2")} floor {me.IsOnFloor()} {F(me.GroundSpeed * 3.6f)} km/h vy {F(me.Velocity.Y, "F2")} pos {me.GlobalPosition}");
                }
            }
            await Stop(me);
            me.RideControls = null;
            Expect(Mathf.Abs(worst) < 0.08f && air < 6 && me.Vehicle is Truck && me.SectionHits == hitsBefore,
                $"{TrailerCatalog.All[Index(body)].Label} {(lowered ? "down" : "up")}: the tractor stays on its wheels (worst {F(worst * 100f, "F0")} cm at {F(worstAt)} km/h, {air} ticks off the floor, {me.SectionHits - hitsBefore} section hits)");
        }
        if (tractor.Implement != null) tractor.Lowered = false;
        else
        {
            // a drawbar trailer folded by the reverse on lock: pulled straight behind before it is
            // dropped, so the tractor backing up meets it rather than passing beside it
            me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
            await Wait(6.0);
            await Stop(me);
        }
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
        // and clear of it
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
        ItemController.Instance?.Inventory.EndMatch();
        if (_local() is { } me) me.RideControls = null;
        if (fatal != null) { _failures++; Log($"FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
