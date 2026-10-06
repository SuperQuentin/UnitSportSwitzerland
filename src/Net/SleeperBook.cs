using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UnitSport.Net;

/// <summary>
/// A player who left, lying asleep where they were (#644). <see cref="Key"/> is their
/// <see cref="PlayerKey.Fingerprint"/> and never leaves the server; <see cref="Id"/> is what clients
/// know it by. Position in LV95 plus altitude at the feet. <see cref="Rider"/> is the peer id they
/// had, which their jersey hue and (if they never chose one) their figure come from.
/// </summary>
public sealed record Sleeper(long Id, string Key, string Name, double E, double N, double Alt, float Yaw,
    int Rider, long Outfit, int Appearance, int Headwear);

/// <summary>
/// The server's sleepers, one per identity, saved as JSON. No Godot here, so it is unit tested.
/// </summary>
public sealed class SleeperBook
{
    private readonly Dictionary<string, Sleeper> _byKey = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public IReadOnlyCollection<Sleeper> All => _byKey.Values;

    /// <summary>Lays a player down; the one already asleep under that key (if any) is replaced and returned.</summary>
    public Sleeper Lay(Sleeper s, out Sleeper? replaced)
    {
        _byKey.Remove(s.Key, out replaced);
        var laid = s with { Id = _nextId++ };
        _byKey[s.Key] = laid;
        return laid;
    }

    /// <summary>Wakes the sleeper with this key: removes and returns it, or null if none sleeps.</summary>
    public Sleeper? Wake(string key) => _byKey.Remove(key, out var s) ? s : null;

    private sealed class Store
    {
        public long Next { get; set; } = 1;
        public List<Sleeper> Sleepers { get; set; } = new();
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>What is saved: also handed to <c>JsonStore.SaveAsync</c>.</summary>
    public object Snapshot() => new Store { Next = _nextId, Sleepers = _byKey.Values.OrderBy(s => s.Id).ToList() };

    public string ToJson() => JsonSerializer.Serialize(Snapshot(), Json);

    public static SleeperBook FromJson(string json)
    {
        var book = new SleeperBook();
        var store = JsonSerializer.Deserialize<Store>(json) ?? new Store();
        foreach (var s in store.Sleepers)
            if (!string.IsNullOrEmpty(s.Key)) book._byKey[s.Key] = s;
        book._nextId = Math.Max(store.Next, book._byKey.Count > 0 ? book._byKey.Values.Max(s => s.Id) + 1 : 1);
        return book;
    }
}
