using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Farming;

/// <summary>
/// Selling farm produce (#494, docs/notes/farming/selling.md), at <c>World/FarmSales</c> on the
/// server and every client (RPCs route by node path). The server prices every delivered load
/// (<see cref="PriceLoad"/>, called by <c>ShopService.ServeDeliver</c>: a co-op's yard with its wish
/// list and the season, or a specialty buyer's weighbridge, <see cref="FarmBuyers"/>), buys sacks
/// carried on foot to a specialty buyer, and keeps each player's delivery contracts
/// (<see cref="FarmContracts"/>, <c>user://farm/contracts.json</c>): taken from a co-op's panel,
/// filled by deliveries there (counter or machine), paid on completion, lapsed at the deadline.
/// Cash is the client's pocket, as at a shop: the server pays by answering, the client adds it.
/// Offline this client plays the server through the same methods.
/// </summary>
public partial class FarmSales : Node
{
    public const string NodeName = "FarmSales";

    public static FarmSales? Instance { get; private set; }

    /// <summary>Probes: the economy's clock (server Unix seconds), to run contracts and stands fast.</summary>
    public static Func<double>? ClockOverride { get; set; }

    /// <summary>The farm economy's time: the server's Unix clock (<see cref="ClockSync.ServerUnixNow"/>).</summary>
    public static double Now => ClockOverride?.Invoke() ?? ClockSync.ServerUnixNow;

    /// <summary>The month prices follow: the one the fields show (the server's, once it said), else <c>--farmmonth</c> / today.</summary>
    public static int Month => FarmField.Instance?.Month ?? FarmRules.MonthFromArgs(OS.GetCmdlineUserArgs(), DateTime.Now);

    public static long Week => FarmCalendar.Week(Now);

    /// <summary>Where contracts and stands are saved: <c>--selldir &lt;dir&gt;</c> (checks), else <c>user://farm</c>.</summary>
    public static string StoreDir => CmdArgs.Value("--selldir") is { } d ? ProjectSettings.GlobalizePath(d.StartsWith("res://") || d.StartsWith("user://") ? d : "res://" + d) : ProjectSettings.GlobalizePath("user://farm");

    private bool _server;
    private WorldOrigin _origin = null!;

    /// <summary>Client: the pack, the pocket and the toasts.</summary>
    public ItemController? Items { get; set; }
    /// <summary>Server: a peer's display name (contracts are kept by name, like bank accounts).</summary>
    public Func<long, string>? NameOf { get; set; }

    public static FarmSales Create(Node world, WorldOrigin origin, bool server, ItemController? items = null)
    {
        var s = new FarmSales { Name = NodeName, _server = server, _origin = origin, Items = items };
        world.AddChild(s);
        if (!server || Instance == null) Instance = s;
        return s;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);
    /// <summary>This peer decides: the dedicated server, or an offline client.</summary>
    private bool Serving => _server || !Online;

    public override void _Ready()
    {
        if (Serving) LoadContracts();
    }

    private double _sweep;

    public override void _Process(double delta)
    {
        if (!Serving) return;
        _sweep -= delta;
        if (_sweep > 0) return;
        _sweep = 2.0;
        SweepExpired();
    }

    // ===================================================================================================
    // pricing a delivered load (server, from ShopService)
    // ===================================================================================================

    /// <summary>Where a load or sacks are sold: a co-op's yard (its building key) or a specialty buyer.</summary>
    public readonly record struct Market(string? Coop, FarmBuyer? Buyer)
    {
        public string Label => Buyer is { } b ? $"{b.Name}, {b.Place}" : "the farm co-op";
        public bool Takes(ItemId item) => Buyer?.Buys(item) ?? Coop != null;
        public double Premium => Buyer?.Premium ?? 1.0;
    }

    /// <summary>The market at a world point: a co-op door within <paramref name="reach"/>, else a buyer's yard (with <paramref name="slack"/>).</summary>
    public Market? MarketAt(Vector3 at, float reach, float slack)
    {
        if (FarmMarket.CoopDoor(at, reach) is { } door) return new Market(door.Key.ToString(), null);
        return BuyerNear(at, slack) is { } b ? new Market(null, b) : null;
    }

    /// <summary>What one unit fetches by the load at <paramref name="m"/> now (the full value × season × wish × premium).</summary>
    public static double UnitPrice(Market m, ItemId item) =>
        ItemDefs.Get(item) is { } def ? def.Value * FarmPrices.Factor(item, Month, m.Coop, Week) * m.Premium : 0;

    /// <summary>The specialty buyer whose yard holds a world point (with <paramref name="slack"/>), or null.</summary>
    public FarmBuyer? BuyerNear(Vector3 at, float slack = 0f)
    {
        var (e, n) = _origin.ToLv95(at);
        return FarmBuyers.At(e, n, slack);
    }

    /// <summary>
    /// Server (from <c>ShopService.ServeDeliver</c>): prices a load of <paramref name="count"/>
    /// <paramref name="item"/> delivered at <paramref name="where"/> (the peer's own body online):
    /// at the co-op <paramref name="coop"/> the shop resolved (its wish list, the season; counted
    /// toward the peer's contracts there), else at a specialty buyer's yard that takes it (its premium).
    /// Returns the francs (0: refused) and what the market is called.
    /// </summary>
    public (long Total, string Where) PriceLoad(long peer, ItemId item, int count, Vector3? where, string? coop, float slack)
    {
        if (ItemDefs.Get(item) is not { } def || !FarmTables.IsHarvest(item) || count is <= 0 or > FarmMarket.MaxLoad || where is not { } w)
            return (0, "");
        var m = coop != null ? new Market(coop, null) : BuyerNear(w, slack) is { } b ? new Market(null, b) : (Market?)null;
        if (m is not { } market || !market.Takes(item)) return (0, "");
        long total = ShopTables.DeliveryPrice(def.Category, item, def.Value, count, Month, market.Coop, Week, market.Premium);
        if (total > 0 && market.Coop != null) Counted(peer, market.Coop, item, count);
        return (total, market.Label);
    }

    // ===================================================================================================
    // sacks carried on foot to a specialty buyer (client asks, server pays)
    // ===================================================================================================

    /// <summary>The buyer whose yard the player stands in, on foot, outdoors.</summary>
    public FarmBuyer? BuyerHere(FootPlayer p)
    {
        if (p.Indoors || p.Ride != RideKind.OnFoot) return null;
        var (e, n) = _origin.ToLv95(p.GlobalPosition);
        return FarmBuyers.At(e, n);
    }

    /// <summary>The first of the buyer's goods in the pack and how many, or None.</summary>
    private (ItemId Item, int Count) GoodsInPack(FarmBuyer b)
    {
        if (Items == null) return (ItemId.None, 0);
        foreach (var g in b.Goods)
            if (Items.Inventory.CountPlain(g) is > 0 and var c) return (g, c);
        return (ItemId.None, 0);
    }

    private bool _selling;

    /// <summary>E at a buyer's weighbridge with its goods in the pack: sells them all.</summary>
    public bool SellHere(FootPlayer p)
    {
        if (BuyerHere(p) is not { } b) return false;
        var (item, count) = GoodsInPack(b);
        if (count <= 0)
        {
            Items?.Ui.Toast($"{b.Name} buys only {b.GoodsText(NameOfItem)}.");
            return true;
        }
        if (_selling) return true;
        _selling = true;
        var (e, n) = _origin.ToLv95(p.GlobalPosition);
        if (Online) RpcId(1, MethodName.AskSellHere, (int)item, count, e, n);
        else ServeSellHere(1, (int)item, count, e, n);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskSellHere(int item, int count, double e, double n) => ServeSellHere(Multiplayer.GetRemoteSenderId(), item, count, e, n);

    private void ServeSellHere(long peer, int item, int count, double e, double n)
    {
        // online the peer's own replicated body says where it is, not what it claims
        if (Online && BodyOf(peer) is { } body) (e, n) = (body.Global.E, body.Global.N);
        else if (Online) { Reply(peer, MethodName.SoldHere, item, 0, 0, ""); return; }
        var b = FarmBuyers.At(e, n, Online ? FarmMarket.ServerSlack : 0f);
        var id = (ItemId)item;
        long total = b != null && b.Buys(id) && ItemDefs.Get(id) is { } def && count is > 0 and <= FarmMarket.MaxLoad
            ? FarmPrices.Delivery(def.Value, id, count, Month, null, Week, b.Premium) : 0;
        GD.Print(total > 0 ? $"[sell] peer {peer} sold {count} {id} at {b!.Name}, {b.Place} for {total} CHF" : $"[sell] peer {peer}: {count} {id} refused here");
        Reply(peer, MethodName.SoldHere, item, total > 0 ? count : 0, (int)total, b?.Name ?? "");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SoldHere(int item, int count, int total, string buyer)
    {
        _selling = false;
        if (Items == null) return;
        if (count <= 0 || total <= 0) { Items.Ui.Toast("They will not take that here."); return; }
        int taken = Items.Inventory.TakePlain((ItemId)item, count);
        if (taken <= 0) return;
        int paid = taken == count ? total : (int)((long)total * taken / count);
        Items.Inventory.Add(ItemId.Francs, paid);
        Items.Ui.Toast($"Sold {taken} {NameOfItem((ItemId)item)} to {buyer}: +{paid} CHF cash");
        LastSold = paid;
        Chime(1.1f);
    }

    /// <summary>Probes: the francs of the last sale on foot.</summary>
    public int LastSold { get; private set; }

    // ===================================================================================================
    // contracts (server keeps them per player; clients see their own)
    // ===================================================================================================

    private readonly Dictionary<string, List<FarmContract>> _contracts = new();
    private List<FarmContract> _mine = new();

    /// <summary>Client: this player's contracts, as the server last said.</summary>
    public IReadOnlyList<FarmContract> Mine => _mine;
    /// <summary>Client: the contracts changed (taken, filled, paid, lapsed).</summary>
    public event Action? ContractsChanged;
    /// <summary>Client: the last word from the server about contracts (taken, refused, paid, lapsed).</summary>
    public string LastNote { get; private set; } = "";
    /// <summary>Probes: the bonus last paid.</summary>
    public int LastBonus { get; private set; }

    private static float ValueOf(ItemId id) => ItemDefs.Get(id)?.Value ?? 0f;

    /// <summary>The co-op's orders this farm week (the same on every peer).</summary>
    public static FarmOrder[] OrdersOf(string coop) => FarmContracts.Orders(coop, Week, ValueOf);

    /// <summary>Client: asks for this player's contracts (the shop panel does when it opens).</summary>
    public void Refresh()
    {
        if (Online) RpcId(1, MethodName.AskContracts);
        else PushContracts(1);
    }

    /// <summary>Client: takes order <paramref name="index"/> of the co-op whose counter is open.</summary>
    public void Accept(string coop, int index)
    {
        if (Online) RpcId(1, MethodName.AskAccept, coop, Week, index);
        else ServeAccept(1, coop, Week, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskContracts() => PushContracts(Multiplayer.GetRemoteSenderId());

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskAccept(string coop, long week, int index) => ServeAccept(Multiplayer.GetRemoteSenderId(), coop, week, index);

    private string PlayerOf(long peer) => Online ? NameOf?.Invoke(peer) ?? $"Rider{peer}" : "local";

    private List<FarmContract> ListOf(string player)
    {
        if (!_contracts.TryGetValue(player, out var list)) _contracts[player] = list = new();
        return list;
    }

    private async void ServeAccept(long peer, string coop, long week, int index)
    {
        string? refused = null;
        if (index is < 0 or >= FarmContracts.PerWeek) refused = "No such order.";
        else if (Online)
        {
            // only at that co-op's counter, as for buying there: the peer stands in that building
            var interiors = InteriorManager.Instance;
            if (interiors == null || interiors.SpaceOf(peer) != coop) refused = "Take orders at the co-op's counter.";
            else
            {
                InteriorLayout? layout = null;
                try { layout = await interiors.GetOrCreate(coop); }
                catch (Exception e) { GD.PushError($"[sell] layout {coop}: {e.Message}"); }
                if (layout?.Shop != ShopType.FarmCoop) refused = "This is not a farm co-op.";
            }
        }
        if (refused == null)
        {
            var order = FarmContracts.Orders(coop, week, ValueOf)[index];
            refused = FarmContracts.Accept(ListOf(PlayerOf(peer)), coop, week, order, ValueOf(order.Item), Now);
            if (refused == null)
            {
                GD.Print($"[sell] {PlayerOf(peer)} took order {index} of {coop} (week {week}): {order.Count} {order.Item} x{order.Multiplier} in {order.Days} d");
                SaveContracts();
            }
        }
        Reply(peer, MethodName.Note, refused ?? "Contract taken.", 0);
        PushContracts(peer);
    }

    /// <summary>
    /// Server: <paramref name="count"/> of <paramref name="item"/> delivered to co-op <paramref name="coop"/>
    /// (its counter, or a machine in its yard): fills the peer's contracts there; a completed one is paid.
    /// </summary>
    public void Counted(long peer, string coop, ItemId item, int count)
    {
        var list = ListOf(PlayerOf(peer));
        if (list.Count == 0) return;
        var step = FarmContracts.Deliver(list, coop, item, count, Now);
        if (step.Counted <= 0) return;
        foreach (var c in step.Completed)
        {
            int bonus = FarmContracts.Bonus(c);
            GD.Print($"[sell] {PlayerOf(peer)} completed {c.Count} {c.Item} for {c.Coop}: bonus {bonus} CHF");
            Reply(peer, MethodName.Note, $"Contract complete: {c.Count} × {NameOfItem(c.Item)}, bonus +{bonus} CHF cash", bonus);
        }
        SaveContracts();
        PushContracts(peer);
    }

    private void SweepExpired()
    {
        double now = Now;
        bool changed = false;
        foreach (var (player, list) in _contracts)
        {
            var gone = FarmContracts.Expire(list, now);
            if (gone.Count == 0) continue;
            changed = true;
            long peer = PeerOf(player);
            foreach (var c in gone)
            {
                GD.Print($"[sell] {player}'s contract lapsed: {c.Delivered}/{c.Count} {c.Item} for {c.Coop}");
                if (peer != 0) Reply(peer, MethodName.Note, $"Contract lapsed: {c.Delivered} of {c.Count} × {NameOfItem(c.Item)} delivered in time.", 0);
            }
            if (peer != 0) PushContracts(peer);
        }
        if (changed) SaveContracts();
    }

    /// <summary>The connected peer playing <paramref name="player"/>, 0 when none (offline: the local one).</summary>
    private long PeerOf(string player)
    {
        if (!Online) return player == "local" ? 1 : 0;
        foreach (int p in Multiplayer.GetPeers())
            if (PlayerOf(p) == player) return p;
        return 0;
    }

    private void PushContracts(long peer)
    {
        var list = ListOf(PlayerOf(peer));
        int k = list.Count;
        var coops = new string[k];
        var weeks = new long[k];
        var ints = new int[k * 4];
        var nums = new double[k * 3];
        for (int i = 0; i < k; i++)
        {
            var c = list[i];
            coops[i] = c.Coop;
            weeks[i] = c.Week;
            ints[4 * i] = c.Index; ints[4 * i + 1] = (int)c.Item; ints[4 * i + 2] = c.Count; ints[4 * i + 3] = c.Delivered;
            nums[3 * i] = c.Multiplier; nums[3 * i + 1] = c.Unit; nums[3 * i + 2] = c.Deadline;
        }
        Reply(peer, MethodName.Contracts, coops, weeks, ints, nums);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Contracts(string[] coops, long[] weeks, int[] ints, double[] nums)
    {
        var list = new List<FarmContract>(coops.Length);
        for (int i = 0; i < coops.Length && 4 * i + 3 < ints.Length && 3 * i + 2 < nums.Length; i++)
            list.Add(new FarmContract
            {
                Coop = coops[i], Week = weeks[i], Index = ints[4 * i], Item = (ItemId)ints[4 * i + 1], Count = ints[4 * i + 2],
                Delivered = ints[4 * i + 3], Multiplier = nums[3 * i], Unit = (float)nums[3 * i + 1], Deadline = nums[3 * i + 2],
            });
        // offline the list is the server's own: hand out a copy, the panel must not hold the live one
        _mine = list;
        ContractsChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Note(string text, int bonus)
    {
        LastNote = text;
        if (bonus > 0 && Items != null)
        {
            Items.Inventory.Add(ItemId.Francs, bonus);
            LastBonus = bonus;
            Chime(1.4f);
        }
        Items?.Ui.Toast(text);
        ContractsChanged?.Invoke();
    }

    private string ContractsPath => Path.Combine(StoreDir, _server ? "contracts.json" : "contracts_offline.json");

    private void LoadContracts()
    {
        try
        {
            if (!File.Exists(ContractsPath)) return;
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<FarmContract>>>(File.ReadAllText(ContractsPath));
            if (data != null) foreach (var (k, v) in data) _contracts[k] = v;
            GD.Print($"[sell] {_contracts.Values.Sum(l => l.Count)} contract(s) from {ContractsPath}");
        }
        catch (Exception e) { GD.PushWarning($"[sell] {ContractsPath}: {e.Message}"); }
    }

    private void SaveContracts() =>
        JsonStore.SaveAsync(ContractsPath, _contracts.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            onError: e => GD.PushError($"[sell] saving contracts: {e.Message}"));

    /// <summary>Probes: forgets every contract (the check's own store).</summary>
    public void ClearContracts()
    {
        _contracts.Clear();
        _mine = new();
        SaveContracts();
    }

    // ===================================================================================================
    // client: E, prompts, the delivery hint
    // ===================================================================================================

    /// <summary>E / Y / a VR hand reaching out: a farm stand in reach, else a buyer's weighbridge. False when neither.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (FarmStands.Instance?.TryOpen(p) == true) return true;
        return Instance?.SellHere(p) == true;
    }

    /// <summary>The outdoor prompt for a stand or a buyer in reach (refreshed a few times a second, built only when it changes).</summary>
    public static string? PromptFor(FootPlayer p) => FarmStands.Instance?.PromptFor(p) ?? Instance?.BuyerPrompt(p);

    private (FarmBuyer? Buyer, ItemId Item, int Count, InputDevice Device) _buyerKey;
    private string? _buyerText;

    private string? BuyerPrompt(FootPlayer p)
    {
        if (BuyerHere(p) is not { } b) return null;
        var (item, count) = GoodsInPack(b);
        var key = (b, item, count, PlayerInput.HintDevice);
        if (_buyerText != null && key == _buyerKey) return _buyerText;
        _buyerKey = key;
        string tag = InputHints.Tag(PlayerInput.InteractMount);
        if (count <= 0) return _buyerText = $"{b.Name}, {b.Place}: buys {b.GoodsText(NameOfItem)} (bring it by the load or the sack)";
        long total = ItemDefs.Get(item) is { } def ? FarmPrices.Delivery(def.Value, item, count, Month, null, Week, b.Premium) : 0;
        return _buyerText = $"{tag} Sell {count} {NameOfItem(item)} to {b.Name}: {total} CHF";
    }

    private (ItemId Item, int Count, string? Coop, string? Buyer, long Week, int Month, InputDevice Device, bool Tip) _hintKey;
    private string? _hintText;

    /// <summary>
    /// The delivery prompt in a farm machine stopped at a market (PlayerFeel): what the load fetches
    /// there, and "WANTED" when the co-op wishes for it this week. Built only when something changes.
    /// </summary>
    public string DeliveryHint(ItemId item, int count, Vector3 at, bool tip = false)
    {
        var m = MarketAt(at, FarmMarket.DeliverReach, 0f);
        var key = (item, count, m?.Coop, m?.Buyer?.Key, Week, Month, PlayerInput.HintDevice, tip);
        if (_hintText != null && key == _hintKey) return _hintText;
        _hintKey = key;
        string control = (InputHints.Pad ? "{car_door}" : "{destination}") + (tip ? "  TIP the trailer:" : "");
        if (m is not { } market) return _hintText = InputHints.Format($"{control}  DELIVER the load");
        if (!market.Takes(item)) return _hintText = $"{market.Label} takes only {market.Buyer!.GoodsText(NameOfItem)}";
        long total = ItemDefs.Get(item) is { } def ? ShopTables.DeliveryPrice(def.Category, item, def.Value, count, Month, market.Coop, Week, market.Premium) : 0;
        string tag = WantedTag(market.Coop, item);
        return _hintText = InputHints.Format($"{control}  DELIVER {count} {NameOfItem(item)} to {market.Label}: {total} CHF{(tag.Length > 0 ? "  " + tag : "")}");
    }

    /// <summary>"WANTED +40 %" when the co-op wishes for the item this week, else "".</summary>
    public static string WantedTag(string? coop, ItemId item)
    {
        double bonus = FarmPrices.WishBonus(coop, Week, item);
        return bonus > 1.0 ? $"WANTED +{Math.Round((bonus - 1) * 100):0} %" : "";
    }

    public static string NameOfItem(ItemId id) => ItemDefs.Get(id)?.Name ?? id.ToString();

    // ===================================================================================================

    private FootPlayer? BodyOf(long peer) => GetParent()?.GetNodeOrNull<FootPlayer>($"Players/{peer}");

    private void Reply(long peer, StringName method, params Variant[] args)
    {
        if (Online) RpcId(peer, method, args);
        else Call(method, args);
    }

    private void Chime(float pitch)
    {
        if (SfxSynth.Chime is not { } stream || Items == null) return;
        var player = new AudioStreamPlayer { Stream = stream, PitchScale = pitch, VolumeDb = -8, Bus = SfxBus.Name };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }
}
