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
        foreach (var (peer, tickets, why) in g.Payouts)
        {
            if (IsBot(peer)) continue;
            GD.Print($"[games] {NameOf(peer)} wins {tickets} tickets ({why})");
            if (peer == MyId || !Online) Tickets(tickets, why);
            else if (peer > 0 && Multiplayer.GetPeers().Contains((int)peer)) RpcId(peer, MethodName.Tickets, tickets, why);
        }
        g.Payouts.Clear();
    }

    /// <summary>Client: tickets won, into the pack.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tickets(int tickets, string why)
    {
        TicketsWon += tickets;
        if (Items == null) return;
        Items.Inventory.Add(ItemId.PrizeTicket, tickets);
        Items.Toast($"+{tickets} prize tickets ({why})");
        ArcadeSounds.Play(ArcadeSounds.Tickets);
    }

    /// <summary>Client: the tickets this peer was paid since it started (the checks read it).</summary>
    public int TicketsWon { get; private set; }
}
