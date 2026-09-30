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
    public readonly record struct Entry(BuildingKey Key, Vector3 World, Vector3 Outward, float Width, float Height, BuildingKind Kind)
    {
        /// <summary>A garage's drive-in room (tile-local), null for every other door.</summary>
        public GarageBay.Bay? Bay { get; init; }
        /// <summary>The tile's origin in world space, to put <see cref="Bay"/> there.</summary>
        public Vector3 TileOrigin { get; init; }
    }

    private static readonly Dictionary<TileId, Entry[]> Tiles = new();

    public static void SetTile(TileId id, Vector3 tileOrigin, DoorSpot[] doors)
    {
        var list = new List<Entry>(doors.Length);
        foreach (var d in doors)
            if (d.Width > 0)
                list.Add(new Entry(new BuildingKey(id.E, id.N, d.Index), tileOrigin + d.Position, d.Outward, d.Width, d.Height, d.Kind)
                    { Bay = d.Bay, TileOrigin = tileOrigin });
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
    public static Entry? Nearest(Vector3 at, float reach) => Nearest(at, reach, _ => true);

    /// <summary>As <see cref="Nearest(Vector3, float)"/>, only doors of buildings of one kind.</summary>
    public static Entry? Nearest(Vector3 at, float reach, BuildingKind kind) => Nearest(at, reach, e => e.Kind == kind);

    /// <summary>
    /// As <see cref="Nearest(Vector3, float, BuildingKind)"/>, and with <paramref name="orInside"/>
    /// also a garage whose drive-in bay holds the point: a car parked inside is at that garage.
    /// </summary>
    public static Entry? Nearest(Vector3 at, float reach, BuildingKind kind, bool orInside) =>
        orInside && GarageAround(at) is { } inside && inside.Kind == kind ? inside : Nearest(at, reach, kind);

    /// <summary>
    /// The nearest door a player on foot enters an interior by: every door but a garage's, which
    /// is walked (or driven) into for real. <paramref name="deeper"/> gives a door extra reach
    /// straight out in front, where its open leaves stand.
    /// </summary>
    public static Entry? NearestEntrance(Vector3 at, float reach, Func<Entry, float>? deeper = null) =>
        Nearest(at, reach, e => e.Kind != BuildingKind.Garage, deeper);

    /// <summary>The garage whose drive-in bay holds a world point, if any.</summary>
    public static Entry? GarageAround(Vector3 at)
    {
        foreach (var doors in Tiles.Values)
            foreach (var e in doors)
                if (e.Bay is { } bay && bay.Contains(at - e.TileOrigin, 0.3f)) return e;
        return null;
    }

    private static Entry? Nearest(Vector3 at, float reach, Func<Entry, bool> wanted, Func<Entry, float>? deeper = null)
    {
        Entry? best = null;
        float bestD = reach;
        foreach (var doors in Tiles.Values)
            foreach (var e in doors)
            {
                if (!wanted(e)) continue;
                var rel = at - e.World;
                if (Mathf.Abs(rel.Y) > 2.5f) continue;
                float outward = rel.X * e.Outward.X + rel.Z * e.Outward.Z;
                if (outward < -0.3f) continue;
                var t = new Vector3(-e.Outward.Z, 0, e.Outward.X);
                float along = Mathf.Max(0, Mathf.Abs(rel.X * t.X + rel.Z * t.Z) - e.Width / 2);
                float depth = Mathf.Max(0, outward - (deeper?.Invoke(e) ?? 0f));
                float d = Mathf.Sqrt(along * along + depth * depth);
                if (d < bestD) { bestD = d; best = e; }
            }
        return best;
    }
}
