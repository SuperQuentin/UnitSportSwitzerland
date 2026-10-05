using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;

namespace UnitSport.Crafting;

/// <summary>
/// Where the player can craft right now (#271, #272). Hands always; a workbench when one stands within
/// <see cref="BenchReach"/> on the player's floor (or a placed field workbench does), a fire within
/// <see cref="FireReach"/>: a burning placed campfire, or a kitchen stove indoors. Whichever way they
/// face — the panel is open, so they are not looking at it.
/// </summary>
public static class CraftStations
{
    public const float BenchReach = 2.2f;
    public const float FireReach = 3f;

    /// <summary>What is in reach, and how the panel's header names it.</summary>
    public readonly record struct Spot(Station Here, string Label);

    public static Station At(FootPlayer? p) => Where(p).Here;

    public static Spot Where(FootPlayer? p)
    {
        var here = Station.Hands;
        var names = new List<string>();
        if (p == null) return new Spot(here, "By hand");

        if (InteriorManager.Instance is { Current: { } layout, CurrentNode: { } node })
        {
            if (LootService.NearestOf(p, layout, node, t => t == FurnitureType.Workbench, BenchReach, facing: false) >= 0)
            {
                here |= Station.Workbench;
                names.Add("a workbench");
            }
            if (LootService.NearestOf(p, layout, node, t => t == FurnitureType.Stove, FireReach, facing: false) >= 0)
            {
                here |= Station.Fire;
                names.Add("a stove");
            }
        }

        if (PlacedObjects.Instance is { } placed)
        {
            double now = Net.ClockSync.ServerUnixNow;
            var at = p.GlobalPosition;
            foreach (var o in placed.All.Values)
            {
                if (o.Kind is not (PlacedKind.Campfire or PlacedKind.FieldWorkbench)) continue;
                float d = o.WorldTransform(placed.Origin).Origin.DistanceTo(at);
                if (o.Kind == PlacedKind.Campfire && (here & Station.Fire) == 0 && d <= FireReach && CampfireClock.Burning(o.Payload, now))
                {
                    here |= Station.Fire;
                    names.Add("a campfire");
                }
                else if (o.Kind == PlacedKind.FieldWorkbench && (here & Station.Workbench) == 0 && d <= BenchReach)
                {
                    here |= Station.Workbench;
                    names.Add("a field workbench");
                }
            }
        }
        return new Spot(here, names.Count == 0 ? "By hand" : "At " + string.Join(", ", names));
    }

    /// <summary>What to call a station in the panel.</summary>
    public static string Name(Station s) => s switch
    {
        Station.Workbench => "Workbench",
        Station.Fire => "Fire",
        _ => "Hands",
    };

    /// <summary>Where to find one, for a recipe shown but out of reach.</summary>
    public static string WhereToFind(Station s) => s switch
    {
        Station.Workbench => "Needs a workbench: workshops, garages and works have one, or set up a field workbench.",
        Station.Fire => "Needs a fire: a burning campfire (made by hand) or a kitchen stove.",
        _ => "",
    };
}

/// <summary>The real inventory as crafting sees it: plain stacks only, and outputs through <see cref="ItemController.Give"/> (no room: dropped at your feet).</summary>
public sealed class InventoryStore : IItemStore
{
    private readonly ItemController _items;

    public InventoryStore(ItemController items) => _items = items;

    public int Count(ItemId id) => _items.Inventory.CountPlain(id);
    public void Take(ItemId id, int count) => _items.Inventory.TakePlain(id, count);
    public void Give(ItemId id, int count) => _items.Give(new ItemStack(id, count));
}
