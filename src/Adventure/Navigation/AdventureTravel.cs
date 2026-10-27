using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The travel network and the map pins (#1226, <c>docs/plans/adventure-navigation.md</c> 2 and 5), at
/// <c>World/Adventure/Travel</c> on the server and every client (RPCs route by node path), so it exists exactly where
/// the service does. The server unlocks travel points as bodies pass them (1 Hz, two hits), prices and charges trips
/// (<see cref="TravelNetwork.MayTravel"/>, <see cref="TravelRules"/>, <c>Bank.Charge</c>), sells and grants the two
/// travelcards, keeps the <c>pins</c> section and sends shared pins to the party; offline the client plays the server's
/// part, as <see cref="AdventureService"/>. The owner hears its sections through <c>SectionSync</c>; a trip arrives as
/// <c>Depart</c>, which plays the kind's curtain and jumps (<see cref="Teleporter"/>). Docs:
/// <c>docs/notes/adventure/travel.md</c>, <c>pins.md</c>, <c>fares.md</c>.
/// </summary>
public partial class AdventureTravel : Node
{
    public const string NodeName = "Travel";
    public const string HalfFareArg = "card:halffare", GaArg = "card:ga";
    private const double TickEvery = 1.0;
    /// <summary>Slower than this a body in a vehicle still unlocks (navigation plan 5.2).</summary>
    private const float UnlockSpeed = 15f;
    /// <summary>Where the body lands from the sign's foot (east of it), metres.</summary>
    private const float ArriveOffsetM = 5f;
    public const string FaresPath = "res://assets/adventure/fares.json";

    public static AdventureTravel? Instance { get; private set; }

    private AdventureService _adventure = null!;
    private bool _server, _networked;
    private bool Authority => _adventure.Authority;

    public PoiCatalogue Catalogue => PoiCatalogueHost.Current;
    public TravelRules Rules { get; private set; } = TravelRules.Default;

    /// <summary>The server's mode (its flags), an online client's as the server said, offline the setting.</summary>
    public TravelMode Mode { get; private set; }
    public event Action? ModeChanged;

    // ---- the authority's hooks (ServerWorld, ClientWorld) ---------------------------------------------
    /// <summary>A peer is an operator (always travels, pays nothing).</summary>
    public Func<long, bool>? IsAdmin { get; set; }
    /// <summary>A peer is an entrant of a race.</summary>
    public Func<long, bool>? Racing { get; set; }
    /// <summary>The ground height at a point (a bot's landing).</summary>
    public Func<double, double, float?>? GroundAt { get; set; }

    private sealed class PeerState
    {
        public readonly UnlockTracker Tracker = new();
        public bool HasLast;
        public double LastE, LastN;
        public double NextAsk;
        public int PinOps;
        public double PinSecond;
    }

    private readonly Dictionary<long, PeerState> _peers = new();
    private readonly List<long> _tickPeers = new();
    private readonly List<Poi> _near = new();
    private double _nextTick;

    /// <summary>Authority: unlocks and trips since start (checks).</summary>
    public int Unlocks { get; private set; }
    public int Trips { get; private set; }

    // ---- the owner's side --------------------------------------------------------------------------

    /// <summary>Client: a travel point was unlocked (the toast).</summary>
    public event Action<Poi>? UnlockedPoint;
    /// <summary>Client: the server refused a trip, a card or a pin, and why.</summary>
    public event Action<string>? Refused;
    /// <summary>Client: a trip goes: the point, the fare paid.</summary>
    public event Action<Poi, int>? Departing;
    /// <summary>Client: the last refusal (checks).</summary>
    public string LastRefusal { get; private set; } = "";

    /// <summary>Client: the party's shared pins, by owner: the owner's name and its pins. Never stored.</summary>
    public Dictionary<long, (string Name, List<Pin> Pins)> PartyPins { get; } = new();
    public int PartyPinsVersion { get; private set; }
    public event Action? PartyPinsChanged;

    /// <summary>Client: this player's travel and pins sections as the server last sent them.</summary>
    /// <remarks>A client's mirror is read without making the section (the server sends it); offline it is the progress itself.</remarks>
    public TravelBook MyBook => Authority || _adventure.Mine.Has(TravelBook.Section.Id) ? _adventure.Mine.Get(TravelBook.Section) : NoBook;
    public PinBook MyPins => Authority || _adventure.Mine.Has(PinBook.Section.Id) ? _adventure.Mine.Get(PinBook.Section) : NoPins;
    private static readonly TravelBook NoBook = new();
    private static readonly PinBook NoPins = new();

    public static AdventureTravel Create(AdventureService adventure, bool server, bool networked)
    {
        var t = new AdventureTravel { Name = NodeName, _adventure = adventure, _server = server, _networked = networked };
        adventure.AddChild(t);
        return t;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (Net.Parties.Server is { } parties) parties.StateSent -= OnPartyState;
    }

    public override void _Ready()
    {
        try
        {
            if (Godot.FileAccess.FileExists(FaresPath)) Rules = TravelRules.Parse(Godot.FileAccess.GetFileAsString(FaresPath));
            else GD.PushWarning($"[travel] {FaresPath} is missing: built-in fares");
        }
        catch (Exception e) { GD.PushWarning($"[travel] {FaresPath}: {e.Message} (built-in fares)"); }
        // the navigation plan's quests (All aboard, Grand tour, the Aletsch cable car) are offered: server and clients
        _adventure.Provides("navigation");
        if (_server) Mode = ServerMode();
        else if (!_networked) Mode = OfflineMode();
        Permissions.SetOfflineTravelNetwork(!_networked && Mode == TravelMode.Network);
        GameSettings.Changed += OnSettings;
        if (!Authority) return;
        _adventure.RegisterReward("travel", TravelReward);
        _adventure.Loaded += OnLoaded;
        if (Net.Parties.Server is { } parties) parties.StateSent += OnPartyState;
        GD.Print($"[travel] {(_server ? "server" : "offline")}: mode {Mode}, rail min CHF {Rules.Rail.MinFare.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GA {Rules.GaCard.Now}");
    }

    /// <summary>The server's mode from its flags: <c>--no-teleport</c> is Off, <c>--adventure-travel network</c> Network.</summary>
    public static TravelMode ServerMode() =>
        CmdArgs.Has("--no-teleport") ? TravelMode.Off
        : CmdArgs.Value("--adventure-travel")?.ToLowerInvariant() switch { "network" => TravelMode.Network, "off" => TravelMode.Off, _ => TravelMode.Free };

    /// <summary>Offline: the setting, or a check's <c>--adventure-travel network</c>.</summary>
    private static TravelMode OfflineMode() =>
        GameSettings.Current.AdventureTravelNetwork || CmdArgs.Value("--adventure-travel")?.ToLowerInvariant() == "network" ? TravelMode.Network : TravelMode.Free;

    private void OnSettings()
    {
        if (_networked || !IsInstanceValid(this)) return;
        var want = OfflineMode();
        if (want == Mode) return;
        Mode = want;
        Permissions.SetOfflineTravelNetwork(Mode == TravelMode.Network);
        ModeChanged?.Invoke();
    }

    private void OnLoaded(long peer)
    {
        if (_server && Connected(peer)) RpcId(peer, MethodName.TellMode, (byte)Mode);
        SendPartyPins(peer);
    }

    private PeerState StateOf(long peer)
    {
        if (!_peers.TryGetValue(peer, out var s)) _peers[peer] = s = new PeerState();
        return s;
    }

    /// <summary>Server: a peer left.</summary>
    public void PeerLeft(long peer) => _peers.Remove(peer);

    // ---- authority: unlocking -----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!Authority) return;
        double now = GameClock.Now;
        if (now < _nextTick) return;
        double dt = _nextTick == 0 ? TickEvery : now - _nextTick + TickEvery;
        _nextTick = now + TickEvery;
        if (Catalogue.Count == 0 || PlacesService.Instance is not { BodyOf: { } bodyOf }) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _tickPeers.Clear();
        foreach (long peer in _adventure.Peers) _tickPeers.Add(peer);
        foreach (long peer in _tickPeers)
        {
            if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) continue;
            if (bodyOf(peer) is not { } body || body.Out) continue;
            var s = StateOf(peer);
            double speed = s.HasLast ? Math.Sqrt((body.E - s.LastE) * (body.E - s.LastE) + (body.N - s.LastN) * (body.N - s.LastN)) / Math.Max(dt, 0.1) : 0;
            (s.LastE, s.LastN, s.HasLast) = (body.E, body.N, true);
            if (!PlacesRules.Reveals(body.RideKind) && speed > UnlockSpeed) { s.Tracker.Last = null; continue; }
            Catalogue.Near(body.E, body.N, TravelNetwork.NearM, _near);
            if (TravelNetwork.CheckUnlock(p.Get(TravelBook.Section), body.E, body.N, _near, s.Tracker) is { } poi) Unlock(peer, p, poi, raise: true);
        }
        Net.ServerStats.Ran("travel", t0);
    }

    /// <summary>Unlocks a travel point for a peer: the section written and touched, then <see cref="TravelUnlocked"/> (when asked), then the owner's toast.</summary>
    private void Unlock(long peer, PlayerProgress p, Poi poi, bool raise)
    {
        var book = p.Get(TravelBook.Section);
        if (!book.Unlocked.Add(poi.Id)) return;
        p.Touch(TravelBook.Section);
        Unlocks++;
        GD.Print($"[travel] {Short(p.Key)} unlocked {poi.Id} ({book.Unlocked.Count} points)");
        if (raise)
        {
            int cantons = CantonsOf(book);
            _adventure.Events.Raise(new TravelUnlocked(peer, p.Key, poi.Id, TravelNetwork.Tag(TravelNetwork.KindOf(poi.Kind)), poi.Canton, book.Unlocked.Count, cantons));
        }
        if (Connected(peer)) RpcId(peer, MethodName.TellUnlocked, poi.Id);
        else if (!_networked && peer == AdventureService.LocalPeer) TellUnlocked(poi.Id);
    }

    /// <summary>The cantons a network spans.</summary>
    public int CantonsOf(TravelBook book)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in book.Unlocked)
            if (Catalogue.Resolve(id) is { Canton.Length: > 0 } poi) set.Add(poi.Canton);
        return set.Count;
    }

    /// <summary>
    /// The <c>"travel"</c> reward: a POI id unlocks that point (<see cref="TravelUnlocked"/> raised only when new);
    /// <c>card:halffare</c> / <c>card:ga</c> give that travelcard. Null when done, else why not.
    /// </summary>
    private string? TravelReward(long peer, PlayerProgress p, string arg)
    {
        if (arg is HalfFareArg or GaArg) return GiveCard(peer, p, arg == GaArg, "quest") ? null : "already held";
        if (Catalogue.Resolve(arg) is not { } poi) return $"no place '{arg}' on this map";
        if (!TravelNetwork.IsPoint(poi)) return $"'{arg}' is no travel point";
        Unlock(peer, p, poi, raise: true);
        return null;
    }

    private bool GiveCard(long peer, PlayerProgress p, bool ga, string why)
    {
        var book = p.Get(TravelBook.Section);
        if (ga ? book.HasGa : book.HasHalfFare) return false;
        if (ga) book.HasGa = true;
        else book.HasHalfFare = true;
        p.Touch(TravelBook.Section);
        GD.Print($"[travel] {Short(p.Key)} holds the {(ga ? "GA" : "half-fare")} travelcard ({why})");
        _adventure.Grant(peer, new Item(ga ? ItemId.GaCard : ItemId.HalfFare), why);
        return true;
    }

    // ---- authority: travelling ----------------------------------------------------------------------

    /// <summary>The ask for a peer's trip, from what the server knows of it.</summary>
    private TravelAsk AskOf(long peer, PlayerProgress p, Poi? to, Poi? sign)
    {
        var body = PlacesService.Instance?.BodyOf?.Invoke(peer);
        bool admin = IsAdmin?.Invoke(peer) ?? false;
        return new TravelAsk(Mode, admin, _adventure.Active(peer), _adventure.InMatch?.Invoke(peer) ?? false,
            body is { Out: true }, body is { } b && !TravelNetwork.MayTravelAs(b.RideKind), Racing?.Invoke(peer) ?? false,
            p.Get(TravelBook.Section), to, sign, body?.E ?? double.NaN, body?.N ?? double.NaN, Now, Bank.Instance?.BalanceOf(peer) ?? 0);
    }

    /// <summary>
    /// A peer's trip (its <c>RequestTravel</c>, a check's call): the rules (<see cref="TravelNetwork.MayTravel"/>), the
    /// fare charged to its account, the section touched, the places told it arrives (<see cref="PlacesService.Arrived"/>),
    /// then the owner's <c>Depart</c>. Null when it goes, else why not (the owner is told).
    /// </summary>
    public string? Travel(long peer, string toId, string fromSignId)
    {
        if (_adventure.Progress(peer) is not { } p) return Refuse(peer, TravelRefusal.NoAdventure);
        var to = Catalogue.Resolve(toId);
        var sign = fromSignId.Length > 0 ? Catalogue.Resolve(fromSignId) : null;
        if (fromSignId.Length > 0 && sign == null) return Refuse(peer, TravelRefusal.NotAtSign);
        var ask = AskOf(peer, p, to, sign);
        var (why, fare) = TravelNetwork.MayTravel(ask, Rules);
        if (why != TravelRefusal.None) return Refuse(peer, why);
        if (fare.Chf > 0 && !(Bank.Instance?.Charge(peer, fare.Chf, out _) ?? false)) return Refuse(peer, TravelRefusal.CantAfford);
        if (fare.Chf > 0) Bank.Instance?.Tell(peer);
        var book = p.Get(TravelBook.Section);
        book.LastTravel = Now;
        book.Trips++;
        book.Spent += fare.Chf;
        p.Touch(TravelBook.Section);
        Trips++;
        var (e, n) = ArrivalOf(to!);
        PlacesService.Instance?.Arrived(peer, e, n);
        var s = StateOf(peer);
        s.HasLast = false;
        s.Tracker.Last = null;
        GD.Print(FormattableString.Invariant($"[travel] {Short(p.Key)} travels to {to!.Id} from {(sign?.Id ?? "anywhere")} for CHF {fare.Chf} ({fare.Basis}, {fare.Km:F1} km)"));
        if (Connected(peer)) RpcId(peer, MethodName.Depart, to.Id, fare.Chf, e, n);
        else if (!_networked && peer == AdventureService.LocalPeer) Depart(to.Id, fare.Chf, e, n);
        return null;
    }

    /// <summary>Where a trip lands: beside the sign's foot.</summary>
    public static (double E, double N) ArrivalOf(Poi to) => (to.E + ArriveOffsetM, to.N);

    /// <summary>
    /// A server-run bot's trip (#837): the same rules, then its body placed with <see cref="Player.FootPlayer.DebugLaunch"/>
    /// (the server owns a bot's body: no RPC). Its progress is the session's. Null when it went, else why not.
    /// </summary>
    public string? TravelBot(Bots.Bot bot, string toId, string fromSignId = "")
    {
        if (!_server || !bot.Alive) return "no bot";
        _adventure.SessionFor(bot.Id);
        if (_adventure.Progress(bot.Id) is not { } p) return "no progress";
        var to = Catalogue.Resolve(toId);
        var sign = fromSignId.Length > 0 ? Catalogue.Resolve(fromSignId) : null;
        var (why, fare) = TravelNetwork.MayTravel(AskOf(bot.Id, p, to, sign), Rules);
        if (why != TravelRefusal.None) return TravelNetwork.Why(why);
        if (fare.Chf > 0 && !(Bank.Instance?.Charge(bot.Id, fare.Chf, out _) ?? false)) return TravelNetwork.Why(TravelRefusal.CantAfford);
        var (e, n) = ArrivalOf(to!);
        if (bot.Body.Origin is not { } origin) return "no origin";
        float ground = GroundAt?.Invoke(e, n) ?? to!.Elevation;
        bot.Body.DebugLaunch(origin.ToWorld(e, n, ground + 1.5), Vector3.Zero);
        p.Get(TravelBook.Section).LastTravel = Now;
        p.Touch(TravelBook.Section);
        PlacesService.Instance?.Arrived(bot.Id, e, n);
        GD.Print($"[travel] bot {bot.Id} travels to {to!.Id}");
        return null;
    }

    /// <summary>
    /// Buys a travelcard at an SBB ticket office (a station sign, <see cref="TravelNetwork.SignReachM"/>): its price from
    /// <see cref="TravelRules"/> on today's date, charged to the account. Null when bought, else why not.
    /// </summary>
    public string? BuyCard(long peer, bool ga, string stationId)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return RefuseText(peer, "No adventuring now.");
        if (Mode == TravelMode.Off && !(IsAdmin?.Invoke(peer) ?? false)) return RefuseText(peer, TravelNetwork.Why(TravelRefusal.Off));
        var book = p.Get(TravelBook.Section);
        if (ga ? book.HasGa : book.HasHalfFare) return RefuseText(peer, "You hold that card already.");
        var body = PlacesService.Instance?.BodyOf?.Invoke(peer);
        if (Catalogue.Resolve(stationId) is not { Kind: PoiKind.RailStation } station || body is not { } b
            || TravelNetwork.Distance(station, b.E, b.N) > TravelNetwork.SignReachM)
            return RefuseText(peer, "Buy it at a station's ticket office.");
        int price = TravelRules.CardPrice(ga ? Rules.GaCard : Rules.HalfFareCard, Today);
        if (!(Bank.Instance?.Charge(peer, price, out _) ?? false)) return RefuseText(peer, $"CHF {price} is more than your account holds.");
        Bank.Instance?.Tell(peer);
        GiveCard(peer, p, ga, $"bought for CHF {price}");
        return null;
    }

    /// <summary>The rewards plan's portable flag (5.6): a travel point "Your flag" for that player.</summary>
    public void SetPortable(long peer, double e, double n, float alt)
    {
        if (_adventure.Progress(peer) is not { } p) return;
        p.Get(TravelBook.Section).Flag = new PortableFlag(e, n, alt, Now);
        p.Touch(TravelBook.Section);
    }

    public void ClearPortable(long peer)
    {
        if (_adventure.Progress(peer) is not { } p || p.Get(TravelBook.Section).Flag == null) return;
        p.Get(TravelBook.Section).Flag = null;
        p.Touch(TravelBook.Section);
    }

    private string Refuse(long peer, TravelRefusal why) => RefuseText(peer, TravelNetwork.Why(why));

    private string RefuseText(long peer, string why)
    {
        GD.Print($"[travel] peer {peer} refused: {why}");
        if (Connected(peer)) RpcId(peer, MethodName.TellRefused, why);
        else if (!_networked && peer == AdventureService.LocalPeer) TellRefused(why);
        return why;
    }

    // ---- authority: pins ------------------------------------------------------------------------------

    /// <summary>10 pin changes a second per peer at most.</summary>
    private bool PinRate(long peer)
    {
        var s = StateOf(peer);
        double now = GameClock.Now;
        if (now - s.PinSecond >= 1) { s.PinSecond = now; s.PinOps = 0; }
        return ++s.PinOps <= 10;
    }

    public string? PinAdd(long peer, double e, double n, byte icon, string note, bool shared)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return RefuseText(peer, "No adventuring now.");
        if (!PinRate(peer)) return "too fast";
        var book = p.Get(PinBook.Section);
        if (book.Add(e, n, icon, note, shared, Now, out string? why) is not { } pin) return RefuseText(peer, why ?? "No pin.");
        p.Touch(PinBook.Section);
        GD.Print(FormattableString.Invariant($"[pins] {Short(p.Key)} pin {pin.Id} at {e:F0}/{n:F0}{(shared ? " shared" : "")}"));
        if (shared) SharePins(peer);
        return null;
    }

    public string? PinEdit(long peer, int id, byte icon, string note, bool shared)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return RefuseText(peer, "No adventuring now.");
        if (!PinRate(peer)) return "too fast";
        var book = p.Get(PinBook.Section);
        bool wasShared = book.Find(id)?.Shared ?? false;
        if (!book.Edit(id, icon, note, shared)) return RefuseText(peer, "No such pin.");
        p.Touch(PinBook.Section);
        if (shared || wasShared) SharePins(peer);
        return null;
    }

    public string? PinRemove(long peer, int id)
    {
        if (_adventure.Progress(peer) is not { } p) return "no progress";
        if (!PinRate(peer)) return "too fast";
        var book = p.Get(PinBook.Section);
        bool wasShared = book.Find(id)?.Shared ?? false;
        if (!book.Remove(id)) return RefuseText(peer, "No such pin.");
        p.Touch(PinBook.Section);
        if (wasShared) SharePins(peer);
        return null;
    }

    /// <summary>A peer's shared pins changed: every other member of its party hears its party's pins again.</summary>
    private void SharePins(long owner)
    {
        if (Net.Parties.Server?.Of(owner) is not { } party) return;
        foreach (long m in party.Members) if (m != owner) SendPartyPins(m);
    }

    /// <summary>The party changed for a peer (the server's <c>Parties</c> told it its state).</summary>
    private void OnPartyState(long peer) => SendPartyPins(peer);

    private readonly List<Pin> _shared = new();

    /// <summary>Sends a peer the shared pins of its party's other members (empty when in none): it replaces what it had.</summary>
    private void SendPartyPins(long peer)
    {
        if (!_server || !Connected(peer)) return;
        var owners = new List<long>();
        var names = new List<string>();
        var ids = new List<int>();
        var es = new List<double>();
        var ns = new List<double>();
        var icons = new List<byte>();
        var notes = new List<string>();
        if (Net.Parties.Server?.Of(peer) is { } party)
            foreach (long m in party.Members)
            {
                if (m == peer || _adventure.Progress(m) is not { } p) continue;
                p.Get(PinBook.Section).SharedPins(_shared);
                foreach (var pin in _shared)
                {
                    owners.Add(m);
                    names.Add(_adventure.NameOf?.Invoke(m) ?? $"Rider{m}");
                    ids.Add(pin.Id);
                    es.Add(pin.E);
                    ns.Add(pin.N);
                    icons.Add(pin.Icon);
                    notes.Add(pin.Note);
                }
            }
        RpcId(peer, MethodName.PartyPinsState, owners.ToArray(), names.ToArray(), ids.ToArray(), es.ToArray(), ns.ToArray(), icons.ToArray(), notes.ToArray());
    }

    // ---- the owner's requests -------------------------------------------------------------------------

    /// <summary>Asks for a trip: from a sign (its POI id) or from anywhere (""): offline at once, online through the server.</summary>
    public void AskTravel(string toId, string fromSignId = "")
    {
        if (Authority) Travel(AdventureService.LocalPeer, toId, fromSignId);
        else RpcId(1, MethodName.RequestTravel, toId, fromSignId);
    }

    public void AskBuy(bool ga, string stationId)
    {
        if (Authority) BuyCard(AdventureService.LocalPeer, ga, stationId);
        else RpcId(1, MethodName.RequestBuy, ga, stationId);
    }

    public void AskPinAdd(double e, double n, byte icon, string note, bool shared)
    {
        if (Authority) PinAdd(AdventureService.LocalPeer, e, n, icon, note, shared);
        else RpcId(1, MethodName.RequestPinAdd, e, n, icon, note, shared);
    }

    public void AskPinEdit(int id, byte icon, string note, bool shared)
    {
        if (Authority) PinEdit(AdventureService.LocalPeer, id, icon, note, shared);
        else RpcId(1, MethodName.RequestPinEdit, id, icon, note, shared);
    }

    public void AskPinRemove(int id)
    {
        if (Authority) PinRemove(AdventureService.LocalPeer, id);
        else RpcId(1, MethodName.RequestPinRemove, id);
    }

    /// <summary>Client: whether this player may travel to a point now, and the fare, as the server will judge it (display only).</summary>
    public (TravelRefusal Why, Fare Fare) Preview(Poi to, Poi? sign, GlobalPos at, bool down, bool driving)
    {
        var ask = new TravelAsk(Mode, Permissions.IsAdmin && Permissions.Online, AdventureLog.Adventuring, Permissions.InMatch,
            down, driving, false, MyBook, to, sign, at.E, at.N, Now, Bank.Instance?.Balance ?? 0);
        return TravelNetwork.MayTravel(ask, Rules);
    }

    // ---- RPCs: client -> server ----------------------------------------------------------------------

    private bool Throttled(long peer)
    {
        var s = StateOf(peer);
        double now = GameClock.Now;
        if (now < s.NextAsk) return true;
        s.NextAsk = now + 1.0;
        return false;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTravel(string toId, string fromSignId)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (Throttled(peer)) { RefuseText(peer, TravelNetwork.Why(TravelRefusal.Cooldown)); return; }
        Travel(peer, toId, fromSignId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBuy(bool ga, string stationId)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!Throttled(peer)) BuyCard(peer, ga, stationId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPinAdd(double e, double n, byte icon, string note, bool shared) { if (_server) PinAdd(Multiplayer.GetRemoteSenderId(), e, n, icon, note, shared); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPinEdit(int id, byte icon, string note, bool shared) { if (_server) PinEdit(Multiplayer.GetRemoteSenderId(), id, icon, note, shared); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPinRemove(int id) { if (_server) PinRemove(Multiplayer.GetRemoteSenderId(), id); }

    // ---- RPCs: server -> owner -----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellMode(byte mode)
    {
        if (_server) return;
        Mode = (TravelMode)Math.Min(mode, (byte)TravelMode.Off);
        GD.Print($"[travel] this server's travel: {Mode}");
        ModeChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellUnlocked(string poiId)
    {
        if (Catalogue.Resolve(poiId) is not { } poi) return;
        GD.Print($"[travel] unlocked {poiId}");
        UnlockedPoint?.Invoke(poi);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellRefused(string why)
    {
        LastRefusal = why;
        GD.Print($"[travel] refused: {why}");
        Refused?.Invoke(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Depart(string poiId, int fare, double e, double n)
    {
        if (Catalogue.Resolve(poiId) is not { } poi) return;
        GD.Print(FormattableString.Invariant($"[travel] departing for {poi.Name} (CHF {fare}) -> {e:F0}/{n:F0}"));
        // a place one tracked is reached by travelling there: nothing to track any more
        if (_adventure.TrackedPlace is { } t && Math.Abs(t.E - poi.E) < 1 && Math.Abs(t.N - poi.N) < 1) _adventure.UntrackPlace();
        Departing?.Invoke(poi, fare);
        TravelCurtain.Play(poi, () => Teleporter.Instance?.TeleportTo(e, n, poi.Name));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PartyPinsState(long[] owners, string[] names, int[] ids, double[] es, double[] ns, byte[] icons, string[] notes)
    {
        PartyPins.Clear();
        int count = Math.Min(owners.Length, Math.Min(Math.Min(names.Length, ids.Length), Math.Min(Math.Min(es.Length, ns.Length), Math.Min(icons.Length, notes.Length))));
        for (int i = 0; i < count; i++)
        {
            if (!PartyPins.TryGetValue(owners[i], out var entry)) PartyPins[owners[i]] = entry = (names[i], new List<Pin>());
            entry.Pins.Add(new Pin { Id = ids[i], E = es[i], N = ns[i], Icon = Math.Min(icons[i], (byte)(PinBook.Icons - 1)), Note = PinBook.Sanitize(notes[i]), Shared = true });
        }
        PartyPinsVersion++;
        GD.Print($"[nav] party pins: {count} from {PartyPins.Count} member(s)");
        PartyPinsChanged?.Invoke();
    }

    // ---- chat: /travel, /pin -----------------------------------------------------------------------------

    /// <summary>
    /// <c>/travel</c> (unlocked points with ids), <c>/travel to &lt;id&gt;</c> (a trip, the same rules), admin:
    /// <c>/travel unlock|lock &lt;id|all&gt; [player]</c>; <c>/pin add|list|clear</c>. The server's (offline the local
    /// player's) reply lines; <paramref name="target"/> resolves a player name for the admin forms.
    /// </summary>
    public List<string> Command(long sender, string verb, string rest, bool admin, Func<string, long>? target = null)
    {
        var lines = new List<string>();
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = words.Length > 0 ? words[0].ToLowerInvariant() : "list";
        if (_adventure.Progress(sender) is not { } me) { lines.Add("[travel] No adventure progress here."); return lines; }
        if (verb == "pin")
        {
            var pins = me.Get(PinBook.Section);
            switch (sub)
            {
                case "add":
                    if (PlacesService.Instance?.BodyOf?.Invoke(sender) is not { } b) { lines.Add("[pin] No body."); break; }
                    byte icon = words.Length > 1 && byte.TryParse(words[1], out byte ic) ? (byte)Math.Clamp(ic - 1, 0, PinBook.Icons - 1) : (byte)0;
                    lines.Add(PinAdd(sender, b.E, b.N, icon, string.Join(' ', words.Skip(2)), shared: false) is { } why ? $"[pin] {why}" : "[pin] Pinned here.");
                    break;
                case "clear":
                    foreach (var pin in pins.Pins.ToList()) PinRemove(sender, pin.Id);
                    lines.Add("[pin] Every pin removed.");
                    break;
                default:
                    lines.Add(pins.Pins.Count == 0 ? "[pin] No pins. /pin add [1-6] [note]" : $"[pin] {pins.Pins.Count} pin(s):");
                    foreach (var pin in pins.Pins)
                        lines.Add(FormattableString.Invariant($"  #{pin.Id} {(PinIcon)pin.Icon} {pin.E:F0} {pin.N:F0} {pin.Note}{(pin.Shared ? " (shared)" : "")}"));
                    break;
            }
            return lines;
        }
        var book = me.Get(TravelBook.Section);
        switch (sub)
        {
            case "to" when words.Length > 1:
                lines.Add(Travel(sender, words[1], "") is { } why ? $"[travel] {why}" : $"[travel] Travelling to {Catalogue.Resolve(words[1])?.Name}.");
                break;
            case "unlock" or "lock" when words.Length > 1:
            {
                if (!admin) { lines.Add("[travel] That is an admin's."); break; }
                long who = words.Length > 2 ? target?.Invoke(string.Join(' ', words.Skip(2))) ?? -1 : sender;
                if (_adventure.Progress(who) is not { } p) { lines.Add("[travel] No such player."); break; }
                var tb = p.Get(TravelBook.Section);
                IEnumerable<Poi> points = words[1] == "all"
                    ? Catalogue.All.Where(TravelNetwork.IsPoint)
                    : Catalogue.Resolve(words[1]) is { } one && TravelNetwork.IsPoint(one) ? new[] { one } : Array.Empty<Poi>();
                int n = 0;
                foreach (var poi in points)
                {
                    if (sub == "unlock" && !tb.Has(poi.Id)) { Unlock(who, p, poi, raise: true); n++; }
                    else if (sub == "lock" && tb.Unlocked.Remove(poi.Id)) n++;
                }
                if (sub == "lock" && n > 0) p.Touch(TravelBook.Section);
                lines.Add($"[travel] {n} point(s) {sub}ed.");
                break;
            }
            default:
                lines.Add($"[travel] {Mode} travel; {book.Unlocked.Count} point(s) unlocked{(book.HasGa ? ", GA" : "")}{(book.HasHalfFare ? ", half-fare" : "")}:");
                foreach (string id in book.Unlocked.Take(40))
                    lines.Add($"  {id} {Catalogue.Resolve(id)?.Name}");
                if (book.Unlocked.Count > 40) lines.Add($"  ... and {book.Unlocked.Count - 40} more");
                break;
        }
        return lines;
    }

    private bool Connected(long peer) => _networked && peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    /// <summary>Calendar time (a trip's time is saved with the travel section; the cards' price changes on a date).</summary>
    public static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static DateTime Today => DateTime.UtcNow.Date;

    private static long Now => UnixNow;

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;
}
