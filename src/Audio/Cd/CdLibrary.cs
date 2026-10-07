using System.Collections.Concurrent;
using Godot;
using UnitSport.Net;
using UnitSport.Core;

namespace UnitSport.Audio.Cd;

/// <summary>
/// Every CD anyone has burnt on this server, at <c>World/CdLibrary</c> on the server and on every
/// client (RPCs route by node path). The server keeps the files under <c>user://cds</c> and the
/// list in <c>library.json</c> there; a client holds the list only, and gets the audio through
/// <see cref="CdCache"/> when a radio plays it. Offline this client is its own server, with the
/// same folder.
///
/// <para>
/// A CD is not an inventory item: it is a track in this shared list, which the radio menu
/// shows. Burning is one at a time and rate-limited per player, because each burn runs two
/// external tools on the server's CPU for a while.
/// </para>
///
/// <para>
/// A player can also keep CDs of their own (#168): burnt on their machine with the same
/// <see cref="CdBurner"/>, kept under <c>user://cds/personal</c>, listed only for them. They have
/// negative random ids so they never collide with the server's or another player's; a radio
/// playing one is heard by its owner only, since nobody else has the file.
/// </para>
/// </summary>
public partial class CdLibrary : Node
{
    public const string NodeName = "CdLibrary";

    /// <summary>The CD folder on this machine, absolute.</summary>
    public static string Directory => ProjectSettings.GlobalizePath("user://cds");

    /// <summary>This player's own CDs, on this machine, absolute.</summary>
    public static string PersonalDirectory => Path.Combine(Directory, "personal");

    private const string IndexFile = "library.json";

    /// <summary>
    /// The chess type beat (#370): the church radio's CD and the rat dance. The project keeps a copy
    /// for dev runs and checks, offline; an export leaves it out (not openly licensed, #718) and
    /// burns it from <see cref="RatBeatUrl"/> instead.
    /// </summary>
    public const string RatBeatRes = "res://assets/audio/chess_type_beat.ogg";

    /// <summary><see cref="CdInfo.Source"/> of the CD burnt from <see cref="RatBeatRes"/>.</summary>
    public const string RatBeatSource = "bundled:chess_type_beat";

    /// <summary>Where a release burns the chess type beat from (#718).</summary>
    public const string RatBeatUrl = "https://www.youtube.com/watch?v=EK2w6qA5zz8";

    /// <summary>
    /// The CDs every release starts with (#718), the chess type beat first: burnt from these links
    /// by the server (or the offline game) of an exported build, once each, one at a time, so no
    /// audio ships with the game. A CD burnt from one is marked <see cref="DefaultSource"/>.
    /// </summary>
    public static readonly string[] DefaultUrls =
    {
        RatBeatUrl,
        "https://www.youtube.com/watch?v=Zc4r7GGXAvw",
        "https://www.youtube.com/watch?v=WxJR8L3y4gY",
        "https://www.youtube.com/watch?v=PHfRJOZ5HpE",
        "https://www.youtube.com/watch?v=NAogfwwqwGY",
        "https://www.youtube.com/watch?v=6BEww_j1FmA",
        "https://www.youtube.com/watch?v=9mxD-mByh0U",
        "https://www.youtube.com/watch?v=zWMpmScHz9g",
        "https://www.youtube.com/watch?v=0wRYvsfhsR8",
        "https://www.youtube.com/watch?v=PGNiXGX2nLU",
    };

    /// <summary><see cref="CdInfo.Source"/> of the CD burnt from a <see cref="DefaultUrls"/> link.</summary>
    public static string DefaultSource(string url) => "default:" + url;

    /// <summary>
    /// The shared CD of the chess type beat, or -1 while it is not burnt (or not yet listed here).
    /// Every peer knows it from the library, so the dance needs nothing replicated.
    /// </summary>
    public int RatBeatId { get; private set; } = -1;

    /// <summary>
    /// The chess type beat's grid, measured (onset autocorrelation, #370): 132.5 bpm, first hit at
    /// 0.10 s. The analyser hears it at half tempo with an offset off the grid, and the rat's intro
    /// and dance need the real one, so the server's copy of the CD carries these.
    /// </summary>
    public const float RatBeatBpm = 132.5f, RatBeatOffset = 0.10f;

    /// <summary>Whether this CD is the chess type beat.</summary>
    public static bool IsRatBeat(int cdId) => cdId >= 0 && Instance is { } lib && lib.RatBeatId == cdId;
    private const double BurnCooldown = 60;

    public static CdLibrary? Instance { get; private set; }

    private readonly Dictionary<int, CdInfo> _all = new();

    /// <summary>Every shared CD known here, by id.</summary>
    public IReadOnlyDictionary<int, CdInfo> All => _all;

    private readonly Dictionary<int, CdInfo> _personal = new();

    /// <summary>This player's own CDs (negative ids), never sent anywhere.</summary>
    public IReadOnlyDictionary<int, CdInfo> Personal => _personal;

    /// <summary>A CD by id, shared or personal; null when this peer does not know it.</summary>
    public CdInfo? Find(int id) => id < 0 ? _personal.GetValueOrDefault(id) : _all.GetValueOrDefault(id);

    /// <summary>The Ogg of one of this player's own CDs, or null.</summary>
    public string? PersonalPath(int id)
    {
        if (!_personal.ContainsKey(id)) return null;
        string path = Path.Combine(PersonalDirectory, $"{id}.ogg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>A CD was added or updated (or the whole list arrived).</summary>
    public event Action? Changed;

    /// <summary>Client (and offline): what the burner is doing with the link you gave it.</summary>
    public event Action<string>? BurnStatus;

    /// <summary>Server: may this peer burn a CD? Null allows everyone.</summary>
    public Func<long, bool>? MayBurn { get; set; }

    private bool _server;
    private bool _burning;
    private int _nextId = 1;
    private readonly Dictionary<long, double> _lastBurn = new();
    private readonly ConcurrentQueue<(long Peer, string Text)> _status = new();
    private readonly ConcurrentQueue<(long Peer, CdInfo? Cd, bool Personal)> _done = new();
    private readonly Queue<string> _fixtures = new();

    /// <summary>CDs whose <see cref="CdAnalysis"/> is missing or old (#725), re-analysed one at a time.</summary>
    private readonly Queue<(int Id, bool Personal)> _stale = new();
    private readonly ConcurrentQueue<(int Id, bool Personal, CdAnalysis? Analysis, bool NoFfmpeg)> _reanalysed = new();
    private bool _reanalysing, _noFfmpeg;

    /// <summary>What a queued burn of this process marks its CD with (a default link, the bundled beat).</summary>
    private readonly Dictionary<string, string> _sources = new();

    /// <summary><paramref name="server"/> for the dedicated server's copy, decided up front like <c>Bank</c>.</summary>
    public static CdLibrary Create(Node world, bool server)
    {
        var lib = new CdLibrary { Name = NodeName, _server = server };
        world.AddChild(lib);
        Instance = lib;
        return lib;
    }

    public override void _Ready()
    {
        // the server and the offline game own a library; a client's list comes from the server
        Load();
        if (!_server) LoadPersonal();
        if (_server) Multiplayer.PeerConnected += SendAll;
        EnsureRatBeat();
        EnsureDefaults();
        BurnFixture();
        QueueBackfill();
    }

    public override void _ExitTree()
    {
        if (_server) Multiplayer.PeerConnected -= SendAll;
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    /// <summary>The copy that runs the burner: the server, or the client offline.</summary>
    private bool Owns => _server || !Online;

    // ---- client ---------------------------------------------------------------------------------

    /// <summary>
    /// Asks for a CD to be burnt from a link: for everyone (on the server) or, when
    /// <paramref name="personal"/>, for this player only, on this machine. Progress comes back on
    /// <see cref="BurnStatus"/>.
    /// </summary>
    public void RequestBurn(string url, bool personal = false)
    {
        url = url.Trim();
        if (url.Length == 0) return;
        if (!personal && !Owns) { RpcId(1, MethodName.RequestBurnRpc, url); return; }
        Begin(0, url, personal, out string refusal);
        if (refusal.Length > 0) BurnStatus?.Invoke(refusal);
    }

    /// <summary>Forgets one of this player's own CDs and deletes its files.</summary>
    public void RemovePersonal(int id)
    {
        if (!_personal.Remove(id)) return;
        foreach (string ext in new[] { ".ogg", ".json" })
        {
            try { File.Delete(Path.Combine(PersonalDirectory, $"{id}{ext}")); }
            catch (Exception e) { GD.PushWarning($"[cd] could not delete personal CD {id}: {e.Message}"); }
        }
        SaveIndex(PersonalDirectory, _personal);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Status(string text) => BurnStatus?.Invoke(text);

    /// <summary>A new CD, or a new version of a listed one (same id: replaced, e.g. its analysis backfilled).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Added(Godot.Collections.Dictionary cd)
    {
        var info = CdInfo.FromDict(cd);
        _all[info.Id] = Note(info);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Library(Godot.Collections.Array cds)
    {
        _all.Clear();
        RatBeatId = -1;
        foreach (var v in cds)
        {
            var info = CdInfo.FromDict(v.AsGodotDictionary());
            _all[info.Id] = Note(info);
        }
        GD.Print($"[cd] library from the server: {_all.Count} CD(s)");
        Changed?.Invoke();
    }

    // ---- server ---------------------------------------------------------------------------------

    private void SendAll(long peer)
    {
        var list = new Godot.Collections.Array();
        foreach (var cd in _all.Values) list.Add(cd.ToDict());
        RpcId(peer, MethodName.Library, list);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBurnRpc(string url)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (url.Length > 512) { RpcId(sender, MethodName.Status, "That is not a link."); return; }
        if (MayBurn != null && !MayBurn(sender)) { RpcId(sender, MethodName.Status, "You may not burn CDs on this server."); return; }
        Begin(sender, url, false, out string refusal);
        if (refusal.Length > 0) RpcId(sender, MethodName.Status, refusal);
    }

    /// <summary>Only video links, from the sites yt-dlp is meant for here: a link is user input.</summary>
    private static bool AllowedSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https" && uri.Scheme != "http") return false;
        string host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be";
    }

    /// <summary>Starts a burn on the worker, or says why not. <paramref name="peer"/> 0 = this process.</summary>
    private void Begin(long peer, string url, bool personal, out string refusal)
    {
        refusal = "";
        bool localFile = peer == 0 && File.Exists(url);
        if (!localFile && !AllowedSource(url)) { refusal = "Only YouTube links can be burnt."; return; }
        if (_burning) { refusal = personal ? "Already burning a CD; try again when it is done." : "Someone is already burning a CD; try again in a minute."; return; }
        double now = Time.GetTicksMsec() / 1000.0;
        if (peer != 0 && _lastBurn.TryGetValue(peer, out double last) && now - last < BurnCooldown)
        {
            refusal = $"One CD a minute: {(int)(BurnCooldown - (now - last))} s to wait.";
            return;
        }
        if (!CdBurner.ToolsAvailable(out string why)) { refusal = why; return; }

        _burning = true;
        _lastBurn[peer] = now;
        int id = personal ? NewPersonalId() : _nextId++;
        string? source = peer == 0 ? _sources.GetValueOrDefault(url) : null;
        // the chess type beat's grid is set by hand (Note): lay its downbeat and sections on that one
        bool ratBeat = source == RatBeatSource || source == DefaultSource(RatBeatUrl);
        var burner = new CdBurner
        {
            CdDirectory = personal ? PersonalDirectory : Directory,
            Grid = ratBeat ? (RatBeatBpm, RatBeatOffset) : null,
        };
        var progress = new Progress<string>(text => _status.Enqueue((peer, text)));
        GD.Print($"[cd] burning {(personal ? "personal " : "")}CD {id} for peer {peer}: {(localFile ? Path.GetFileName(url) : url)}");
        // The tools run for a while; RPCs must go out from _Process, so the results are queued.
        Task.Run(async () =>
        {
            CdInfo? cd = null;
            try
            {
                cd = await burner.BurnAsync(id, url, progress, CancellationToken.None);
                if (cd != null && source != null) cd = cd with { Source = source };
            }
            catch (Exception e) { _status.Enqueue((peer, $"Burn failed: {e.Message}")); }
            _done.Enqueue((peer, cd, personal));
        });
    }

    public override void _Process(double delta)
    {
        while (_status.TryDequeue(out var s))
        {
            if (s.Peer == 0) BurnStatus?.Invoke(s.Text);
            else if (Online) RpcId(s.Peer, MethodName.Status, s.Text);
        }
        while (_done.TryDequeue(out var d))
        {
            _burning = false;
            if (d.Cd is not { } cd) continue;
            if (d.Personal)
            {
                _personal[cd.Id] = cd;
                SaveIndex(PersonalDirectory, _personal);
                GD.Print($"[cd] personal CD {cd.Id} ready: {cd.Describe()}");
                BurnStatus?.Invoke($"Burnt for you only: {cd.Title}");
                Changed?.Invoke();
                continue;
            }
            _all[cd.Id] = Note(cd);
            Save();
            GD.Print($"[cd] CD {cd.Id} ready: {cd.Describe()}");
            Changed?.Invoke();
            if (_server && Online) Rpc(MethodName.Added, _all[cd.Id].ToDict());
        }
        if (!_burning && _fixtures.TryDequeue(out string? next))
        {
            Begin(0, next, false, out string refusal);
            if (refusal.Length > 0) GD.PushWarning($"[cd] fixture refused: {refusal}");
        }
        while (_reanalysed.TryDequeue(out var r)) Reanalysed(r.Id, r.Personal, r.Analysis, r.NoFfmpeg);
        if (!_reanalysing && !_noFfmpeg && _stale.TryDequeue(out var stale)) Reanalyse(stale.Id, stale.Personal);
    }

    // ---- analysis backfill (#725) ---------------------------------------------------------------

    /// <summary>
    /// Queues every CD of this library (the shared one where this copy owns it, and this player's
    /// own) whose <see cref="CdAnalysis"/> is missing or older than the analyser. Its own flag, not
    /// the burn queue's, so a <c>--cdfixture</c> never waits behind it.
    /// </summary>
    private void QueueBackfill()
    {
        if (Owns)
            foreach (var cd in _all.Values) if (CdAnalysis.IsStale(cd)) _stale.Enqueue((cd.Id, false));
        foreach (var cd in _personal.Values) if (CdAnalysis.IsStale(cd)) _stale.Enqueue((cd.Id, true));
        if (_stale.Count > 0) GD.Print($"[cd] analysing {_stale.Count} older CD(s) again");
    }

    /// <summary>Decodes one CD's Ogg on a worker and analyses it on its stored grid; the result comes back through <see cref="_reanalysed"/>.</summary>
    private void Reanalyse(int id, bool personal)
    {
        var cd = personal ? _personal.GetValueOrDefault(id) : _all.GetValueOrDefault(id);
        if (cd == null) return;
        string ogg = Path.Combine(personal ? PersonalDirectory : Directory, $"{id}.ogg");
        if (!File.Exists(ogg)) return;
        _reanalysing = true;
        float bpm = cd.Bpm, offset = cd.BeatOffset;
        Task.Run(async () =>
        {
            CdAnalysis? analysis = null;
            bool noFfmpeg = false;
            try { analysis = await CdBurner.ReanalyseAsync(ogg, bpm, offset, CancellationToken.None); }
            catch (System.ComponentModel.Win32Exception) { noFfmpeg = true; }   // ffmpeg missing: skip quietly
            catch (Exception e) { GD.Print($"[cd] could not analyse CD {id} again: {e.Message}"); }
            _reanalysed.Enqueue((id, personal, analysis, noFfmpeg));
        });
    }

    /// <summary>Main thread: stores the new block in the list and the CD's files, and gives the server's to every client.</summary>
    private void Reanalysed(int id, bool personal, CdAnalysis? analysis, bool noFfmpeg)
    {
        _reanalysing = false;
        if (noFfmpeg) { _noFfmpeg = true; _stale.Clear(); return; }
        var list = personal ? _personal : _all;
        if (analysis == null || !list.TryGetValue(id, out var cd)) return;
        cd = cd with { Analysis = analysis };
        list[id] = cd;
        string dir = personal ? PersonalDirectory : Directory;
        SaveIndex(dir, list);
        try { Core.JsonStore.Save(Path.Combine(dir, $"{id}.json"), cd, CdInfo.Json); }
        catch (Exception e) { GD.PushWarning($"[cd] could not save CD {id}: {e.Message}"); }
        GD.Print($"[cd] CD {id} analysed again: {analysis.SectionStarts.Length} section(s), downbeat {analysis.Downbeat}");
        Changed?.Invoke();
        // Added replaces a listed id on the clients
        if (!personal && _server && Online) Rpc(MethodName.Added, cd.ToDict());
    }

    // ---- storage --------------------------------------------------------------------------------

    private void Load()
    {
        if (!Owns) return;
        foreach (var cd in LoadIndex(Directory))
        {
            _all[cd.Id] = Note(cd);
            _nextId = Math.Max(_nextId, cd.Id + 1);
        }
        GD.Print($"[cd] library: {_all.Count} CD(s) in {Directory}");
    }

    private void LoadPersonal()
    {
        foreach (var cd in LoadIndex(PersonalDirectory))
            if (cd.Id < 0) _personal[cd.Id] = cd;
        if (_personal.Count > 0) GD.Print($"[cd] {_personal.Count} personal CD(s) in {PersonalDirectory}");
    }

    private void Save() => SaveIndex(Directory, _all);

    private static readonly System.Text.Json.JsonSerializerOptions IndexJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The CDs listed in a folder's index whose audio is still there.</summary>
    private static List<CdInfo> LoadIndex(string directory)
    {
        try
        {
            string path = Path.Combine(directory, IndexFile);
            if (!File.Exists(path)) return new();
            var list = System.Text.Json.JsonSerializer.Deserialize<List<CdInfo>>(File.ReadAllText(path), IndexJson) ?? new();
            return list.Where(cd => File.Exists(Path.Combine(directory, $"{cd.Id}.ogg"))).ToList();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not read {directory}: {e.Message}");
            return new();
        }
    }

    private static void SaveIndex(string directory, Dictionary<int, CdInfo> cds)
    {
        try
        {
            var list = cds.Values.OrderBy(c => c.Id).ToList();
            Core.JsonStore.Save(Path.Combine(directory, IndexFile), list, IndexJson);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not save {directory}: {e.Message}");
        }
    }

    /// <summary>A negative id nobody else will pick: random, so two players' own CDs never meet.</summary>
    private int NewPersonalId()
    {
        int id;
        do id = -Random.Shared.Next(1, int.MaxValue);
        while (_personal.ContainsKey(id));
        return id;
    }

    /// <summary>Remembers the chess type beat's id (bundled or from its link), and gives it its measured grid.</summary>
    private CdInfo Note(CdInfo cd)
    {
        if (cd.Source != RatBeatSource && cd.Source != DefaultSource(RatBeatUrl)) return cd;
        RatBeatId = cd.Id;
        return cd with { Bpm = RatBeatBpm, BeatOffset = RatBeatOffset };
    }

    /// <summary>
    /// The server (or offline game) burns the shipped chess type beat into the shared list once, the
    /// way a fixture is burnt: ffmpeg encodes it and the analyser finds its tempo and first beat,
    /// which the rat dance and its intro run on. The res:// file sits in the pck in an export, so it
    /// is copied out first; its name is the CD's title.
    /// </summary>
    private void EnsureRatBeat()
    {
        if (!Owns || RatBeatId >= 0) return;
        try
        {
            if (!Godot.FileAccess.FileExists(RatBeatRes)) return;   // an export: EnsureDefaults burns it from its link
            using var src = Godot.FileAccess.Open(RatBeatRes, Godot.FileAccess.ModeFlags.Read);
            if (src == null) { GD.PushWarning($"[cd] could not open {RatBeatRes}: no chess type beat"); return; }
            string dir = Path.Combine(Directory, "_bundled");
            System.IO.Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Chess Type Beat.ogg");
            File.WriteAllBytes(file, src.GetBuffer((long)src.GetLength()));
            _sources[file] = RatBeatSource;
            _fixtures.Enqueue(file);
            GD.Print("[cd] burning the chess type beat");
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not burn the chess type beat: {e.Message}");
        }
    }

    /// <summary>
    /// An exported build (or <c>--defaultcds</c>, to try it from the editor) burns every
    /// <see cref="DefaultUrls"/> link it has no CD of yet, through the fixture queue. Dev runs and
    /// checks skip it: they stay offline, and a fixture would wait behind the downloads. A link that
    /// fails (no internet, a video gone) is tried again on the next start.
    /// </summary>
    private void EnsureDefaults()
    {
        if (!Owns || !(OS.HasFeature("template") || CmdArgs.Has("--defaultcds"))) return;
        var have = _all.Values.Select(c => c.Source).ToHashSet();
        foreach (string url in DefaultUrls)
        {
            if (have.Contains(DefaultSource(url)) || (url == RatBeatUrl && (RatBeatId >= 0 || _sources.ContainsValue(RatBeatSource)))) continue;
            _sources[url] = DefaultSource(url);
            _fixtures.Enqueue(url);
        }
        if (_fixtures.Count > 0) GD.Print($"[cd] burning the default CDs: {_fixtures.Count} to go");
    }

    /// <summary>
    /// <c>--cdfixture &lt;audio file&gt;</c>: burns a local file at boot, once, so a loopback test
    /// has a CD without yt-dlp or the internet (ffmpeg still runs).
    /// </summary>
    private void BurnFixture()
    {
        if (!Owns) return;
        var args = CmdArgs.All;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != "--cdfixture") continue;
            string file = args[i + 1];
            if (!File.Exists(file)) { GD.PushWarning($"[cd] fixture not found: {file}"); continue; }
            string title = Path.GetFileNameWithoutExtension(file);
            if (_all.Values.Any(c => c.Title == title)) { GD.Print($"[cd] fixture already burnt: {title}"); continue; }
            _fixtures.Enqueue(file);   // one at a time, from _Process
        }
    }
}
