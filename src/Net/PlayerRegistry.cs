using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>One connected peer, as the server sees them.</summary>
public sealed class PlayerInfo
{
    public required long PeerId { get; init; }

    /// <summary>Display name the client announced. Never trusted for anything but display.</summary>
    public required string Name { get; set; }

    /// <summary>True when this peer has proved it is an operator this session.</summary>
    public bool IsAdmin
    {
        get => _isAdmin;
        set
        {
            if (_isAdmin == value) return;
            _isAdmin = value;
            AdminChanged?.Invoke(this);
        }
    }

    private bool _isAdmin;

    /// <summary>
    /// Joined through the server console (<see cref="ConsoleLink"/>, #1088): a player with no body in the world,
    /// whose game may speak another protocol. Its lines go through the link, never the chat's RPCs; it is not
    /// counted as playing.
    /// </summary>
    public bool Bodiless { get; init; }

    /// <summary>
    /// Any peer gaining or losing operator rights, however it happened (join, /login, /admin).
    /// The server's chat tells that client, which is how its menus learn what they may offer.
    /// </summary>
    public static event Action<PlayerInfo>? AdminChanged;
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => AdminChanged = null;


    public override string ToString() => (IsAdmin ? $"{Name} (admin)" : Name) + (Bodiless ? " (console)" : "");
}

/// <summary>
/// Who is connected, what they are called, and who may run privileged commands.
///
/// Server-side only — a client copy would be a client that could edit its own permissions.
/// Names are claimed by the client and deduplicated here; identity is the peer id, which the
/// client cannot forge because ENet assigns it.
///
/// Admins are bootstrapped one of two ways:
/// <list type="bullet">
/// <item>a persisted list in <c>user://admins.json</c>, granted on join (#1359: by the player's id, <see cref="PlayerIds"/>,
/// never by a name anyone can type; a name left from an older file is claimed once, by the next proven player who joins
/// under it);</item>
/// <item><c>/login &lt;password&gt;</c>, where the password comes from <c>--admin-password</c>
/// on the server command line. Without that argument the command is disabled entirely, so a
/// server that never sets one cannot be elevated by guessing.</item>
/// </list>
/// <para>
/// The transport is plain ENet with no encryption, so the password crosses the wire in clear.
/// That is acceptable on a LAN or a trusted link and is not acceptable over the open internet;
/// for that, Godot's DTLS support would need certificates wiring in.
/// </para>
/// </summary>
public sealed class PlayerRegistry
{
    private const string AdminFile = "user://admins.json";

    private readonly Dictionary<long, PlayerInfo> _players = new();
    /// <summary>The persisted admins' ids (<see cref="PlayerIds.IdOf"/>).</summary>
    private readonly HashSet<string> _adminIds = new(StringComparer.Ordinal);
    /// <summary>Admins from a file older than #1359, still by name until their player is next seen.</summary>
    private readonly HashSet<string> _legacyAdminNames = new(StringComparer.OrdinalIgnoreCase);
    private PlayerIds? _ids;

    /// <summary>The server's ids: admins are granted when a peer is identified.</summary>
    public PlayerIds? Ids
    {
        get => _ids;
        set
        {
            if (_ids != null) _ids.Identified -= OnIdentified;
            _ids = value;
            if (_ids != null) _ids.Identified += OnIdentified;
        }
    }

    private readonly string? _adminPassword;
    private readonly string? _hostToken;

    /// <param name="hostToken">From <c>--host-token</c>: the client that hosts this server
    /// from its menu (<see cref="HostedServer"/>) proves it is the host with it.</param>
    public PlayerRegistry(string? adminPassword, string? hostToken = null)
    {
        _adminPassword = string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword;
        _hostToken = string.IsNullOrWhiteSpace(hostToken) ? null : hostToken;
        LoadAdmins();

        GD.Print(_adminPassword is null
            ? "[admin] no --admin-password set; /login is disabled"
            : "[admin] /login enabled");
        GD.Print($"[admin] {_adminIds.Count} persisted admin(s), {_legacyAdminNames.Count} by name until seen");
    }

    /// <summary>Everyone currently connected.</summary>
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values;

    /// <summary>Everyone with a body in the world: the console's players (<see cref="PlayerInfo.Bodiless"/>) left out.</summary>
    public IEnumerable<PlayerInfo> InWorld => _players.Values.Where(p => !p.Bodiless);

    /// <summary>The persisted admins as names, looked up now; a name not yet claimed by its player says so.</summary>
    public IReadOnlyCollection<string> PersistentAdmins =>
        _adminIds.Select(id => _ids?.NameOfId(id) ?? id)
            .Concat(_legacyAdminNames.Select(n => n + " (by name, not seen since)")).ToList();

    /// <summary>True when the server was started with a password, so /login can work.</summary>
    public bool LoginEnabled => _adminPassword is not null;

    public PlayerInfo? Find(long peerId) =>
        _players.TryGetValue(peerId, out var player) ? player : null;

    /// <summary>Case-insensitive lookup by display name, then by a unique prefix.</summary>
    public PlayerInfo? FindByName(string name)
    {
        var exact = _players.Values.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var matches = _players.Values
            .Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Registers a peer. Called on connect, before a name is known.</summary>
    public PlayerInfo Add(long peerId, bool bodiless = false)
    {
        var player = new PlayerInfo { PeerId = peerId, Name = $"Rider{peerId}", Bodiless = bodiless };
        _players[peerId] = player;
        return player;
    }

    public void Remove(long peerId) => _players.Remove(peerId);

    /// <summary>
    /// Applies a name the client asked for, sanitised and made unique, then grants admin if
    /// the name is on the persisted list.
    /// </summary>
    /// <returns>The name actually assigned, which may differ from the request.</returns>
    public string Rename(long peerId, string requested)
    {
        if (!_players.TryGetValue(peerId, out var player)) return requested;

        string clean = Sanitize(requested);
        if (clean.Length == 0) clean = $"Rider{peerId}";

        // A duplicate name would make every /kick and /tp ambiguous.
        string unique = clean;
        int suffix = 2;
        while (_players.Values.Any(p => p.PeerId != peerId
                   && string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)))
        {
            unique = $"{clean}{suffix++}";
        }

        player.Name = unique;
        return unique;
    }

    /// <summary>Checks a password and elevates the peer for this session only.</summary>
    public bool TryLogin(long peerId, string password)
    {
        if (_adminPassword is null) return false;
        if (!_players.TryGetValue(peerId, out var player)) return false;

        // Fixed-time comparison so a wrong password cannot be narrowed down by timing.
        if (!FixedTimeEquals(password, _adminPassword)) return false;

        player.IsAdmin = true;
        GD.Print($"[admin] peer {peerId} ({player.Name}) elevated by password");
        return true;
    }

    /// <summary>The hosting client's token: makes it an admin for this session.</summary>
    public bool TryClaimHost(long peerId, string token)
    {
        if (_hostToken is null) return false;
        if (!_players.TryGetValue(peerId, out var player)) return false;
        if (!FixedTimeEquals(token, _hostToken)) return false;

        player.IsAdmin = true;
        GD.Print($"[admin] peer {peerId} ({player.Name}) is the host");
        return true;
    }

    /// <summary>
    /// Adds a player to the persisted admins by their id, and elevates them if online. The name finds an online player,
    /// else the only player last seen under it. Null on success, else why not.
    /// </summary>
    public string? GrantAdmin(string name)
    {
        if (Resolve(name, out string? id, out string shown) is { } why) return why;
        if (!_adminIds.Add(id!)) return $"{shown} is already an operator.";
        SaveAdmins();
        if (_ids?.PeerOf(id!) is long peer and not 0 && Find(peer) is { } online) online.IsAdmin = true;
        GD.Print($"[admin] granted to {shown} ({id})");
        return null;
    }

    /// <summary>Removes a player from the persisted admins and drops their session privilege. Null on success, else why not.</summary>
    public string? RevokeAdmin(string name)
    {
        string shown = Sanitize(name);
        bool removed = _legacyAdminNames.Remove(shown);
        if (Resolve(name, out string? id, out string found) is null)
        {
            shown = found;
            removed |= _adminIds.Remove(id!);
            if (_ids?.PeerOf(id!) is long peer and not 0 && Find(peer) is { } online) online.IsAdmin = false;
        }
        if (!removed) return $"{shown} was not an operator.";
        SaveAdmins();
        return null;
    }

    /// <summary>The id behind a typed name: an online player's (once proven), else the one id last seen under it. Null when found, else why not.</summary>
    private string? Resolve(string name, out string? id, out string shown)
    {
        id = null;
        shown = Sanitize(name);
        if (shown.Length == 0) return "Usage: /admin add <name>";
        if (_ids is null) return "Admins are kept by player id, which this server does not track.";
        if (FindByName(shown) is { } online)
        {
            shown = online.Name;
            id = _ids.IdOf(online.PeerId);
            return id is null ? $"{shown} is not identified yet: try again in a moment." : null;
        }
        string typed = shown;
        var seen = _ids.LastNames.Where(p => string.Equals(p.Value, typed, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (seen.Count == 1) { id = seen[0].Key; shown = seen[0].Value; return null; }
        return seen.Count == 0 ? $"Nobody called {shown} has played here." : $"Several players went by {shown}: grant it while they are online.";
    }

    /// <summary>A peer has its id and name: a persisted admin gets their rights; an admin by name from an old file is claimed.</summary>
    private void OnIdentified(long peer, string id, string name)
    {
        if (_legacyAdminNames.Remove(name))
        {
            _adminIds.Add(id);
            SaveAdmins();
            GD.Print($"[admin] the admin rights of {name} now follow their id ({id})");
        }
        if (_adminIds.Contains(id) && Find(peer) is { } player) player.IsAdmin = true;
    }

    /// <summary>
    /// Strips anything that would break the chat display or make a name ambiguous, and caps
    /// the length. A client can send whatever it likes, so this is the boundary.
    /// </summary>
    public static string Sanitize(string name)
    {
        var text = new System.Text.StringBuilder(24);
        foreach (char c in name.Trim())
        {
            if (text.Length >= 20) break;
            if (char.IsLetterOrDigit(c) || c is '_' or '-') text.Append(c);
        }
        return text.ToString();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(a);
        var right = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void LoadAdmins()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(AdminFile)) return;

            using var file = Godot.FileAccess.Open(AdminFile, Godot.FileAccess.ModeFlags.Read);
            string json = file.GetAsText();
            using var doc = JsonDocument.Parse(json);
            // before #1359 the file was an array of names: kept by name until each player is next seen
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                Strings(doc.RootElement, _legacyAdminNames);
            else
            {
                if (doc.RootElement.TryGetProperty("ids", out var ids)) Strings(ids, _adminIds);
                if (doc.RootElement.TryGetProperty("names", out var names)) Strings(names, _legacyAdminNames);
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[admin] could not read {AdminFile}: {e.Message}");
        }
    }

    private void SaveAdmins()
    {
        try
        {
            Core.JsonStore.Save(AdminFile, new AdminList(_adminIds.ToArray(), _legacyAdminNames.ToArray()), Core.JsonStore.Indented);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[admin] could not write {AdminFile}: {e.Message}");
        }
    }

    private static void Strings(JsonElement array, HashSet<string> into)
    {
        if (array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) into.Add(s);
    }

    /// <summary><c>user://admins.json</c> since #1359: ids, and the names an older file left until their player is seen.</summary>
    private sealed record AdminList(string[] ids, string[] names);

    /// <summary>Reads "--admin-password &lt;pw&gt;" from the server command line.</summary>
    public static string? ParseAdminPassword() => CmdArgs.Value("--admin-password");

    /// <summary>Reads "--host-token &lt;t&gt;", set by <see cref="HostedServer"/>.</summary>
    public static string? ParseHostToken() => CmdArgs.Value("--host-token");

    /// <summary>Reads "--name &lt;n&gt;" from the client command line.</summary>
    public static string ParseRequestedName() => CmdArgs.Value("--name") ?? string.Empty;
}
