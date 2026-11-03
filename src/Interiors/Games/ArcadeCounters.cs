using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors.Games;

/// <summary>
/// An arcade's change machine and prize counter (#1326). E (pad Y, a VR hand reaching out) at the change machine
/// swaps <see cref="Change"/> francs of cash for as many Batzen coins; at the prize counter it opens
/// <see cref="PrizeCounterUi"/>, where tickets buy prizes (<see cref="Prizes"/>). Both are the local player's own
/// pocket, as a vending machine's cash is: nothing goes to the server, and bots have no use for them.
/// </summary>
public static class ArcadeCounters
{
    /// <summary>Francs a press of the change machine swaps, for one Batzen each.</summary>
    public const int Change = 5;
    private const float Reach = 1.4f;

    /// <summary>What the prize counter swaps, and for how many tickets: the cheap things low, the big plush on the top shelf.</summary>
    public static readonly (ItemId Item, int Tickets)[] Prizes =
    {
        (ItemId.Keyring, 20), (ItemId.BouncyBall, 30), (ItemId.SweetsBag, 40),
        (ItemId.PlushMarmot, 150), (ItemId.PlushCow, 200), (ItemId.PlushBernese, 400),
    };

    private static PrizeCounterUi? _ui;

    private static (InteriorNode Node, int Index, FurnitureType Type)? At(FootPlayer p)
    {
        if (!p.Indoors || TableGamePlay.Current != null || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        int index = Loot.LootService.NearestOf(p, layout, node, t => t is FurnitureType.ChangeMachine or FurnitureType.PrizeCounter, Reach);
        return index < 0 ? null : (node, index, layout.Furniture[index].Type);
    }

    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (At(p) is not { } at || TableGames.Instance?.Items == null) return null;
        string text = at.Type == FurnitureType.ChangeMachine ? $"Change {Change} CHF for {Change} Batzen" : "The prize counter";
        var f = at.Node.Layout.Furniture[at.Index];
        var frame = TableGames.PieceFrame(at.Node.Layout, f) * new Transform3D(Basis.Identity, new Vector3(0, f.H / 2, 0));
        return new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, text), Outline.Box(at.Node, frame, new Vector3(f.W, f.H, f.D)));
    }

    public static bool TryUse(FootPlayer p)
    {
        if (At(p) is not { } at || TableGames.Instance?.Items is not { } items) return false;
        if (at.Type == FurnitureType.ChangeMachine) Swap(items.Inventory, items.Toast);
        else Open(items);
        return true;
    }

    /// <summary>The change machine: cash in, Batzen out (a clatter of coins), or why not.</summary>
    public static bool Swap(Inventory inv, Action<string> say)
    {
        if (inv.Cash < Change)
        {
            say($"The change machine takes {Change} CHF: you hold {inv.Cash}.");
            return false;
        }
        inv.TakeCash(Change);
        int left = inv.Add(ItemId.ArcadeCoin, Change);
        if (left > 0) inv.Add(ItemId.Francs, left);
        say($"+{Change - left} Batzen");
        ArcadeSounds.Play(ArcadeSounds.Coins);
        return true;
    }

    /// <summary>The prize counter: one prize for its tickets, or why not.</summary>
    public static bool Buy(Inventory inv, ItemId prize, Action<string> say)
    {
        int price = Array.Find(Prizes, p => p.Item == prize).Tickets;
        if (price <= 0) return false;
        int have = inv.CountPlain(ItemId.PrizeTicket);
        if (have < price)
        {
            say($"{ItemDefs.Get(prize)?.Name} takes {price} tickets: you hold {have}.");
            return false;
        }
        if (inv.Add(prize, 1) > 0)
        {
            say("Your pack is full.");
            return false;
        }
        inv.TakePlain(ItemId.PrizeTicket, price);
        say($"{ItemDefs.Get(prize)?.Name} for {price} tickets");
        return true;
    }

    private static void Open(ItemController items)
    {
        if (_ui == null || !GodotObject.IsInstanceValid(_ui))
        {
            _ui = new PrizeCounterUi { Name = "PrizeCounterUi" };
            (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(_ui);
        }
        _ui.Open(items.Inventory, items.Toast);
    }
}
