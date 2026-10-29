namespace UnitSport.Net;

/// <summary>
/// Server: who a peer is, apart from what they are called (#1359). Saved data (bank accounts, owners, careers, farm
/// contracts, admins, high scores) is keyed by a peer's <b>id</b>, never by its name, and shows the name as it is now.
/// <list type="bullet">
/// <item><b>The id</b> (<see cref="IdOf"/>): <c>acct:&lt;user id&gt;</c> for a player whose account the server checked
/// (<see cref="Accounts.AccountGate"/>), else the proven PlayerKey fingerprint (<see cref="Sleepers.KeyOf"/>), else
/// <c>bot:&lt;name&gt;</c> for a server-run bot. Null while a player is not proven yet: nothing is saved under it.</item>
/// <item><b>The name</b> (<see cref="NameOfId"/>): an online peer's current name, else the last one seen, from
/// <c>user://players/names.json</c>. That file is only a cache for players who are away; nothing is keyed by it.</item>
/// <item><b>Legacy data</b>: before #1359 these systems keyed by name. <see cref="Identified"/> is raised once per
/// session, as soon as a peer has both its id and the name it chose; each system then moves an entry still under that
/// name to the id (<c>ClaimLegacy</c>), if the id has none yet. Claimed once, an entry follows the id, whatever name
/// the player takes later.</item>
/// </list>
/// No Godot here (unit tested in <c>PlayerIdsTests</c>): the cache file is read and written by <see cref="PlayerIdsCache"/>.
/// </summary>
public sealed class PlayerIds
{
    /// <summary>A peer's checked account's user id, or null.</summary>
    public Func<long, string?> AccountOf { get; set; } = _ => null;
    /// <summary>A peer's proven PlayerKey fingerprint, or null.</summary>
    public Func<long, string?> KeyOf { get; set; } = _ => null;
    /// <summary>A peer's current name, or null while it has chosen none (the "Rider&lt;peer&gt;" placeholder).</summary>
    public Func<long, string?> NameOf { get; set; } = _ => null;
    /// <summary>The peers connected now, bots included.</summary>
    public Func<IEnumerable<long>> Online { get; set; } = () => [];

    /// <summary>
    /// A peer has its id and its name: (peer, id, name). Raised once per peer and session, for the systems to claim
    /// what they still keep under that name (<c>ClaimLegacy</c>).
    /// </summary>
    public event Action<long, string, string>? Identified;

    /// <summary>The server's single instance, for code that has no wiring of its own; null on a client.</summary>
    public static PlayerIds? Server { get; set; }

    private readonly Dictionary<string, string> _lastName = new(StringComparer.Ordinal);
    private readonly HashSet<long> _identified = new();
    private readonly Action<IReadOnlyDictionary<string, string>>? _save;

    /// <param name="lastNames">The cache as saved: id to the last name seen.</param>
    /// <param name="save">Writes the cache after a change; null keeps it in memory.</param>
    public PlayerIds(IReadOnlyDictionary<string, string>? lastNames = null, Action<IReadOnlyDictionary<string, string>>? save = null)
    {
        if (lastNames != null) foreach (var (id, name) in lastNames) _lastName[id] = name;
        _save = save;
    }

    /// <summary>The last name seen for every id that has played here.</summary>
    public IReadOnlyDictionary<string, string> LastNames => _lastName;

    /// <summary>The id saved data hangs on, or null while the peer is not proven (save nothing then).</summary>
    public string? IdOf(long peer)
    {
        if (peer < 0) return NameOf(peer) is { Length: > 0 } bot ? "bot:" + bot : null;
        if (AccountOf(peer) is { Length: > 0 } account) return "acct:" + account;
        return KeyOf(peer) is { Length: > 0 } key ? key : null;
    }

    /// <summary>The connected peer with this id, or 0.</summary>
    public long PeerOf(string id)
    {
        foreach (long peer in Online())
            if (IdOf(peer) == id) return peer;
        return 0;
    }

    /// <summary>The name to show for an id: the online player's own, else the last one seen, else "Rider".</summary>
    public string NameOfId(string id)
    {
        if (PeerOf(id) is not 0 and var peer && NameOf(peer) is { Length: > 0 } now) return now;
        return _lastName.TryGetValue(id, out var last) ? last : "Rider";
    }

    /// <summary>Server: a peer proved its key, or chose or changed its name. Keeps the cache, raises <see cref="Identified"/> once.</summary>
    public void Refresh(long peer)
    {
        if (IdOf(peer) is not { } id || NameOf(peer) is not { Length: > 0 } name) return;
        if (!id.StartsWith("bot:", StringComparison.Ordinal) && (!_lastName.TryGetValue(id, out var was) || was != name))
        {
            _lastName[id] = name;
            _save?.Invoke(_lastName);
        }
        if (_identified.Add(peer)) Identified?.Invoke(peer, id, name);
    }

    /// <summary>Server: a peer left; its next session raises <see cref="Identified"/> again.</summary>
    public void Left(long peer) => _identified.Remove(peer);
}
