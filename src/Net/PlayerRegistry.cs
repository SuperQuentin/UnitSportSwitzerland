using System.Text.Json;
using Godot;

namespace UnitSport.Net;

/// <summary>One connected peer, as the server sees them.</summary>
public sealed class PlayerInfo
{
    public required long PeerId { get; init; }

    /// <summary>Display name the client announced. Never trusted for anything but display.</summary>
    public required string Name { get; set; }

    /// <summary>True when this peer has proved it is an operator this session.</summary>
    public bool IsAdmin { get; set; }

    public DateTimeOffset JoinedAt { get; } = DateTimeOffset.UtcNow;

    public override string ToString() => IsAdmin ? $"{Name} (admin)" : Name;
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
/// <item>a persisted name list in <c>user://admins.json</c>, granted automatically on join;</item>
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
    private readonly HashSet<string> _persistentAdmins =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _adminPassword;

    public PlayerRegistry(string? adminPassword)
    {
        _adminPassword = string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword;
        LoadAdmins();

        GD.Print(_adminPassword is null
            ? "[admin] no --admin-password set; /login is disabled"
            : "[admin] /login enabled");
        GD.Print($"[admin] {_persistentAdmins.Count} persisted admin name(s)");
    }

    /// <summary>Everyone currently connected.</summary>
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values;

    /// <summary>Names that are granted admin automatically on join.</summary>
    public IReadOnlyCollection<string> PersistentAdmins => _persistentAdmins;

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
    public PlayerInfo Add(long peerId)
    {
        var player = new PlayerInfo { PeerId = peerId, Name = $"Rider{peerId}" };
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
        if (_persistentAdmins.Contains(unique)) player.IsAdmin = true;
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

    /// <summary>Adds a name to the persisted admin list and elevates them if online.</summary>
    public bool GrantAdmin(string name)
    {
        string clean = Sanitize(name);
        if (clean.Length == 0) return false;

        if (!_persistentAdmins.Add(clean)) return false;
        SaveAdmins();

        if (FindByName(clean) is { } online) online.IsAdmin = true;
        GD.Print($"[admin] granted to {clean}");
        return true;
    }

    /// <summary>Removes a name from the persisted list and drops their session privilege.</summary>
    public bool RevokeAdmin(string name)
    {
        string clean = Sanitize(name);
        bool removed = _persistentAdmins.Remove(clean);
        if (removed) SaveAdmins();

        if (FindByName(clean) is { } online) online.IsAdmin = false;
        return removed;
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
            var names = JsonSerializer.Deserialize<string[]>(json) ?? [];
            foreach (string name in names) _persistentAdmins.Add(name);
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
            using var file = Godot.FileAccess.Open(AdminFile, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(_persistentAdmins.ToArray()));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[admin] could not write {AdminFile}: {e.Message}");
        }
    }

    /// <summary>Reads "--admin-password &lt;pw&gt;" from the server command line.</summary>
    public static string? ParseAdminPassword()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--admin-password")
                return args[i + 1];
        return null;
    }

    /// <summary>Reads "--name &lt;n&gt;" from the client command line.</summary>
    public static string ParseRequestedName()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--name")
                return args[i + 1];
        return string.Empty;
    }
}
