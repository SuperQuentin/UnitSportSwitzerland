using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Farming;

/// <summary>
/// <c>--coopnet</c> with <c>--connect</c> (driven by <c>tools/coopnetcheck.sh</c>), #494: a real farm
/// co-op on the real map, delivered to over loopback. The dedicated server draws no doors, so it plans
/// the building the client names (<c>ShopService.CoopPlanNear</c> → <c>InteriorManager.GetOrCreate</c>)
/// and checks it is a co-op and the peer's own body is by its door. The client finds the nearest
/// co-op among its loaded doors, takes a combine with 40 sacks of wheat into its yard and delivers:
/// paid the market's price, the server names the real building. Refused: the co-op's key claimed
/// from 200 m off, and another building's key at that building's own door.
/// </summary>
public partial class CoopNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--coopnet");

    public CoopNetProbe(ItemController items) : base(items, "coopnet", "CN") { }
    public CoopNetProbe() : this(null!) { }

    private const int Sacks = 40;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240)) { await Finish(0); return; }
        try { await Run(Me!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        await Finish(1.5);
    }

    private async Task Run(FootPlayer me)
    {
        Chat?.Send("/login test");
        // the doors index as the buildings round the spot are drawn
        await Until(() => me.IsOnFloor() && DoorIndex.Nearest(me.GlobalPosition, 400f) != null, 120);
        await Seconds(8);
        var start = me.GlobalPosition;
        var coops = DoorIndex.All().Where(d => d.Shop == ShopType.FarmCoop).OrderBy(d => d.World.DistanceTo(start)).ToList();
        GD.Print($"{Log} {DoorIndex.All().Count()} doors loaded, {coops.Count} at farm co-ops");
        if (coops.Count == 0) { Fail("no farm co-op among the loaded doors: try another --at"); return; }
        var door = coops[0];
        string key = door.Building.ToString();
        Say($"coop {key}");
        Expect(true, $"the nearest co-op {key}, {F(door.World.DistanceTo(start))} m off");

        // into its yard with a loaded combine, facing the door
        var outward = (door.Outward with { Y = 0 }).Normalized();
        var yard = door.World + outward * 12f;
        me.PlaceAt(yard + Vector3.Up * 1.5f, Mathf.Atan2(outward.X, outward.Z));
        await Until(() => me.IsOnFloor(), 10);
        await Seconds(1);
        var combineSpec = HeavyCatalog.All.First(h => h.Class == HeavyClass.Combine);
        Expect(me.SetRide(combineSpec.Kind) && me.Vehicle is Truck, "a combine in the co-op's yard");
        if (me.Vehicle is not Truck combine) return;
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, Handbrake: true);
        await Seconds(3);
        combine.SetTank(new Tank(CropKind.Wheat, Sacks));
        await Until(() => me.GroundSpeed < 0.2f, 10);
        Expect(FarmMarket.NearCoop(me.GlobalPosition) && me.CanDeliver(combine),
            $"the delivery is offered {F(me.GlobalPosition.DistanceTo(door.World))} m from the door ({FarmSales.Instance?.DeliveryHint(ItemId.Wheat, Sacks, me.GlobalPosition)})");
        int due = (int)FarmPrices.Delivery(ItemDefs.Get(ItemId.Wheat)?.Value ?? 0f, ItemId.Wheat, Sacks, FarmSales.Month, key, FarmSales.Week);
        int cash = _items.Inventory.Cash;
        me.FarmAction(combine);
        bool paid = await Until(() => combine.Tank.Items == 0 && _items.Inventory.Cash != cash, 30);
        Expect(paid && _items.Inventory.Cash - cash == due, $"the server planned the co-op and paid {_items.Inventory.Cash - cash} CHF (due {due}), the tank {combine.Tank.Items}");

        // its key, claimed from 200 m off: the server reads where the peer is, not the claim
        me.RideControls = null;
        me.SetRide(RideKind.OnFoot);
        await Seconds(1);
        var away = door.World + outward * 200f;
        me.PlaceAt(away + Vector3.Up * 30f, 0f);
        await Until(() => me.IsOnFloor(), 20);
        await Seconds(1.5);
        int answer = await Ask(ItemId.Wheat, 10, door.World + outward * 3f, key);
        Expect(answer == 0, $"refused: the co-op's key claimed {F(me.GlobalPosition.DistanceTo(door.World))} m from its door");

        // another building's key at its own door: planned, not a co-op
        var other = DoorIndex.All().Where(d => d.Shop != ShopType.FarmCoop && d.Vehicle == false && d.Building != door.Building
                && DoorIndex.Nearest(d.World, FarmMarket.DeliverReach + FarmMarket.ServerSlack + 5f, ShopType.FarmCoop) == null)
            .OrderBy(d => d.World.DistanceTo(start)).Cast<DoorIndex.Entry?>().FirstOrDefault();
        if (other is not { } o) { Fail("no other building's door to try"); return; }
        me.PlaceAt(o.World + (o.Outward with { Y = 0 }).Normalized() * 3f + Vector3.Up * 1.5f, 0f);
        await Until(() => me.IsOnFloor(), 20);
        await Seconds(1.5);
        answer = await Ask(ItemId.Wheat, 10, me.GlobalPosition, o.Building.ToString());
        Expect(answer == 0, $"refused: {o.Building} ({o.Shop}) is not a co-op, at its door");
        Say("done");
    }

    /// <summary>A delivery asked of the server; the francs it answers (0: refused).</summary>
    private async Task<int> Ask(ItemId id, int count, Vector3 at, string door)
    {
        int answer = -1;
        ShopService.Instance?.Deliver(id, count, at, door, f => answer = f);
        await Until(() => answer >= 0, 30);
        return answer;
    }

    private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
