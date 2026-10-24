using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnitSport.Net;

/// <summary>A connected player as the dashboard lists them (#1211).</summary>
public sealed record AdminPlayer(long Peer, string Name, string? Account, bool Admin, bool Console, int PingMs, double? E, double? N);

/// <summary>What the admin API asks of the running server; every call runs on the game's main thread.</summary>
public interface IAdminBackend
{
    /// <summary>A console command run for <paramref name="admin"/> (no leading slash needed): the lines it answered at once.</summary>
    Task<IReadOnlyList<string>> Command(string admin, string line);

    /// <summary>The server at a glance: version, players, time, weather... any JSON-serialisable object.</summary>
    Task<object> Status();

    Task<IReadOnlyList<AdminPlayer>> Players();

    /// <summary>Kicks a peer: the line that says what happened, and whether it was refused.</summary>
    Task<(string Line, bool Refused)> Kick(string admin, long peer, string reason);

    /// <summary>Warns everyone, then restarts after <paramref name="minutes"/>: the line, and whether it was refused.</summary>
    Task<(string Line, bool Refused)> Restart(string admin, int minutes);
}

/// <summary>
/// The server's log lines with a sequence number each, so a page polls for what came after the last one it has (#1211).
/// Godot calls a logger from any thread, so it is locked; it never logs itself.
/// </summary>
public sealed class AdminLogRing
{
    private readonly int _keep;
    private readonly Queue<(long Seq, string Line)> _lines = new();
    private readonly object _gate = new();
    private long _seq;

    public AdminLogRing(int keep = 2000) => _keep = keep;

    public void Add(string line)
    {
        line = line.TrimEnd();
        if (line.Length == 0) return;
        lock (_gate)
        {
            _lines.Enqueue((++_seq, line));
            while (_lines.Count > _keep) _lines.Dequeue();
        }
    }

    /// <summary>The lines after <paramref name="after"/> (at most <paramref name="max"/>, the newest), and the last sequence number.</summary>
    public (List<(long Seq, string Line)> Lines, long Last) Since(long after, int max)
    {
        lock (_gate)
        {
            var list = _lines.Where(l => l.Seq > after).ToList();
            if (list.Count > max) list = list.GetRange(list.Count - max, max);
            return (list, _seq);
        }
    }
}

/// <summary>The startup arguments an admin may add to a deployed server (<c>server-args.local</c>, #1211).</summary>
public static class StartupArgs
{
    public const int MaxLength = 1000;

    /// <summary>The deploy owns these: the port players and Caddy know, and the admin API itself.</summary>
    private static readonly string[] Owned = ["--server", "--port", "--admin-http", "--admin-nakama", "--headless"];

    // what a server argument is made of: no quotes, no shell, no newline (start-server.sh splits it on spaces, unglobbed)
    private static readonly Regex Allowed = new(@"^[A-Za-z0-9 ._:/,=@+\-]*$", RegexOptions.CultureInvariant);

    /// <summary>The arguments tidied to single spaces, or why they are refused.</summary>
    public static (string Args, string? Error) Check(string? args)
    {
        string tidy = string.Join(' ', (args ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (tidy.Length > MaxLength) return (tidy, $"At most {MaxLength} characters.");
        if (!Allowed.IsMatch(tidy)) return (tidy, "Only letters, digits, spaces and . _ : / , = @ + - (no quotes, no newline).");
        string[] words = tidy.Length == 0 ? [] : tidy.Split(' ');
        if (words.Length > 0 && !words[0].StartsWith("--", StringComparison.Ordinal)) return (tidy, "Start with an option, such as --server-name.");
        foreach (string w in words)
            if (Owned.Contains(w, StringComparer.Ordinal)) return (tidy, $"{w} is set by the deploy.");
        return (tidy, null);
    }
}

/// <summary>
/// The dedicated server's admin API (#1211, docs/notes/net/web-dashboard.md): a loopback HTTP endpoint the site's Caddy
/// serves at <c>/server-api/</c>. Every request carries <c>Authorization: Bearer &lt;Nakama session token&gt;</c>; the
/// verifier says whose admin token it is (the Nakama module's <c>me</c>), and a yes is cached a minute. No Godot in it:
/// the game side is an <see cref="IAdminBackend"/>.
/// </summary>
public sealed class AdminApi : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private const int MaxCached = 256, MaxLogLines = 500;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LoopbackServer _server;
    private readonly Func<string, CancellationToken, Task<string?>> _verify;
    private readonly IAdminBackend _backend;
    private readonly AdminLogRing _log;
    private readonly string? _startupFile;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _audit;
    private readonly Dictionary<string, (string Admin, DateTime Until)> _cache = new();
    private readonly object _gate = new();

    public int Port => _server.Port;

    /// <param name="verify">A token's admin name, or null when it is no admin's; throws when the account server is out of reach.</param>
    /// <param name="startupFile">The deploy's <c>server-args.local</c>, null when this server was not started by <c>start-server.sh</c>.</param>
    /// <param name="audit">Where a change made here is written down (the server's log; the ring when not given).</param>
    public AdminApi(int port, Func<string, CancellationToken, Task<string?>> verify, IAdminBackend backend, AdminLogRing log,
        string? startupFile, Action<string>? audit = null, Func<DateTime>? now = null)
    {
        _verify = verify;
        _backend = backend;
        _log = log;
        _startupFile = startupFile;
        _now = now ?? (() => DateTime.UtcNow);
        _audit = audit ?? log.Add;
        _server = new LoopbackServer(port, Answer, maxBody: 16 << 10);
    }

    private async Task<LoopbackResponse> Answer(LoopbackRequest r, CancellationToken token)
    {
        string? bearer = r.Headers.TryGetValue("authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth[7..].Trim() : null;
        if (string.IsNullOrEmpty(bearer)) return Error(401, "Sign in with an admin account.");
        string? admin;
        try { admin = await Admin(bearer, token); }
        catch (Exception) { return Error(502, "The account server does not answer: try again in a moment."); }
        if (admin == null) return Error(403, "This account is not an admin.");

        try
        {
            return (r.Method, r.Route) switch
            {
                ("GET", "/status") => Ok(await _backend.Status()),
                ("GET", "/log") => Log(r.Query("after")),
                ("GET", "/players") => Ok(new { players = await _backend.Players() }),
                ("POST", "/command") => await Command(admin, Field(r.Body, "line")),
                ("POST", "/kick") => await Kick(admin, r.Body),
                ("POST", "/restart") => await Restart(admin, r.Body),
                ("POST", "/update") => Ok(new { lines = await _backend.Command(admin, "update") }),
                ("GET", "/startup") => Startup(),
                ("POST", "/startup") => SaveStartup(admin, Field(r.Body, "args")),
                (_, "/status" or "/log" or "/players" or "/command" or "/kick" or "/restart" or "/update" or "/startup") => Error(405, "Wrong method."),
                _ => Error(404, "No such call."),
            };
        }
        catch (JsonException) { return Error(400, "The body is not the JSON this call takes."); }
    }

    /// <summary>The admin a token belongs to, from the cache while it lasts.</summary>
    private async Task<string?> Admin(string token, CancellationToken cancel)
    {
        lock (_gate)
            if (_cache.TryGetValue(token, out var hit) && hit.Until > _now()) return hit.Admin;
        string? admin = await _verify(token, cancel);
        lock (_gate)
        {
            if (admin == null) _cache.Remove(token);
            else
            {
                if (_cache.Count >= MaxCached) _cache.Clear();
                _cache[token] = (admin, _now() + CacheFor);
            }
        }
        return admin;
    }

    private LoopbackResponse Log(string? after)
    {
        long from = long.TryParse(after, out long a) ? a : 0;
        var (lines, last) = _log.Since(from, MaxLogLines);
        return Ok(new { lines = lines.Select(l => new { seq = l.Seq, text = l.Line }), last });
    }

    private async Task<LoopbackResponse> Command(string admin, string? line)
    {
        line = line?.Trim();
        if (string.IsNullOrEmpty(line)) return Error(400, "Type a command.");
        if (line.Length > 400 || line.Contains('\n')) return Error(400, "One line, at most 400 characters.");
        return Ok(new { lines = await _backend.Command(admin, line) });
    }

    private async Task<LoopbackResponse> Kick(string admin, string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("peer", out var p) || !p.TryGetInt64(out long peer)) return Error(400, "Which player? (peer)");
        string reason = doc.RootElement.TryGetProperty("reason", out var why) ? why.GetString() ?? "" : "";
        var (line, refused) = await _backend.Kick(admin, peer, reason.Trim());
        return refused ? Error(409, line) : Ok(new { line });
    }

    private async Task<LoopbackResponse> Restart(string admin, string body)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        int minutes = doc.RootElement.TryGetProperty("minutes", out var m) && m.TryGetInt32(out int v) ? v : 0;
        if (minutes is < 0 or > 30) return Error(400, "Between 0 and 30 minutes.");
        var (line, refused) = await _backend.Restart(admin, minutes);
        return refused ? Error(409, line) : Ok(new { line });
    }

    private LoopbackResponse Startup()
    {
        if (_startupFile == null) return Ok(new { available = false, args = "" });
        string args = File.Exists(_startupFile) ? File.ReadAllText(_startupFile).Trim() : "";
        return Ok(new { available = true, args });
    }

    private LoopbackResponse SaveStartup(string admin, string? args)
    {
        if (_startupFile == null) return Error(409, "This server was not started by the deploy's start-server.sh: it has no startup file.");
        var (tidy, error) = StartupArgs.Check(args);
        if (error != null) return Error(400, error);
        // written whole, then moved over the old one: start-server.sh never reads half a file
        string part = _startupFile + ".part";
        File.WriteAllText(part, tidy + "\n");
        File.Move(part, _startupFile, overwrite: true);
        _audit($"[admin] {admin} (website) set the startup arguments: {(tidy.Length == 0 ? "(none)" : tidy)}");
        return Ok(new { available = true, args = tidy });
    }

    /// <summary>A string field of a JSON object body, or null.</summary>
    private static string? Field(string body, string name)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }

    private static LoopbackResponse Ok(object value) => LoopbackResponse.Json(JsonSerializer.Serialize(value, value.GetType(), Json));

    private static LoopbackResponse Error(int status, string message) =>
        new(status, "application/json", JsonSerializer.Serialize(new { error = message }, Json));

    public void Dispose() => _server.Dispose();
}
