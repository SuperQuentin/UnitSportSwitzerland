using Godot;
using UnitSport.Items;

namespace UnitSport.Interiors.Games;

// The arcade's coins and tickets (#1326). The inventory is the client's (as cash at a shop: the server cannot see the
// pocket), so a client pays its Batzen coin itself before it asks a machine for a game, and the server pays tickets by
// telling the winner's client, which adds them. Bots pay nothing and are paid nothing.
public partial class TableGames
{
    /// <summary>Client: the local player's items: where coins are paid from and tickets go.</summary>
    public ItemController? Items { get; set; }

    /// <summary>
    /// Client: pays one Batzen coin for a game, or says where to get one. True when paid (with no inventory to pay
    /// from, a check's run, it is free).
    /// </summary>
    public bool PayCoin()
    {
        if (Items == null) return true;
        if (Items.Inventory.TakePlain(ItemId.ArcadeCoin, 1) == 1)
        {
            ArcadeSounds.Play(ArcadeSounds.Coin);
            return true;
        }
        Told?.Invoke("No Batzen: the change machine by the door swaps francs for them.");
        return false;
    }

    /// <summary>Server: what the games paid out since last time, to their humans' clients.</summary>
    private void PayOut(TableGame g)
    {
        if (g.Payouts.Count == 0) return;
        foreach (var (peer, item, count, why) in g.Payouts)
        {
            if (IsBot(peer)) continue;
            GD.Print($"[games] {NameOf(peer)} wins {count} {item} ({why})");
            if (peer == MyId || !Online) Won((int)item, count, why);
            else if (peer > 0 && Multiplayer.GetPeers().Contains((int)peer)) RpcId(peer, MethodName.Won, (int)item, count, why);
        }
        g.Payouts.Clear();
    }

    /// <summary>Client: tickets or a prize won, into the pack (a prize that does not fit drops at the player's feet).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Won(int item, int count, string why)
    {
        var id = (ItemId)item;
        if (id == ItemId.PrizeTicket) TicketsWon += count;
        else PrizesWon += count;
        if (Items == null) return;
        int left = Items.Inventory.Add(id, count);
        if (left > 0) Items.DropStack(null, new ItemStack(id, left));
        Items.Toast(id == ItemId.PrizeTicket ? $"+{count} prize tickets ({why})" : $"{ItemDefs.Get(id)?.Name} from {why}!");
        ArcadeSounds.Play(id == ItemId.PrizeTicket ? ArcadeSounds.Tickets : ArcadeSounds.Coins);
    }

    /// <summary>Client: the tickets and prizes this peer was paid since it started (the checks read them).</summary>
    public int TicketsWon { get; private set; }
    public int PrizesWon { get; private set; }
}
