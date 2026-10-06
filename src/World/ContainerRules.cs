using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// One vehicle or dropped item as the containers keep it (#689): its stable id, which incarnation
/// this is, what it is, where it stands (LV95) and its full spawn state, opaque here (a Godot
/// dictionary written with <c>GD.VarToStr</c> by <see cref="ObjectContainers"/>).
/// </summary>
/// <param name="Oid">Given when it first entered the world; never changes, through every sleep, wake, rename and restart.</param>
/// <param name="Gen">Bumped on every sleep and every wake: of two copies of one <paramref name="Oid"/>, the higher is the truth.</param>
/// <param name="Kind"><see cref="ContainerRecord.Vehicle"/> or <see cref="ContainerRecord.Item"/>.</param>
/// <param name="SavedAt">Server wall clock (unix seconds) of the last time this record was written.</param>
public sealed record ContainerRecord(long Oid, int Gen, string Kind, string Name, double E, double N, double SavedAt, string Data)
{
    public const string Vehicle = "v", Item = "i";

    public TileId Tile => TileId.FromLv95(E, N);
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
/// each written whole through <see cref="SaveQueue.WriteAtomic"/>, synchronously: the order of the
/// writes is what keeps an entity in exactly one place (<see cref="ContainerBook"/>), and a queued
/// write could land after a later one.
/// </summary>
public sealed class ContainerFileDisk : IContainerDisk
{
    private const string CheckpointName = "checkpoint.json";
    private readonly string _dir;

    public ContainerFileDisk(string dir) => _dir = dir;

    public IEnumerable<TileId> Tiles()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out int e) && int.TryParse(parts[1], out int n))
                yield return new TileId(e, n);
        }
    }

    public List<ContainerRecord> ReadTile(TileId id) => Read(Path.Combine(_dir, $"{id}.json"));

    public void WriteTile(TileId id, List<ContainerRecord> records) => Write(Path.Combine(_dir, $"{id}.json"), records);

    public List<ContainerRecord> ReadCheckpoint() => Read(Path.Combine(_dir, CheckpointName));

    public void WriteCheckpoint(List<ContainerRecord> records) => Write(Path.Combine(_dir, CheckpointName), records);

    private static List<ContainerRecord> Read(string path)
    {
        if (!File.Exists(path)) return new List<ContainerRecord>();
        try { return JsonSerializer.Deserialize<List<ContainerRecord>>(File.ReadAllText(path)) ?? new List<ContainerRecord>(); }
        catch (JsonException) { return new List<ContainerRecord>(); }
    }

    private static void Write(string path, List<ContainerRecord> records)
    {
        if (records.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        SaveQueue.WriteAtomic(path, JsonSerializer.Serialize(records));
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

    /// <summary>A dropped item left this long (seconds) is gone: the world is not a landfill.</summary>
    public const double ItemLifetime = 24 * 3600;

    private readonly IContainerDisk _disk;
    private readonly Dictionary<long, ContainerRecord> _live = new();
    private readonly Dictionary<TileId, int> _filedTiles = new();
    /// <summary>Name → oid of everything asleep: a vehicle by that name must not be placed again meanwhile.</summary>
    private readonly Dictionary<string, long> _filedNames = new();

    public ContainerBook(IContainerDisk disk) => _disk = disk;

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
            if (!after.TryGetValue(rec.Tile, out var list)) after[rec.Tile] = list = new List<ContainerRecord>();
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
        foreach (var (kind, max) in new[] { (ContainerRecord.Vehicle, MaxVehiclesPerTile), (ContainerRecord.Item, MaxItemsPerTile) })
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
            if (!byTile.TryGetValue(rec.Tile, out var list)) byTile[rec.Tile] = list = new List<ContainerRecord>();
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
    public static Dictionary<string, double> KeepAwake(IReadOnlyDictionary<string, double> saved, double now, Func<string, bool> filed)
    {
        var keep = new Dictionary<string, double>();
        foreach (var (key, since) in saved)
            if (now - since <= AwakeFor || filed(key)) keep[key] = since;
        return keep;
    }

    /// <summary>Moving less than this (m) between two looks is standing still.</summary>
    public const double StillWithin = 0.05;

    /// <summary>Tiles apart, the larger of the two axes (a ring).</summary>
    public static int Rings(TileId a, TileId b) => Math.Max(Math.Abs(a.E - b.E), Math.Abs(a.N - b.N));

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
            for (int de = -WakeRings; de <= WakeRings; de++)
                for (int dn = -WakeRings; dn <= WakeRings; dn++)
                {
                    var id = new TileId(p.E + de, p.N + dn);
                    if (hasFile(id) && !wake.Contains(id)) wake.Add(id);
                }
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
