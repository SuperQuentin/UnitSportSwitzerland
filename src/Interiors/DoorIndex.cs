using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Every front door currently drawn, in world space, for "is there a door in reach". Filled by
/// <c>ChunkManager</c> as building meshes commit and emptied as tiles unload, so it only ever
/// holds doors the player can actually see. Main thread only.
/// </summary>
public static class DoorIndex
{
    public readonly record struct Entry(BuildingKey Key, Vector3 World, Vector3 Outward, float Width);

    private static readonly Dictionary<TileId, Entry[]> Tiles = new();

    public static void SetTile(TileId id, Vector3 tileOrigin, DoorSpot[] doors)
    {
        var list = new List<Entry>(doors.Length);
        foreach (var d in doors)
            if (d.Width > 0)
                list.Add(new Entry(new BuildingKey(id.E, id.N, d.Index), tileOrigin + d.Position, d.Outward, d.Width));
        Tiles[id] = list.ToArray();
    }

    public static void ClearTile(TileId id) => Tiles.Remove(id);

    /// <summary>A given building's door, if its tile is drawn and it has one.</summary>
    public static Entry? Find(BuildingKey key)
    {
        if (!Tiles.TryGetValue(key.Tile, out var doors)) return null;
        foreach (var e in doors)
            if (e.Key == key) return e;
        return null;
    }

    public static void Clear() => Tiles.Clear();

    /// <summary>Every door currently drawn (for probes).</summary>
    public static IEnumerable<Entry> All() => Tiles.Values.SelectMany(t => t);

    /// <summary>
    /// The nearest door within <paramref name="reach"/> of a point, measured to the doorway rather
    /// than its centre line, and only from the outside: a player standing behind a wall must not
    /// open the door on its far side.
    /// </summary>
    public static Entry? Nearest(Vector3 at, float reach)
    {
        Entry? best = null;
        float bestD = reach;
        foreach (var doors in Tiles.Values)
            foreach (var e in doors)
            {
                var rel = at - e.World;
                if (Mathf.Abs(rel.Y) > 2.5f) continue;
                float outward = rel.X * e.Outward.X + rel.Z * e.Outward.Z;
                if (outward < -0.3f) continue;
                var t = new Vector3(-e.Outward.Z, 0, e.Outward.X);
                float along = Mathf.Max(0, Mathf.Abs(rel.X * t.X + rel.Z * t.Z) - e.Width / 2);
                float d = Mathf.Sqrt(along * along + outward * outward);
                if (d < bestD) { bestD = d; best = e; }
            }
        return best;
    }
}
