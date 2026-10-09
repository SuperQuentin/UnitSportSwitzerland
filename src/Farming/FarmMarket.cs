using Godot;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// Selling a harvest by the load at a farm co-op (#494): a combine's tank or a tipping trailer
/// driven up to a co-op's yard is weighed and paid into the driver's pocket.
///
/// <para>
/// A farm co-op is a building whose door sign says so (<see cref="ShopType.FarmCoop"/>, set by
/// <see cref="ShopTables.TypeFor"/>: a big agricultural building or a village shop on a rural tile),
/// found through <see cref="DoorIndex"/>. <see cref="Deliver"/> pays the **full producer price**
/// (<see cref="ItemDef.Value"/>) of every unit (<see cref="ShopTables.DeliveryPrice"/>); the same
/// co-op's counter pays only 35 % for a pack item. Only harvests are taken (`FarmTables.IsHarvest`: not seeds or fertiliser). The
/// server checks the peer's own replicated position against the co-op's door (outdoors, no counter
/// to stand at); the pocket is the client's, so the client adds the cash on the server's answer
/// (<see cref="ShopService"/>), and offline this peer plays the server.
/// </para>
/// </summary>
public static class FarmMarket
{
    /// <summary>How close to a co-op's door a load must stand to be delivered, m.</summary>
    public const float DeliverReach = 25f;

    /// <summary>The server allows a bit more than the client: its copy of the vehicle lags behind.</summary>
    public const float ServerSlack = 12f;

    /// <summary>A load counts at most this many units a call (the server's sanity limit).</summary>
    public const int MaxLoad = 999;

    private static Vector3 _lastAt = new(float.NaN, 0, 0);
    private static double _lastAsked;
    private static bool _lastNear;

    /// <summary>
    /// Whether a farm co-op's yard is within <see cref="DeliverReach"/> of <paramref name="at"/> (world).
    /// Cached for half a second or 4 m of travel, so a vehicle may ask every frame.
    /// </summary>
    public static bool NearCoop(Vector3 at)
    {
        double now = Core.RealClock.Now;   // a lookup cache, real time
        if (!float.IsNaN(_lastAt.X) && now - _lastAsked < 0.5 && _lastAt.DistanceSquaredTo(at) < 16f) return _lastNear;
        _lastAt = at;
        _lastAsked = now;
        // a specialty buyer's yard is a market too (#494, FarmSales / FarmBuyers)
        return _lastNear = CoopDoor(at, DeliverReach) != null || FarmSales.Instance?.BuyerNear(at) != null;
    }

    /// <summary>The nearest farm co-op's door within <paramref name="reach"/> of a point, or null.</summary>
    public static DoorIndex.Entry? CoopDoor(Vector3 at, float reach) =>
        DoorIndex.Nearest(at, reach, ShopType.FarmCoop);

    // ---- a stand-in co-op for the checks (a fixture world has no buildings) ----

    /// <summary>The made-up tile the stand-in's door is filed under in <see cref="DoorIndex"/>: no real tile has it.</summary>
    public static readonly TileId StandInTile = new(-1, -1);

    /// <summary>
    /// A farm co-op's door at <paramref name="door"/> (world, on the ground) facing
    /// <paramref name="outward"/>, with no building behind it: what a check delivers to. Replaces
    /// the previous stand-in. Main thread.
    /// </summary>
    public static void StandIn(Vector3 door, Vector3 outward)
    {
        var spot = new DoorSpot(0, door, outward.Normalized(), 6f, 4.5f) { Kind = BuildingKind.Commercial, Shop = ShopType.FarmCoop };
        DoorIndex.SetTile(StandInTile, Vector3.Zero, [spot]);
        _lastAt = new Vector3(float.NaN, 0, 0);
    }

    /// <summary>
    /// <c>--farmcoop E,N</c> (LV95) on a fixture world: the stand-in's door there, facing south (its yard), on
    /// the course's ground. The server and every client are given the same flag, so all agree
    /// where it is (<c>tools/tractornetcheck.sh</c>). Ignored on a real map.
    /// </summary>
    public static void StandInFromArgs(Terrain.IChunkSource source, Core.WorldOrigin origin)
    {
        if (source is not Terrain.Fixture.FixtureChunkSource fixture || Core.SpawnPoint.ParseLv95("--farmcoop") is not var (e, n)) return;
        StandIn(origin.ToWorld(e, n, fixture.GroundAt(e, n)), Vector3.Back);
        GD.Print($"[farm] stand-in farm co-op at LV95 {e:F0}/{n:F0}");
    }

    /// <summary>
    /// Sell <paramref name="count"/> of <paramref name="item"/> at the co-op by <paramref name="at"/>:
    /// asks the server (offline: this peer plays it), and calls <paramref name="done"/> on the main
    /// thread with the francs added to the pocket (0: refused, nothing sold).
    /// </summary>
    public static void Deliver(Node from, Vector3 at, ItemId item, int count, System.Action<int> done)
    {
        if (count <= 0 || count > MaxLoad || !FarmTables.IsHarvest(item) || ItemDefs.Get(item) is not { } def
            || !ShopTables.Buys(ShopType.FarmCoop, def.Category) || !NearCoop(at)
            || ShopService.Instance is not { } shops)
        {
            done(0);
            return;
        }
        shops.Deliver(item, count, at, CoopDoor(at, DeliverReach)?.Building.ToString() ?? "", done);
    }
}
