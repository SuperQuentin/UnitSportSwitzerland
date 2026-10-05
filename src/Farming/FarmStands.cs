using System.Text.Json;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// Farm stands (#494, a self-service Hofladen with an honesty box), at <c>World/FarmStands</c> on the
/// server and every client. The stand itself is a <see cref="PlacedObject"/> (<see cref="PlacedKind.FarmStand"/>:
/// placed, saved, sent on join and taken back like a field workbench); what is on it lives here,
/// keyed by its id: the crates and the cash box (<see cref="StandState"/>, server-owned, saved to
/// <c>user://farm/stands.json</c>). The server sells to passers-by every few seconds
/// (<see cref="FarmStandRules.Advance"/>: faster by a road and a village, cooked dishes faster) and
/// answers the owner (stock a crate, take it back, empty the box) and other players (buy, the
/// money goes into the owner's box). Every change goes to every peer (<c>State</c>), so all see the
/// same crates. The pocket and the pack are the client's, as at a shop: the client pays or takes
/// once the server agreed. Offline this client plays the server.
/// </summary>
public partial class FarmStands : Node
{
    public const string NodeName = "FarmStands";
    /// <summary>How close to the stand its panel is used (server check, plus a little slack online).</summary>
    public const float Reach = 3.0f;
    private const float ServerSlack = 2.0f;
    /// <summary>Sales to passers-by are worked out this often (server seconds).</summary>
    private const double TickSeconds = 5.0;

    public static FarmStands? Instance { get; private set; }

    private bool _server;
    private WorldOrigin _origin = null!;
    private PlacedObjects? _placed;

    /// <summary>Where the roads come from (the server's or the offline client's chunk source).</summary>
    public Func<IChunkSource?>? Source { get; set; }
    /// <summary>Client: pack, pocket, toasts.</summary>
    public ItemController? Items { get; set; }
    /// <summary>Server: a peer's display name, the owner recorded on placed objects.</summary>
    public Func<long, string>? NameOf { get; set; }

    private readonly Dictionary<long, StandState> _stands = new();
    /// <summary>Client: the stands this player owns (the server says, per peer).</summary>
    private readonly HashSet<long> _mine = new();

    /// <summary>A stand's crates or box changed (its id): the visuals and the panel redraw.</summary>
    public event Action<long>? Changed;
    /// <summary>The last answer: (op, refused or "").</summary>
    public (int Op, string Refused) LastAnswer { get; private set; }

    public IReadOnlyDictionary<long, StandState> All => _stands;
    public bool IsMine(long id) => _mine.Contains(id);

    public static FarmStands Create(Node world, WorldOrigin origin, bool server)
    {
        var s = new FarmStands { Name = NodeName, _server = server, _origin = origin };
        world.AddChild(s);
        if (!server || Instance == null) Instance = s;
        return s;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_placed != null)
        {
            _placed.Added -= OnAdded;
            _placed.Removed -= OnRemoved;
        }
    }

    private bool Online => NetLink.Online(this);
    private bool Serving => _server || !Online;

    public override void _Ready()
    {
        _placed = GetParent().GetNodeOrNull<PlacedObjects>(PlacedObjects.NodeName);
        if (_placed != null)
        {
            _placed.Added += OnAdded;
            _placed.Removed += OnRemoved;
        }
        if (!_server)
        {
            _ui = new FarmStandUi(this) { Name = "FarmStandUi" };
            AddChild(_ui);
        }
        if (Serving) Load();
        if (Serving && _placed != null) Reconcile();
    }

    /// <summary>Server: what was saved against what is placed: a state for every stand, none for a stand gone.</summary>
    private void Reconcile()
    {
        foreach (var o in _placed!.All.Values)
            if (o.Kind == PlacedKind.FarmStand) OnAdded(o);
        foreach (long id in _stands.Keys.ToList())
            if (!_placed.All.ContainsKey(id)) _stands.Remove(id);
    }

    public static double Now => FarmSales.Now;

    // ---- placed objects come and go -----------------------------------------------------------------

    private void OnAdded(PlacedObject o)
    {
        if (o.Kind != PlacedKind.FarmStand || !Serving) return;
        if (!_stands.TryGetValue(o.Id, out var s))
        {
            _stands[o.Id] = s = new StandState { Id = o.Id, Owner = o.Owner, LastTick = Now };
            Save();
        }
        if (float.IsNaN(s.RoadM)) Measure(o, s);
        Broadcast(o.Id);
    }

    private void OnRemoved(long id)
    {
        bool had = _stands.Remove(id);
        _mine.Remove(id);
        if (had && Serving) Save();
        if (had) Changed?.Invoke(id);
        if (_ui?.OpenId == id) _ui.Close();
    }

    /// <summary>Server: a stand may be packed up only empty, its box emptied (<c>PlacedObjects.ServeRemove</c> asks).</summary>
    public static string? RemoveProblem(PlacedObject o) =>
        o.Kind == PlacedKind.FarmStand && Instance is { } s && s._stands.TryGetValue(o.Id, out var st) && (st.Stocked > 0 || st.Cash > 0)
            ? "Take the produce and the cash first." : null;

    // ---- where it may stand: by a road, apart from other stands -------------------------------------

    private readonly Dictionary<TileId, List<RoadSegment>?> _roads = new();
    private readonly HashSet<TileId> _roadsLoading = new();

    /// <summary>
    /// Server: the site rule a stand is placed under (<c>Build.Gadgets.Check</c> asks): within
    /// <see cref="FarmStandRules.RoadMax"/> m of a road, at least <see cref="FarmStandRules.Spacing"/> m from another stand.
    /// </summary>
    public static string? SiteProblem(PlacedObject o)
    {
        if (Instance is not { } s || s._placed == null) return null;
        foreach (var other in s._placed.All.Values)
            if (other.Kind == PlacedKind.FarmStand && Math.Sqrt((other.E - o.E) * (other.E - o.E) + (other.N - o.N) * (other.N - o.N)) < FarmStandRules.Spacing)
                return "Another farm stand stands right here.";
        if (RoadDistanceOverride is { } f) return f(o.E, o.N) > FarmStandRules.RoadMax ? RoadText : null;
        float? d = s.RoadDistance(o.E, o.N);
        if (d == null) return "Reading the roads here: try again in a moment.";
        return d > FarmStandRules.RoadMax ? RoadText : null;
    }

    private const string RoadText = "A farm stand needs passers-by: set it up within 60 m of a road.";

    /// <summary>Probes: the distance to a road at an LV95 point, instead of the map's roads.</summary>
    public static Func<double, double, float>? RoadDistanceOverride { get; set; }

    /// <summary>Metres from an LV95 point to the nearest road (not a railway), or null while its tiles load; past 200 m, 200.</summary>
    public float? RoadDistance(double e, double n)
    {
        if (RoadDistanceOverride is { } f) return f(e, n);
        const float cap = 200f;
        float best = cap;
        bool known = true;
        foreach (var tile in TilesAround(e, n, FarmStandRules.RoadMax + 1))
        {
            if (!_roads.TryGetValue(tile, out var segs)) { LoadRoads(tile); known = false; continue; }
            if (segs == null) continue;
            foreach (var seg in segs)
            {
                if (seg.Class is RoadClass.Railway) continue;
                var p = seg.Points;
                for (int i = 0; i + 5 < p.Length; i += 3)
                {
                    double ax = tile.MinE + p[i], an = tile.MaxN - p[i + 2], bx = tile.MinE + p[i + 3], bn = tile.MaxN - p[i + 5];
                    double vx = bx - ax, vn = bn - an, len2 = vx * vx + vn * vn;
                    double u = len2 > 1e-9 ? Math.Clamp(((e - ax) * vx + (n - an) * vn) / len2, 0, 1) : 0;
                    double dx = ax + vx * u - e, dn = an + vn * u - n;
                    float d = (float)Math.Sqrt(dx * dx + dn * dn) - seg.Width * 0.5f;
                    if (d < best) best = Math.Max(0f, d);
                }
            }
        }
        return known || best < FarmStandRules.RoadMax ? best : null;
    }

    private static IEnumerable<TileId> TilesAround(double e, double n, double r)
    {
        var seen = new HashSet<TileId>();
        foreach (var (de, dn) in new[] { (0.0, 0.0), (-r, -r), (r, -r), (-r, r), (r, r) })
            if (seen.Add(TileId.FromLv95(e + de, n + dn))) yield return TileId.FromLv95(e + de, n + dn);
    }

    private async void LoadRoads(TileId tile)
    {
        if (!_roadsLoading.Add(tile) || Source?.Invoke() is not { } source) return;
        List<RoadSegment>? segs = null;
        try { segs = (await source.LoadRoadsAsync(tile))?.Segments; }
        catch (Exception e) { GD.PushWarning($"[stand] roads {tile}: {e.Message}"); }
        _roads[tile] = segs;
        _roadsLoading.Remove(tile);
        if (_roads.Count > 64) foreach (var k in _roads.Keys.Take(16).ToList()) _roads.Remove(k);
    }

    /// <summary>Server: the road and the houses round a new stand, once its road tiles are in.</summary>
    private async void Measure(PlacedObject o, StandState s)
    {
        for (int i = 0; i < 40 && float.IsNaN(s.RoadM); i++)
        {
            if (RoadDistance(o.E, o.N) is { } d) s.RoadM = d;
            else await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        }
        // buildings from the map, not the drawn doors: a dedicated server draws none
        int houses = 0;
        if (Source?.Invoke() is { } source)
            foreach (var tile in TilesAround(o.E, o.N, FarmStandRules.TownRadius))
            {
                BuildingTile? b = null;
                try { b = await source.LoadBuildingsAsync(tile); }
                catch (Exception e) { GD.PushWarning($"[stand] buildings {tile}: {e.Message}"); }
                if (b == null) continue;
                foreach (var house in b.Buildings)
                {
                    if (house.Triangles.Length < 3) continue;
                    double he = tile.MinE + house.Triangles[0], hn = tile.MaxN - house.Triangles[2];
                    if ((he - o.E) * (he - o.E) + (hn - o.N) * (hn - o.N) < FarmStandRules.TownRadius * FarmStandRules.TownRadius) houses++;
                }
            }
        if (!IsInsideTree()) return;
        s.Houses = houses;
        GD.Print(FormattableString.Invariant($"[stand] #{o.Id} by {o.Owner}: road {s.RoadM:F0} m, {houses} houses near, {FarmStandRules.PerHour(s, ItemId.Potato):F1} sales/h a crate"));
        Save();
        Broadcast(o.Id);
    }

    // ---- the passers-by -----------------------------------------------------------------------------

    private double _tick, _prefetch;

    public override void _Process(double delta)
    {
        if (!Serving) return;
        // the road tiles under every player, so a stand set up there is checked at once
        _prefetch -= delta;
        if (_prefetch <= 0)
        {
            _prefetch = 2.0;
            if (GetParent().GetNodeOrNull("Players") is { } players)
                foreach (var child in players.GetChildren())
                    if (child is FootPlayer { Npc: false } p)
                    {
                        var (e, n) = _origin.ToLv95(p.GlobalPosition);
                        foreach (var t in TilesAround(e, n, FarmStandRules.RoadMax + 1))
                            if (!_roads.ContainsKey(t)) LoadRoads(t);
                    }
        }
        _tick -= delta;
        if (_tick > 0) return;
        _tick = TickSeconds;
        Tick();
    }

    /// <summary>Server: sells to passers-by up to now; probes call it after moving the clock.</summary>
    public void Tick()
    {
        double now = Now;
        bool any = false;
        foreach (var s in _stands.Values)
        {
            if (s.Stocked == 0) { s.LastTick = now; continue; }
            var sales = FarmStandRules.Advance(s, now, PriceOf);
            if (sales.Count == 0) continue;
            any = true;
            foreach (var sale in sales)
                GD.Print($"[stand] #{s.Id} sold {sale.Count} {sale.Item} to passers-by for {sale.Total} CHF (box {s.Cash})");
            Broadcast(s.Id);
        }
        if (any) Save();
    }

    /// <summary>A stand's price for one now (<see cref="FarmPrices.Stand"/>).</summary>
    public static int PriceOf(ItemId id) => ItemDefs.Get(id) is { } def ? FarmPrices.Stand(def.Value, id, FarmSales.Month) : 0;

    // ---- client requests ----------------------------------------------------------------------------

    public const int OpStock = 1, OpTake = 2, OpBuy = 3, OpCollect = 4;
    private bool _waiting;
    public bool Waiting => _waiting;

    /// <summary>Owner: puts <paramref name="count"/> of a pack item on the stand.</summary>
    public bool Stock(long id, ItemId item, int count)
    {
        if (_waiting || Items == null || !FarmStandRules.Stockable(item)) return false;
        count = Math.Min(count, Items.Inventory.CountPlain(item));
        if (count <= 0) return false;
        return Ask(OpStock, id, (int)item, count);
    }

    /// <summary>Owner: takes a crate's produce back into the pack.</summary>
    public bool Take(long id, int slot, int count) => !_waiting && Ask(OpTake, id, slot, count);

    /// <summary>Anyone else: buys from a crate, paying cash into the owner's box.</summary>
    public bool Buy(long id, int slot, int count)
    {
        if (_waiting || Items == null || !_stands.TryGetValue(id, out var s) || slot < 0 || slot >= s.Slots.Count) return false;
        var c = s.Slots[slot];
        count = Math.Min(count, c.Count);
        if (count <= 0) return false;
        int cost = PriceOf(c.Item) * count;
        if (Items.Inventory.Cash < cost) { Items.Ui.Toast($"Not enough cash: {cost} CHF."); return false; }
        return Ask(OpBuy, id, slot, count);
    }

    /// <summary>Owner: empties the honesty box into the pocket.</summary>
    public bool Collect(long id) => !_waiting && Ask(OpCollect, id, 0, 0);

    private bool Ask(int op, long id, int a, int b)
    {
        _waiting = true;
        if (Online) RpcId(1, MethodName.AskOp, op, id, a, b);
        else ServeOp(1, op, id, a, b);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskOp(int op, long id, int a, int b) => ServeOp(Multiplayer.GetRemoteSenderId(), op, id, a, b);

    private string OwnerName(long peer) => Online ? NameOf?.Invoke(peer) ?? $"Rider{peer}" : "local";

    private void ServeOp(long peer, int op, long id, int a, int b)
    {
        string who = OwnerName(peer);
        string? refused = !_stands.TryGetValue(id, out var s) || _placed?.All.TryGetValue(id, out var o) != true ? "That stand is not there any more."
            : !InReach(peer, o!) ? "Too far from the stand."
            : null;
        int item = 0, count = 0, amount = 0;
        if (refused == null)
        {
            Tick();   // what passers-by bought until now goes first
            bool owner = s!.Owner == who;
            switch (op)
            {
                case OpStock when !owner:
                case OpTake when !owner:
                case OpCollect when !owner:
                    refused = "That is not your stand.";
                    break;
                case OpStock:
                    item = a;
                    count = b is > 0 and <= 999 ? FarmStandRules.Stock(s, (ItemId)a, b) : 0;
                    if (count <= 0) refused = FarmStandRules.Stockable((ItemId)a) ? "The stand is full." : "That does not go on a farm stand.";
                    break;
                case OpTake:
                    item = a >= 0 && a < s.Slots.Count ? (int)s.Slots[a].Item : 0;
                    count = FarmStandRules.Take(s, a, b);
                    if (count <= 0) refused = "Nothing there.";
                    break;
                case OpBuy when owner:
                    refused = "It is your own stand: take it back instead.";
                    break;
                case OpBuy:
                    item = a >= 0 && a < s.Slots.Count ? (int)s.Slots[a].Item : 0;
                    int each = PriceOf((ItemId)item);
                    count = each > 0 ? FarmStandRules.Take(s, a, b) : 0;
                    amount = count * each;
                    s.Cash += amount;
                    s.Takings += amount;
                    if (count <= 0) refused = "Sold out.";
                    break;
                case OpCollect:
                    amount = s.Cash;
                    s.Cash = 0;
                    if (amount <= 0) refused = "The box is empty.";
                    break;
                default:
                    refused = "?";
                    break;
            }
        }
        if (refused == null)
        {
            GD.Print($"[stand] #{id} {who}: {OpName(op)} {count} {(ItemId)item} {amount} CHF (box {s!.Cash}, {s.Stocked} on it)");
            Save();
            Broadcast(id);
        }
        Reply(peer, MethodName.Answer, op, id, item, refused == null ? count : 0, refused == null ? amount : 0, refused ?? "");
    }

    private static string OpName(int op) => op switch { OpStock => "stocked", OpTake => "took back", OpBuy => "bought", OpCollect => "collected", _ => "?" };

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Answer(int op, long id, int item, int count, int amount, string refused)
    {
        _waiting = false;
        LastAnswer = (op, refused);
        if (Items == null) return;
        var what = (ItemId)item;
        if (refused.Length > 0) { Items.Ui.Toast(refused); _ui?.Status(refused); Changed?.Invoke(id); return; }
        string name = FarmSales.NameOfItem(what);
        string text = op switch
        {
            OpStock => $"{count} {name} on the stand.",
            OpTake => $"{count} {name} taken back.",
            OpBuy => $"Bought {count} {name}: {amount} CHF into the honesty box.",
            OpCollect => $"Emptied the honesty box: +{amount} CHF cash",
            _ => "",
        };
        switch (op)
        {
            case OpStock: Items.Inventory.TakePlain(what, count); break;
            case OpTake: Items.Give(new ItemStack(what, count)); break;
            case OpBuy:
                Items.Inventory.TakeCash(Math.Min(amount, Items.Inventory.Cash));
                Items.Give(new ItemStack(what, count));
                break;
            case OpCollect: Items.Inventory.Add(ItemId.Francs, amount); break;
        }
        Items.Ui.Toast(text);
        _ui?.Status(text);
        Changed?.Invoke(id);
    }

    /// <summary>Within reach of the requester's body; offline there is nobody to doubt.</summary>
    private bool InReach(long peer, PlacedObject o)
    {
        if (!Online) return true;
        if (GetParent().GetNodeOrNull<FootPlayer>($"Players/{peer}") is not { } body) return false;
        return body.Global.HorizontalDistanceTo(new GlobalPos(o.E, o.N, o.Altitude)) <= Reach + 1.5f + ServerSlack;
    }

    // ---- replication: every peer sees every stand's crates ------------------------------------------

    /// <summary>Server: one stand's state to every peer (each told whether it is theirs); offline, here.</summary>
    private void Broadcast(long id)
    {
        if (!_stands.TryGetValue(id, out var s)) return;
        var (items, counts) = Pack(s);
        if (!Online)
        {
            State(id, s.Owner, items, counts, s.Cash, s.RoadM, s.Houses, s.Takings, s.Owner == "local");
            return;
        }
        foreach (int p in Multiplayer.GetPeers())
            RpcId(p, MethodName.State, id, s.Owner, items, counts, s.Cash, s.RoadM, s.Houses, s.Takings, NameOf?.Invoke(p) == s.Owner);
    }

    /// <summary>Server: a joining peer gets every stand.</summary>
    public void SendTo(long peer)
    {
        foreach (var s in _stands.Values)
        {
            var (items, counts) = Pack(s);
            RpcId(peer, MethodName.State, s.Id, s.Owner, items, counts, s.Cash, s.RoadM, s.Houses, s.Takings, NameOf?.Invoke(peer) == s.Owner);
        }
    }

    private static (int[] Items, int[] Counts) Pack(StandState s)
    {
        var items = new int[s.Slots.Count];
        var counts = new int[s.Slots.Count];
        for (int i = 0; i < s.Slots.Count; i++) { items[i] = (int)s.Slots[i].Item; counts[i] = s.Slots[i].Count; }
        return (items, counts);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(long id, string owner, int[] items, int[] counts, int cash, float road, int houses, int takings, bool mine)
    {
        if (!Serving)
        {
            if (!_stands.TryGetValue(id, out var s)) _stands[id] = s = new StandState { Id = id };
            s.Owner = owner;
            s.Cash = cash;
            s.RoadM = road;
            s.Houses = houses;
            s.Takings = takings;
            while (s.Slots.Count < items.Length) s.Slots.Add(new StandSlot());
            if (s.Slots.Count > items.Length) s.Slots.RemoveRange(items.Length, s.Slots.Count - items.Length);
            for (int i = 0; i < items.Length && i < counts.Length; i++) { s.Slots[i].Item = (ItemId)items[i]; s.Slots[i].Count = counts[i]; }
        }
        if (mine) _mine.Add(id); else _mine.Remove(id);
        Changed?.Invoke(id);
    }

    private void Reply(long peer, StringName method, params Variant[] args)
    {
        if (Online) RpcId(peer, method, args);
        else Call(method, args);
    }

    // ---- the panel, the prompt ----------------------------------------------------------------------

    private FarmStandUi? _ui;
    public bool IsOpen => _ui?.IsOpen == true;

    /// <summary>The stand within reach of the player on foot, outdoors, or null.</summary>
    public PlacedObject? StandAt(FootPlayer p)
    {
        if (_placed == null || p.Indoors || p.Ride != RideKind.OnFoot) return null;
        PlacedObject? best = null;
        float bestD = Reach;
        foreach (var o in _placed.All.Values)
        {
            if (o.Kind != PlacedKind.FarmStand) continue;
            float d = o.WorldTransform(_placed.Origin).Origin.DistanceTo(p.GlobalPosition);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    /// <summary>E at a stand: its panel (or closes it).</summary>
    public bool TryOpen(FootPlayer p)
    {
        if (IsOpen) { _ui!.Close(); return true; }
        if (StandAt(p) is not { } o || _ui == null) return false;
        _ui.Open(p, o);
        return true;
    }

    private (long Id, bool Mine, int Stocked, int Cash, InputDevice Device) _promptKey;
    private string? _promptText;

    public string? PromptFor(FootPlayer p)
    {
        if (IsOpen || StandAt(p) is not { } o) return null;
        _stands.TryGetValue(o.Id, out var s);
        var key = (o.Id, IsMine(o.Id), s?.Stocked ?? 0, s?.Cash ?? 0, PlayerInput.HintDevice);
        if (_promptText != null && key == _promptKey) return _promptText;
        _promptKey = key;
        string tag = InputHints.Tag(PlayerInput.InteractMount);
        return _promptText = key.Item2
            ? $"{tag} Your farm stand: stock it, take the cash ({key.Item4} CHF in the box)"
            : key.Item3 > 0 ? $"{tag} {o.Owner}'s farm stand: buy ({key.Item3} for sale)" : $"{o.Owner}'s farm stand: nothing for sale";
    }

    // ---- persistence --------------------------------------------------------------------------------

    private string StorePath => Path.Combine(FarmSales.StoreDir, _server ? "stands.json" : "stands_offline.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<StandState>>(File.ReadAllText(StorePath)) ?? new();
            foreach (var s in list) _stands[s.Id] = s;
            GD.Print($"[stand] {list.Count} stand(s) from {StorePath}");
        }
        catch (Exception e) { GD.PushWarning($"[stand] {StorePath}: {e.Message}"); }
    }

    private void Save()
    {
        if (!Serving) return;
        // NaN is not JSON: an unmeasured road is saved as -1 and read back as "measure again"
        var list = _stands.Values.OrderBy(s => s.Id).Select(s => new StandState
        {
            Id = s.Id, Owner = s.Owner, Slots = s.Slots, Cash = s.Cash, RoadM = float.IsNaN(s.RoadM) ? -1 : s.RoadM,
            Houses = s.Houses, LastTick = s.LastTick, Takings = s.Takings,
        }).ToList();
        JsonStore.SaveAsync(StorePath, list, onError: e => GD.PushError($"[stand] saving: {e.Message}"));
    }
}
