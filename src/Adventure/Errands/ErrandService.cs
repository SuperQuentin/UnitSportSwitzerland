using Godot;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Adventure.Together;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Adventure.Errands;

/// <summary>A Postkarte card was done (#1267): <see cref="With"/> the party members it was done with (their peers), empty alone. Together's mentor and group code may read it.</summary>
public sealed record ErrandDone(long Peer, string Key, string QuestId, string Canton, long[] With) : AdventureEvent(Peer, Key);

/// <summary>
/// The errands (#1267, <c>docs/plans/adventure-errands.md</c>), at <c>World/Errands</c> beside <c>World/Adventure</c> on the
/// server and every client (RPCs route by node path), made when <see cref="Systems.Adventure"/> is on. Offline the client plays
/// the server's part (peer <see cref="AdventureService.LocalPeer"/>).
///
/// <para>
/// <b>Authority.</b> The server computes the daily cards from the catalogues (<see cref="PostkarteSource"/>, the same on every
/// peer, never sent), checks the taking (<see cref="Accept"/>: the card, its giver, where the body stands, three a day), keeps
/// the <c>errands</c> section (reputation, the day's cards, the friend bonus), takes cargo back when a card ends and pays the
/// friend bonus. A client only asks to accept, share or join; talking to a giver needs no message.
/// </para>
/// </summary>
public partial class ErrandService : Node
{
    public const string NodeName = "Errands";
    public const float ReachM = 3, SlackM = 5;
    /// <summary>A friend finished the same card at most this long ago: both get the bonus (GameClock seconds).</summary>
    public const double BonusWindow = 600;

    public static ErrandService? Instance { get; private set; }

    /// <summary><c>--errand-day-offset n</c> (checks): the Swiss day moves n days on.</summary>
    public static int DayOffset { get; set; } = int.TryParse(CmdArgs.Value("--errand-day-offset"), out int d) ? d : 0;

    private bool _server, _networked;
    private AdventureService _adventure = null!;

    public bool Authority => _server || !_networked;

    public PostkarteSource Source { get; private set; } = null!;

    /// <summary>A peer's display name (toasts), set by the world.</summary>
    public Func<long, string>? NameOf { get; set; }
    /// <summary>Client: shows a line (the stamp toast, the bonus).</summary>
    public Action<string>? Toast { get; set; }

    /// <summary>The Swiss day now (yyyymmdd).</summary>
    public static int Today => ErrandClock.Day(DateTime.UtcNow.AddDays(DayOffset));

    public static ErrandService Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new ErrandService { Name = NodeName, _server = server, _networked = networked, _adventure = adventure };
        adventure.GetParent().AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Source = new PostkarteSource(() => GiverHost.Current, () => PoiCatalogueHost.Current, () => AdventureTravel.Instance?.Rules ?? TravelRules.Default, () => Today);
        _adventure.Quests.AddSource(Source);
        _adventure.Provides("errands");
        if (Authority)
        {
            _adventure.RegisterReward("rep", GrantRep);
            _adventure.Events.Raised += OnEvent;
            _adventure.Loaded += peer => Sweep(peer);
        }
        GD.Print($"[errands] {(_server ? "server" : _networked ? "client" : "offline")}: {GiverHost.Current.Count} givers");
    }

    // ---- what a client reads (no message: the same catalogues everywhere) -----------------------------------

    /// <summary>Today's cards of a giver.</summary>
    public List<Offer> CardsOf(Giver giver)
    {
        var cards = new List<Offer>(2);
        var (ce, cn) = giver.Cell;
        foreach (var o in Source.Cell(Today, ce, cn))
            if (o.Giver.Id == giver.Id) cards.Add(o);
        return cards;
    }

    /// <summary>Today's cards of a 5 km cell.</summary>
    public IReadOnlyList<Offer> CardsIn(int cellE, int cellN) => Source.Cell(Today, cellE, cellN);

    /// <summary>Where a card stands for a progress: not taken, taken (and still open), running, done.</summary>
    public static ErrandCardState StateOf(PlayerProgress progress, string cardId)
    {
        var q = progress.Get(QuestsProgress.Section);
        if (q.Active.ContainsKey(cardId)) return ErrandCardState.Running;
        if (q.Done.ContainsKey(cardId)) return ErrandCardState.Done;
        return progress.Get(ErrandsProgress.Section).Taken.Contains(cardId) ? ErrandCardState.Taken : ErrandCardState.Open;
    }

    // ---- taking a card, on the authority ------------------------------------------------------------------

    /// <summary>The cards a giver gives a peer now (a bot's sense): today's cards of the giver that are open to it.</summary>
    public List<Offer> Offers(long peer, string giverId)
    {
        var open = new List<Offer>(2);
        if (GiverHost.Current.Resolve(giverId) is not { } giver || _adventure.Progress(peer) is not { } p) return open;
        foreach (var o in CardsOf(giver))
            if (StateOf(p, o.Id) == ErrandCardState.Open) open.Add(o);
        return open;
    }

    /// <summary>A peer takes a card from its giver (the client's request, a bot's call). Null when taken, else why not.</summary>
    public string? Accept(long peer, string giverId, string cardId)
    {
        if (!Authority) return "Not here.";
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Refuse(peer, "No adventuring now.");
        if (GiverHost.Current.Resolve(giverId) is not { } giver) return Refuse(peer, "Nobody here by that name.");
        if (Source.Find(cardId) is not { } offer || offer.Giver.Id != giver.Id || !ErrandClock.Alive(offer.Day, Today))
            return Refuse(peer, "That card is no longer on offer.");
        if (_adventure.PositionOf?.Invoke(peer) is not { } at
            || Math.Sqrt((at.E - giver.E) * (at.E - giver.E) + (at.N - giver.N) * (at.N - giver.N)) > ReachM + SlackM)
            return Refuse(peer, $"Go and see {giver.Name} for it.");
        return Take(peer, p, offer, "Three cards a day: come back tomorrow.");
    }

    private string? Take(long peer, PlayerProgress p, Offer offer, string capText)
    {
        var errands = p.Get(ErrandsProgress.Section);
        if (errands.Roll(Today)) p.Touch(ErrandsProgress.Section);
        if (!errands.CanTake(Today)) return Refuse(peer, capText);
        if (_adventure.StartGiven(peer, offer.Id, offer.Hash) is { } why) return Refuse(peer, why);
        errands.Taken.Add(offer.Id);
        p.Touch(ErrandsProgress.Section);
        if (offer.Cargo is { } cargo && offer.CargoCount > 0) _adventure.Grant(peer, new Item(cargo, offer.CargoCount), "errand:" + offer.Id);
        GD.Print($"[errands] {Short(p.Key)} takes {offer.Id} ({offer.Template}, CHF {offer.Chf}) from {offer.Giver.Id}");
        if (Connected(peer)) RpcId(peer, MethodName.Accepted, offer.Id);
        else if (!_networked) Accepted(offer.Id);
        return null;
    }

    // ---- sharing a card inside a party ----------------------------------------------------------------------

    private readonly Dictionary<(long From, string Card), double> _shares = new();

    /// <summary>A peer offers its running card to its party; the members see it under "Shared by". Null when shared.</summary>
    public string? Share(long peer, string cardId)
    {
        if (!Authority) return "Not here.";
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Refuse(peer, "No adventuring now.");
        if (!Postkarte.IsCard(cardId) || !p.Get(QuestsProgress.Section).Active.ContainsKey(cardId)) return Refuse(peer, "That card is not running.");
        var party = TogetherService.Instance?.Party(peer);
        if (party == null || party.Count < 2) return Refuse(peer, "Join a party to share a card.");
        _shares[(peer, cardId)] = GameClock.Now;
        string title = Source.Find(cardId)?.Quest.Title ?? cardId;
        foreach (long m in party)
        {
            if (m == peer) continue;
            if (Connected(m)) RpcId(m, MethodName.Shared, cardId, peer, NameOf?.Invoke(peer) ?? $"#{peer}", title);
        }
        GD.Print($"[errands] {Short(p.Key)} shares {cardId} with {party.Count - 1}");
        return null;
    }

    /// <summary>A party member joins a card another shared, from anywhere: it counts as one of its three. Null when joined.</summary>
    public string? Join(long peer, string cardId, long fromPeer)
    {
        if (!Authority) return "Not here.";
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Refuse(peer, "No adventuring now.");
        if (TogetherService.Instance?.SameParty(peer, fromPeer) != true || !_shares.ContainsKey((fromPeer, cardId))
            || _adventure.Progress(fromPeer)?.Get(QuestsProgress.Section).Active.ContainsKey(cardId) != true)
            return Refuse(peer, "That card is not shared with you any more.");
        if (Source.Find(cardId) is not { } offer || !ErrandClock.Alive(offer.Day, Today)) return Refuse(peer, "That card has expired.");
        return Take(peer, p, offer, "Three cards a day: that one would be a fourth.");
    }

    // ---- reputation -------------------------------------------------------------------------------------------

    /// <summary>The <c>"rep"</c> reward kind (<c>VS:+5</c>): points in a canton, with the unlocks of the levels it reaches.</summary>
    private string? GrantRep(long peer, PlayerProgress p, string arg)
    {
        if (Reputation.Parse(arg) is not { } r) return "bad argument";
        AddRep(peer, p, r.Canton, r.Points);
        return null;
    }

    public void AddRep(long peer, PlayerProgress p, string canton, int points)
    {
        var errands = p.Get(ErrandsProgress.Section);
        int before = errands.Level(canton);
        errands.Rep[canton] = errands.Points(canton) + points;
        int after = errands.Level(canton);
        p.Touch(ErrandsProgress.Section);
        for (int level = before + 1; level <= after; level++)
        {
            Tell(peer, $"{Reputation.Name(level)} in {Places.AtlasChapters.NameOf(canton)}");
            GD.Print($"[errands] {Short(p.Key)} is {Reputation.Name(level)} in {canton}");
            if (level == 3 && !errands.Flags.Contains(canton)) errands.Flags.Add(canton);
            if (level == 4) _adventure.Grant(peer, new Title(Reputation.TitleOf(canton)), "reputation:" + canton);
        }
    }

    // ---- cards done, ended, expired -----------------------------------------------------------------------------

    private readonly Dictionary<string, List<(long Peer, double At)>> _done = new();

    private void OnEvent(AdventureEvent e)
    {
        switch (e)
        {
            case QuestCompleted done when Postkarte.IsCard(done.QuestId): CardDone(done.Peer, done.QuestId); break;
            case QuestAbandoned gone when Postkarte.IsCard(gone.QuestId): TakeCargoBack(gone.Peer, gone.QuestId, gone.Step, gone.Count); break;
        }
    }

    private void CardDone(long peer, string cardId)
    {
        if (_adventure.Progress(peer) is not { } p || Source.Find(cardId) is not { } offer) return;
        var errands = p.Get(ErrandsProgress.Section);
        errands.Done++;
        p.Touch(ErrandsProgress.Section);
        double now = GameClock.Now;
        if (!_done.TryGetValue(cardId, out var list)) _done[cardId] = list = new();
        list.RemoveAll(x => now - x.At > BonusWindow);
        var with = new List<long>();
        if (TogetherService.Instance?.Party(peer) is { } party)
            foreach (var (other, at) in list)
                if (other != peer && party.Contains(other) && _adventure.Active(other)) with.Add(other);
        list.Add((peer, now));
        if (with.Count > 0)
        {
            Pay(peer, p, offer, with);
            foreach (long other in with)
                if (_adventure.Progress(other) is { } q) Pay(other, q, offer, new List<long> { peer });
        }
        _adventure.Events.Raise(new ErrandDone(peer, p.Key, cardId, offer.Giver.Canton, with.ToArray()));
        // the card's cargo, when it was given up on its last step (none left to take back after a clean finish)
    }

    /// <summary>The friend bonus: +50 % cash and +1 reputation, once per card per player.</summary>
    private void Pay(long peer, PlayerProgress p, Offer offer, List<long> mates)
    {
        var errands = p.Get(ErrandsProgress.Section);
        if (errands.BonusPaid.Contains(offer.Id)) return;
        errands.BonusPaid.Add(offer.Id);
        errands.Together++;
        p.Touch(ErrandsProgress.Section);
        int chf = ErrandRewards.Bonus(offer.Chf);
        _adventure.Grant(peer, new Cash(chf), "errand-bonus:" + offer.Id);
        AddRep(peer, p, offer.Giver.Canton, 1);
        if (errands.Together >= ErrandsProgress.TogetherTitleAt) _adventure.Grant(peer, new Title(ErrandsProgress.TogetherTitle), "errands-together");
        string with = NameOf?.Invoke(mates[0]) ?? $"#{mates[0]}";
        GD.Print($"[errands] bonus {Short(p.Key)} +CHF {chf} with {with} for {offer.Id}");
        Tell(peer, $"Together with {with}: +CHF {chf}");
    }

    /// <summary>A card given up or expired: the cargo it still holds goes back to the giver.</summary>
    private void TakeCargoBack(long peer, string cardId, int step, int count)
    {
        if (Source.Find(cardId) is not { Cargo: { } cargo } offer) return;
        int left = 0;
        for (int i = step; i < offer.Quest.Steps.Length; i++)
            if (offer.Quest.Steps[i] is DeliverStep d && d.Item == cargo) left += d.Count - (i == step ? count : 0);
        if (left <= 0) return;
        GD.Print($"[errands] {cardId} ends: {left} {cargo} go back");
        if (Connected(peer)) RpcId(peer, MethodName.TookBack, (int)cargo, left);
        else if (!_networked) TookBack((int)cargo, left);
    }

    private double _nextSweep;
    private readonly List<long> _peers = new();

    public override void _Process(double delta)
    {
        if (!Authority || GameClock.Now < _nextSweep) return;
        _nextSweep = GameClock.Now + 1;
        _peers.Clear();
        foreach (long peer in _adventure.Peers) _peers.Add(peer);
        foreach (long peer in _peers) Sweep(peer);
    }

    /// <summary>A day change or a login: the cards past the next day are given up, and the progress is pruned of old records.</summary>
    private void Sweep(long peer)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return;
        int today = Today;
        var errands = p.Get(ErrandsProgress.Section);
        var quests = p.Get(QuestsProgress.Section);
        if (errands.Day == today && !quests.Active.Keys.Any(k => Postkarte.IsCard(k) && Postkarte.DayOf(k) is { } d && !ErrandClock.Alive(d, today))) return;
        if (errands.Roll(today)) p.Touch(ErrandsProgress.Section);
        foreach (string id in quests.Active.Keys.Where(Postkarte.IsCard).ToList())
        {
            bool stale = Postkarte.DayOf(id) is not { } day || !ErrandClock.Alive(day, today)
                || Source.Find(id) is not { } offer || quests.Active[id].Seed != offer.Hash;
            if (!stale) continue;
            string from = Source.Find(id)?.Giver.Name ?? "a giver";
            if (_adventure.Abandon(peer, id)) Tell(peer, $"The card from {from} expired.");
        }
        int keepFrom = ErrandClock.AddDays(today, -30);
        errands.Prune(keepFrom);
        foreach (string id in quests.Done.Keys.Where(k => Postkarte.DayOf(k) is { } d && d < keepFrom).ToList()) quests.Done.Remove(id);
        p.Touch(ErrandsProgress.Section);
        p.Touch(QuestsProgress.Section);
    }

    // ---- the client's asks ------------------------------------------------------------------------------------

    /// <summary>Client: asks the giver to hand over a card.</summary>
    public void AskAccept(string giverId, string cardId)
    {
        if (Authority) Accept(AdventureService.LocalPeer, giverId, cardId);
        else RpcId(1, MethodName.RequestAccept, giverId, cardId);
    }

    public void AskShare(string cardId)
    {
        if (Authority) Share(AdventureService.LocalPeer, cardId);
        else RpcId(1, MethodName.RequestShare, cardId);
    }

    public void AskJoin(string cardId, long fromPeer)
    {
        if (Authority) Join(AdventureService.LocalPeer, cardId, fromPeer);
        else RpcId(1, MethodName.RequestJoin, cardId, fromPeer);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAccept(string giverId, string cardId) { if (_server) Accept(Multiplayer.GetRemoteSenderId(), giverId, cardId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestShare(string cardId) { if (_server) Share(Multiplayer.GetRemoteSenderId(), cardId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestJoin(string cardId, long fromPeer) { if (_server) Join(Multiplayer.GetRemoteSenderId(), cardId, fromPeer); }

    // ---- server -> owner ------------------------------------------------------------------------------------

    /// <summary>Client: a card was taken (the stamp).</summary>
    public event Action<string>? CardAccepted;
    /// <summary>Client: a party member shared a card (card id, from peer, their name, the card's title).</summary>
    public event Action<string, long, string, string>? CardShared;
    /// <summary>Client: a shared card that can be joined.</summary>
    public List<(string Card, long From, string Name, string Title)> SharedCards { get; } = new();

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Accepted(string cardId)
    {
        Toast?.Invoke($"Stamped: {Source.Find(cardId)?.Quest.Title ?? "a card"}");
        ErrandSounds.Play(this, ErrandSounds.Stamp);
        CardAccepted?.Invoke(cardId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Shared(string cardId, long fromPeer, string name, string title)
    {
        SharedCards.RemoveAll(s => s.Card == cardId && s.From == fromPeer);
        SharedCards.Add((cardId, fromPeer, name, title));
        Toast?.Invoke($"{name} shared a card: {title}");
        CardShared?.Invoke(cardId, fromPeer, name, title);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TookBack(int item, int count)
    {
        int taken = _adventure.Pack?.TakePlain((ItemId)item, count) ?? 0;
        GD.Print($"[errands] gave {taken} {(ItemId)item} back");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Told(string line) => Toast?.Invoke(line);

    private void Tell(long peer, string line)
    {
        if (Connected(peer)) RpcId(peer, MethodName.Told, line);
        else if (!_networked && peer == AdventureService.LocalPeer) Toast?.Invoke(line);
    }

    private string Refuse(long peer, string why) => _adventure.Refuse(peer, why);

    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;
}
