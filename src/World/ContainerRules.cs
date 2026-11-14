using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// One vehicle or dropped item as the containers keep it (#689): its stable id, which incarnation
/// this is, what it is, where it stands (LV95) and its full spawn state, opaque here (a Godot
/// dictionary written with <c>GD.VarToStr</c> by <see cref="ObjectContainers"/>).
/// </summary>
/// <param name="Oid">Given when it first entered the world; never changes, through every sleep, wake, rename and restart.</param>
/// <param name="Gen">Bumped on every sleep and every wake: of two copies of one <paramref name="Oid"/>, the higher is the truth.</param>
/// <param name="Kind"><see cref="ContainerRecord.Vehicle"/>, <see cref="ContainerRecord.Item"/> or <see cref="ContainerRecord.Radio"/>.</param>
/// <param name="SavedAt">Server wall clock (unix seconds) of the last time this record was written.</param>
public sealed record ContainerRecord(long Oid, int Gen, string Kind, string Name, double E, double N, double SavedAt, string Data)
{
    public const string Vehicle = "v", Item = "i", Radio = "r";

    public TileId Tile => TileId.FromLv95(E, N);

    /// <summary>The leaf it stands on, as its tile name (<see cref="LeafTileName"/>): its container under <c>--frame earth</c> (#1726).</summary>
    public TileId Leaf => LeafTileName.At(At);

    /// <summary>Where it stands, an Earth place on the ground (what an earth container file keeps, #1726).</summary>
    public GlobalPos At => GlobalPos.FromLv95(E, N, 0);

    /// <summary>Which container a record goes in: its LV95 tile under flat, its leaf under earth.</summary>
    public static Func<ContainerRecord, TileId> CellOf(bool earth) => earth ? r => r.Leaf : r => r.Tile;
}

/// <summary>Where the containers live: one file per tile, plus the checkpoint of what is live.</summary>
public interface IContainerDisk
{
    /// <summary>Every tile that has a file.</summary>
    IEnumerable<TileId> Tiles();

    /// <summary>A tile's records; empty when it has no file.</summary>
    List<ContainerRecord> ReadTile(TileId id);

    /// <summary>Replaces a tile's file, at once and whole (an empty list deletes it).</summary>
    void WriteTile(TileId id, List<ContainerRecord> records);

    List<ContainerRecord> ReadCheckpoint();

    /// <summary>Replaces the checkpoint, at once and whole.</summary>
    void WriteCheckpoint(List<ContainerRecord> records);
}

/// <summary>
/// The containers on disk: <c>&lt;dir&gt;/E_N.json</c> per tile and <c>&lt;dir&gt;/checkpoint.json</c>,
/// or under <c>--frame earth</c> (#1726) <c>&lt;dir&gt;/F_L14_X_Y.json</c> per leaf
/// (<see cref="LeafTileName.FileStem"/>), each record's place an Earth position (<see cref="EarthRecord"/>),
/// each written whole through <see cref="SaveQueue.WriteAtomic"/>, synchronously: the order of the
/// writes is what keeps an entity in exactly one place (<see cref="ContainerBook"/>), and a queued
/// write could land after a later one.
/// </summary>
public sealed class ContainerFileDisk : IContainerDisk
{
    private const string CheckpointName = "checkpoint.json";
    private readonly string _dir;
    private readonly bool _earth;

    public ContainerFileDisk(string dir, bool earth = false)
    {
        _dir = dir;
        _earth = earth;
    }

    public IEnumerable<TileId> Tiles()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
            if (LeafTileName.TryParseStem(Path.GetFileNameWithoutExtension(path), out var id)) yield return id;
    }

    private string PathOf(TileId id) => Path.Combine(_dir, LeafTileName.FileStem(id) + ".json");

    public List<ContainerRecord> ReadTile(TileId id) => Read(PathOf(id));

    public void WriteTile(TileId id, List<ContainerRecord> records) => Write(PathOf(id), records);

    public List<ContainerRecord> ReadCheckpoint() => Read(Path.Combine(_dir, CheckpointName));

    public void WriteCheckpoint(List<ContainerRecord> records) => Write(Path.Combine(_dir, CheckpointName), records);

    private List<ContainerRecord> Read(string path)
    {
        if (!File.Exists(path)) return new List<ContainerRecord>();
        try
        {
            string text = File.ReadAllText(path);
            return _earth ? EarthRecord.Parse(text) : JsonSerializer.Deserialize<List<ContainerRecord>>(text) ?? new List<ContainerRecord>();
        }
        catch (JsonException) { return new List<ContainerRecord>(); }
    }

    private void Write(string path, List<ContainerRecord> records)
    {
        if (records.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        SaveQueue.WriteAtomic(path, _earth ? EarthRecord.Text(records) : JsonSerializer.Serialize(records));
    }
}

/// <summary>
/// A container record as an earth container file holds it (#1726, G2 D2): <c>{"version": 3, "records": [...]}</c>, each
/// record's place an Earth position (<c>at</c>, ECEF, <see cref="GlobalPos"/>'s JSON), never LV95; the rest as
/// <see cref="ContainerRecord"/>. The record in memory keeps LV95 metres until D3 rewrites it.
/// </summary>
public sealed class EarthRecord
{
    public const int Version = 3;

    [JsonPropertyName("oid")] public long Oid { get; set; }
    [JsonPropertyName("gen")] public int Gen { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("at")] public GlobalPos At { get; set; }
    [JsonPropertyName("savedAt")] public double SavedAt { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";

    private sealed class Book
    {
        [JsonPropertyName("version")] public int Version { get; set; } = EarthRecord.Version;
        [JsonPropertyName("records")] public List<EarthRecord> Records { get; set; } = new();
    }

    public static EarthRecord Of(ContainerRecord r) =>
        new() { Oid = r.Oid, Gen = r.Gen, Kind = r.Kind, Name = r.Name, At = r.At, SavedAt = r.SavedAt, Data = r.Data };

    public ContainerRecord ToRecord()
    {
        var (e, n, _) = At.Lv95;
        return new ContainerRecord(Oid, Gen, Kind, Name, e, n, SavedAt, Data);
    }

    public static string Text(IEnumerable<ContainerRecord> records) =>
        JsonSerializer.Serialize(new Book { Records = records.Select(Of).ToList() });

    public static List<ContainerRecord> Parse(string json) =>
        (JsonSerializer.Deserialize<Book>(json) ?? new Book()).Records.Select(r => r.ToRecord()).ToList();
}

/// <summary>
/// The flat containers moved to earth (#1726, G2 D2): every record of the flat folder (each tile and the checkpoint)
/// filed by its place into the earth folder's leaves, and the woken slots whose owner is a leaf's
/// (<see cref="AwakeSlots.ToEarth"/>). Runs once: the earth folder's <see cref="Marker"/> says it ran, so a second start
/// changes nothing. The flat folder is only read, so a rollback to <c>--frame flat</c> finds it as it was. Two copies of
/// one entity keep the higher generation, as <see cref="ContainerBook.Load"/> would.
/// </summary>
public static class ContainerMigration
{
    public const string Marker = "earth.json";

    /// <summary>How many records it moved; -1 when the earth folder was migrated already.</summary>
    public static int FlatToEarth(string flatDir, string earthDir)
    {
        string marker = Path.Combine(earthDir, Marker);
        if (File.Exists(marker)) return -1;
        Directory.CreateDirectory(earthDir);
        var flat = new ContainerFileDisk(flatDir);
        var best = new Dictionary<long, ContainerRecord>();
        foreach (var r in flat.Tiles().ToList().SelectMany(flat.ReadTile).Concat(flat.ReadCheckpoint()))
            if (!best.TryGetValue(r.Oid, out var had) || r.Gen > had.Gen) best[r.Oid] = r;
        var earth = new ContainerFileDisk(earthDir, earth: true);
        foreach (var group in best.Values.GroupBy(r => r.Leaf))
        {
            var list = earth.ReadTile(group.Key);
            list.RemoveAll(r => best.ContainsKey(r.Oid));
            list.AddRange(group.OrderBy(r => r.Oid));
            ContainerBook.Cap(list);
            earth.WriteTile(group.Key, list);
        }
        string flatAwake = Path.Combine(flatDir, AwakeSlots.FileName), earthAwake = Path.Combine(earthDir, AwakeSlots.FileName);
        if (File.Exists(flatAwake) && !File.Exists(earthAwake))
        {
            try { SaveQueue.WriteAtomic(earthAwake, AwakeSlots.ToEarth(File.ReadAllText(flatAwake))); }
            catch (JsonException) { }
        }
        SaveQueue.WriteAtomic(marker, JsonSerializer.Serialize(new Dictionary<string, object> { ["version"] = EarthRecord.Version, ["from"] = "flat", ["records"] = best.Count }));
        return best.Count;
    }
}

/// <summary>
/// The book of every persistent vehicle and dropped item on the server (#689): live in the world,
/// or asleep in its tile's container. No Godot, so tier 0 can try every crash.
///
/// <para>
/// <b>An entity is in exactly one place.</b> Each has a stable <see cref="ContainerRecord.Oid"/> and a
/// generation (<see cref="ContainerRecord.Gen"/>), bumped on every sleep and wake. The writes come in
/// an order that leaves at worst two copies of one entity after a crash, never none, and
/// <see cref="Load"/> keeps the higher generation of any two:
/// <list type="bullet">
/// <item>Sleep writes the tile (gen + 1), then the checkpoint without it.</item>
/// <item>Wake writes the checkpoint with it (gen + 1), then the tile without it.</item>
/// <item>Taking one out of the world (a claim, a pick-up) writes the checkpoint before the game
/// hands it over.</item>
/// </list>
/// A hard crash loses only how far a live entity moved since the last <see cref="Update"/>.
/// </para>
/// </summary>
public sealed class ContainerBook
{
    /// <summary>Most vehicles one tile's container holds; past it the oldest are let go.</summary>
    public const int MaxVehiclesPerTile = 64;

    /// <summary>Most items one tile's container holds; past it the oldest are let go.</summary>
    public const int MaxItemsPerTile = 200;

    /// <summary>Most radios one tile's container holds; past it the oldest are let go.</summary>
    public const int MaxRadiosPerTile = 20;

    /// <summary>A dropped item left this long (seconds) is gone: the world is not a landfill. Radios and vehicles stay.</summary>
    public const double ItemLifetime = 24 * 3600;

    private readonly IContainerDisk _disk;
    private readonly Dictionary<long, ContainerRecord> _live = new();
    private readonly Dictionary<TileId, int> _filedTiles = new();
    /// <summary>Name → oid of everything asleep: a vehicle by that name must not be placed again meanwhile.</summary>
    private readonly Dictionary<string, long> _filedNames = new();

    private readonly Func<ContainerRecord, TileId> _cellOf;

    /// <param name="cellOf">Which container a record goes in (<see cref="ContainerRecord.CellOf"/>): its LV95 tile by default.</param>
    public ContainerBook(IContainerDisk disk, Func<ContainerRecord, TileId>? cellOf = null)
    {
        _disk = disk;
        _cellOf = cellOf ?? (r => r.Tile);
    }

    public int LiveCount => _live.Count;
    public int FiledCount => _filedNames.Count;
    public IReadOnlyCollection<TileId> FiledTiles => _filedTiles.Keys;
    public IEnumerable<string> FiledNamesList => _filedNames.Keys;

    public bool IsLive(long oid) => _live.ContainsKey(oid);
    public bool HasFile(TileId id) => _filedTiles.ContainsKey(id);
    public bool IsFiled(string name) => _filedNames.ContainsKey(name);
    public ContainerRecord? LiveRecord(long oid) => _live.GetValueOrDefault(oid);

    /// <summary>
    /// Starts the book from the disk: every copy of every entity in the tiles and the checkpoint,
    /// the highest generation of each kept, items past their lifetime dropped, caps applied. What
    /// was live at the last checkpoint (the server stopped, or crashed) is filed into its tile: it
    /// comes back when someone does. Returns how many entities are filed.
    /// </summary>
    public int Load(double now)
    {
        _live.Clear();
        _filedTiles.Clear();
        _filedNames.Clear();

        var best = new Dictionary<long, (ContainerRecord Rec, bool FromTile)>();
        var tiles = _disk.Tiles().Distinct().ToList();
        var before = new Dictionary<TileId, List<ContainerRecord>>();
        foreach (var id in tiles)
        {
            var list = _disk.ReadTile(id);
            before[id] = list;
            foreach (var r in list) Consider(best, r, fromTile: true);
        }
        var checkpoint = _disk.ReadCheckpoint();
        foreach (var r in checkpoint) Consider(best, r, fromTile: false);

        var after = new Dictionary<TileId, List<ContainerRecord>>();
        foreach (var (rec, _) in best.Values)
        {
            if (rec.Kind == ContainerRecord.Item && now - rec.SavedAt > ItemLifetime) continue;
            var cell = _cellOf(rec);
            if (!after.TryGetValue(cell, out var list)) after[cell] = list = new List<ContainerRecord>();
            list.Add(rec);
        }
        foreach (var (id, list) in after) Cap(list);

        // the tiles first, then the checkpoint: a crash in between leaves copies Load sorts out again
        foreach (var id in before.Keys.Union(after.Keys).ToList())
        {
            var was = before.GetValueOrDefault(id) ?? new List<ContainerRecord>();
            var now_ = after.GetValueOrDefault(id) ?? new List<ContainerRecord>();
            if (!Same(was, now_)) _disk.WriteTile(id, now_);
        }
        if (checkpoint.Count > 0) _disk.WriteCheckpoint(new List<ContainerRecord>());

        foreach (var (id, list) in after)
        {
            if (list.Count == 0) continue;
            _filedTiles[id] = list.Count;
            foreach (var r in list) _filedNames[r.Name] = r.Oid;
        }
        return _filedNames.Count;
    }

    private static void Consider(Dictionary<long, (ContainerRecord Rec, bool FromTile)> best, ContainerRecord r, bool fromTile)
    {
        // a tie is the same incarnation twice (a crash inside Load): the tile's copy is kept
        if (!best.TryGetValue(r.Oid, out var had) || r.Gen > had.Rec.Gen || r.Gen == had.Rec.Gen && fromTile && !had.FromTile)
            best[r.Oid] = (r, fromTile);
    }

    private static bool Same(List<ContainerRecord> a, List<ContainerRecord> b) =>
        a.Count == b.Count && a.OrderBy(r => r.Oid).SequenceEqual(b.OrderBy(r => r.Oid));

    /// <summary>Keeps at most the caps of each kind in one tile's list, the newest; the oldest are let go.</summary>
    public static int Cap(List<ContainerRecord> list)
    {
        int dropped = 0;
        foreach (var (kind, max) in new[] { (ContainerRecord.Vehicle, MaxVehiclesPerTile), (ContainerRecord.Item, MaxItemsPerTile), (ContainerRecord.Radio, MaxRadiosPerTile) })
        {
            int count = list.Count(r => r.Kind == kind);
            if (count <= max) continue;
            var oldest = list.Where(r => r.Kind == kind).OrderBy(r => r.SavedAt).ThenBy(r => r.Oid).Take(count - max).ToHashSet();
            dropped += list.RemoveAll(oldest.Contains);
        }
        return dropped;
    }

    /// <summary>A new entity in the world (dropped, parked, placed): on the checkpoint at once.</summary>
    public void Track(ContainerRecord record)
    {
        _live[record.Oid] = record;
        FlushCheckpoint();
    }

    /// <summary>A live entity's latest state, for the next checkpoint. Not written by itself.</summary>
    public void Update(ContainerRecord record)
    {
        if (_live.TryGetValue(record.Oid, out var had)) _live[record.Oid] = record with { Gen = had.Gen };
    }

    /// <summary>
    /// An entity left the world for good (picked up, claimed, cleared as a wreck): off the
    /// checkpoint before the game hands it to anyone. Only the node of that <paramref name="name"/>:
    /// one respawned under another name with the same oid (a thrower who left) stays.
    /// </summary>
    public bool Untrack(long oid, string name)
    {
        if (!_live.TryGetValue(oid, out var had) || had.Name != name) return false;
        _live.Remove(oid);
        FlushCheckpoint();
        return true;
    }

    /// <summary>Writes what is live now, whole.</summary>
    public void FlushCheckpoint() => _disk.WriteCheckpoint(_live.Values.OrderBy(r => r.Oid).ToList());

    /// <summary>
    /// Puts live entities to sleep: each into its tile's container (generation + 1), the tiles
    /// written first, then the checkpoint without them. The caller frees their nodes only after
    /// this returned, and only those it returns: one not live, or whose name another filed entity
    /// holds, is skipped and stays in the world.
    /// </summary>
    public HashSet<long> Sleep(IReadOnlyList<ContainerRecord> records, double now)
    {
        var byTile = new Dictionary<TileId, List<ContainerRecord>>();
        foreach (var r in records)
        {
            if (!_live.TryGetValue(r.Oid, out var had)) continue;
            if (_filedNames.TryGetValue(r.Name, out long other) && other != r.Oid) continue;
            var rec = r with { Gen = had.Gen + 1, SavedAt = now };
            var cell = _cellOf(rec);
            if (!byTile.TryGetValue(cell, out var list)) byTile[cell] = list = new List<ContainerRecord>();
            list.Add(rec);
        }
        var filed = new HashSet<long>();
        foreach (var (id, adds) in byTile)
        {
            var list = _disk.ReadTile(id);
            var oids = adds.Select(a => a.Oid).ToHashSet();
            var before = list.ToList();
            list.RemoveAll(r => oids.Contains(r.Oid));
            list.AddRange(adds);
            Cap(list);
            _disk.WriteTile(id, list);
            // what the cap let go is gone: its name is free again
            var kept = list.Select(r => r.Oid).ToHashSet();
            foreach (var r in before.Concat(adds))
                if (!kept.Contains(r.Oid) && _filedNames.TryGetValue(r.Name, out long oid) && oid == r.Oid) _filedNames.Remove(r.Name);
            if (list.Count > 0) _filedTiles[id] = list.Count;
            else _filedTiles.Remove(id);
            foreach (var r in list) _filedNames[r.Name] = r.Oid;
            foreach (var a in adds)
            {
                _live.Remove(a.Oid);
                filed.Add(a.Oid);
            }
        }
        if (filed.Count > 0) FlushCheckpoint();
        return filed;
    }

    /// <summary>
    /// Wakes a tile's container: its records come back live (generation + 1), the checkpoint
    /// written with them first, then the tile without them. The caller spawns what it returns. An
    /// entity already live (a copy a crash left) is not woken twice.
    /// </summary>
    public List<ContainerRecord> Wake(TileId id, double now)
    {
        var woken = new List<ContainerRecord>();
        if (!_filedTiles.ContainsKey(id)) return woken;
        var list = _disk.ReadTile(id);
        foreach (var r in list)
        {
            if (_live.ContainsKey(r.Oid)) continue;
            if (r.Kind == ContainerRecord.Item && now - r.SavedAt > ItemLifetime) continue;
            woken.Add(r with { Gen = r.Gen + 1 });
        }
        foreach (var r in woken) _live[r.Oid] = r;
        if (woken.Count > 0) FlushCheckpoint();
        _disk.WriteTile(id, new List<ContainerRecord>());
        _filedTiles.Remove(id);
        foreach (var r in list)
            if (_filedNames.TryGetValue(r.Name, out long oid) && oid == r.Oid) _filedNames.Remove(r.Name);
        return woken;
    }

    /// <summary>A woken entity came back under another name (its own was taken meanwhile).</summary>
    public void Renamed(long oid, string name)
    {
        if (_live.TryGetValue(oid, out var had)) _live[oid] = had with { Name = name };
    }
}

/// <summary>
/// A woken dormant slot (#499) as the server keeps it across restarts (#689): when it woke, and where
/// its car stood and what it was, when the slot was known then, so a car put back in its bay can
/// become scenery again (<see cref="ContainerRules.BackInBay"/>). No pose: it can only stay awake.
/// </summary>
public sealed record AwakeSlot(double Since, double? E = null, double? N = null, float Yaw = 0f, int KindId = -1, int Train = 0, float Load = 0f);

/// <summary>
/// <c>awake.json</c>, the woken slots (#499) on disk (#1503, G2 A2b): <c>{"version": 2, "slots": {key: {since, at,
/// yawTrue, kindId, train, load}}}</c>, the bay an Earth place on the ground and its heading from true north. Before,
/// the file was the bare dictionary of <see cref="AwakeSlot"/> (LV95 <c>E</c>, <c>N</c>, grid <c>Yaw</c>): still read,
/// and the caller writes it back.
/// </summary>
public static class AwakeSlots
{
    /// <summary>The file in the containers' folder.</summary>
    public const string FileName = "awake.json";

    /// <summary>The earth file's version (#1726, G2 D2): its owners are leaves' (a stand-in tile, a leaf's building), never an LV95 tile's.</summary>
    public const int EarthVersion = 3;

    private sealed class Saved
    {
        [JsonPropertyName("since")] public double Since { get; set; }
        [JsonPropertyName("at")] public GlobalPos? At { get; set; }
        [JsonPropertyName("yawTrue")] public float YawTrue { get; set; }
        [JsonPropertyName("kindId")] public int KindId { get; set; } = -1;
        [JsonPropertyName("train")] public int Train { get; set; }
        [JsonPropertyName("load")] public float Load { get; set; }
    }

    private sealed class SavedFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        [JsonPropertyName("slots")] public Dictionary<string, Saved> Slots { get; set; } = new();
    }

    public static string ToJson(IReadOnlyDictionary<string, AwakeSlot> awake, bool earth = false)
    {
        var file = new SavedFile { Version = earth ? EarthVersion : 2 };
        foreach (var (key, s) in awake)
        {
            GlobalPos? at = s.E is { } e && s.N is { } n ? GlobalPos.FromLv95(e, n, 0) : null;
            file.Slots[key] = new Saved { Since = s.Since, At = at, YawTrue = at is { } g ? Lv95Grid.TrueYaw(g, s.Yaw) : s.Yaw, KindId = s.KindId, Train = s.Train, Load = s.Load };
        }
        return JsonSerializer.Serialize(file);
    }

    /// <summary>
    /// A flat <c>awake.json</c> (any version) as the earth folder's (#1726, version 3): the slots whose owner is a leaf's
    /// kept (a run under <c>--frame earth</c> before the flip wrote them there), the LV95 owners dropped. A car park's
    /// ordinal counts its bays in the tile, a yard's owner is the tile's building index: neither names a slot of a leaf,
    /// so the slot fills its bay again and the car a player woke stays theirs, under its own name (the plan's section 2).
    /// </summary>
    public static string ToEarth(string flatJson)
    {
        var kept = new Dictionary<string, AwakeSlot>();
        foreach (var (key, slot) in FromJson(flatJson, out _))
        {
            var p = key.Split('|')[0].Split('_');
            if (p.Length >= 2 && int.TryParse(p[0], out int e) && int.TryParse(p[1], out int n) && LeafTileName.TryLeaf(new TileId(e, n), out _))
                kept[key] = slot;
        }
        return ToJson(kept, earth: true);
    }

    /// <summary>The slots a file holds; <paramref name="old"/> when it is the form before A2b (write it back).</summary>
    public static Dictionary<string, AwakeSlot> FromJson(string json, out bool old)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        old = root != null && root["slots"] == null && root["version"] == null;
        if (root == null) return new();
        if (old) return root.Deserialize<Dictionary<string, AwakeSlot>>() ?? new();
        var file = root.Deserialize<SavedFile>() ?? new SavedFile();
        var slots = new Dictionary<string, AwakeSlot>();
        foreach (var (key, s) in file.Slots)
        {
            if (s.At is { } at)
            {
                var (e, n, _) = at.Lv95;
                slots[key] = new AwakeSlot(s.Since, e, n, Lv95Grid.GridYaw(at, s.YawTrue), s.KindId, s.Train, s.Load);
            }
            else slots[key] = new AwakeSlot(s.Since, null, null, s.YawTrue, s.KindId, s.Train, s.Load);
        }
        return slots;
    }
}

/// <summary>The decisions of the containers (#689), pure: which tiles wake, what may sleep.</summary>
public static class ContainerRules
{
    /// <summary>A container wakes when a player is within this many tiles of it.</summary>
    public const int WakeRings = 2;

    /// <summary>An entity sleeps once no player has been within this many tiles of it (hysteresis over <see cref="WakeRings"/>).</summary>
    public const int SleepRings = 3;

    /// <summary>Seconds with nobody within <see cref="SleepRings"/> before an entity sleeps (a check shortens it).</summary>
    public static double SleepAfter { get; set; } = 60;

    /// <summary>Seconds an entity must have stood still (its server copy) before it may sleep (a check shortens it).</summary>
    public static double StillFor { get; set; } = 10;

    /// <summary>A woken dormant slot (#499) stays awake across restarts this long, s: then its bay may fill again.</summary>
    public const double AwakeFor = 7 * 24 * 3600;

    /// <summary>
    /// The awake slots kept across a restart: those woken within <see cref="AwakeFor"/>, and any whose
    /// vehicle is asleep in a container under the slot's name whatever its age.
    /// </summary>
    public static Dictionary<string, AwakeSlot> KeepAwake(IReadOnlyDictionary<string, AwakeSlot> saved, double now, Func<string, bool> filed)
    {
        var keep = new Dictionary<string, AwakeSlot>();
        foreach (var (key, slot) in saved)
            if (now - slot.Since <= AwakeFor || filed(key)) keep[key] = slot;
        return keep;
    }

    /// <summary>A car back in its bay stands within this of the slot's spot, flat m...</summary>
    public const double BayReach = 0.3;

    /// <summary>...and within this of its heading, degrees: one that re-sleeps anywhere else would teleport in front of whoever looks (#497).</summary>
    public const double BayYawDeg = 5;

    /// <summary>
    /// Whether a vehicle is the slot's car back in its bay, so it may become scenery again rather
    /// than sleep in a container: the slot's kind, trailer and load, where the slot put it, facing
    /// the same way, and <paramref name="pristine"/> (undamaged, untuned, doors shut, nothing playing):
    /// anything else would be silently repaired or undone by turning back into the slot's own look.
    /// </summary>
    public static bool BackInBay(AwakeSlot slot, double e, double n, float yaw, int kindId, int train, float load, bool pristine) =>
        BackInBay(slot, GlobalPos.FromLv95(e, n, 0), yaw, kindId, train, load, pristine);

    /// <inheritdoc cref="BackInBay(AwakeSlot, double, double, float, int, int, float, bool)"/>
    public static bool BackInBay(AwakeSlot slot, GlobalPos at, float yaw, int kindId, int train, float load, bool pristine)
    {
        if (!pristine || slot.E is not { } se || slot.N is not { } sn) return false;
        if (kindId != slot.KindId || train != slot.Train || Math.Abs(load - slot.Load) > 0.05f) return false;
        if (at.HorizontalDistanceTo(GlobalPos.FromLv95(se, sn, 0)) > BayReach) return false;
        double turn = Math.IEEERemainder(yaw - slot.Yaw, 2 * Math.PI);
        return Math.Abs(turn) <= BayYawDeg * Math.PI / 180;
    }

    /// <summary>Moving less than this (m) between two looks is standing still.</summary>
    public const double StillWithin = 0.05;

    /// <summary>Tiles apart, the larger of the two axes (a ring); leaves apart under earth, across a face edge too (<see cref="LeafTileName.Rings"/>).</summary>
    public static int Rings(TileId a, TileId b) => LeafTileName.Rings(a, b, SleepRings + 1);

    /// <summary>True when some player tile is within <paramref name="rings"/> of <paramref name="tile"/>.</summary>
    public static bool Near(TileId tile, IReadOnlyList<TileId> players, int rings)
    {
        for (int i = 0; i < players.Count; i++)
            if (Rings(tile, players[i]) <= rings) return true;
        return false;
    }

    /// <summary>The tiles whose containers should wake for these players: every filed tile within <see cref="WakeRings"/>.</summary>
    public static List<TileId> ToWake(IReadOnlyList<TileId> players, Func<TileId, bool> hasFile)
    {
        var wake = new List<TileId>();
        foreach (var p in players)
            foreach (var id in LeafTileName.Around(p, WakeRings))
                if (hasFile(id) && !wake.Contains(id)) wake.Add(id);
        return wake;
    }

    /// <summary>
    /// Whether an entity may sleep now: nobody near for <see cref="SleepAfter"/>, still for
    /// <see cref="StillFor"/>, and nothing holding it in the world (<paramref name="busy"/>: claimed, a
    /// pick-up in flight, burning, carried, carrying).
    /// </summary>
    public static bool MaySleep(double lonelyFor, double stillFor, bool busy) =>
        !busy && lonelyFor >= SleepAfter && stillFor >= StillFor;

    /// <summary>The next seconds-alone of an entity after <paramref name="step"/>: reset whenever a player is within <see cref="SleepRings"/>.</summary>
    public static double Lonely(double was, double step, TileId at, IReadOnlyList<TileId> players) =>
        Near(at, players, SleepRings) ? 0 : was + step;

    /// <summary>The next seconds-still of an entity: reset when it moved more than <see cref="StillWithin"/>.</summary>
    public static double Still(double was, double step, double movedM) => movedM > StillWithin ? 0 : was + step;
}
