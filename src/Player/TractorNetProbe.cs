using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--tractornet A|B</c> with <c>--connect</c> on the flat fixture (driven by
/// <c>tools/tractornetcheck.sh</c>, #494): what another player sees of the farm machines.
/// <list type="bullet">
/// <item>A (admin) takes the tractor with the plough and lowers it: B sees the plough on A's
/// tractor, down (the kneel bit), its rig lowered;</item>
/// <item>A gets out: B sees the parked tractor with its plough, still down;</item>
/// <item>A gets back in, drops the plough and takes a tipping trailer with 77 sacks of wheat: B
/// sees them in the trailer's code and the heap on its rig; A gets out, B sees the parked train
/// keep them;</item>
/// <item>A takes the combine with 50 sacks of barley in its tank: B reads them from the flags.</item>
/// <item>Logistics (<see cref="LogisticsA"/>, <see cref="LogisticsB"/>): B drives a tractor with an
/// empty tipping trailer; A brings a combine with 30 sacks of wheat alongside, the auger over B's
/// bin: the sacks go across through the server and both peers agree (A's tank empty, B's trailer
/// 30). A, far from the co-op, is refused a delivery it claims to make at its door. B drives to the
/// stand-in co-op (<c>--farmcoop</c>), is refused seed, tips the trailer: the server pays 30 × 25
/// CHF, B's load is gone and A sees the bin up and the trailer empty.</item>
/// </list>
/// </summary>
public partial class TractorNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--tractornet");

    public TractorNetProbe(ItemController items) : base(items, "tractornet", "TN") { }
    public TractorNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private FootPlayer? Other(FootPlayer me)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != me && !p.Npc) return p;
        return null;
    }

    private static HeavySpec Tractor => HeavyCatalog.All.First(h => h.Class == HeavyClass.FarmTractor);
    private static HeavySpec Combine => HeavyCatalog.All.First(h => h.Class == HeavyClass.Combine);
    private static int Index(TrailerBody body) => System.Array.FindIndex(TrailerCatalog.All.ToArray(), t => t.Body == body);

    private static VehicleBody? Parked() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .FirstOrDefault(v => v.Ride is Truck { Spec.Class: HeavyClass.FarmTractor } && !v.IsQueuedForDeletion());

    private async Task RunA(FootPlayer me)
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        Expect(me.SetRide(Tractor.Kind) && me.Vehicle is Truck, "A takes the tractor");
        if (me.Vehicle is not Truck tractor) return;
        await Seconds(1.5);
        Expect(me.SpawnTrailer(Index(TrailerBody.Plough), 0f) && tractor.Implement != null, "the plough on its linkage");
        me.ToggleLowered(tractor);
        await Seconds(2.5);
        Say("lowered");
        if (!await Heard("B", "seen lowered", 20)) Fail("B did not see the plough down");

        me.ExitVehicle();
        await Until(() => Parked() != null, 10);
        await Seconds(2);
        Say("parked");
        if (!await Heard("B", "seen parked", 20)) Fail("B did not see the parked tractor");

        if (Parked() is { } parked)
        {
            me.PlaceAt(parked.ToGlobal(parked.Ride.EntryPoint) + Vector3.Up * 0.5f, parked.Rotation.Y);
            await Until(() => me.IsOnFloor(), 5);
        }
        Expect(me.TryGetIn() && await Until(() => me.Vehicle is Truck { Implement: not null, Lowered: true }, 8), "A back in, the plough still down");
        if (me.Vehicle is not Truck again) return;
        again.Lowered = false;
        me.ToggleCouple(again);
        await Seconds(1);
        // away from the plough just dropped, then the tipping trailer with 77 sacks
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Seconds(3);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.2f, 8);
        me.RideControls = null;
        Expect(me.SpawnTrailer(Index(TrailerBody.Tipper), 77f / 200f) && again.TrailerTank.Items == 77, $"a tipping trailer with {again.TrailerTank.Items} sacks of {again.TrailerTank.Crop}");
        await Seconds(2.5);
        Say("tipper");
        if (!await Heard("B", "seen tipper", 20)) Fail("B did not see the trailer's load");

        me.ExitVehicle();
        await Seconds(3);
        Say("parked tipper");
        if (!await Heard("B", "seen parked tipper", 20)) Fail("B did not see the parked trailer's load");

        me.PlaceAt(me.GlobalPosition + new Vector3(0f, 0.5f, 40f), 0f);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(1);
        Expect(me.SetRide(Combine.Kind) && me.Vehicle is Truck, "A takes the combine");
        if (me.Vehicle is Truck combine) combine.SetTank(new Tank(CropKind.Barley, 50));
        await Seconds(2.5);
        Say("combine");
        if (!await Heard("B", "seen combine", 20)) Fail("B did not see the combine's tank");
        await LogisticsA(me);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        me.PlaceAt(me.GlobalPosition + new Vector3(25f, 0.5f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");

        if (!await Heard("A", "lowered", 60)) { Fail("A never lowered the plough"); return; }
        var a = Other(me);
        bool seen = a != null && await Until(() => a.RideModel is Truck { Implement.Body: TrailerBody.Plough, Lowered: true }, 10);
        Expect(seen, "B sees the plough on A's tractor, down");
        var lift = a?.GetNodeOrNull<Node3D>("Section1/Visual/Body/Lift");
        Expect(lift != null && await Until(() => lift.Position.Y < 0.05f, 5), $"and drawn down on A's rig ({lift?.Position.Y.ToString("F2", CultureInfo.InvariantCulture)} m)");
        Say("seen lowered");

        if (!await Heard("A", "parked", 30)) { Fail("A never parked"); return; }
        bool parked = await Until(() => Parked() is { Ride: Truck { Implement.Body: TrailerBody.Plough, Lowered: true } }, 10);
        Expect(parked, $"B sees the parked tractor keep its plough, down (train {Parked()?.Capture().Train}, flags {Parked()?.Capture().Flags})");
        Say("seen parked");

        if (!await Heard("A", "tipper", 60)) { Fail("A never took the tipping trailer"); return; }
        a = Other(me);
        bool load = a != null && await Until(() => a.RideModel is Truck { TrailerTank.Items: 77 } t && t.TrailerTank.Crop == CropKind.Wheat, 10);
        Expect(load, $"B sees 77 sacks of wheat in A's trailer ({(a?.RideModel as Truck)?.TrailerTank})");
        var heap = a?.GetNodeOrNull<Node3D>("Section2/Visual/Body/Tip/Heap");
        Expect(heap != null && heap.Visible && Mathf.Abs(heap.Scale.Y - 77f / 200f) < 0.02f, $"and the heap on its rig ({heap?.Scale.Y.ToString("F2", CultureInfo.InvariantCulture)})");
        Say("seen tipper");

        if (!await Heard("A", "parked tipper", 30)) { Fail("A never parked the train"); return; }
        bool kept = await Until(() => Parked() is { } v && TrailerCatalog.TankOf(v.Capture().Train) == new Tank(CropKind.Wheat, 77), 10);
        Expect(kept, $"B sees the parked train keep the 77 sacks ({(Parked() is { } pv ? TrailerCatalog.TankOf(pv.Capture().Train) : default)})");
        Say("seen parked tipper");

        if (!await Heard("A", "combine", 60)) { Fail("A never took the combine"); return; }
        a = Other(me);
        bool tank = a != null && await Until(() => a.RideModel is Truck { Spec.Class: HeavyClass.Combine } c && c.Tank == new Tank(CropKind.Barley, 50), 10);
        Expect(tank, $"B reads 50 sacks of barley in A's combine ({(a?.RideModel as Truck)?.Tank})");
        Say("seen combine");
        await LogisticsB(me);
    }

    // ---- logistics: the auger into a driven trailer, delivering at the co-op (#494) -------------

    /// <summary>The stand-in co-op's door on this peer (<see cref="FarmMarket.StandIn"/> from <c>--farmcoop</c>), or null.</summary>
    private static Interiors.DoorIndex.Entry? Coop() => Interiors.DoorIndex.Find(new Interiors.DoorKey(new Interiors.BuildingKey(FarmMarket.StandInTile.E, FarmMarket.StandInTile.N, 0)));

    private const int Augered = 30;

    private async Task LogisticsA(FootPlayer me)
    {
        if (!await Heard("B", "trailer waiting", 60)) { Fail("B never brought the empty tipping trailer"); return; }
        var b = Other(me);
        // the combine alongside B's bin, heading the same way, its spout over the bin's middle
        if (b?.TipperBinFrame() is not { } bin || b.RideModel is not Truck { } bt) { Fail("A does not see B's tipping trailer"); return; }
        var binBody = bt.Train.Bodies[bt.SectionCount - 1];
        var binMiddle = bin * new Vector3(0f, 0f, binBody.Spec.Length * 0.5f - binBody.CgAt);
        me.ExitVehicle();
        await Seconds(2);
        float yaw = bin.Basis.GetEuler().Y;
        var spoutLocal = Avatar.FarmMeshBuilder.AugerSpout(Combine.Sections[0] is { } s0 ? new HeavyTrain.Body(s0, 0f).CgAt : 0f);
        var at = binMiddle - new Basis(Vector3.Up, yaw) * (spoutLocal with { Y = 0 });
        me.PlaceAt(at with { Y = me.GlobalPosition.Y + 0.5f }, yaw);
        await Until(() => me.IsOnFloor(), 5);
        Expect(me.SetRide(Combine.Kind) && me.Vehicle is Truck, "A takes a combine alongside B's trailer");
        if (me.Vehicle is not Truck combine) return;
        await Seconds(2);
        combine.SetTank(new Tank(CropKind.Wheat, Augered));
        var spout = me.ToGlobal(Avatar.FarmMeshBuilder.AugerSpout(combine.Train.Bodies[0].CgAt));
        Expect(b.TipperBinHas(spout, 0.6f), $"its spout over B's bin ({F(spout.DistanceTo(binMiddle))} m from its middle)");
        me.FarmAction(combine);
        Expect(combine.AugerOut, "A swings the auger out");
        bool emptied = await Until(() => combine.Tank.Items == 0, 30);
        Expect(emptied && me.FarmSacksAugered == Augered, $"A's tank empties into B's trailer ({combine.Tank.Items} left, {me.FarmSacksAugered} moved)");
        bool seen = await Until(() => Other(me)?.RideModel is Truck { TrailerTank: var t } && t == new Tank(CropKind.Wheat, Augered), 10);
        Expect(seen, $"A sees the {Augered} sacks in B's trailer ({(Other(me)?.RideModel as Truck)?.TrailerTank})");
        me.FarmAction(combine);
        Say("augered");
        if (!await Heard("B", "seen augered", 30)) Fail("B did not agree on the sacks");

        // a lie: A's claimed position is the co-op's door, its replicated one is far from it
        if (Coop() is { } door)
        {
            int answer = -1;
            Loot.ShopService.Instance?.Deliver(ItemId.Wheat, 10, door.World + door.Outward * 3f, "", f => answer = f);
            await Until(() => answer >= 0, 10);
            Expect(answer == 0, $"the server refuses A a delivery claimed at the co-op's door from {F(me.GlobalPosition.DistanceTo(door.World))} m away");
        }
        else Fail("A has no stand-in co-op (--farmcoop)");

        if (!await Heard("B", "tipped", 90)) { Fail("B never tipped at the co-op"); return; }
        b = Other(me);
        bool tipped = b != null && await Until(() => b.RideModel is Truck { Tipping: true, TrailerTank.Items: 0 }, 10);
        Expect(tipped, $"A sees B's trailer tipped and empty ({(b?.RideModel as Truck)?.TrailerTank})");
        var tip = b?.GetNodeOrNull<Node3D>("Section2/Visual/Body/Tip");
        Expect(tip != null && await Until(() => tip.Rotation.X > 0.6f, 6), $"and its bin drawn up on B's rig ({F(Mathf.RadToDeg(tip?.Rotation.X ?? 0f))}°)");
        Say("seen tipped");
    }

    private async Task LogisticsB(FootPlayer me)
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        if (Coop() is not { } door) { Fail("B has no stand-in co-op (--farmcoop)"); return; }
        // 75 m out in front of the co-op's door (it faces south), facing it
        me.PlaceAt(door.World + new Vector3(0f, 0.5f, 75f), 0f);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(1);
        Expect(me.SetRide(Tractor.Kind) && me.Vehicle is Truck, "B takes a tractor");
        if (me.Vehicle is not Truck tractor) return;
        await Seconds(1.5);
        Expect(me.SpawnTrailer(Index(TrailerBody.Tipper), 0f) && tractor.TrailerCapacity > 0 && tractor.TrailerTank.Items == 0, "with an empty tipping trailer");
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, Handbrake: true);
        await Seconds(2);
        Say("trailer waiting");

        if (!await Heard("A", "augered", 90)) { Fail("A never augered"); return; }
        bool got = await Until(() => tractor.TrailerTank == new Tank(CropKind.Wheat, Augered), 10);
        Expect(got && me.FarmSacksAugered == Augered, $"B's trailer took the {Augered} sacks ({tractor.TrailerTank}, {me.FarmSacksAugered} augered in)");
        var a = Other(me);
        bool empty = a != null && await Until(() => a.RideModel is Truck { Spec.Class: HeavyClass.Combine, Tank.Items: 0 }, 10);
        Expect(empty, $"and B sees A's combine tank empty ({(a?.RideModel as Truck)?.Tank})");
        var heap = me.GetNodeOrNull<Node3D>("Section2/Visual/Body/Tip/Heap");
        Expect(heap != null && heap.Visible && Mathf.Abs(heap.Scale.Y - Augered / 200f) < 0.02f, $"the heap in B's own bin ({F(heap?.Scale.Y ?? 0f, "F2")})");
        Say("seen augered");

        // to the co-op, then tip
        await Seconds(3);
        me.RideControls = () => new RideInput(0.5f, 0f, 0f, false);
        await Until(() => me.GlobalPosition.DistanceTo(door.World) < 18f, 40);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.2f, 15);
        await Seconds(1);
        Expect(me.CanDeliver(tractor), $"B stopped {F(me.GlobalPosition.DistanceTo(door.World))} m from the co-op's door");
        int seed = -1;
        Loot.ShopService.Instance?.Deliver(ItemId.WheatSeed, 5, me.GlobalPosition, door.Building.ToString(), f => seed = f);
        await Until(() => seed >= 0, 10);
        Expect(seed == 0, "the server refuses seed at the co-op");
        int cash = ItemController.Instance?.Inventory.Cash ?? 0;
        me.FarmAction(tractor);
        bool paid = await Until(() => tractor.TrailerTank.Items == 0, 15);
        int gained = (ItemController.Instance?.Inventory.Cash ?? 0) - cash;
        // the market's price at this co-op this week (#494, Farming.FarmPrices: season and wishes)
        int due = (int)Farming.FarmPrices.Delivery(ItemDefs.Get(ItemId.Wheat)?.Value ?? 0f, ItemId.Wheat, Augered, Farming.FarmSales.Month, door.Building.ToString(), Farming.FarmSales.Week);
        Expect(paid && tractor.Tipping && me.FarmFrancsPaid == due && gained == due, $"B tips at the co-op: paid {me.FarmFrancsPaid} CHF (pocket +{gained}, due {due}), the trailer {tractor.TrailerTank.Items}");
        Say("tipped");
        if (!await Heard("A", "seen tipped", 30)) Fail("A did not see the tipped, empty trailer");
        me.RideControls = null;
    }

    private static string F(float v, string f = "F1") => v.ToString(f, CultureInfo.InvariantCulture);
}
