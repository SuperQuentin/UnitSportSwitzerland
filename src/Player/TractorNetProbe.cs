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
        var heap = a?.GetNodeOrNull<Node3D>("Section2/Visual/Body/Heap");
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
    }
}
