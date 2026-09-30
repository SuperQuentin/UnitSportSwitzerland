using System.Collections.Concurrent;
using Godot;

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
/// </summary>
public partial class CdLibrary : Node
{
    public const string NodeName = "CdLibrary";

    /// <summary>The CD folder on this machine, absolute.</summary>
    public static string Directory => ProjectSettings.GlobalizePath("user://cds");

    private const string IndexFile = "library.json";
    private const double BurnCooldown = 60;

    public static CdLibrary? Instance { get; private set; }

    private readonly Dictionary<int, CdInfo> _all = new();

    /// <summary>Every CD known here, by id.</summary>
    public IReadOnlyDictionary<int, CdInfo> All => _all;

    /// <summary>A CD was added (or the whole list arrived).</summary>
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
    private readonly ConcurrentQueue<(long Peer, CdInfo? Cd)> _done = new();

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
        if (_server) Multiplayer.PeerConnected += SendAll;
        BurnFixture();
    }

    public override void _ExitTree()
    {
        if (_server) Multiplayer.PeerConnected -= SendAll;
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>The copy that runs the burner: the server, or the client offline.</summary>
    private bool Owns => _server || !Online;

    // ---- client ---------------------------------------------------------------------------------

    /// <summary>Asks for a CD to be burnt from a link. Progress comes back on <see cref="BurnStatus"/>.</summary>
    public void RequestBurn(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return;
        if (!Owns) { RpcId(1, MethodName.RequestBurnRpc, url); return; }
        Begin(0, url, out string refusal);
        if (refusal.Length > 0) BurnStatus?.Invoke(refusal);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Status(string text) => BurnStatus?.Invoke(text);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Added(Godot.Collections.Dictionary cd)
    {
        var info = CdInfo.FromDict(cd);
        _all[info.Id] = info;
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Library(Godot.Collections.Array cds)
    {
        _all.Clear();
        foreach (var v in cds)
        {
            var info = CdInfo.FromDict(v.AsGodotDictionary());
            _all[info.Id] = info;
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
        Begin(sender, url, out string refusal);
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
    private void Begin(long peer, string url, out string refusal)
    {
        refusal = "";
        bool localFile = peer == 0 && File.Exists(url);
        if (!localFile && !AllowedSource(url)) { refusal = "Only YouTube links can be burnt."; return; }
        if (_burning) { refusal = "Someone is already burning a CD; try again in a minute."; return; }
        double now = Time.GetTicksMsec() / 1000.0;
        if (peer != 0 && _lastBurn.TryGetValue(peer, out double last) && now - last < BurnCooldown)
        {
            refusal = $"One CD a minute: {(int)(BurnCooldown - (now - last))} s to wait.";
            return;
        }
        if (!CdBurner.ToolsAvailable(out string why)) { refusal = why; return; }

        _burning = true;
        _lastBurn[peer] = now;
        int id = _nextId++;
        var burner = new CdBurner { CdDirectory = Directory };
        var progress = new Progress<string>(text => _status.Enqueue((peer, text)));
        GD.Print($"[cd] burning CD {id} for peer {peer}: {(localFile ? Path.GetFileName(url) : url)}");
        // The tools run for a while; RPCs must go out from _Process, so the results are queued.
        Task.Run(async () =>
        {
            CdInfo? cd = null;
            try { cd = await burner.BurnAsync(id, url, progress, CancellationToken.None); }
            catch (Exception e) { _status.Enqueue((peer, $"Burn failed: {e.Message}")); }
            _done.Enqueue((peer, cd));
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
            _all[cd.Id] = cd;
            Save();
            GD.Print($"[cd] CD {cd.Id} ready: {cd.Describe()}");
            Changed?.Invoke();
            if (_server && Online) Rpc(MethodName.Added, cd.ToDict());
        }
    }

    // ---- storage --------------------------------------------------------------------------------

    private void Load()
    {
        if (!Owns) return;
        try
        {
            string path = Path.Combine(Directory, IndexFile);
            if (!File.Exists(path)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<CdInfo>>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            foreach (var cd in list ?? new())
                if (File.Exists(Path.Combine(Directory, $"{cd.Id}.ogg")))
                {
                    _all[cd.Id] = cd;
                    _nextId = Math.Max(_nextId, cd.Id + 1);
                }
            GD.Print($"[cd] library: {_all.Count} CD(s) in {Directory}");
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not read the library: {e.Message}");
        }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var list = _all.Values.OrderBy(c => c.Id).ToList();
            File.WriteAllText(Path.Combine(Directory, IndexFile), System.Text.Json.JsonSerializer.Serialize(list,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not save the library: {e.Message}");
        }
    }

    /// <summary>
    /// <c>--cdfixture &lt;audio file&gt;</c>: burns a local file at boot, once, so a loopback test
    /// has a CD without yt-dlp or the internet (ffmpeg still runs).
    /// </summary>
    private void BurnFixture()
    {
        if (!Owns) return;
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--cdfixture");
        if (i < 0 || i + 1 >= args.Length) return;
        string file = args[i + 1];
        if (!File.Exists(file)) { GD.PushWarning($"[cd] fixture not found: {file}"); return; }
        string title = Path.GetFileNameWithoutExtension(file);
        if (_all.Values.Any(c => c.Title == title)) { GD.Print($"[cd] fixture already burnt: {title}"); return; }
        Begin(0, file, out string refusal);
        if (refusal.Length > 0) GD.PushWarning($"[cd] fixture refused: {refusal}");
    }
}
