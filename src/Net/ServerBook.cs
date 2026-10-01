using System.Text.Json;
using Godot;

namespace UnitSport.Net;

/// <summary>A server the player has joined or saved.</summary>
public sealed class SavedServer
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTime? LastPlayed { get; set; }
}

/// <summary>
/// The Multiplayer screen's address book, <c>user://servers.json</c>: favourites the player
/// added, and the last servers joined. Favourites are kept forever and listed first; recents are
/// capped at <see cref="MaxRecents"/>, newest first.
/// </summary>
public sealed class ServerBook
{
    private const string File = "user://servers.json";
    public const int MaxRecents = 10;

    private sealed class Stored
    {
        public int Version { get; set; } = 1;
        public List<SavedServer> Servers { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<SavedServer> _servers = new();

    /// <summary>Favourites first, then by when they were last played.</summary>
    public IReadOnlyList<SavedServer> Servers => _servers
        .OrderByDescending(s => s.Favorite)
        .ThenByDescending(s => s.LastPlayed ?? DateTime.MinValue)
        .ToList();

    public static ServerBook Load()
    {
        var book = new ServerBook();
        try
        {
            if (Godot.FileAccess.FileExists(File))
            {
                using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
                var stored = JsonSerializer.Deserialize<Stored>(f.GetAsText(), JsonOptions);
                if (stored != null) book._servers.AddRange(stored.Servers.Where(s => s.Endpoint.Length > 0));
            }
            else
            {
                // the old menu kept only the last address typed: bring it over once
                string last = Core.GameSettings.Current.LastHost;
                if (last.Length > 0 && last != "127.0.0.1" && last != "localhost")
                    book._servers.Add(new SavedServer { Name = last, Endpoint = last });
                book.Save();
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[servers] could not read {File}: {e.Message}");
        }
        return book;
    }

    public void Save()
    {
        try
        {
            using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(new Stored { Servers = _servers }, JsonOptions));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[servers] could not write {File}: {e.Message}");
        }
    }

    public SavedServer? Find(string endpoint) =>
        _servers.FirstOrDefault(s => Same(s.Endpoint, endpoint));

    /// <summary>Two ways of writing the same server ("host" and "host:7777") are one entry.</summary>
    public static bool Same(string a, string b)
    {
        var (ha, pa) = NetworkManager.ParseEndpoint(a.Trim());
        var (hb, pb) = NetworkManager.ParseEndpoint(b.Trim());
        return pa == pb && string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records a join: the entry is created or moved to the top of the recents.</summary>
    public void NotePlayed(string endpoint, string? name)
    {
        var s = Find(endpoint);
        if (s == null) _servers.Add(s = new SavedServer { Endpoint = endpoint.Trim(), Name = name ?? endpoint.Trim() });
        else if (!s.Favorite && !string.IsNullOrWhiteSpace(name)) s.Name = name;
        s.LastPlayed = DateTime.UtcNow;
        // drop the oldest recents past the cap; favourites never go
        foreach (var old in _servers.Where(x => !x.Favorite).OrderByDescending(x => x.LastPlayed ?? DateTime.MinValue).Skip(MaxRecents).ToList())
            _servers.Remove(old);
        Save();
    }

    public void AddOrUpdate(SavedServer server, string? previousEndpoint = null)
    {
        var existing = previousEndpoint != null ? Find(previousEndpoint) : Find(server.Endpoint);
        if (existing != null) { existing.Name = server.Name; existing.Endpoint = server.Endpoint; existing.Favorite = server.Favorite; }
        else _servers.Add(server);
        Save();
    }

    public void Remove(string endpoint)
    {
        _servers.RemoveAll(s => Same(s.Endpoint, endpoint));
        Save();
    }

    public void SetFavorite(string endpoint, bool favorite)
    {
        if (Find(endpoint) is { } s) { s.Favorite = favorite; Save(); }
    }

    /// <summary>"3 min ago", "yesterday", "12 Sep": how a last-played time reads in a list.</summary>
    public static string Ago(DateTime? when)
    {
        if (when is not { } w) return "never";
        var d = DateTime.UtcNow - w;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return w.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
    }
}
