using UnitSport.Player;

namespace UnitSport.Items;

// TEMPORARY (#208): stands in for #206's DroppedItems until feat/206-drop-throw lands; removed on rebase.
public partial class ItemController
{
    /// <summary>Puts a stack that is in no slot on the ground in front of the player. False: nowhere to put it.</summary>
    public bool DropStack(FootPlayer? player, ItemStack stack)
    {
        _ui.Toast("Dropping on the ground is not available yet.");
        return false;
    }

    /// <summary>Drops from a slot (one, or the whole stack).</summary>
    public void DropSlot(FootPlayer? player, int slot, bool all)
    {
        var stack = _inventory[slot];
        if (stack.IsEmpty) return;
        var taken = _inventory.TakeFrom(slot, all ? stack.Count : 1);
        if (!DropStack(player, taken)) _inventory.Add(taken);
    }
}
