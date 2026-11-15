using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Which facade doors the interior plans gave no doorway (#1766, #1767), per tile: a building's extra door
/// (slot 1 and up, #498) the plan could fit no way in for reads as locked, and a door that never opens is not
/// drawn. The main door (slot 0) always has one. <see cref="DoorWarden"/> plans the buildings near the player
/// and records here what it finds; the tile builds leave the locked doors out (<see cref="IsLocked"/>).
/// <para>
/// A building's record carries a print of what its plan was made from (<see cref="InteriorLayout.Matches"/>'s
/// terms): a building whose data changed is planned again. Records are kept per tile in
/// <c>&lt;dir&gt;/E_N.txt</c>, one line a building, <c>index print slot slot…</c>, under a directory named for
/// the plan version, so a new generator plans everything again. Thread-safe: tile builds read it on workers.
/// </para>
/// </summary>
public sealed class DoorLocks
{
    private readonly Dictionary<TileId, Dictionary<int, Entry>> _tiles = new();
    private readonly object _gate = new();

    /// <summary>Where the tiles' records are kept (null: memory only).</summary>
    public string? Dir { get; }

    /// <summary>A tile whose record gained or changed a locked door: its doors should be drawn again.</summary>
    public event Action<TileId>? Changed;

    public DoorLocks(string? dir) => Dir = dir;

    /// <summary>The one the game's tile builds read (null: none, every door drawn).</summary>
    public static DoorLocks? Shared { get; set; }

    private readonly record struct Entry(uint Print, int[] Locked);

    /// <summary>Whether this building's doors were judged against a plan of what it is now.</summary>
    public bool Known(BuildingKey building, uint print)
    {
        lock (_gate)
            return Tile(building.Tile).TryGetValue(building.Index, out var e) && e.Print == print;
    }

    /// <summary>
    /// Whether a door is known to have no doorway inside, for a building of this print. Unknown buildings and
    /// main doors are never locked: what has not been planned yet is drawn.
    /// </summary>
    public bool IsLocked(DoorKey door, uint print)
    {
        if (door.Slot == 0) return false;
        lock (_gate)
            return Tile(door.Tile).TryGetValue(door.Index, out var e) && e.Print == print && Array.IndexOf(e.Locked, door.Slot) >= 0;
    }

    /// <summary>Whether a door is on record as having no doorway, whatever its building's print (for checks).</summary>
    public bool Recorded(DoorKey door)
    {
        lock (_gate)
            return Tile(door.Tile).TryGetValue(door.Index, out var e) && Array.IndexOf(e.Locked, door.Slot) >= 0;
    }

    /// <summary>Whether a tile has any locked door on record (the filter's quick way out).</summary>
    public bool AnyLocked(TileId tile)
    {
        lock (_gate)
            return Tile(tile).Values.Any(e => e.Locked.Length > 0);
    }

    /// <summary>
    /// A building's verdict: the slots its plan has no doorway for. Kept, and on disk at once; <see cref="Changed"/>
    /// when its locked doors differ from what the tile drew before.
    /// </summary>
    public void Record(BuildingKey building, uint print, IEnumerable<int> lockedSlots)
    {
        var locked = lockedSlots.Where(s => s > 0).Distinct().OrderBy(s => s).ToArray();
        bool changed;
        string? text = null;
        lock (_gate)
        {
            var tile = Tile(building.Tile);
            var before = tile.TryGetValue(building.Index, out var e) ? e.Locked : [];
            changed = !before.SequenceEqual(locked);
            tile[building.Index] = new Entry(print, locked);
            if (Dir != null) text = Format(tile);
        }
        if (text != null)
        {
            try
            {
                Directory.CreateDirectory(Dir!);
                string path = PathOf(building.Tile);
                string tmp = $"{path}.{Guid.NewGuid():N}.part";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (changed) Changed?.Invoke(building.Tile);
    }

    private string PathOf(TileId tile) => Path.Combine(Dir!, $"{tile.E}_{tile.N}.txt");

    /// <summary>A tile's records, read from disk the first time it is asked for. Under the gate.</summary>
    private Dictionary<int, Entry> Tile(TileId id)
    {
        if (_tiles.TryGetValue(id, out var t)) return t;
        t = new Dictionary<int, Entry>();
        if (Dir != null)
        {
            try
            {
                string path = PathOf(id);
                if (File.Exists(path)) Parse(File.ReadAllText(path), t);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _tiles[id] = t;
        return t;
    }

    /// <summary>One line a building, in index order: <c>index print slot slot…</c>.</summary>
    private static string Format(Dictionary<int, Entry> tile)
    {
        var sb = new StringBuilder();
        foreach (var (index, e) in tile.OrderBy(x => x.Key))
        {
            sb.Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Print.ToString(CultureInfo.InvariantCulture));
            foreach (int s in e.Locked) sb.Append(' ').Append(s.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The lines <see cref="Format"/> wrote; a line it cannot read is skipped (that building is planned again).</summary>
    private static void Parse(string text, Dictionary<int, Entry> into)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || !uint.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint print)) continue;
            var slots = new List<int>();
            bool ok = true;
            for (int i = 2; i < p.Length && ok; i++)
                if (int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0) slots.Add(s);
                else ok = false;
            if (ok) into[index] = new Entry(print, slots.ToArray());
        }
    }

    /// <summary>
    /// The print of what a plan is made from, as <see cref="InteriorLayout.Matches"/> compares it: the building's
    /// triangles and heights, its group's print, its house layout. FNV-1a, the same on every machine.
    /// </summary>
    public static uint Print(int triangles, float minY, float maxY, string group, string house)
    {
        string s = string.Create(CultureInfo.InvariantCulture, $"{triangles}|{minY:F2}|{maxY:F2}|{group}|{house}");
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
