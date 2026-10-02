using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;

namespace UnitSport.Crafting;

/// <summary>
/// Where the player can craft right now (#271). Hands always; a workbench when one stands within
/// <see cref="BenchReach"/> on the player's floor, whichever way they face — the panel is open, so
/// they are not looking at it.
/// </summary>
public static class CraftStations
{
    public const float BenchReach = 2.2f;

    public static Station At(FootPlayer? p)
    {
        var here = Station.Hands;
        if (p != null && InteriorManager.Instance is { Current: { } layout, CurrentNode: { } node }
            && LootService.NearestOf(p, layout, node, t => t == FurnitureType.Workbench, BenchReach, facing: false) >= 0)
            here |= Station.Workbench;
        return here;
    }

    /// <summary>What to call a station in the panel.</summary>
    public static string Name(Station s) => s switch
    {
        Station.Workbench => "Workbench",
        _ => "Hands",
    };

    /// <summary>Where to find one, for a recipe shown but out of reach.</summary>
    public static string WhereToFind(Station s) => s switch
    {
        Station.Workbench => "Needs a workbench: workshops, garages and works have one.",
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
