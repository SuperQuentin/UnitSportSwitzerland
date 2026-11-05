using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// A player who left, lying asleep where they were (#644). <see cref="Key"/> is their
/// <see cref="PlayerKey.Fingerprint"/> and never leaves the server; <see cref="Id"/> is what clients
/// know it by. <see cref="At"/> is where they lie, at the feet; <see cref="Yaw"/> their heading in GlobalPos's
/// offset axes there (grid north until G2 A1; the file keeps it from true north). <see cref="Rider"/> is the peer id they
/// had, which their jersey hue and (if they never chose one) their figure come from.
/// </summary>
public sealed record Sleeper(long Id, string Key, string Name, GlobalPos At, float Yaw,
    int Rider, long Outfit, int Appearance, int Headwear)
{
    /// <summary>
    /// The name for the tag (#1359): the player's name now, from their key; <see cref="Name"/>, the backup name from
    /// falling asleep, when the key resolves to nothing.
    /// </summary>
    public string NameShown(PlayerIds? ids) =>
        ids != null && Key.Length > 0 ? ids.NameOfId(Key, Name) : Name;
}

/// <summary>
/// The server's sleepers, one per identity, saved as JSON (Earth places and true north since G2 A2b, #1503). No Godot
/// here, so it is unit tested.
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

    /// <summary>The file before G2 A2b (#1503): the records as they are, LV95 and grid north. Read by <see cref="FromJson"/>'s migration only.</summary>
    private sealed class OldStore
    {
        public long Next { get; set; } = 1;
        public List<OldSleeper> Sleepers { get; set; } = new();
    }

    private sealed record OldSleeper(long Id, string Key, string Name, double E, double N, double Alt, float Yaw,
        int Rider, long Outfit, int Appearance, int Headwear)
    {
        public Sleeper ToSleeper() => new(Id, Key, Name, GlobalPos.FromLv95(E, N, Alt), Yaw, Rider, Outfit, Appearance, Headwear);
    }

    /// <summary>The file: <c>{"version": 2, "Next", "Sleepers": [{…, "at": {x, y, z}, "yawTrue"}]}</c>, Earth places and true north.</summary>
    private sealed class Store
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        public long Next { get; set; } = 1;
        public List<Saved> Sleepers { get; set; } = new();
    }

    private sealed class Saved
    {
        public long Id { get; set; }
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonPropertyName("at")] public GlobalPos At { get; set; }
        [JsonPropertyName("yawTrue")] public float YawTrue { get; set; }
        public int Rider { get; set; }
        public long Outfit { get; set; }
        public int Appearance { get; set; }
        public int Headwear { get; set; }

        public static Saved Of(Sleeper s) => new()
        {
            Id = s.Id, Key = s.Key, Name = s.Name, At = s.At, YawTrue = Lv95Grid.TrueYaw(s.At, s.Yaw),
            Rider = s.Rider, Outfit = s.Outfit, Appearance = s.Appearance, Headwear = s.Headwear,
        };

        public Sleeper ToSleeper() => new(Id, Key, Name, At, Lv95Grid.GridYaw(At, YawTrue), Rider, Outfit, Appearance, Headwear);
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>What is saved: also handed to <c>JsonStore.SaveAsync</c>.</summary>
    public object Snapshot() => new Store { Next = _nextId, Sleepers = _byKey.Values.OrderBy(s => s.Id).Select(Saved.Of).ToList() };

    public string ToJson() => JsonSerializer.Serialize(Snapshot(), Json);

    public static SleeperBook FromJson(string json) => FromJson(json, out _);

    /// <summary>The book a file holds; <paramref name="migrated"/> when it was the form before G2 A2b (the caller writes it back).</summary>
    public static SleeperBook FromJson(string json, out bool migrated)
    {
        var book = new SleeperBook();
        var root = JsonNode.Parse(json) as JsonObject;
        migrated = root != null && root["version"] == null;
        long next = 1;
        IEnumerable<Sleeper> sleepers = Array.Empty<Sleeper>();
        if (migrated)
        {
            var old = root.Deserialize<OldStore>() ?? new OldStore();
            (next, sleepers) = (old.Next, old.Sleepers.Select(s => s.ToSleeper()));
        }
        else if (root != null)
        {
            var store = root.Deserialize<Store>() ?? new Store();
            (next, sleepers) = (store.Next, store.Sleepers.Select(s => s.ToSleeper()));
        }
        foreach (var s in sleepers)
            if (!string.IsNullOrEmpty(s.Key)) book._byKey[s.Key] = s;
        book._nextId = Math.Max(next, book._byKey.Count > 0 ? book._byKey.Values.Max(s => s.Id) + 1 : 1);
        return book;
    }
}
