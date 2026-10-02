using System.Text.Json;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Loot;

/// <summary>
/// Shops and PAUSA vending machines (#273), at <c>World/Shops</c> on the server and on every client
/// (RPCs route by node path, as for <c>World/Loot</c>).
///
/// <para>
/// <b>Stock is computed, never stored</b> (<see cref="ShopTables.Stock"/>, seeded by building,
/// furniture and restock epoch); the server keeps only what was <i>sold</i> per slot
/// (<see cref="ShopLedger"/>, <c>user://shops/E_N.json</c>), checks the buyer stands in that
/// building, that the slot still holds it and that the line takes the payment, and charges a card
/// payment to the bank account itself. Cash is the client's (like a bank deposit, the server cannot
/// see the pocket): the client pays when the server answers. Offline this client plays the server.
/// </para>
/// </summary>
public partial class ShopService : Node
{
    public const string NodeName = "Shops";

    /// <summary>How far from the counter's or the machine's edge it can be used.</summary>
    private const float Reach = 1.4f;
    /// <summary>Walking this far away closes the panel.</summary>
    private const float CloseReach = 3.0f;

    public static ShopService? Instance { get; private set; }

    /// <summary>Client: where bought items go, where cash comes from, where the toasts appear.</summary>
    public ItemController? Items { get; set; }

    /// <summary>Whether the shotgun is sold now: the hunting season (<see cref="HuntingSeasonIn"/>). The server's say.</summary>
    public Func<bool> HuntingSeason { get; set; } = DefaultSeason;

    /// <summary>Probes: the next machine purchase sticks on the spiral (<c>--shopstuck</c> on the server, or offline).</summary>
    public static bool ForceStuck { get; set; }

    private string _storeDir = "";
    private ShopLedger _ledger = null!;
    private readonly Random _luck = new();

    // ---- client state -----------------------------------------------------------------------------

    private ShopUi? _shopUi;
    private VendingUi? _vendingUi;
    private FootPlayer? _user;

    /// <summary>The counter or machine open in a panel: building, furniture index and what it sells.</summary>
    public (string Key, int Furniture, ShopType Type)? Open { get; private set; }
    public long OpenEpoch { get; private set; }
    /// <summary>This period's stock of the open counter or machine (from the tables, with the server's season).</summary>
    public IReadOnlyList<StockLine> OpenStock { get; private set; } = Array.Empty<StockLine>();
    /// <summary>How many of each slot were sold, as the server last said.</summary>
    public int[] OpenSold { get; private set; } = Array.Empty<int>();
    /// <summary>The open machine is "Hors service" this period.</summary>
    public bool OutOfOrder { get; private set; }
    /// <summary>Something caught on the open machine's spiral: a hit frees it.</summary>
    public bool Stuck { get; private set; }
    /// <summary>The server has not answered yet (the stock, a purchase, a sale).</summary>
    public bool Waiting { get; private set; }
    /// <summary>The last outcome, for the panels and the probes.</summary>
    public BuyOutcome? LastOutcome { get; private set; }
    /// <summary>The slot the last item dropped from (the machine's animation), -1 none.</summary>
    public int LastDropped { get; private set; } = -1;

    /// <summary>The open panel's data changed: stock, sold counts, an answer.</summary>
    public event Action? Changed;

    public static ShopService Create(Node world)
    {
        var s = new ShopService { Name = NodeName };
        world.AddChild(s);
        Instance = s;
        return s;
    }

    public override void _Ready()
    {
        _storeDir = ProjectSettings.GlobalizePath("user://shops");
        _ledger = new ShopLedger(LoadTile, SaveTile, InteriorLayout.CurrentVersion);
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shopstuck") >= 0) ForceStuck = true;
        if (DisplayServer.GetName() == "headless" && Multiplayer.IsServer() && Online) return;
        _shopUi = new ShopUi(this) { Name = "ShopUi" };
        AddChild(_shopUi);
        _vendingUi = new VendingUi(this) { Name = "VendingUi" };
        AddChild(_vendingUi);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>What an item is worth (<see cref="ItemDef.Value"/>), the base of every price.</summary>
    public static float ValueOf(ItemId id) => ItemDefs.Get(id)?.Value ?? 0f;

    /// <summary>This month is the hunting season (<c>--birdmonth N</c> pretends another month).</summary>
    private static bool DefaultSeason()
    {
        int month = DateTime.Now.Month;
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--birdmonth");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int m) && m is >= 1 and <= 12) month = m;
        return HuntingSeasonIn(month);
    }

    /// <summary>
    /// The hunting season: the months when at least half the game birds are open
    /// (<c>Birds.BirdSpecies.InSeason</c>). The pigeons and crows are open almost all year, so
    /// "any species" would be every month; this gives September to January, the Swiss season.
    /// </summary>
    public static bool HuntingSeasonIn(int month)
    {
        int game = Birds.BirdCatalog.All.Count(s => s.IsGame);
        return game > 0 && Birds.BirdCatalog.All.Count(s => s.InSeason(month)) * 2 >= game;
    }

    // ---- client: what is in reach -------------------------------------------------------------------

    /// <summary>The shop's counter the player faces, if this building is a shop.</summary>
    public static int NearestCounter(FootPlayer p, InteriorLayout layout, InteriorNode node) =>
        layout.Shop != ShopType.None ? LootService.NearestOf(p, layout, node, t => t == FurnitureType.ShopCounter, Reach) : -1;

    /// <summary>The PAUSA machine the player faces.</summary>
    public static int NearestMachine(FootPlayer p, InteriorLayout layout, InteriorNode node) =>
        LootService.NearestOf(p, layout, node, t => t == FurnitureType.VendingMachine, Reach);

    public bool IsOpen => _shopUi?.IsOpen == true || _vendingUi?.IsOpen == true;

    /// <summary>The prompt line for a counter or a machine in reach, or null.</summary>
    public string? PromptFor(FootPlayer p)
    {
        if (IsOpen) return null;
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        string key = InputHints.Tag(PlayerInput.InteractMount);
        if (NearestMachine(p, layout, node) >= 0) return $"{key} PAUSA vending machine";
        if (NearestCounter(p, layout, node) >= 0) return $"{key} {ShopTables.Name(layout.Shop)}: buy and sell";
        return null;
    }

    /// <summary>E indoors: opens (or closes) the counter or the machine in reach. False when there is none.</summary>
    public bool TryOpen(FootPlayer p)
    {
        if (IsOpen) { Close(); return true; }
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return false;
        int i = NearestMachine(p, layout, node);
        var type = ShopType.Vending;
        if (i < 0) { i = NearestCounter(p, layout, node); type = layout.Shop; }
        if (i < 0) return false;
        OpenAt(p, layout, node, i, type);
        return true;
    }

    /// <summary>Opens the panel of furniture <paramref name="furniture"/> and asks the server for its sold counts.</summary>
    public void OpenAt(FootPlayer p, InteriorLayout layout, InteriorNode node, int furniture, ShopType type)
    {
        _user = p;
        Open = (layout.Key, furniture, type);
        OpenStock = Array.Empty<StockLine>();
        OpenSold = Array.Empty<int>();
        OutOfOrder = Stuck = false;
        LastOutcome = null;
        LastDropped = -1;
        Waiting = true;
        if (type == ShopType.Vending) _vendingUi?.Open(node, layout.Furniture[furniture]);
        else _shopUi?.Open(node, layout.Furniture[furniture], type);
        Play(SfxSynth.Tick, 0.8f);
        Ask();
    }

    private void Ask()
    {
        if (Open is not { } o) return;
        Waiting = true;
        if (Online) RpcId(1, MethodName.RequestStock, o.Key, o.Furniture);
        else ServeStock(1, o.Key, o.Furniture);
    }

    public void Close()
    {
        Open = null;
        _user = null;
        Waiting = false;
        _shopUi?.Close();
        _vendingUi?.Close();
    }

    /// <summary>What is left of a slot, as this client knows it.</summary>
    public int Left(int slot) => slot >= 0 && slot < OpenStock.Count
        ? Math.Max(0, OpenStock[slot].Stock - (slot < OpenSold.Length ? OpenSold[slot] : 0)) : 0;

    public override void _Process(double delta)
    {
        if (Open is not { } o) return;
        var interior = InteriorManager.Instance;
        if (_user is not { } p || !IsInstanceValid(p) || !p.IsViewing || interior?.Current?.Key != o.Key || interior.CurrentNode == null
            || o.Furniture >= interior.Current.Furniture.Count)
        {
            Close();
            return;
        }
        var f = interior.Current.Furniture[o.Furniture];
        var local = interior.CurrentNode.ToLocal(p.GlobalPosition);
        if (new Vector2(local.X - f.X, local.Z - f.Z).Length() > CloseReach + Mathf.Max(f.W, f.D) / 2) Close();
    }

    // ---- client: buying, selling, hitting the machine -------------------------------------------------

    /// <summary>
    /// Buys <paramref name="count"/> of a slot. Cash is checked here (the pocket is this client's) and
    /// paid when the server answers; on the card the server charges the account.
    /// </summary>
    public bool Buy(int slot, int count, PayWith with)
    {
        if (Open is not { } o || Waiting || slot < 0 || slot >= OpenStock.Count || count <= 0 || Items == null) return false;
        var line = OpenStock[slot];
        count = Math.Min(count, Left(slot));
        if (count <= 0) { Say("Sold out."); return false; }
        if (!ShopTables.Accepts(line.Pay, with))
        {
            Say(line.Pay == Payment.Card ? "Card only." : "Cash only.");
            return false;
        }
        if (with == PayWith.Cash && Items.Inventory.Cash < line.Price * count)
        {
            Say($"Not enough cash: {line.Price * count} CHF.");
            return false;
        }
        if (with == PayWith.Card && (Bank.Instance?.Balance ?? 0) < line.Price * count)
        {
            Say("Card declined: the account does not cover it.");
            return false;
        }
        Waiting = true;
        Changed?.Invoke();
        if (Online) RpcId(1, MethodName.RequestBuy, o.Key, o.Furniture, OpenEpoch, slot, count, (int)with);
        else ServeBuy(1, o.Key, o.Furniture, OpenEpoch, slot, count, (int)with);
        return true;
    }

    /// <summary>What the open shop pays for one of these, 0 when it does not buy them.</summary>
    public int SellPriceOf(ItemId id) =>
        Open is { } o && ItemDefs.Get(id) is { } def && ShopTables.Buys(o.Type, def.Category) ? ShopTables.SellPrice(def.Value) : 0;

    /// <summary>Sells <paramref name="count"/> plain stacks of an item to the open shop: the cash comes when the server agrees.</summary>
    public bool Sell(ItemId id, int count)
    {
        if (Open is not { } o || Waiting || Items == null || count <= 0) return false;
        if (SellPriceOf(id) <= 0) { Say($"The {ShopTables.Name(o.Type).ToLowerInvariant()} does not buy that."); return false; }
        count = Math.Min(count, Items.Inventory.CountPlain(id));
        if (count <= 0) return false;
        Waiting = true;
        Changed?.Invoke();
        if (Online) RpcId(1, MethodName.RequestSell, o.Key, o.Furniture, (int)id, count);
        else ServeSell(1, o.Key, o.Furniture, (int)id, count);
        return true;
    }

    /// <summary>A hit on the open machine: what was stuck drops, sometimes with one more.</summary>
    public bool Bump()
    {
        if (Open is not { Type: ShopType.Vending } o || Waiting) return false;
        Waiting = true;
        Play(SfxSynth.Impact, 0.7f);
        if (Online) RpcId(1, MethodName.RequestBump, o.Key, o.Furniture);
        else ServeBump(1, o.Key, o.Furniture);
        return true;
    }

    private void Say(string text)
    {
        Items?.Ui.Toast(text);
        _vendingUi?.Lcd(text);
        _shopUi?.Status(text);
    }

    // ---- client: answers ------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Stock(string key, int furniture, long epoch, int[] sold, bool season, bool outOfOrder, bool stuck)
    {
        if (Open is not { } o || o.Key != key || o.Furniture != furniture) return;
        Waiting = false;
        OpenEpoch = epoch;
        OpenStock = ShopTables.Stock(key, furniture, epoch, o.Type, ValueOf, season);
        OpenSold = sold;
        OutOfOrder = outOfOrder;
        Stuck = stuck;
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Bought(string key, int furniture, long epoch, int slot, int outcome, int count, int total, int soldAfter, int with, long balance)
    {
        Waiting = false;
        var what = (BuyOutcome)outcome;
        LastOutcome = what;
        bool mine = Open is { } o && o.Key == key && o.Furniture == furniture;
        if (what is BuyOutcome.Sold or BuyOutcome.Stuck && Items != null && slot >= 0 && mine && slot < OpenStock.Count)
        {
            var id = OpenStock[slot].Id;
            if ((PayWith)with == PayWith.Cash) Items.Inventory.TakeCash(Math.Min(total, Items.Inventory.Cash));
            else Bank.Instance?.Report(balance);
            if (what == BuyOutcome.Sold)
            {
                Items.Give(new ItemStack(id, count));
                LastDropped = slot;
                Items.Ui.Toast($"+{count} {ItemDefs.Get(id)?.Name} ({total} CHF{((PayWith)with == PayWith.Card ? ", card" : "")})");
                Play(SfxSynth.Chime, 1.4f);
            }
            else
            {
                Stuck = true;
                Say("It's stuck on the spiral! Hit the machine.");
                Play(SfxSynth.Tick, 0.5f);
            }
            if (slot < OpenSold.Length) OpenSold[slot] = soldAfter;
        }
        else
        {
            Say(what switch
            {
                BuyOutcome.SoldOut => "Sold out.",
                BuyOutcome.WrongPayment => "That payment is not taken for this.",
                BuyOutcome.Declined => "Card declined: the account does not cover it.",
                BuyOutcome.OutOfOrder => "Hors service.",
                BuyOutcome.Restocked => "The shelves were restocked.",
                _ => "Refused.",
            });
            if (what is BuyOutcome.SoldOut && mine && slot >= 0 && slot < OpenSold.Length) OpenSold[slot] = soldAfter;
            if (what == BuyOutcome.Declined && balance >= 0) Bank.Instance?.Report(balance);
            if (what == BuyOutcome.Restocked) Ask();
        }
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SoldBack(string key, int id, int count, int each)
    {
        Waiting = false;
        if (Items != null && count > 0)
        {
            int taken = Items.Inventory.TakePlain((ItemId)id, count);
            if (taken > 0)
            {
                Items.Inventory.Add(ItemId.Francs, taken * each);
                Items.Ui.Toast($"Sold {taken} {ItemDefs.Get((ItemId)id)?.Name}: +{taken * each} CHF cash");
                Play(SfxSynth.Chime, 1.1f);
            }
        }
        else Say("They will not buy that here.");
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Freed(string key, int furniture, int[] slots, int[] soldAfter)
    {
        Waiting = false;
        if (Open is not { } o || o.Key != key || o.Furniture != furniture) return;
        Stuck = false;
        if (slots.Length == 0) Say("Nothing falls.");
        foreach (int slot in slots)
            if (slot >= 0 && slot < OpenStock.Count)
            {
                Items?.Give(new ItemStack(OpenStock[slot].Id, 1));
                LastDropped = slot;
            }
        for (int i = 0; i < Math.Min(soldAfter.Length, OpenSold.Length); i++) OpenSold[i] = soldAfter[i];
        if (slots.Length > 1) Say("Two for one!");
        else if (slots.Length == 1) Say("It drops.");
        if (slots.Length > 0) Play(SfxSynth.Chime, 1.4f);
        Changed?.Invoke();
    }

    /// <summary>Client: someone else bought from the counter or the machine open here: the counts move.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SoldCounts(string key, int furniture, long epoch, int[] sold)
    {
        if (Open is not { } o || o.Key != key || o.Furniture != furniture || epoch != OpenEpoch) return;
        OpenSold = sold;
        Changed?.Invoke();
    }

    // ---- server ---------------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStock(string key, int furniture) => ServeStock(Multiplayer.GetRemoteSenderId(), key, furniture);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBuy(string key, int furniture, long epoch, int slot, int count, int with) =>
        ServeBuy(Multiplayer.GetRemoteSenderId(), key, furniture, epoch, slot, count, with);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSell(string key, int furniture, int id, int count) =>
        ServeSell(Multiplayer.GetRemoteSenderId(), key, furniture, id, count);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBump(string key, int furniture) => ServeBump(Multiplayer.GetRemoteSenderId(), key, furniture);

    /// <summary>Server: what a piece of furniture sells, if the peer stands in its building: a shop's counter or a machine.</summary>
    private async Task<(InteriorLayout Layout, ShopType Type)?> ShopFor(long peer, string key, int furniture)
    {
        var interiors = InteriorManager.Instance;
        if (interiors == null) return null;
        if (Online && interiors.SpaceOf(peer) != key) return null;   // only from inside, like the bank's InBank
        InteriorLayout? layout;
        try { layout = await interiors.GetOrCreate(key); }
        catch (Exception e)
        {
            GD.PushError($"[shop] layout {key}: {e.Message}");
            return null;
        }
        if (layout == null || furniture < 0 || furniture >= layout.Furniture.Count) return null;
        var t = layout.Furniture[furniture].Type;
        if (t == FurnitureType.VendingMachine) return (layout, ShopType.Vending);
        if (t == FurnitureType.ShopCounter && layout.Shop != ShopType.None) return (layout, layout.Shop);
        return null;
    }

    private List<StockLine> StockOf(string key, int furniture, long epoch, ShopType type) =>
        ShopTables.Stock(key, furniture, epoch, type, ValueOf, HuntingSeason());

    private async void ServeStock(long peer, string key, int furniture)
    {
        if (await ShopFor(peer, key, furniture) is not { } shop) return;
        long epoch = LootTables.Epoch(key, Now);
        var stock = StockOf(key, furniture, epoch, shop.Type);
        bool vending = shop.Type == ShopType.Vending;
        Reply(peer, MethodName.Stock, key, furniture, epoch, _ledger.Sold(key, furniture, epoch, stock.Count), HuntingSeason(),
            vending && ShopTables.OutOfOrder(key, furniture, epoch), vending && _ledger.HasStuck(key, furniture, epoch));
    }

    private async void ServeBuy(long peer, string key, int furniture, long epoch, int slot, int count, int with)
    {
        if (await ShopFor(peer, key, furniture) is not { } shop)
        {
            Reply(peer, MethodName.Bought, key, furniture, epoch, slot, (int)BuyOutcome.Refused, 0, 0, 0, with, -1L);
            return;
        }
        long now = LootTables.Epoch(key, Now);
        var stock = StockOf(key, furniture, now, shop.Type);
        bool vending = shop.Type == ShopType.Vending;
        if (vending) count = 1;   // a machine drops one at a time
        long balance = -1;
        bool Charge(int amount)
        {
            if (Bank.Instance is not { } bank) return false;
            bool ok = bank.Charge(peer, amount, out balance);
            return ok;
        }
        bool stuck = vending && (ForceStuck || _luck.NextDouble() < ShopTables.StuckChance);
        var r = _ledger.Buy(key, furniture, epoch, now, stock, slot, count, (PayWith)with, Charge,
            vending && ShopTables.OutOfOrder(key, furniture, now), stuck);
        if (r.Outcome is BuyOutcome.Sold or BuyOutcome.Stuck)
        {
            if (stuck) ForceStuck = false;
            GD.Print($"[shop] peer {peer} bought {r.Count} {stock[slot].Id} at {key} #{furniture} ({shop.Type}) for {r.Total} CHF "
                + $"{((PayWith)with == PayWith.Card ? "by card" : "cash")}{(r.Outcome == BuyOutcome.Stuck ? ", stuck" : "")}; slot {ShopTables.SlotName(slot)} {r.SoldAfter}/{stock[slot].Stock} sold");
        }
        else GD.Print($"[shop] peer {peer} purchase at {key} #{furniture} slot {slot}: {r.Outcome}");
        Reply(peer, MethodName.Bought, key, furniture, now, slot, (int)r.Outcome, r.Count, r.Total, r.SoldAfter, with, balance);
        if (r.Outcome is BuyOutcome.Sold or BuyOutcome.Stuck) Tell(peer, key, furniture, now, stock.Count);
    }

    /// <summary>Everyone else inside sees the counts move, or a panel open elsewhere keeps offering what is gone.</summary>
    private void Tell(long buyer, string key, int furniture, long epoch, int slots)
    {
        if (!Online || InteriorManager.Instance is not { } interiors) return;
        var sold = _ledger.Sold(key, furniture, epoch, slots);
        foreach (int other in Multiplayer.GetPeers())
            if (other != buyer && interiors.SpaceOf(other) == key)
                RpcId(other, MethodName.SoldCounts, key, furniture, epoch, sold);
    }

    private async void ServeSell(long peer, string key, int furniture, int id, int count)
    {
        var shop = await ShopFor(peer, key, furniture);
        var def = ItemDefs.Get((ItemId)id);
        int each = def == null ? 0 : ShopTables.SellPrice(def.Value);
        // the pack is the client's, like the cash: what the server checks is where, what and how many at most
        bool ok = shop is { } s && def != null && ShopTables.Buys(s.Type, def.Category) && each > 0 && count is > 0 and <= 999;
        if (ok) GD.Print($"[shop] peer {peer} sold {count} {(ItemId)id} at {key} ({shop!.Value.Type}) for {count * each} CHF");
        Reply(peer, MethodName.SoldBack, key, id, ok ? count : 0, ok ? each : 0);
    }

    private async void ServeBump(long peer, string key, int furniture)
    {
        if (await ShopFor(peer, key, furniture) is not { Type: ShopType.Vending })
        {
            Reply(peer, MethodName.Freed, key, furniture, Array.Empty<int>(), Array.Empty<int>());
            return;
        }
        long now = LootTables.Epoch(key, Now);
        var stock = StockOf(key, furniture, now, ShopType.Vending);
        var freed = _ledger.Bump(key, furniture, now, stock, _luck.NextDouble() < ShopTables.BonusChance);
        if (freed.Count > 0) GD.Print($"[shop] peer {peer} hit the machine at {key} #{furniture}: {freed.Count} dropped");
        Reply(peer, MethodName.Freed, key, furniture, freed.ToArray(), _ledger.Sold(key, furniture, now, stock.Count));
        if (freed.Count > 1) Tell(peer, key, furniture, now, stock.Count);
    }

    private void Reply(long peer, StringName method, params Variant[] args)
    {
        if (Online) RpcId(peer, method, args);
        else Call(method, args);
    }

    private void Play(AudioStreamWav? stream, float pitch)
    {
        if (stream == null || Items == null) return;
        var player = new AudioStreamPlayer { Stream = stream, PitchScale = pitch, VolumeDb = -8, Bus = SfxBus.Name };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    // ---- server: the sold counts on disk -------------------------------------------------------------

    private string PathFor(string tile) => Path.Combine(_storeDir, $"{tile}.json");

    private ShopLedger.TileState? LoadTile(string tile)
    {
        try
        {
            string path = PathFor(tile);
            return File.Exists(path) ? JsonSerializer.Deserialize<ShopLedger.TileState>(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[shop] {tile}: {e.Message}");
            return null;
        }
    }

    private void SaveTile(string tile, ShopLedger.TileState state)
    {
        try { JsonStore.SaveAsync(PathFor(tile), state, onError: e => GD.PushError($"[shop] saving: {e.Message}")); }
        catch (Exception e) { GD.PushError($"[shop] saving: {e.Message}"); }
    }
}
