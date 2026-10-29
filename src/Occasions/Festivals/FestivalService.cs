using System.Text.Json;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.August1;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions.Festivals;

/// <summary>A festival that runs now, as the server sees it and tells every client.</summary>
/// <param name="ShowStart">Server time (<c>ClockSync.SimNow</c>) the closing show's first second falls at, or 0 when no show is on the books.</param>
/// <param name="ShowSeed">The seed the show is planned from (<see cref="FireworkShow.Plan"/>): every peer plans the same one.</param>
public sealed record RunningFestival(string Id, string Instance, string Title, int Day, int Length, DateOnly Start, DateOnly End, ResolvedSite? Site,
    double ShowStart = 0, int ShowSeed = 0);

/// <summary>One member's points on the board, for the Journal.</summary>
public sealed record BoardMember(string Name, int Points);

/// <summary>A player's own crew's standing on a festival's board (the server sends each member exactly this).</summary>
public sealed record BoardView(string Festival, string Instance, int Points, int[] Taken, BoardMember[] Members);

/// <summary>
/// The festival framework's service (#1269 part 1, <c>docs/plans/adventure-festivals.md</c> section 1), at
/// <c>World/Adventure/Festivals</c> on the server and every client (RPCs route by node path). The server decides which festival
/// runs and on which day (from the occasion manager's running set and the calendar), where it is held
/// (<see cref="FestivalSites"/>), every Batzen and point (<see cref="Award"/>, the only way they are earned), the party board
/// and the conversion of Batzen to francs; clients get the running list, their crew's standing and their own section.
/// Offline the client plays the server's part (peer 1). Everything is per adventuring player (<see cref="AdventureService.Active"/>):
/// nothing is earned in a Battle Royale round. Bots earn nothing (a negative peer id): they make up the field.
/// Docs: <c>docs/notes/occasions/festival-framework.md</c>.
/// </summary>
public partial class FestivalService : Node
{
    public const string NodeName = "Festivals";

    /// <summary>How often the authority re-reads the occasion manager and the calendar (day rollover, a moved date).</summary>
    private const double EvaluateEvery = 5, BoardSaveEvery = 30, CrewCheckEvery = 2;

    public static FestivalService? Instance { get; private set; }

    private AdventureService _adventure = null!;
    private bool _server, _networked;
    private bool Authority => _adventure.Authority;

    // ---- wiring (set by the world that creates it) ----------------------------------------------

    /// <summary>Authority: whether this server's terrain holds an LV95 point (its tile is in the region).</summary>
    public Func<double, double, bool>? InRegion { get; set; }

    /// <summary>Authority: the peers in the same party as a peer, itself included; null when none.</summary>
    public Func<long, IReadOnlyList<long>?>? PartyOf { get; set; }

    /// <summary>The game hour the closing show waits for (the sky clock; a check sets its own).</summary>
    public Func<double> Hour { get; set; } = () => UnitSport.World.WorldClock.CurrentHour;

    /// <summary>The fixture course's start, where the check-only festival is held.</summary>
    public Func<(double E, double N)> Start { get; set; } = () => (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    // ---- what both sides show ---------------------------------------------------------------------

    /// <summary>The festivals running now (the server's own, or what it last told this client).</summary>
    public IReadOnlyList<RunningFestival> Running { get; private set; } = Array.Empty<RunningFestival>();

    /// <summary>Raised on the main thread when <see cref="Running"/> changes (a day opens, a festival starts or ends, its site moves).</summary>
    public event Action? Changed;

    /// <summary>Raised when a board view arrives or changes: the festival id.</summary>
    public event Action<string>? BoardChanged;

    /// <summary>Client: Batzen were converted to francs: the festival's title, the Batzen, the francs.</summary>
    public event Action<string, int, int>? Converted;

    /// <summary>Client: the server refused something: why.</summary>
    public event Action<string>? Refused;

    /// <summary>The standing of this player's crew on a festival's board, or null before the server sent it.</summary>
    public BoardView? BoardOf(string festival) => _views.GetValueOrDefault(festival);

    private readonly Dictionary<string, BoardView> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The running festival with this id, or null.</summary>
    public RunningFestival? RunningOf(string id) => Running.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>This player's festivals section (the owner's mirror; offline its own progress).</summary>
    public FestivalBook Mine => _adventure.Mine.Get(FestivalBook.Section);

    // ---- the authority's state --------------------------------------------------------------------

    private readonly Dictionary<string, FestivalBoard> _boards = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _boardsDirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long Peer, string Festival), string> _sentCrew = new();
    private readonly List<long> _pending = new();
    private readonly Dictionary<string, ResolvedSite?> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string _runningSignature = "-", _siteKey = "";
    private double? _lastHour;
    private readonly Dictionary<string, (double Start, int Seed, int Count)> _shows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long Peer, string Instance), double> _watched = new();
    private readonly HashSet<(long Peer, string Instance)> _watchPaid = new();
    private double _nextEvaluate, _nextBoardSave, _nextCrewCheck;
    private bool _evaluated, _sitesStale;

    /// <summary>Authority: every Batzen granted since start (checks).</summary>
    public int GrantedBatzen { get; private set; }

    public static FestivalService Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new FestivalService { Name = NodeName, _adventure = adventure, _server = server, _networked = networked };
        adventure.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        OccasionManager.Changed -= OnOccasionsChanged;
        OccasionTowns.Changed -= OnTownsChanged;
        if (Authority) SaveBoards();
        if (Instance == this) Instance = null;
        foreach (var r in Running) FestivalOccasion.SetSite(r.Id, null);
    }

    public override void _Ready()
    {
        // the check-only festival: held at the fixture course's start, on the client and the server alike
        if (FestivalCatalog.TestRequested && FestivalCatalog.Get(FestivalCatalog.TestId) == null)
        {
            var (e, n) = Start();
            FestivalCatalog.Register(FestivalCatalog.Test(e, n));
            // the Älplerfest's games are checked at the same field (on a date inside its real dates, --date 2027-08-16)
            if (FestivalCatalog.Get(FestivalCatalog.AelplerfestId) is { } alp)
                FestivalCatalog.Register(alp with { Sites = [new FestivalSite("the test field", e, n, 400)] });
            if (FestivalCatalog.Get(FestivalCatalog.DesalpeId) is { } desalpe)
                FestivalCatalog.Register(desalpe with { Sites = [new FestivalSite("the test field", e, n, 400)] });
            if (FestivalCatalog.Get(FestivalCatalog.ChristmasMarketId) is { } market)
                FestivalCatalog.Register(market with { Sites = [new FestivalSite("the test field", e, n, 90)] });
            if (FestivalCatalog.Get(FestivalCatalog.EscaladeId) is { } escalade)
                FestivalCatalog.Register(escalade with { Sites = [new FestivalSite("the test field", e, n, 100)] });
            if (FestivalCatalog.Get(FestivalCatalog.August1Id) is { } august1)
                FestivalCatalog.Register(august1 with { Sites = [new FestivalSite("the test field", e, n, 120)] });
        }
        // a check's sky: --august1hour 22.5 holds the server's game hour (a dedicated server without a sky reads noon)
        if (CmdArgs.Value("--august1hour") is { } fixedHour && double.TryParse(fixedHour, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h1)) Hour = () => h1;
        if (!Authority) return;
        _adventure.Loaded += OnLoaded;
        OccasionManager.Changed += OnOccasionsChanged;
        OccasionTowns.Changed += OnTownsChanged;
        // a dedicated server reads the places itself: nothing else there loads the towns a festival may move to
        if (_server) OccasionTowns.Reload();
        // offline the local player's progress loaded before this node existed
        if (!_server && _adventure.Progress(AdventureService.LocalPeer) != null) OnLoaded(AdventureService.LocalPeer);
        GD.Print($"[festival] {(_server ? "server" : "offline")}: {FestivalCatalog.All.Count} festival(s) known{(FestivalCatalog.TestRequested ? " (the check-only one included)" : "")}");
    }

    private void OnOccasionsChanged() => _nextEvaluate = 0;

    private void OnTownsChanged() => _sitesStale = true;

    private bool Connected(long peer) => _networked && peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;

    // ---- the loop (authority) ---------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!Authority) return;
        if (_server) OccasionTowns.Poll();
        double now = GameClock.Now;
        if (now >= _nextEvaluate)
        {
            _nextEvaluate = now + EvaluateEvery;
            Evaluate();
        }
        if (!_evaluated) return;
        if (_pending.Count > 0)
        {
            var peers = _pending.ToArray();
            _pending.Clear();
            foreach (long peer in peers) Welcome(peer);
        }
        EscaladeTick(now);
        August1Tick(now);
        if (now >= _nextCrewCheck)
        {
            _nextCrewCheck = now + CrewCheckEvery;
            CheckCrews();
            CheckShow();
            HornTick(now);
        }
        if (now >= _nextBoardSave && _boardsDirty.Count > 0)
        {
            _nextBoardSave = now + BoardSaveEvery;
            SaveBoards();
        }
    }

    /// <summary>Reads the occasions and the calendar: which festivals run, on which day, held where. Tells everyone when that changed.</summary>
    private void Evaluate()
    {
        _evaluated = true;
        var om = OccasionManager.Instance;
        var list = new List<RunningFestival>();
        double hour = Hour(), serverNow = Net.ClockSync.SimNow;
        if (om != null)
            foreach (var def in FestivalCatalog.All)
            {
                var active = om.Active.FirstOrDefault(a => string.Equals(a.Id, def.Id, StringComparison.OrdinalIgnoreCase) && a.Has(OccasionFacets.Games));
                if (active == null) continue;
                var windows = WindowsOf(def, active.Entry);
                int year = FestivalCalendar.YearOf(active.Instance);
                var span = SpanFor(windows, year) ?? new FestivalSpan(om.Today, om.Today);
                // started by hand outside its dates: the nearest day
                int day = Math.Clamp(span.Day(om.Today), 1, Math.Max(1, span.Length));
                var site = SiteOf(def);
                var (showStart, showSeed) = ShowOf(def, active.Instance, day, span.Length, site, hour, serverNow);
                list.Add(new RunningFestival(def.Id, active.Instance, def.Title, day, span.Length, span.Start, span.End, site, showStart, showSeed));
            }

        _lastHour = hour;
        string signature = list.Count == 0 ? "-" : string.Join(";", list.Select(r => $"{r.Instance}:{r.Day}:{r.Site}:{r.ShowStart}"));
        if (signature == _runningSignature) return;
        var before = Running;
        _runningSignature = signature;
        Running = list;
        foreach (var r in before)
            if (list.All(x => x.Id != r.Id)) FestivalOccasion.SetSite(r.Id, null);
        bool dress = false;
        foreach (var r in list) dress |= FestivalOccasion.SetSite(r.Id, r.Site);
        foreach (var r in before)
            if (list.All(x => x.Id != r.Id)) dress = true;
        GD.Print($"[festival] running: {(list.Count == 0 ? "none" : string.Join(", ", list.Select(r => $"{r.Id} day {r.Day}/{r.Length} at {r.Site?.Name ?? "nowhere"}{(r.Site?.Relocated == true ? " (relocated)" : "")}{(r.ShowStart > 0 ? $", closing show from {r.ShowStart:F0}" : "")}")))}");
        Changed?.Invoke();
        if (dress) OccasionManager.NotifyContentChanged();
        if (_networked)
            foreach (long peer in _adventure.Peers)
                if (peer > 0) SendFestivals(peer);

        // a day opened: every adventuring peer hears of it on the bus; an ended festival pays out
        foreach (var r in list)
        {
            var was = before.FirstOrDefault(x => x.Instance == r.Instance);
            if (was != null && was.Day == r.Day) continue;
            foreach (long peer in _adventure.Peers.ToArray())
                if (peer > 0 && _adventure.Active(peer) && _adventure.Progress(peer) is { } p)
                    _adventure.Events.Raise(new FestivalOpened(peer, p.Key, r.Id, r.Day));
        }
        foreach (long peer in _adventure.Peers.ToArray())
            if (peer > 0) ConvertFor(peer);
    }

    /// <summary>
    /// The closing show of a festival's last real day (#1269 part 2): on every game evening of that day, when the sky clock crosses
    /// <see cref="FestivalShows.StartHour"/>, the server fixes a start a few seconds ahead and a seed, and tells everyone with the running
    /// list. Returns the show on the books (start 0 when none is). A site that is not held in the region has no show.
    /// </summary>
    private (double Start, int Seed) ShowOf(FestivalDef def, string instance, int day, int length, ResolvedSite? site, double hour, double now)
    {
        bool lakes = def.Id == August1Rules.FestivalId;   // the 1 August (#1355): a show over the lake on every game evening of both days, at 22:00
        if (!(FestivalShows.HasFireworks(def.Show) || lakes) || site == null) return (0, 0);
        var (start, seed, count) = _shows.GetValueOrDefault(instance);
        if ((lakes ? August1Rules.ShowStarts(_lastHour, hour) : day >= length && FestivalShows.Starts(_lastHour, hour)) && !FestivalShows.Active(start, seed, now))
        {
            count++;
            start = now + FestivalShows.StartLead;
            seed = FestivalShows.SeedOf(instance, count);
            _shows[instance] = (start, seed, count);
            GD.Print($"[festival] {def.Id}: the closing show #{count} starts in {FestivalShows.StartLead:F0} s (game hour {hour:F2}), {FireworkShow.DurationOf(seed) / 60:F0} minutes");
        }
        return FestivalShows.Active(start, seed, now) ? (start, seed) : (0, 0);
    }

    private static (string From, string To)[] WindowsOf(FestivalDef def, OccasionEntry entry) =>
        entry.Schedule.Count > 0 ? entry.Schedule.Select(w => (w.From, w.To)).ToArray() : def.Schedule;

    private static FestivalSpan? SpanFor((string From, string To)[] windows, int year)
    {
        foreach (var (from, to) in windows)
            if (FestivalCalendar.SpanOf(from, to, year) is { } span) return span;
        return null;
    }

    /// <summary>Where a festival is held now: the real site if the region holds it, else the largest town (cached until the region or the towns change).</summary>
    private ResolvedSite? SiteOf(FestivalDef def)
    {
        string key = $"{InRegionKey()}/{OccasionTowns.All.Count}";
        if (key != _siteKey || _sitesStale)
        {
            _siteKey = key;
            _sitesStale = false;
            _sites.Clear();
        }
        if (_sites.TryGetValue(def.Id, out var site)) return site;
        var towns = OccasionTowns.All.Select(t => new TownRef(t.Name, t.E, t.N, t.Buildings));
        return _sites[def.Id] = FestivalSites.Resolve(def, InRegion ?? ((_, _) => false), towns);
    }

    /// <summary>Changes when the region does (more tiles arrive): the sites are worked out again then.</summary>
    private string InRegionKey() => InRegion == null ? "none" : "set";

    // ---- the authority's API ----------------------------------------------------------------------

    /// <summary>
    /// A play of a festival game ended: <paramref name="batzen"/> and <paramref name="points"/> (the score of the play, which also goes
    /// to the party's board) for <paramref name="peer"/>, in <paramref name="game"/> (an open game of the festival, or "" for
    /// something that is no game: an errand). The tokens are cut to the game's daily cap and the festival's cap; points are not.
    /// Authority only. Returns the Batzen actually granted; 0 when refused (no such festival now, the game not open yet, a
    /// bot, a peer in a Battle Royale round).
    /// </summary>
    public int Award(long peer, string festival, int batzen, string why, string game = "", int points = 0)
    {
        if (!Authority || peer <= 0) return 0;
        if (RunningOf(festival) is not { } run || FestivalCatalog.Get(run.Id) is not { } def) return Refuse(peer, "That festival is not on now.");
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Refuse(peer, "No adventuring now.");
        if (game.Length > 0 && !def.IsOpen(game, run.Day)) return Refuse(peer, "That game is not open yet.");

        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        int granted = FestivalRules.Earn(def, inst, game, run.Day, batzen, points);
        GrantedBatzen += granted;
        _adventure.Touch(peer, FestivalBook.Section);
        GD.Print($"[festival] {Short(p.Key)}: +{granted} Batzen (asked {batzen}), +{points} points in {def.Id}{(game.Length > 0 ? "/" + game : "")}: {why}; {inst.Batzen} held");
        if (points > 0) AddToBoard(peer, p, run, def, points);
        _adventure.Events.Raise(new FestivalGamePlayed(peer, p.Key, def.Id, game, points));
        return granted;
    }

    /// <summary>The Batzen a player holds in the instance of a running festival (checks, the shop later), or 0.</summary>
    public int BatzenOf(long peer, string festival) =>
        RunningOf(festival) is { } run && _adventure.Progress(peer) is { } p ? p.Get(FestivalBook.Section).Of(run.Instance, run.Id).Batzen : 0;

    /// <summary>Authority: how many plays of <paramref name="game"/> a player has made today (the festival's day) in a running festival.</summary>
    public int PlaysToday(long peer, string festival, string game) =>
        RunningOf(festival) is { } run && _adventure.Progress(peer) is { } p
            ? p.Get(FestivalBook.Section).Of(run.Instance, run.Id).Played.GetValueOrDefault(FestivalRules.PlayKey(game, run.Day))
            : 0;

    /// <summary>The party score of the crew a peer plays with now, on a running festival.</summary>
    public int CrewPoints(long peer, string festival)
    {
        if (RunningOf(festival) is not { } run || CrewKeys(peer) is not { } keys) return 0;
        return GetBoard(run.Instance, run.Id).Find(keys)?.Points ?? 0;
    }

    // ---- the party board --------------------------------------------------------------------------

    /// <summary>The progress keys of the people a peer plays with now (its party's humans, itself included), or null when it has none yet.</summary>
    private List<string>? CrewKeys(long peer)
    {
        if (_adventure.KeyOf(peer) is not { } own) return null;
        var keys = new List<string>();
        if (PartyOf?.Invoke(peer) is { } members)
            foreach (long m in members)
                if (m > 0 && _adventure.KeyOf(m) is { } k) keys.Add(k);
        if (!keys.Contains(own)) keys.Add(own);
        return keys;
    }

    private void AddToBoard(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, int points)
    {
        if (CrewKeys(peer) is not { } keys) return;
        var board = GetBoard(run.Instance, def.Id);
        var hits = board.Add(def, keys, p.Key, NameOfPeer(peer), points);
        _boardsDirty.Add(run.Instance);
        foreach (var hit in hits)
        {
            GD.Print($"[festival] {def.Id}: the crew reached \"{hit.Milestone.Title}\" ({hit.Milestone.Points} points): {hit.Payees.Length} member(s) paid {hit.Milestone.Batzen} Batzen");
            foreach (string payee in hit.Payees) PayMilestone(board, run, def, hit, payee);
        }
        // everyone in the crew sees the new standing at once
        foreach (long m in CrewPeers(keys)) SendBoard(m, run);
    }

    private void PayMilestone(FestivalBoard board, RunningFestival run, FestivalDef def, MilestoneHit hit, string payeeKey)
    {
        long peer = PeerOfKey(payeeKey);
        if (peer > 0 && _adventure.Progress(peer) is { } p)
        {
            if (!FestivalRules.PayMilestone(p.Get(FestivalBook.Section).Of(run.Instance, def.Id), hit.Index, hit.Milestone.Batzen)) return;
            _adventure.Touch(peer, FestivalBook.Section);
            _adventure.Events.Raise(new FestivalMilestoneReached(peer, p.Key, def.Id, hit.Index));
            return;
        }
        // not here now: owed until their next load
        board.Owe(payeeKey, hit.Milestone.Batzen);
    }

    private long PeerOfKey(string key)
    {
        foreach (long peer in _adventure.Peers)
            if (peer > 0 && _adventure.KeyOf(peer) == key) return peer;
        return 0;
    }

    private IEnumerable<long> CrewPeers(List<string> keys)
    {
        foreach (string k in keys)
            if (PeerOfKey(k) is var peer and > 0) yield return peer;
    }

    private string NameOfPeer(long peer) => _adventure.NameOf?.Invoke(peer) ?? "";

    private FestivalBoard GetBoard(string instance, string festival)
    {
        if (_boards.TryGetValue(instance, out var board)) return board;
        board = new FestivalBoard { Instance = instance, Festival = festival };
        try
        {
            string path = BoardPath(instance);
            if (System.IO.File.Exists(path))
            {
                board = FestivalBoard.FromJson(System.IO.File.ReadAllText(path));
                board.Instance = instance;
                board.Festival = festival;
            }
        }
        catch (Exception e) { GD.PushWarning($"[festival] board {instance}: {e.Message}"); }
        return _boards[instance] = board;
    }

    /// <summary>Where a board is kept: <c>user://festivals/&lt;instance&gt;.json</c>, a test world's or a check's own folder aside.</summary>
    public static string BoardPath(string instance) => System.IO.Path.Combine(BoardDir, instance + ".json");

    public static string BoardDir => AdventureService.ScratchDir is { } scratch
        ? System.IO.Path.Combine(scratch, "festivals")
        : ProjectSettings.GlobalizePath(WorldChoice.SavePath("user://festivals"));

    /// <summary>Writes every board that changed (every 30 s, when the server stops, in checks).</summary>
    public void SaveBoards()
    {
        foreach (string instance in _boardsDirty)
        {
            if (!_boards.TryGetValue(instance, out var board)) continue;
            try
            {
                System.IO.Directory.CreateDirectory(BoardDir);
                string path = BoardPath(instance);
                System.IO.File.WriteAllText(path + ".tmp", board.ToJson());
                System.IO.File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception e) { GD.PushWarning($"[festival] saving board {instance}: {e.Message}"); }
        }
        _boardsDirty.Clear();
    }

    /// <summary>Re-tells each peer its crew's standing when its party changed (a crew is who plays together, not a session id).</summary>
    /// <summary>
    /// While a closing show plays, every adventuring peer within <see cref="FestivalShows.WatchRadiusM"/> of the site for
    /// <see cref="FestivalShows.WatchSeconds"/> of it has watched it: <see cref="FestivalShowWatched"/> goes on the bus once per
    /// player and festival instance (the errands plan's "watch the show" step). The server reads positions; nothing is asked of a client.
    /// </summary>
    private void CheckShow()
    {
        double now = Net.ClockSync.SimNow;
        foreach (var run in Running)
        {
            if (run.ShowStart <= 0 || now < run.ShowStart || run.Site is not { } site) continue;
            foreach (long peer in _adventure.Peers.ToArray())
            {
                var key = (peer, run.Instance);
                if (peer <= 0 || _watchPaid.Contains(key) || !_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) continue;
                if (_adventure.PositionOf?.Invoke(peer) is not { } at || site.DistanceTo(at.E, at.N) > FestivalShows.WatchRadiusM) continue;
                double seen = _watched.GetValueOrDefault(key) + CrewCheckEvery;
                _watched[key] = seen;
                if (seen < FestivalShows.WatchSeconds) continue;
                _watchPaid.Add(key);
                if (run.Id == August1Rules.FestivalId) WatchedAugust1(peer, p, run);
                GD.Print($"[festival] {Short(p.Key)} watched the closing show of {run.Id}");
                _adventure.Events.Raise(new FestivalShowWatched(peer, p.Key, run.Id));
            }
        }
    }

    /// <summary>Authority: the peers that have watched the closing show of an instance (checks).</summary>
    public int WatchersOf(string instance) => _watchPaid.Count(k => k.Instance == instance);

    private void CheckCrews()
    {
        foreach (var key in _sentCrew.Keys.ToArray())
            if (!_adventure.Peers.Contains(key.Peer)) _sentCrew.Remove(key);
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0 || CrewKeys(peer) is not { } keys) continue;
            string crew = FestivalBoard.CrewKey(keys);
            foreach (var run in Running)
                if (_sentCrew.GetValueOrDefault((peer, run.Id)) != crew) SendBoard(peer, run);
        }
    }

    // ---- a peer arrives ---------------------------------------------------------------------------

    private void OnLoaded(long peer)
    {
        if (peer > 0 && !_pending.Contains(peer)) _pending.Add(peer);
    }

    /// <summary>A peer's progress is loaded (or the local one): what it is owed, what ended, what runs, its board.</summary>
    private void Welcome(long peer)
    {
        if (_adventure.Progress(peer) is not { } p) return;
        SettleOwed(peer, p);
        ConvertFor(peer);
        SendFestivals(peer);
        DesalpeWelcome(peer);
        MarketWelcome(peer);
        foreach (var run in Running)
        {
            SendBoard(peer, run);
            if (_adventure.Active(peer)) _adventure.Events.Raise(new FestivalOpened(peer, p.Key, run.Id, run.Day));
        }
    }

    /// <summary>Milestones paid while this player was away land in their instance now (and convert with it if it is over).</summary>
    private void SettleOwed(long peer, PlayerProgress p)
    {
        string dir = BoardDir;
        var instances = new HashSet<string>(_boards.Keys, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.Directory.Exists(dir))
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.json"))
                    instances.Add(System.IO.Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception e) { GD.PushWarning($"[festival] boards: {e.Message}"); }
        foreach (string instance in instances)
        {
            var board = GetBoard(instance, FestivalCalendar.FestivalOf(instance));
            int owed = board.TakeOwed(p.Key);
            if (owed <= 0) continue;
            var inst = p.Get(FestivalBook.Section).Of(instance, board.Festival);
            inst.Batzen += owed;
            inst.Converted = false;   // the instance may be over and paid already: it pays the rest with the next conversion
            _boardsDirty.Add(instance);
            _adventure.Touch(peer, FestivalBook.Section);
            GD.Print($"[festival] {Short(p.Key)}: {owed} Batzen owed from {instance}");
        }
    }

    /// <summary>
    /// Pays out the instances that are over and not paid yet, once: 1 Batzen = CHF 1 into the account. Runs when a peer's progress
    /// loads and when a festival ends under a peer who is online.
    /// </summary>
    public int ConvertFor(long peer)
    {
        if (!Authority || peer <= 0 || _adventure.Progress(peer) is not { } p) return 0;
        var book = p.Get(FestivalBook.Section);
        var paid = FestivalRules.Convert(book, IsOver);
        int total = 0;
        foreach (var (instance, festival, chf) in paid)
        {
            _adventure.Touch(peer, FestivalBook.Section);
            if (chf > 0) Bank.Instance?.Credit(peer, chf, "festival");
            total += chf;
            string title = FestivalCatalog.Get(festival)?.Title ?? festival;
            GD.Print($"[festival] {Short(p.Key)}: {instance} is over, {chf} Batzen paid as CHF {chf}");
            if (Connected(peer)) RpcId(peer, MethodName.TellConverted, title, chf, chf);
            else if (!_networked) TellConverted(title, chf, chf);
        }
        return total;
    }

    /// <summary>Whether an instance is over: not running, and today is past its last date. An unknown festival is never over (a newer build's).</summary>
    private bool IsOver(string instance)
    {
        string id = FestivalCalendar.FestivalOf(instance);
        if (FestivalCatalog.Get(id) is not { } def || OccasionManager.Instance is not { } om) return false;
        if (Running.Any(r => r.Instance == instance)) return false;
        var windows = om.Known.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) is { Schedule.Count: > 0 } entry
            ? entry.Schedule.Select(w => (w.From, w.To)).ToArray()
            : def.Schedule;
        return FestivalCalendar.Ended(windows, FestivalCalendar.YearOf(instance), om.Today);
    }

    // ---- what is sent -----------------------------------------------------------------------------

    private void SendFestivals(long peer)
    {
        if (!Connected(peer)) return;
        RpcId(peer, MethodName.TellFestivals, JsonSerializer.Serialize(Running, _json));
    }

    private void SendBoard(long peer, RunningFestival run)
    {
        if (peer <= 0 || CrewKeys(peer) is not { } keys) return;
        var board = GetBoard(run.Instance, run.Id);
        var crew = board.Find(keys);
        var view = new BoardView(run.Id, run.Instance, crew?.Points ?? 0, crew?.Taken.ToArray() ?? [],
            crew == null ? [] : crew.Contributed.OrderByDescending(c => c.Value)
                .Select(c => new BoardMember(crew.Names.GetValueOrDefault(c.Key, "?"), c.Value)).ToArray());
        _sentCrew[(peer, run.Id)] = FestivalBoard.CrewKey(keys);
        string json = JsonSerializer.Serialize(view, _json);
        if (Connected(peer)) RpcId(peer, MethodName.TellBoard, json);
        else if (!_networked) TellBoard(json);
    }

    private int Refuse(long peer, string why)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellRefused, why);
        else if (!_networked) TellRefused(why);
        return 0;
    }
}
