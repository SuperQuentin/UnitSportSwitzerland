using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/progress-store.md).

/// <summary>
/// One player's adventure progress (#1223): its sections, each decoded on first use by the
/// <see cref="SectionKey{T}"/> of the system that owns it. A section no running code asked for (a
/// system off under <c>--systems</c>, a newer build's) stays raw JSON and is written back unchanged.
/// A section saved by a newer version than the code's is kept raw too, and read as a read-only empty
/// value: a downgrade never overwrites newer data.
/// </summary>
public sealed class PlayerProgress
{
    private sealed class Slot
    {
        public JsonNode? Raw;
        public int RawVersion;
        public object? Value;
        public ISectionKey? Key;
        /// <summary>Decoded but not to be written: the file holds a newer version.</summary>
        public bool ReadOnly;
        public bool SaveDirty, SyncDirty;
        /// <summary>The JSON last written or read for the decoded value: tells a change made without <c>Touch</c>.</summary>
        public string? Baseline;
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>The identity: a <c>PlayerKey</c> fingerprint, <c>acct:&lt;user id&gt;</c>, <c>local</c> offline, <c>peer:&lt;id&gt;</c> unsaved.</summary>
    public string Key { get; }
    /// <summary>The name last used: a cache for when the key's name is not known (#1359), never an identity.</summary>
    public string LastName { get; set; } = "";

    /// <summary>The name to show: the player's own now from their key (<paramref name="nameOf"/>, for a key that is an id), else <see cref="LastName"/>.</summary>
    public string NameShown(Func<string, string?, string>? nameOf) =>
        nameOf != null && Net.PlayerIds.IsId(Key) ? nameOf(Key, LastName) : LastName;
    /// <summary>Unix seconds.</summary>
    public long Created { get; set; }
    /// <summary>Unix seconds, the last join.</summary>
    public long Seen { get; set; }

    public PlayerProgress(string key) => Key = key;

    /// <summary>The section, decoded (and migrated) on first use, or a new one; the same instance after that.</summary>
    public T Get<T>(SectionKey<T> key) where T : class, new()
    {
        if (!_slots.TryGetValue(key.Id, out var slot)) _slots[key.Id] = slot = new Slot();
        if (slot.Value is T value) return value;
        slot.Key = key;
        if (slot.Raw != null && slot.RawVersion > key.Version)
        {
            AdventureJson.Warning($"[adventure] {Key}: section '{key.Id}' is version {slot.RawVersion}, this build reads {key.Version}: kept as it is, read as empty");
            slot.ReadOnly = true;
            var empty = new T();
            slot.Value = empty;
            return empty;
        }
        T decoded;
        try { decoded = key.DecodeTyped(slot.Raw, slot.RawVersion); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
        {
            // a broken section is kept for a later build to look at, never thrown away by this one
            AdventureJson.Warning($"[adventure] {Key}: section '{key.Id}' unreadable ({e.Message}): kept as it is, read as empty");
            slot.ReadOnly = true;
            decoded = new T();
            slot.Value = decoded;
            return decoded;
        }
        slot.Value = decoded;
        // migrated: written back in the new shape at the next save
        if (slot.Raw != null && slot.RawVersion < key.Version) slot.SaveDirty = slot.SyncDirty = true;
        slot.Baseline = key.Encode(decoded)?.ToJsonString(AdventureJson.Options);
        return decoded;
    }

    /// <summary>Marks a section changed: the service saves it and sends it to its owner.</summary>
    public void Touch<T>(SectionKey<T> key) where T : class, new() => Touch(key.Id);

    public void Touch(string id)
    {
        if (!_slots.TryGetValue(id, out var slot) || slot.Value == null) return;
        if (slot.ReadOnly)
        {
            AdventureJson.Warning($"[adventure] {Key}: section '{id}' is read-only here (newer or unreadable data), the change is not kept");
            return;
        }
        slot.SaveDirty = slot.SyncDirty = true;
    }

    /// <summary>Sections touched since the last save.</summary>
    public bool NeedsSave
    {
        get
        {
            foreach (var s in _slots.Values) if (s.SaveDirty) return true;
            return false;
        }
    }

    /// <summary>Sections touched since the last sync (checked every frame by the service: no allocation).</summary>
    public bool NeedsSync
    {
        get
        {
            foreach (var s in _slots.Values) if (s.SyncDirty) return true;
            return false;
        }
    }

    /// <summary>Marks every section for the next sync (a player just loaded: its owner gets everything).</summary>
    public void SyncAll()
    {
        foreach (var s in _slots.Values) s.SyncDirty = true;
    }

    /// <summary>The ids of the sections touched since the last sync, cleared.</summary>
    public List<string> TakeSync()
    {
        var ids = new List<string>();
        foreach (var (id, slot) in _slots)
            if (slot.SyncDirty) { slot.SyncDirty = false; ids.Add(id); }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>Every section id the progress holds, decoded or raw.</summary>
    /// <summary>Whether a section exists here (a client's mirror reads without making an empty one, #1226).</summary>
    public bool Has(string id) => _slots.ContainsKey(id);

    public IEnumerable<string> Ids => _slots.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>A section's saved version: the code's for a decoded one, the file's for a raw or read-only one.</summary>
    public int VersionOf(string id) =>
        _slots.TryGetValue(id, out var s) ? s.Value != null && !s.ReadOnly && s.Key != null ? s.Key.Version : s.RawVersion : 0;

    /// <summary>A section's data as JSON (what is saved and synced), or null when absent.</summary>
    public JsonNode? DataOf(string id)
    {
        if (!_slots.TryGetValue(id, out var s)) return null;
        if (s.Value != null && !s.ReadOnly && s.Key != null) return s.Key.Encode(s.Value);
        return s.Raw?.DeepClone();
    }

    /// <summary>
    /// Sets a section from JSON: the client's mirror on <c>SectionSync</c>, and loading a file. A decoded
    /// value is dropped and decoded again on the next <see cref="Get{T}"/>.
    /// </summary>
    public void SetRaw(string id, int version, JsonNode? data)
    {
        _slots[id] = new Slot { Raw = data, RawVersion = version };
    }

    /// <summary>A save is under way: what was touched is now on its way to the disk.</summary>
    public void Saved()
    {
        foreach (var s in _slots.Values)
        {
            s.SaveDirty = false;
            if (s.Value != null && !s.ReadOnly && s.Key != null) s.Baseline = s.Key.Encode(s.Value)?.ToJsonString(AdventureJson.Options);
        }
    }

    /// <summary>
    /// Decoded sections whose value changed since they were read or saved without a <c>Touch</c>: a bug
    /// (the change would be lost or never reach the owner). The tests and the service's save check it.
    /// </summary>
    public List<string> Untouched()
    {
        var ids = new List<string>();
        foreach (var (id, s) in _slots)
            if (s.Value != null && !s.ReadOnly && s.Key != null && !s.SaveDirty
                && s.Key.Encode(s.Value)?.ToJsonString(AdventureJson.Options) != s.Baseline)
                ids.Add(id);
        return ids;
    }
}

/// <summary>
/// The adventure progress the server keeps (#1223): one <see cref="PlayerProgress"/> per player
/// identity, loaded one player at a time (one file each), never one big file. No Godot: the service
/// does the files, this does the shapes.
/// </summary>
public sealed class AdventureBook
{
    /// <summary>The envelope's layout version (each section migrates itself).</summary>
    public const int FileVersion = 1;

    private readonly Dictionary<string, PlayerProgress> _players = new(StringComparer.Ordinal);

    public IReadOnlyCollection<PlayerProgress> Loaded => _players.Values;

    /// <summary>The player's progress, made new when not loaded.</summary>
    public PlayerProgress For(string key)
    {
        if (!_players.TryGetValue(key, out var p)) _players[key] = p = new PlayerProgress(key);
        return p;
    }

    public bool TryGet(string key, out PlayerProgress progress) => _players.TryGetValue(key, out progress!);

    /// <summary>Puts a progress read from a file in the book (replacing one of that key).</summary>
    public PlayerProgress Add(PlayerProgress progress) => _players[progress.Key] = progress;

    /// <summary>Unloads a player (after its last save).</summary>
    public void Forget(string key) => _players.Remove(key);

    /// <summary>The file: <c>{ "v": 1, "key", "name", "created", "seen", "sections": { id: { "v", "data" } } }</c>.</summary>
    public static JsonObject ToNode(PlayerProgress p)
    {
        var sections = new JsonObject();
        foreach (string id in p.Ids)
            sections[id] = new JsonObject { ["v"] = p.VersionOf(id), ["data"] = p.DataOf(id) };
        return new JsonObject
        {
            ["v"] = FileVersion,
            ["key"] = p.Key,
            ["name"] = p.LastName,
            ["created"] = p.Created,
            ["seen"] = p.Seen,
            ["sections"] = sections,
        };
    }

    public static string ToJson(PlayerProgress p) => ToNode(p).ToJsonString(AdventureJson.Options);

    /// <summary>Reads a file; throws <see cref="JsonException"/> on a file that is not one.</summary>
    public static PlayerProgress FromJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        int v = root["v"]?.GetValue<int>() ?? 0;
        if (v > FileVersion) AdventureJson.Warning($"[adventure] file version {v}, this build knows {FileVersion}: read as far as it goes");
        string key = root["key"]?.GetValue<string>() ?? throw new JsonException("no key");
        var p = new PlayerProgress(key)
        {
            LastName = root["name"]?.GetValue<string>() ?? "",
            Created = root["created"]?.GetValue<long>() ?? 0,
            Seen = root["seen"]?.GetValue<long>() ?? 0,
        };
        if (root["sections"] is JsonObject sections)
            foreach (var (id, node) in sections)
                if (node is JsonObject s)
                    p.SetRaw(id, s["v"]?.GetValue<int>() ?? 0, s["data"]?.DeepClone());
        return p;
    }

    /// <summary>The file name of an identity: <c>acct:abc</c> -> <c>acct_abc.json</c>; nothing but letters, digits, <c>_</c> and <c>-</c>.</summary>
    public static string FileName(string key)
    {
        var chars = key.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '-' or '_')) chars[i] = '_';
        return new string(chars) + ".json";
    }
}
