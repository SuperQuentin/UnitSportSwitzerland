using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// The dedicated server's admin API for the website's dashboard (#1211, docs/notes/net/web-dashboard.md):
/// <c>--admin-http &lt;port&gt;</c> listens on 127.0.0.1 (the site's Caddy serves it at <c>/server-api/</c>), and
/// <c>--admin-nakama &lt;url&gt;</c> (else <c>--nakama-url</c>) is the account server that says whose admin token a
/// request carries. The HTTP side is <see cref="AdminApi"/>; this node is its <see cref="IAdminBackend"/>, every call
/// handed to the main thread, and the logger that feeds its log ring.
/// </summary>
public partial class AdminHttp : Node, IAdminBackend
{
    private readonly ChatManager _chat;
    private readonly Accounts.AccountGate? _gate;
    private readonly int _port;
    private readonly string _nakama;
    private readonly AdminLogRing _log = new();
    private RingLogger? _logger;
    private AdminApi? _api;
    private readonly double _started = RealClock.Now;

    private AdminHttp(ChatManager chat, Accounts.AccountGate? gate, int port, string nakama)
    {
        _chat = chat;
        _gate = gate;
        _port = port;
        _nakama = nakama;
        Name = "AdminHttp";
    }

    /// <summary>The node when <c>--admin-http</c> is given (null otherwise, or after saying what is missing).</summary>
    public static AdminHttp? Create(ChatManager chat, Accounts.AccountGate? gate)
    {
        if (CmdArgs.Value("--admin-http", notFlag: true) is not { } arg) return null;
        if (!int.TryParse(arg, out int port) || port is < 1 or > 65535)
        {
            GD.PushError($"[admin-http] --admin-http {arg}: a port number");
            return null;
        }
        string nakama = Accounts.AccountRules.NormalizeUrl(CmdArgs.Value("--admin-nakama", notFlag: true) ?? CmdArgs.Value("--nakama-url", notFlag: true));
        if (nakama.Length == 0)
        {
            GD.PushError("[admin-http] needs --admin-nakama <the account server, e.g. http://127.0.0.1:7350>: it says who is an admin");
            return null;
        }
        return new AdminHttp(chat, gate, port, nakama);
    }

    public override void _Ready()
    {
        _logger = new RingLogger(_log);
        OS.AddLogger(_logger);
        string? dir = System.Environment.GetEnvironmentVariable("UNITSPORT_DEPLOY_DIR");
        string? startup = string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, "server-args.local");
        try
        {
            _api = new AdminApi(_port, Verify, this, _log, startup, audit: line => Callable.From(() => GD.Print(line)).CallDeferred());
            GD.Print($"[admin-http] the website's dashboard on 127.0.0.1:{_api.Port}, admins checked on {_nakama}"
                + (startup == null ? "; no startup file (not started by start-server.sh)" : ""));
        }
        catch (System.Net.Sockets.SocketException e)
        {
            GD.PushError($"[admin-http] cannot listen on 127.0.0.1:{_port}: {e.Message}");
        }
    }

    public override void _ExitTree()
    {
        _api?.Dispose();
        _api = null;
        if (_logger != null) OS.RemoveLogger(_logger);
        _logger = null;
    }

    private Task<string?> Verify(string token, CancellationToken cancel) => Accounts.NakamaRest.AdminName(_nakama, token, cancel);

    /// <summary>Runs <paramref name="work"/> on the main thread (the scene tree and the peer live there) and hands back its result.</summary>
    private static Task<T> Main<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }).CallDeferred();
        return done.Task;
    }

    public Task<IReadOnlyList<string>> Command(string admin, string line) => Main(() => _chat.RunWebCommand(admin, line));

    public Task<object> Status() => Main<object>(() => new
    {
        name = CmdArgs.Value("--server-name", notFlag: true) ?? "",
        version = ServerUpdater.RunningVersion,
        players = _chat.PlayerCount,
        uptimeSeconds = (long)(RealClock.Now - _started),
        time = _chat.AskQuietly("time"),
        speed = _chat.AskQuietly("speed"),
        weather = _chat.AskQuietly("weather"),
        sea = _chat.AskQuietly("seastate"),
        accounts = (_gate?.Mode ?? Accounts.ServerAccounts.Off).ToString().ToLowerInvariant(),
        canUpdate = ServerUpdater.Script != null,
        canRestart = ChatManager.RestartMarker != null,
    });

    public Task<IReadOnlyList<AdminPlayer>> Players() =>
        Main<IReadOnlyList<AdminPlayer>>(() => _chat.WebPlayers(peer => _gate?.Of(peer)?.Username));

    public Task<(string Line, bool Refused)> Kick(string admin, long peer, string reason) => Main(() => _chat.KickFromWeb(admin, peer, reason));

    public Task<(string Line, bool Refused)> Restart(string admin, int minutes) => Main(() => _chat.RestartFromWeb(admin, minutes));

    /// <summary>Every engine log line into the ring, as <c>Playtest/PlaytestLog</c> does; any thread.</summary>
    private sealed partial class RingLogger(AdminLogRing ring) : Logger
    {
        public override void _LogMessage(string message, bool error) => ring.Add(error ? "ERR " + message : message);

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
            int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces) =>
            ring.Add($"{(errorType == (int)ErrorType.Warning ? "WARN" : "ERROR")} {(string.IsNullOrEmpty(rationale) ? code : rationale)} ({System.IO.Path.GetFileName(file)}:{line} {function})");
    }
}
