using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Adventure;

/// <summary>
/// The Adventure layer's service (#1223), at <c>World/Adventure</c> on the server and on every client
/// (RPCs route by node path, as <c>World/Bank</c>). <see cref="Instance"/> is null when
/// <see cref="Systems.Adventure"/> is off: every user calls <c>?.</c>.
///
/// <para>
/// <b>Authority.</b> The server keeps every player's <see cref="PlayerProgress"/> (an
/// <see cref="AdventureBook"/>), keyed by its proven identity (<see cref="Net.Sleepers.Proven"/>, or
/// <c>acct:&lt;user id&gt;</c> on a <c>--accounts required</c> server), never by the requested name.
/// Offline the client plays the server's part (peer <see cref="LocalPeer"/>, key <c>local</c>): one code
/// path, the "server" methods called directly. A peer that never proves gets progress for the session
/// only (<c>peer:&lt;id&gt;</c>), never saved, and is told so (<c>Unsaved</c>).
/// </para>
///
/// <para>
/// <b>Other plans</b> read and write their own section (<see cref="Progress"/>, <see cref="Touch{T}"/>),
/// raise <see cref="Events"/>, grant through <see cref="Grant"/> and register their reward kinds
/// (<see cref="RegisterReward"/>). Touched sections reach their owner through <c>SectionSync</c> (4 a
/// second at most) and are saved at most every 5 s and when the player leaves. Nothing about anyone's
/// progress ever goes to another peer. Docs: <c>docs/notes/adventure/</c>.
/// </para>
/// </summary>
public partial class AdventureService : Node
{
    public const string NodeName = "Adventure";
    /// <summary>The local player offline, the peer id the client playing the server's part answers as.</summary>
    public const long LocalPeer = 1;
    public const string LocalKey = "local";

    private const double SyncEvery = 0.25, SaveEvery = 5, ReachEvery = 0.5, UnsavedAfter = 12;
    /// <summary>Slack on a hand-over's distance: the peer's published position lags its body.</summary>
    private const float DeliverSlackM = 5;

    public static AdventureService? Instance { get; private set; }

    private bool _server, _networked;
    /// <summary>This copy keeps progress: the server, or an offline client.</summary>
    public bool Authority => _server || !_networked;
    /// <summary>A server or a client of one (false offline).</summary>
    public bool Networked => _networked;

    public AdventureEvents Events { get; } = new();
    public QuestEngine Quests { get; } = new(new QuestCatalog());

    // ---- the authority's state --------------------------------------------------------------------

    private readonly AdventureBook _book = new();
    private readonly Dictionary<long, string> _keyOf = new();
    /// <summary>Keys whose progress goes to a file (proven or an account, or offline).</summary>
    private readonly HashSet<string> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<long, double> _lastSync = new();
    private readonly Dictionary<string, double> _saveDue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<long, PlayerProgress, string, string?>> _rewards = new(StringComparer.Ordinal);
    private readonly List<long> _peers = new();
    private double _nextReach;

    /// <summary>Server: the peer is in a Battle Royale round (its lobby or its match); set by <c>ServerWorld</c>.</summary>
    public Func<long, bool>? InMatch { get; set; }
    /// <summary>Where a peer is (LV95): the server's proxy of it, offline the local body.</summary>
    public Func<long, GlobalPos?>? PositionOf { get; set; }
    /// <summary>A peer's display name, kept as <see cref="PlayerProgress.LastName"/> only.</summary>
    public Func<long, string>? NameOf { get; set; }
    /// <summary>Server: <c>acct:&lt;user id&gt;</c> on a <c>--accounts required</c> server, else null (the key is then the proven PlayerKey).</summary>
    public Func<long, string?>? AccountKey { get; set; }
    /// <summary>Friends counted round a peer by the together rule (#1266, <c>TogetherService.Near</c>), set by that service.</summary>
    public Func<long, int>? TogetherNear { get; set; }

    // ---- the owner's side -------------------------------------------------------------------------

    /// <summary>Client: this player's progress as the server last sent it (offline: the progress itself).</summary>
    public PlayerProgress Mine { get; private set; } = new("mine");
    /// <summary>Client: a section of <see cref="Mine"/> changed (its id). UIs refresh on it, never per frame.</summary>
    public event Action<string>? SectionChanged;
    /// <summary>Client: this server keeps no progress for us (no proven identity): it ends with the session.</summary>
    public bool Unsaved { get; private set; }
    public event Action? UnsavedChanged;
    /// <summary>Client: a quest moved (for the toasts): quest id, what, step, count.</summary>
    public event Action<string, QuestChangeKind, int, int>? Noted;
    /// <summary>Client: the server refused a request, and why (for the Journal's status line).</summary>
    public event Action<string>? Refused;
    /// <summary>Client: what the tracked quest points at now (navigation draws its marker from it), null when nothing.</summary>
    public (string Label, double E, double N, float RadiusM)? TrackedTarget { get; private set; }
    public event Action? TrackedChanged;

    /// <summary>Client: puts items in the pack, dropping what does not fit; set by <c>ClientWorld</c>.</summary>
    public Func<ItemStack, int>? GiveItems { get; set; }
    /// <summary>Client: the pack the hand-overs come from.</summary>
    public Inventory? Pack { get; set; }

    public static AdventureService Create(Node world, bool server, bool networked)
    {
        var s = new AdventureService { Name = NodeName, _server = server, _networked = networked };
        world.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Authority) SaveAll();
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        AdventureJson.Warn = w => GD.PushWarning(w);
        Events.IsActive = Active;
        Events.Raised += OnEvent;
        // the checks' own quests, a few tens of metres from the default spawn (never offered to players)
        if (CmdArgs.Has("--adventure-test") || AdventureCheck.Requested) Quests.AddSource(new ProbeQuests(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N));
        if (!Authority)
        {
            StaminaUpgrades.Attach(this);   // #1225: the owner's steps from the section sync
            return;
        }
        if (AdventureCheck.Requested || Places.PlacesCheck.Requested || Navigation.NavCheck.Requested || Navigation.TravelCheck.Requested || Rewards.RewardCheck.Requested
            || Together.TogetherCheck.Requested || Together.FondueCheck.Requested || Occasions.Festivals.FestivalCheck.Requested
            || Occasions.Festivals.FireworksCheck.Requested || Occasions.Festivals.Horn.HornCheck.Requested) AdventureCheck.ClearScratch();
        if (!_server)
        {
            // offline: this client is the server, and the local player is its one peer
            Load(LocalPeer, LocalKey, saved: true);
            Mine = _book.For(LocalKey);
        }
        StaminaUpgrades.Attach(this);   // #1225: the "stamina" reward kind and section
        GD.Print($"[adventure] {(_server ? "server" : "offline")}: {Quests.Sources.Sum(s => s.For(new QuestContext(new QuestsProgress())).Count())} quests");
    }

    // ---- server API (also every other plan's, and bots') ----------------------------------------------

    /// <summary>The peer adventures now: the system is on, the peer known, and not in a Battle Royale round.</summary>
    public bool Active(long peer) => Authority && _keyOf.ContainsKey(peer) && !(InMatch?.Invoke(peer) ?? false);

    /// <summary>A peer's progress, or null when it is not known (not joined yet, or gone).</summary>
    public PlayerProgress? Progress(long peer) => _keyOf.TryGetValue(peer, out var key) && _book.TryGet(key, out var p) ? p : null;

    /// <summary>The peers whose progress is loaded (the authority's).</summary>
    public IReadOnlyCollection<long> Peers => _keyOf.Keys;

    /// <summary>The progress key of a peer (checks, admin tools), or null.</summary>
    public string? KeyOf(long peer) => _keyOf.GetValueOrDefault(peer);

    /// <summary>Marks a peer's section changed: saved and sent to its owner.</summary>
    public void Touch<T>(long peer, SectionKey<T> key) where T : class, new() => Progress(peer)?.Touch(key);

    /// <summary>A plan says its system runs here: quests with steps of that plan are offered (<c>places</c>, <c>rewards</c>, <c>navigation</c>, <c>stamina</c>).</summary>
    public void Provides(string plan) => Quests.Plans.Add(plan);

    /// <summary>A plan's system stopped running here (the places catalogue went empty): its quests are no longer offered.</summary>
    public void Withdraws(string plan) => Quests.Plans.Remove(plan);

    private readonly HashSet<string> _ownSync = new(StringComparer.Ordinal);

    /// <summary>
    /// A section its plan sends to the owner by its own RPCs (fog's deltas): <c>Touch</c> then only saves it,
    /// <c>SectionSync</c> never carries it.
    /// </summary>
    public void SyncsItself(string sectionId) => _ownSync.Add(sectionId);

    /// <summary>
    /// Authority: a peer's progress is loaded and its owner hears from now on (on join with an account, on
    /// proof, or once told its progress is the session's; offline at once). Plans that sync by themselves send
    /// their full state then.
    /// </summary>
    public event Action<long>? Loaded;

    /// <summary>Authority: a progress is about to be saved: plans that keep a working copy write it into their section now.</summary>
    public event Action<PlayerProgress>? Saving;

    /// <summary>
    /// A plan's reward kind (<c>"reveal"</c>, <c>"travel"</c>, <c>"stamina"</c>): <paramref name="apply"/>
    /// takes the peer, its progress and the argument, writes and touches its own section, and returns null,
    /// or why it could not.
    /// </summary>
    public void RegisterReward(string kind, Func<long, PlayerProgress, string, string?> apply) => _rewards[kind] = apply;

    /// <summary>A bot's progress (#837): for the session, never saved; made on first use.</summary>
    public PlayerProgress SessionFor(long peer)
    {
        if (Progress(peer) is { } p) return p;
        string key = $"peer:{peer}";
        _keyOf[peer] = key;
        return _book.For(key);
    }

    /// <summary>Server: a peer is in (after the version and account checks). On an account server its progress loads now, else on proof.</summary>
    public void PeerJoined(long peer)
    {
        if (!_server) return;
        if (AccountKey?.Invoke(peer) is { } account)
        {
            Load(peer, account, saved: true);
            return;
        }
        // until the key is proven: progress for the session, told only if no proof comes
        string session = $"peer:{peer}";
        _keyOf[peer] = session;
        _book.For(session);
        GetTree().CreateTimer(UnsavedAfter).Timeout += () =>
        {
            if (!IsInstanceValid(this) || _keyOf.GetValueOrDefault(peer) != session || !Connected(peer)) return;
            GD.Print($"[adventure] peer {peer} proved no identity: its progress is for this session only");
            RpcId(peer, MethodName.TellUnsaved);
            _toldUnsaved.Add(peer);
            _book.For(session).SyncAll();
            Loaded?.Invoke(peer);
        };
    }

    /// <summary>Server: a peer proved its key (<see cref="Net.Sleepers.Proven"/>): its saved progress loads.</summary>
    public void PeerProven(long peer, string key)
    {
        if (!_server || AccountKey?.Invoke(peer) != null) return;   // an account keys it already
        if (_keyOf.GetValueOrDefault(peer) is { } was && was.StartsWith("peer:", StringComparison.Ordinal)) _book.Forget(was);
        Load(peer, key, saved: true);
    }

    /// <summary>Server: a peer left. Its progress is saved, then unloaded.</summary>
    public void PeerLeft(long peer)
    {
        _toldUnsaved.Remove(peer);
        if (!_keyOf.Remove(peer, out var key)) return;
        _lastSync.Remove(peer);
        if (!_book.TryGet(key, out var p)) return;
        if (_saved.Contains(key)) Save(p);
        _saveDue.Remove(key);
        // the same identity may still be on under another peer id (a fast reconnect): keep it loaded then
        if (!_keyOf.ContainsValue(key)) _book.Forget(key);
    }

    private void Load(long peer, string key, bool saved)
    {
        long now = Now;
        PlayerProgress p;
        if (_book.TryGet(key, out var loaded)) p = loaded;
        else
        {
            p = ReadFile(key) ?? new PlayerProgress(key) { Created = now };
            _book.Add(p);
        }
        p.Seen = now;
        if (NameOf?.Invoke(peer) is { Length: > 0 } name) p.LastName = name;
        p.Get(QuestsProgress.Section);   // every player has the quests section: the owner's first sync carries it
        _keyOf[peer] = key;
        if (saved) _saved.Add(key);
        p.SyncAll();
        _lastSync.Remove(peer);
        // offline the local player loads in _Ready, before any plan's node is there: the plans read it when they start
        if (IsInsideTree()) Loaded?.Invoke(peer);
        GD.Print($"[adventure] peer {peer} is {Short(key)}: {p.Get(QuestsProgress.Section).Active.Count} running, {p.Get(QuestsProgress.Section).Done.Count} done");
    }

    private PlayerProgress? ReadFile(string key)
    {
        string path = ProjectSettings.GlobalizePath(PathOf(key));
        try
        {
            if (!File.Exists(path)) return null;
            var p = AdventureBook.FromJson(File.ReadAllText(path));
            // a file renamed by hand under another identity never gives that identity its progress
            return p.Key == key ? p : null;
        }
        catch (Exception e)
        {
            // kept beside, as it was: the next save writes fresh progress over the original name
            GD.PushWarning($"[adventure] {path}: {e.Message} (kept as {System.IO.Path.GetFileName(path)}.unreadable)");
            try { File.Copy(path, path + ".unreadable", overwrite: true); }
            catch (Exception copy) { GD.PushWarning($"[adventure] {path}: {copy.Message}"); }
            return null;
        }
    }

    private string PathOf(string key)
    {
        // a check's own folder (--adventure-dir, and always under --adventurecheck): never the player's save
        if (ScratchDir is { } dir) return System.IO.Path.Combine(dir, key == LocalKey ? "local.json" : "players/" + AdventureBook.FileName(key));
        return key == LocalKey
            ? WorldChoice.SavePath("user://adventure/local.json")
            : WorldChoice.SavePath("user://adventure/players/" + AdventureBook.FileName(key));
    }

    /// <summary>Where a check keeps its progress instead of <c>user://adventure</c>, or null.</summary>
    public static string? ScratchDir => CmdArgs.Value("--adventure-dir") is { } dir ? ProjectSettings.GlobalizePath(dir)
        : AdventureCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/adventurecheck")
        : Places.PlacesCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/placescheck")
        : Rewards.RewardCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/rewardcheck")
        : Navigation.NavCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/navcheck")
        : Navigation.TravelCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/travelcheck")
        : Together.TogetherCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/togethercheck")
        : Together.FondueCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/fonduecheck")
        : Occasions.Festivals.FestivalCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/festivalcheck")
        : Occasions.Festivals.FireworksCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/fireworkscheck")
        : Occasions.Festivals.Horn.HornCheck.Requested ? ProjectSettings.GlobalizePath("res://test_output/hornscheck") : null;

    /// <summary>The save file of a key (checks read it back).</summary>
    public string FileOf(string key) => ProjectSettings.GlobalizePath(PathOf(key));

    private void Save(PlayerProgress p)
    {
        Saving?.Invoke(p);
        foreach (string id in p.Untouched())
            GD.PushWarning($"[adventure] {Short(p.Key)}: section '{id}' changed without Touch (a bug: saved now, but its owner was never told)");
        try
        {
            JsonStore.SaveAsync(PathOf(p.Key), AdventureBook.ToNode(p), AdventureJson.Options,
                e => GD.PushWarning($"[adventure] saving {Short(p.Key)}: {e.Message}"));
            p.Saved();
        }
        catch (Exception e) { GD.PushWarning($"[adventure] saving {Short(p.Key)}: {e.Message}"); }
    }

    /// <summary>Saves every loaded progress that goes to a file now (quitting, checks).</summary>
    public void SaveAll()
    {
        foreach (var p in _book.Loaded)
            if (_saved.Contains(p.Key)) Save(p);
        _saveDue.Clear();
    }

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    // ---- quests, server side -----------------------------------------------------------------------------

    /// <summary>Starts a quest for a peer (the client's request, a bot's call). Null when started, else why not.</summary>
    public string? Start(long peer, string questId)
    {
        if (!Active(peer) || Progress(peer) is not { } p) return Refuse(peer, "No adventuring now.");
        var q = p.Get(QuestsProgress.Section);
        if (Quests.Start(q, questId, Now) is { } why) return Refuse(peer, why);
        p.Touch(QuestsProgress.Section);
        Note(peer, new QuestChange(questId, QuestChangeKind.Started, 0, 0));
        return null;
    }

    public bool Abandon(long peer, string questId)
    {
        if (!Active(peer) || Progress(peer) is not { } p) { Refuse(peer, "No adventuring now."); return false; }
        if (!Quests.Abandon(p.Get(QuestsProgress.Section), questId)) return false;
        p.Touch(QuestsProgress.Section);
        Note(peer, new QuestChange(questId, QuestChangeKind.Abandoned, 0, 0));
        return true;
    }

    /// <summary>Tracks a running quest (empty or null: none).</summary>
    public bool Track(long peer, string? questId)
    {
        if (Progress(peer) is not { } p) return false;
        if (!Quests.Track(p.Get(QuestsProgress.Section), string.IsNullOrEmpty(questId) ? null : questId)) return false;
        p.Touch(QuestsProgress.Section);
        return true;
    }

    /// <summary>
    /// A hand-over (#1223 §4.3): the inventory is the client's, so the server checks where, not what: the
    /// quest runs, <paramref name="step"/> is its current one, a delivery, and the peer stands at its target.
    /// True when accepted; the owner then takes the items, and <see cref="ItemDelivered"/> is raised.
    /// </summary>
    public bool Deliver(long peer, string questId, int step)
    {
        if (!Active(peer) || Progress(peer) is not { } p) { Refuse(peer, "No adventuring now."); return false; }
        var q = p.Get(QuestsProgress.Section);
        if (!q.Active.TryGetValue(questId, out var state) || state.Step != step
            || Quests.Resolve(questId) is not { } def || step >= def.Steps.Length || def.Steps[step] is not DeliverStep deliver)
        {
            Refuse(peer, "Nothing to hand over for that quest now.");
            return false;
        }
        if (Quests.Locate(deliver.At) is not { } at || PositionOf?.Invoke(peer) is not { } pos
            || Math.Sqrt((pos.E - at.E) * (pos.E - at.E) + (pos.N - at.N) * (pos.N - at.N)) > at.RadiusM + DeliverSlackM)
        {
            Refuse(peer, $"Hand it over at {(deliver.At is AreaTarget a ? a.Name : deliver.At.Id)}.");
            return false;
        }
        int count = deliver.Count - state.Count;
        if (Connected(peer)) RpcId(peer, MethodName.DeliverOk, questId, step, (int)deliver.Item, count);
        else if (!_networked) DeliverOk(questId, step, (int)deliver.Item, count);
        GD.Print($"[adventure] {Short(p.Key)} hands {count} {deliver.Item} over for {questId}");
        Events.Raise(new ItemDelivered(peer, p.Key, deliver.Item, count, deliver.At.Id));
        return true;
    }

    /// <summary>
    /// Gives a reward to a peer, now (a quest's, a chest's...): cash to its account, items to its pack,
    /// a title, or another plan's unlock. <paramref name="why"/> goes to the logs and the item events.
    /// </summary>
    public void Grant(long peer, Reward reward, string why)
    {
        if (Progress(peer) is not { } p) return;
        switch (reward)
        {
            case Cash cash:
                // a catalog quest's cash with friends near (#1266): +10 % a friend; Postkarte cards pay their own
                int bonus = Together.TogetherRules.PaysTogetherCash(why) && TogetherNear?.Invoke(peer) is int n and > 0
                    ? Together.TogetherRules.CashBonus(cash.Chf, n) : 0;
                if (bonus > 0) GD.Print($"[together] {Short(p.Key)} quest cash +{bonus} ({why})");
                Bank.Instance?.Credit(peer, cash.Chf + bonus, why);
                break;
            case Item item:
                if (Connected(peer)) RpcId(peer, MethodName.Granted, (int)item.Id, item.Count, why);
                else if (!_networked) Granted((int)item.Id, item.Count, why);
                Events.Raise(new ItemObtained(peer, p.Key, item.Id, item.Count, why));
                break;
            case Title title:
                var q = p.Get(QuestsProgress.Section);
                if (!q.Titles.Contains(title.Name)) q.Titles.Add(title.Name);
                q.Title ??= title.Name;
                p.Touch(QuestsProgress.Section);
                break;
            case Unlock unlock:
                if (!_rewards.TryGetValue(unlock.Kind, out var apply))
                    GD.Print($"[adventure] {Short(p.Key)}: no system grants '{unlock.Kind}' here yet ({why})");
                else if (apply(peer, p, unlock.Arg) is { } failed)
                    GD.PushWarning($"[adventure] {Short(p.Key)}: {unlock.Kind} {unlock.Arg} not granted: {failed}");
                break;
        }
    }

    private void OnEvent(AdventureEvent e)
    {
        if (Progress(e.Peer) is not { } p || p.Key != e.Key) return;
        Apply(e.Peer, p, Quests.OnEvent(p.Get(QuestsProgress.Section), e, Now));
    }

    private void Apply(long peer, PlayerProgress p, List<QuestChange> changes)
    {
        if (changes.Count == 0) return;
        p.Touch(QuestsProgress.Section);
        foreach (var c in changes)
        {
            Note(peer, c);
            if (c.What != QuestChangeKind.Completed || Quests.Resolve(c.QuestId) is not { } def) continue;
            GD.Print($"[adventure] {Short(p.Key)} completes {c.QuestId}");
            foreach (var r in def.Rewards) Grant(peer, r, "quest:" + c.QuestId);
            Events.Raise(new QuestCompleted(peer, p.Key, c.QuestId));
        }
    }

    private void Note(long peer, QuestChange c)
    {
        if (Connected(peer)) RpcId(peer, MethodName.QuestNote, c.QuestId, (int)c.What, c.Step, c.Count);
        else if (!_networked) QuestNote(c.QuestId, (int)c.What, c.Step, c.Count);
    }

    private string Refuse(long peer, string why)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellRefused, why);
        else if (!_networked) TellRefused(why);
        return why;
    }

    public override void _Process(double delta)
    {
        if (!Authority) return;
        double now = GameClock.Now;
        // the reach steps, at 2 Hz, only for the players whose quests wait on a position
        if (now >= _nextReach)
        {
            _nextReach = now + ReachEvery;
            _peers.Clear();
            foreach (long peer in _keyOf.Keys) _peers.Add(peer);
            foreach (long peer in _peers)
            {
                if (!Active(peer) || Progress(peer) is not { } p) continue;
                var q = p.Get(QuestsProgress.Section);
                if (!Quests.WaitsOnPosition(q) || PositionOf?.Invoke(peer) is not { } at) continue;
                Apply(peer, p, Quests.OnPosition(q, at.E, at.N, Now));
            }
        }
        foreach (var (peer, key) in _keyOf)
        {
            if (!_book.TryGet(key, out var p)) continue;
            // to the owner, 4 times a second at most
            if (p.NeedsSync && now - _lastSync.GetValueOrDefault(peer, double.NegativeInfinity) >= SyncEvery
                && (!_server || !key.StartsWith("peer:", StringComparison.Ordinal) || Connected(peer)))
            {
                _lastSync[peer] = now;
                foreach (string id in p.TakeSync()) SendSection(peer, p, id);
            }
            // to the disk, every 5 s at most
            if (_saved.Contains(key) && p.NeedsSave)
            {
                if (!_saveDue.TryGetValue(key, out double due)) _saveDue[key] = now + SaveEvery;
                else if (now >= due)
                {
                    _saveDue.Remove(key);
                    Save(p);
                }
            }
        }
    }

    private void SendSection(long peer, PlayerProgress p, string id)
    {
        if (!_networked)
        {
            // offline Mine is this very progress: only tell the screens
            OnSectionChanged(id);
            return;
        }
        if (_ownSync.Contains(id) || !Connected(peer)) return;
        // an unproven peer hears nothing until it is told its progress is the session's
        if (p.Key.StartsWith("peer:", StringComparison.Ordinal) && !_toldUnsaved.Contains(peer)) return;
        RpcId(peer, MethodName.SectionSync, id, p.VersionOf(id), p.DataOf(id)?.ToJsonString(AdventureJson.Options) ?? "null");
    }

    private readonly HashSet<long> _toldUnsaved = new();

    // ---- RPCs: client -> server ----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStart(string questId) { if (_server) Start(Multiplayer.GetRemoteSenderId(), questId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAbandon(string questId) { if (_server) Abandon(Multiplayer.GetRemoteSenderId(), questId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTrack(string questId) { if (_server) Track(Multiplayer.GetRemoteSenderId(), questId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDeliver(string questId, int step) { if (_server) Deliver(Multiplayer.GetRemoteSenderId(), questId, step); }

    // ---- the owner's requests -------------------------------------------------------------------------

    public void AskStart(string questId)
    {
        if (Authority) Start(LocalPeer, questId);
        else RpcId(1, MethodName.RequestStart, questId);
    }

    public void AskAbandon(string questId)
    {
        if (Authority) Abandon(LocalPeer, questId);
        else RpcId(1, MethodName.RequestAbandon, questId);
    }

    public void AskTrack(string? questId)
    {
        _place = null;
        if (Authority) Track(LocalPeer, questId);
        else RpcId(1, MethodName.RequestTrack, questId ?? "");
    }

    /// <summary>Hands over the current delivery step's items: the pack must hold them; they leave it only when the server agrees.</summary>
    public void AskDeliver(string questId, int step)
    {
        if (Quests.Resolve(questId) is not { } def || step >= def.Steps.Length || def.Steps[step] is not DeliverStep d) return;
        if ((Pack?.CountPlain(d.Item) ?? 0) < 1)
        {
            TellRefused($"You have no {d.Item} to hand over.");
            return;
        }
        if (Authority) Deliver(LocalPeer, questId, step);
        else RpcId(1, MethodName.RequestDeliver, questId, step);
    }

    // ---- RPCs: server -> owner -----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SectionSync(string id, int version, string json)
    {
        try { Mine.SetRaw(id, version, JsonNode.Parse(json)); }
        catch (Exception e) { GD.PushWarning($"[adventure] section '{id}' from the server: {e.Message}"); return; }
        OnSectionChanged(id);
    }

    private void OnSectionChanged(string id)
    {
        if (id == QuestsProgress.Section.Id) UpdateTracked();
        SectionChanged?.Invoke(id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void QuestNote(string questId, int what, int step, int count) => Noted?.Invoke(questId, (QuestChangeKind)what, step, count);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Granted(int item, int count, string why)
    {
        int lost = GiveItems?.Invoke(new ItemStack((ItemId)item, count)) ?? count;
        GD.Print($"[adventure] got {count} {(ItemId)item} ({why}){(lost > 0 ? $", {lost} could go nowhere" : "")}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DeliverOk(string questId, int step, int item, int count)
    {
        int taken = Pack?.TakePlain((ItemId)item, count) ?? 0;
        GD.Print($"[adventure] handed {taken} {(ItemId)item} over ({questId}, step {step + 1})");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellRefused(string why)
    {
        GD.Print($"[adventure] refused: {why}");
        Refused?.Invoke(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellUnsaved()
    {
        if (_server) return;
        Unsaved = true;
        UnsavedChanged?.Invoke();
    }

    // ---- the tracked target (navigation's contract, §5.3) -------------------------------------------------

    private (string Label, double E, double N, float RadiusM)? _place;

    /// <summary>
    /// Client: tracks a place that is no quest's (the adventure map's and the Atlas's Track button, #1224):
    /// it is the <see cref="TrackedTarget"/> until another place or a quest is tracked, or
    /// <see cref="UntrackPlace"/>. Kept on this client only, for the session (navigation may persist it later).
    /// </summary>
    public void TrackPlace(string label, double e, double n, float radiusM)
    {
        _place = (label, e, n, radiusM);
        UpdateTracked();
    }

    /// <summary>Client: drops a tracked place (reached, or the button again); the tracked quest's target shows again.</summary>
    public void UntrackPlace()
    {
        if (_place == null) return;
        _place = null;
        UpdateTracked();
    }

    /// <summary>Client: the place tracked with <see cref="TrackPlace"/>, or null.</summary>
    public (string Label, double E, double N, float RadiusM)? TrackedPlace => _place;

    private void UpdateTracked()
    {
        var q = Mine.Get(QuestsProgress.Section);
        (string, double, double, float)? target = _place;
        if (target == null && q.Tracked is { } id && q.Active.TryGetValue(id, out var state) && Quests.Current(id, state) is { Target: { } t } step
            && Quests.Locate(t) is { } at)
            target = (step.Line, at.E, at.N, at.RadiusM);
        if (target == TrackedTarget) return;
        TrackedTarget = target;
        TrackedChanged?.Invoke();
    }
}
