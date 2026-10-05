using Godot;
using UnitSport.Items;

namespace UnitSport.Farming;

/// <summary>
/// Selling a harvest by the load at a farm co-op (#494): a combine's tank or a tipping trailer
/// driven up to a co-op's yard is weighed and paid into the driver's pocket.
///
/// <para>PINNED INTERFACE: the vehicles code calls it; the economy side implements it (STUB).</para>
/// </summary>
public static class FarmMarket
{
    /// <summary>How close to a co-op's door a load must stand to be delivered, m.</summary>
    public const float DeliverReach = 25f;

    /// <summary>Whether a farm co-op's yard is within <see cref="DeliverReach"/> of <paramref name="at"/> (world).</summary>
    public static bool NearCoop(Vector3 at) => false;

    /// <summary>
    /// Sell <paramref name="count"/> of <paramref name="item"/> at the co-op by <paramref name="at"/>:
    /// asks the server (offline: this peer plays it), and calls <paramref name="done"/> on the main
    /// thread with the francs added to the pocket (0: refused, nothing sold).
    /// </summary>
    public static void Deliver(Node from, Vector3 at, ItemId item, int count, System.Action<int> done) => done(0);
}
