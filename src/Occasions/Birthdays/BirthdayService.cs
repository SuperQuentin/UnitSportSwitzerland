using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// Birthdays in a world (#1423, docs/notes/occasions/birthdays.md), at <c>World/Birthdays</c> on the server and every client; offline the
/// client plays the server's part through the same methods.
/// <list type="bullet">
/// <item><b>Who celebrates</b>: each client tells the server its birthday on joining (and on a change); a player whose birthday is today
/// and who let others see it is a celebrant. The server announces them in the chat (with the age when a year was given) and sends the
/// list to every peer, so a client knows whom to offer "Wish a happy birthday". The day turns at the server's midnight.</item>
/// <item><b>Wishes</b>: one per wisher and celebrant and day, within <see cref="BirthdayRules.WishReach"/>; the celebrant reads who
/// and how many, everyone near sees paper and hears the chime (<see cref="ItemEventKind.BirthdayWish"/>). Bots wish through
/// <see cref="ServeWish"/>.</item>
/// <item><b>The cake</b>: a <see cref="PlacedKind.BirthdayCake"/>; this keeps its slices left (in memory: a restart lays the cake
/// whole again) and takes it away eaten. A slice is <see cref="ServeSlice"/>'s, a bot's too.</item>
/// <item><b>The fireworks</b>: a birthday rocket asks <see cref="ServeShow"/>; on the player's birthday, one at a time, at most
/// <see cref="BirthdayRules.ShowsPerDay"/> a day, every peer plays the same minute (<see cref="Festivals.Fireworks.QueueBirthday"/>).</item>
/// </list>
/// The gift is the client's (<see cref="MyBirthday.TakeGift"/>): the pack is the client's own.
/// </summary>
public partial class BirthdayService : Node
{
    public const string NodeName = "Birthdays";

    /// <summary>The client's copy (offline: the one copy).</summary>
    public static BirthdayService? Instance { get; private set; }

    /// <summary>The dedicated server's copy (chat commands, bots).</summary>
    public static BirthdayService? Server { get; private set; }

    private bool _server, _networked;
    private WorldOrigin _origin = null!;

    // ---- hooks, set by the world that makes it --------------------------------------------------------

    /// <summary>A peer's body (a bot's too, on the server).</summary>
    public Func<long, FootPlayer?>? BodyOf { get; set; }
    /// <summary>A peer's display name (a bot's too, on the server).</summary>
    public Func<long, string>? NameOf { get; set; }
    /// <summary>Client: the local player's body on foot.</summary>
    public Func<FootPlayer?>? Me { get; set; }
    /// <summary>Client: a line to the local player.</summary>
    public Action<string>? Toast { get; set; }
    /// <summary>Client: puts a stack in the pack (the overflow at the feet).</summary>
    public Action<ItemStack>? Give { get; set; }
    /// <summary>Server: the chat, for the announcements.</summary>
    public ChatManager? Chat { get; set; }

    public static BirthdayService Create(Node world, WorldOrigin origin, bool server, bool networked)
    {
        var s = new BirthdayService { Name = NodeName, _server = server, _networked = networked, _origin = origin };
        world.AddChild(s);
        return s;
    }

    /// <summary>This copy decides: the server, or an offline client.</summary>
    public bool Authority => _server || !_networked;

    /// <summary>The local player's id (1 offline).</summary>
    public long Self => _networked ? Multiplayer.GetUniqueId() : 1;

    public override void _EnterTree()
    {
        if (_server) Server = this;
        else Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (Server == this) Server = null;
        MyBirthday.Changed -= SendMine;
    }

    public override void _Ready()
    {
        _day = Calendar.Today;
        if (!_server)
        {
            MyBirthday.Hook();
            MyBirthday.Changed += SendMine;
            PlacedObjects.RegisterFactory(PlacedKind.BirthdayCake, CakeNode);
            // offline nobody welcomes us: tell ourselves now; online ClientWorld.OnWelcomed does, after the name
            if (!_networked) Callable.From(SendMine).CallDeferred();
        }
        GD.Print($"[birthday] {(_server ? "server" : _networked ? "client" : "offline")}, today {_day:yyyy-MM-dd}");
    }

    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);
    private string Who(long peer) => NameOf?.Invoke(peer) is { Length: > 0 } n ? n : $"#{peer}";
    private PlacedObjects? Placed => GetParent()?.GetNodeOrNull<PlacedObjects>(PlacedObjects.NodeName);

    // ---- the server's state --------------------------------------------------------------------------

    private DateOnly _day;
    private readonly Dictionary<long, Birthday> _dates = new();
    private readonly HashSet<long> _shown = new();
    /// <summary>Today's celebrants (who let it be seen), peer → name and age.</summary>
    private readonly Dictionary<long, (string Name, int? Age)> _celebrants = new();
    private readonly HashSet<(long From, long To)> _wished = new();
    private readonly Dictionary<long, int> _wishes = new();
    private readonly Dictionary<long, (int Count, double Last)> _shows = new();
    private readonly Dictionary<long, int> _eaten = new();
    private double _nextLook;

    // ---- the client's state --------------------------------------------------------------------------

    private readonly Dictionary<long, string> _seen = new();
    private readonly Dictionary<long, int> _cakeLeft = new();
    private readonly HashSet<long> _wishedByMe = new();
    private double _lastShow = double.NegativeInfinity, _nextGift;
    private bool _greeted;

    /// <summary>Client: the players celebrating today who let it be seen, peer → name.</summary>
    public IReadOnlyDictionary<long, string> Celebrants => _seen;

    /// <summary>Slices left on a cake (all of them until told otherwise).</summary>
    public int CakeLeft(long id) => (Authority ? BirthdayRules.CakeSlices - _eaten.GetValueOrDefault(id) : _cakeLeft.GetValueOrDefault(id, BirthdayRules.CakeSlices));

    /// <summary>Wishes a celebrant got today (the server's count; checks and <c>/birthday</c>).</summary>
    public int WishesOf(long peer) => _wishes.GetValueOrDefault(peer);

    // ---- telling the server our birthday --------------------------------------------------------------

    /// <summary>Client: tells the authority this player's birthday (on joining, on a change, when the day turns).</summary>
    public void SendMine()
    {
        if (_server) return;
        var b = MyBirthday.Date;
        if (Authority) ServeTell(Self, b.Day, b.Month, b.Year, MyBirthday.Public);
        else if (Multiplayer.MultiplayerPeer is { } && !(Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer))
            RpcId(1, MethodName.Tell, b.Day, b.Month, b.Year, MyBirthday.Public);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tell(int day, int month, int year, bool shown)
    {
        if (_server) ServeTell(Multiplayer.GetRemoteSenderId(), day, month, year, shown);
    }

    /// <summary>The authority: <paramref name="peer"/>'s birthday (0/0 for none); announced when it is today and shown.</summary>
    public void ServeTell(long peer, int day, int month, int year, bool shown)
    {
        if (!Authority) return;
        var date = BirthdayRules.Valid(day, month, year, Calendar.Today) ? new Birthday(day, month, year) : Birthday.None;
        _dates[peer] = date;
        if (shown) _shown.Add(peer);
        else _shown.Remove(peer);
        Recount(peer, announce: true);
    }

    /// <summary>Whether <paramref name="peer"/> celebrates today, as told; announces a new celebrant and sends the list on a change.</summary>
    private void Recount(long peer, bool announce)
    {
        var date = _dates.GetValueOrDefault(peer);
        bool celebrates = _shown.Contains(peer) && BirthdayRules.IsToday(date, _day);
        if (celebrates == _celebrants.ContainsKey(peer)) return;
        if (celebrates)
        {
            string name = Who(peer);
            int? age = BirthdayRules.AgeIn(date, _day);
            _celebrants[peer] = (name, age);
            GD.Print($"[birthday] {name} celebrates today{(age is { } a ? $", turns {a}" : "")}");
            if (announce && _networked) Chat?.Broadcast(BirthdayRules.Announcement(name, age), ChatKind.System);
        }
        else _celebrants.Remove(peer);
        if (!_server) SetCelebrants(_celebrants.Keys.ToArray(), _celebrants.Values.Select(c => c.Name).ToArray());
        SendCelebrants();
    }

    private void SendCelebrants()
    {
        if (!_server) return;
        foreach (int p in Multiplayer.GetPeers()) SendCelebrantsTo(p);
    }

    private void SendCelebrantsTo(long peer)
    {
        if (!Connected(peer)) return;
        RpcId(peer, MethodName.SetCelebrants, _celebrants.Keys.ToArray(), _celebrants.Values.Select(c => c.Name).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetCelebrants(long[] peers, string[] names)
    {
        _seen.Clear();
        for (int i = 0; i < peers.Length && i < names.Length; i++) _seen[peers[i]] = names[i];
    }

    /// <summary>Server: a joining peer gets who celebrates and what is left of the cakes.</summary>
    public void SendTo(long peer)
    {
        if (!_server || !Connected(peer)) return;
        SendCelebrantsTo(peer);
        if (_eaten.Count > 0)
            RpcId(peer, MethodName.CakeState, _eaten.Keys.ToArray(), _eaten.Values.Select(e => BirthdayRules.CakeSlices - e).ToArray());
    }

    /// <summary>Server: a peer left: it celebrates no more here.</summary>
    public void PeerLeft(long peer)
    {
        _dates.Remove(peer);
        _shown.Remove(peer);
        _shows.Remove(peer);
        if (_celebrants.Remove(peer)) SendCelebrants();
    }

    public override void _Process(double delta)
    {
        double now = RealClock.Now;   // polling the calendar and the pack: real seconds, whatever the game speed
        if (Authority && now >= _nextLook)
        {
            _nextLook = now + 20;
            // midnight: the day's wishes and shows start again, and who celebrates is counted again
            if (Calendar.Today != _day)
            {
                _day = Calendar.Today;
                _wished.Clear();
                _wishes.Clear();
                _shows.Clear();
                foreach (long peer in _dates.Keys.ToList()) Recount(peer, announce: true);
            }
        }
        if (!_server && now >= _nextGift) LookForGift(now);
    }

    /// <summary>Client: on the birthday, once a session the greeting; once a year the gift into the pack.</summary>
    private void LookForGift(double now)
    {
        _nextGift = now + 2;
        if (Me?.Invoke() == null || Give == null || !MyBirthday.IsToday)
        {
            if (!MyBirthday.IsToday) _greeted = false;
            return;
        }
        string greeting = MyBirthday.Greeting(GameSettings.Current.PlayerName) ?? "";
        if (MyBirthday.TakeGift())
        {
            Give(new ItemStack(ItemId.BirthdayGift, 1, Calendar.Today.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Toast?.Invoke(greeting + " A gift is in your pack: open it from the inventory.");
            _greeted = true;
            SendMine();   // the day may have turned while playing
        }
        else if (!_greeted)
        {
            Toast?.Invoke(greeting);
            _greeted = true;
            SendMine();
        }
    }

    // ---- lines to one player ------------------------------------------------------------------------

    /// <summary>The authority: a line to one player (a bot reads nothing).</summary>
    private void Note(long peer, string text)
    {
        if (peer <= 0) return;
        if (_networked && _server)
        {
            if (Connected(peer)) RpcId(peer, MethodName.Told, text);
        }
        else Toast?.Invoke(text);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Told(string text) => Toast?.Invoke(text);

    // ---- wishes --------------------------------------------------------------------------------------

    /// <summary>Server: today's celebrants with their bodies (what a bot senses: the list every peer holds).</summary>
    public IEnumerable<(long Peer, FootPlayer Body)> CelebrantBodies()
    {
        foreach (long peer in _celebrants.Keys)
            if (BodyOf?.Invoke(peer) is { } body) yield return (peer, body);
    }

    /// <summary>Server: <paramref name="from"/> already wished <paramref name="to"/> today.</summary>
    public bool HasWished(long from, long to) => _wished.Contains((from, to));

    /// <summary>Client: whether "Wish a happy birthday" is offered at <paramref name="peer"/>.</summary>
    public bool CanWish(long peer) => peer > 0 && peer != Self && Celebrants.ContainsKey(peer) && !_wishedByMe.Contains(peer);

    /// <summary>The prompt's words at a celebrant.</summary>
    public string WishPrompt(long peer) => $"Wish {(Celebrants.TryGetValue(peer, out var n) ? n : "them")} a happy birthday";

    /// <summary>
    /// Client: wishes <paramref name="peer"/> a happy birthday (the server checks and tells both). The wish is offered again until the
    /// server confirms it (<see cref="WishTaken"/>): a refused one ("walk up first") may be tried again.
    /// </summary>
    public void Wish(long peer)
    {
        if (Authority) ServeWish(Self, peer);
        else RpcId(1, MethodName.RequestWish, peer);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WishTaken(long target) => _wishedByMe.Add(target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestWish(long target)
    {
        if (_server) ServeWish(Multiplayer.GetRemoteSenderId(), target);
    }

    /// <summary>
    /// The authority: <paramref name="from"/> (a player or a bot) wishes <paramref name="to"/> a happy birthday. Null when done, else
    /// why not (told to a player). Bots call this directly.
    /// </summary>
    public string? ServeWish(long from, long to)
    {
        if (!Authority) return "Not here.";
        _celebrants.TryGetValue(to, out var c);
        FootPlayer? a = BodyOf?.Invoke(from), b = BodyOf?.Invoke(to);
        string? why = from == to ? "You cannot wish yourself a happy birthday."
            : c.Name == null ? "It is not their birthday today."
            : _wished.Contains((from, to)) ? $"You already wished {c.Name} a happy birthday today."
            : a == null || b == null ? "They are not here."
            : a.Global.DistanceTo(b.Global) > BirthdayRules.WishReach + 1.5f ? $"Walk up to {c.Name} first."
            : null;
        if (why != null)
        {
            GD.Print($"[birthday] {Who(from)}'s wish to peer {to} refused: {why}"
                + (a != null && b != null ? $" ({a.Global.DistanceTo(b.Global):F1} m apart)" : ""));
            Note(from, why);
            return why;
        }
        _wished.Add((from, to));
        int count = _wishes[to] = _wishes.GetValueOrDefault(to) + 1;
        string wisher = Who(from);
        GD.Print($"[birthday] {wisher} wished {c.Name} a happy birthday ({count} today)");
        Note(to, BirthdayRules.Wished(wisher, count));
        Note(from, $"You wished {c.Name} a happy birthday.");
        if (!_server) WishTaken(to);
        else if (Connected(from)) RpcId(from, MethodName.WishTaken, to);
        var head = b!.Global;
        var at = _origin.ToWorld(head.E, head.N, head.Alt + 1.9);
        if (_server) GetParent()?.GetNodeOrNull<ItemEvents>(ItemEvents.NodeName)?.ServerSend(to, ItemEventKind.BirthdayWish, at, Vector3.Zero, wisher);
        return null;
    }

    // ---- the cake ------------------------------------------------------------------------------------

    /// <summary>A cake's visual: the cake with the slices left, its mesh swapped when a slice is taken.</summary>
    private Node3D CakeNode(PlacedObject o)
    {
        var root = new Node3D();
        root.AddChild(new MeshInstance3D { Name = "Cake", Mesh = BirthdayMeshes.Cake(CakeLeft(o.Id)), MaterialOverride = ItemDefs.Material });
        return root;
    }

    /// <summary>Client: the cake within <see cref="BirthdayRules.SliceReach"/> of <paramref name="me"/>'s feet with a slice left, nearest first.</summary>
    public PlacedObject? CakeAt(FootPlayer me)
    {
        if (PlacedObjects.Instance is not { } placed) return null;
        PlacedObject? best = null;
        float bestD = BirthdayRules.SliceReach;
        foreach (var o in placed.All.Values)
        {
            if (o.Kind != PlacedKind.BirthdayCake || CakeLeft(o.Id) <= 0) continue;
            float d = me.GlobalPosition.DistanceTo(o.WorldTransform(_origin).Origin);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    /// <summary>Client: a cake within reach of the camera's spot, for the Polaroid's birthday frame (8 m).</summary>
    public bool CakeNear(Vector3 at, float within = 8f) =>
        PlacedObjects.Instance is { } placed && placed.All.Values.Any(o => o.Kind == PlacedKind.BirthdayCake && o.WorldTransform(_origin).Origin.DistanceTo(at) <= within);

    /// <summary>The prompt and outline at a cake in reach (<c>InteriorManager.Interacting</c>, in E's order).</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } s || s.CakeAt(p) is not { } cake) return null;
        var node = PlacedObjects.Instance?.GetNodeOrNull<Node3D>($"P{cake.Id}");
        string whose = cake.Owner.Length > 0 ? $"{cake.Owner}'s cake" : "the cake";
        return new(InputHints.Prompt(PlayerInput.InteractMount, $"Take a slice of {whose} ({s.CakeLeft(cake.Id)} left)"), Outline.Of(node));
    }

    /// <summary>E at a cake: asks for a slice. True when a cake was in reach (E is spent).</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } s || s.CakeAt(p) is not { } cake) return false;
        if (s.Authority) s.ServeSlice(s.Self, cake.Id);
        else s.RpcId(1, MethodName.RequestSlice, cake.Id);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSlice(long cake)
    {
        if (_server) ServeSlice(Multiplayer.GetRemoteSenderId(), cake);
    }

    /// <summary>The authority: <paramref name="peer"/> (a player or a bot) takes a slice of <paramref name="cake"/>. Null when taken, else why not.</summary>
    public string? ServeSlice(long peer, long cake)
    {
        if (!Authority) return "Not here.";
        var placed = Placed;
        PlacedObject? o = placed != null && placed.All.TryGetValue(cake, out var found) && found.Kind == PlacedKind.BirthdayCake ? found : null;
        FootPlayer? body = BodyOf?.Invoke(peer);
        string? why = o == null ? "The cake is gone."
            : CakeLeft(cake) <= 0 ? "Not a crumb left."
            : body == null ? "Not here."
            : body.Global.DistanceTo(new GlobalPos(o.E, o.N, o.Altitude)) > BirthdayRules.SliceReach + 1.5f ? "Too far from the cake."
            : null;
        if (why != null)
        {
            Note(peer, why);
            return why;
        }
        int left = BirthdayRules.CakeSlices - (_eaten[cake] = _eaten.GetValueOrDefault(cake) + 1);
        GD.Print($"[birthday] {Who(peer)} took a slice of cake #{cake}, {left} left");
        if (_server)
        {
            foreach (int p in Multiplayer.GetPeers()) RpcId(p, MethodName.CakeState, new[] { cake }, new[] { left });
            if (Connected(peer)) RpcId(peer, MethodName.Sliced, o!.Owner);
        }
        else
        {
            ShowLeft(cake, left);
            Sliced(o!.Owner);
        }
        if (left <= 0)
        {
            _eaten.Remove(cake);
            placed!.ServerRemove(cake);
        }
        return null;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CakeState(long[] cakes, int[] left)
    {
        for (int i = 0; i < cakes.Length && i < left.Length; i++) ShowLeft(cakes[i], left[i]);
    }

    private void ShowLeft(long cake, int left)
    {
        if (!Authority) _cakeLeft[cake] = left;
        if (PlacedObjects.Instance?.GetNodeOrNull<MeshInstance3D>($"P{cake}/Cake") is { } mesh) mesh.Mesh = BirthdayMeshes.Cake(left);
    }

    /// <summary>Stamina a slice gives back, and the minutes it keeps you fed.</summary>
    public const float SliceStamina = 40f, SliceFedSeconds = 120f;

    /// <summary>Client: the slice is ours: eaten at once, a burst of stamina.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Sliced(string owner)
    {
        string whose = owner.Length > 0 ? $"{owner}'s birthday cake" : "birthday cake";
        if (Me?.Invoke() is { } me && me.StaminaWouldTake(SliceStamina, StaminaBuff.Fed))
        {
            float gained = me.RestoreStamina(SliceStamina);
            me.BuffStamina(StaminaBuff.Fed, SliceFedSeconds);
            Toast?.Invoke($"A slice of {whose}: +{gained:F0} stamina, fed for 2 minutes.");
        }
        else Toast?.Invoke($"A slice of {whose}. Delicious!");
    }

    // ---- the fireworks -------------------------------------------------------------------------------

    /// <summary>Client: a birthday rocket in <paramref name="me"/>'s hand is lit. False with why not (the rocket is kept).</summary>
    public bool TryLight(FootPlayer me, out string? why)
    {
        double now = ClockSync.SimNow;
        why = !MyBirthday.IsToday ? "A birthday rocket is lit on your birthday."
            : now - _lastShow < BirthdayRules.ShowCooldown ? "Wait for your fireworks to end."
            : null;
        if (why != null) return false;
        _lastShow = now;
        var at = me.Global;
        if (Authority) ServeShow(Self, at.E, at.N);
        else RpcId(1, MethodName.RequestShow, at.E, at.N);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestShow(double e, double n)
    {
        if (_server) ServeShow(Multiplayer.GetRemoteSenderId(), e, n);
    }

    /// <summary>The authority: <paramref name="peer"/> lights birthday fireworks at <paramref name="e"/>, <paramref name="n"/>. Null when lit, else why not.</summary>
    public string? ServeShow(long peer, double e, double n)
    {
        if (!Authority) return "Not here.";
        double now = ClockSync.SimNow;
        (int Count, double Last) shows = _shows.TryGetValue(peer, out var had) ? had : (0, double.NegativeInfinity);
        string? why = !BirthdayRules.IsToday(_dates.GetValueOrDefault(peer), _day) ? "A birthday rocket is lit on your birthday."
            : shows.Count >= BirthdayRules.ShowsPerDay ? "That was today's last show."
            : now - shows.Last < BirthdayRules.ShowCooldown ? "Wait for your fireworks to end."
            : !double.IsFinite(e) || !double.IsFinite(n) || BodyOf?.Invoke(peer) is not { } body ? "Not here."
            : body.Global.DistanceTo(new GlobalPos(e, n, body.Global.Alt)) > 30 ? "Light it where you stand."
            : null;
        if (why != null)
        {
            Note(peer, why);
            return why;
        }
        _shows[peer] = (shows.Count + 1, now);
        double start = now + 3;
        int seed = BirthdayRules.ShowSeed(Who(peer), _day, shows.Count + 1);
        GD.Print($"[birthday] {Who(peer)} lit birthday fireworks, seed {seed}");
        if (_server)
        {
            foreach (int p in Multiplayer.GetPeers()) RpcId(p, MethodName.Show, start, seed, e, n);
        }
        else Show(start, seed, e, n);
        return null;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Show(double start, int seed, double e, double n) => Festivals.Fireworks.Instance?.QueueBirthday(start, seed, e, n);

    // ---- /birthday -----------------------------------------------------------------------------------

    /// <summary>The <c>/birthday</c> line: who celebrates today, and your wishes when it is yours.</summary>
    public string Command(long asker)
    {
        if (_celebrants.Count == 0) return "Nobody here celebrates a birthday today.";
        string who = string.Join(", ", _celebrants.Values.Select(c => c.Age is { } a ? $"{c.Name} ({a})" : c.Name));
        string mine = _celebrants.ContainsKey(asker) ? " " + BirthdayRules.WishesToday(WishesOf(asker)) : "";
        return $"Birthdays today: {who}.{mine}";
    }
}
