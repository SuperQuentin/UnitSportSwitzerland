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
    public readonly record struct Entry(BuildingKey Key, Vector3 World, Vector3 Outward, float Width, float Height, BuildingKind Kind);

    private static readonly Dictionary<TileId, Entry[]> Tiles = new();

    public static void SetTile(TileId id, Vector3 tileOrigin, DoorSpot[] doors)
    {
        var list = new List<Entry>(doors.Length);
        foreach (var d in doors)
            if (d.Width > 0)
                list.Add(new Entry(new BuildingKey(id.E, id.N, d.Index), tileOrigin + d.Position, d.Outward, d.Width, d.Height, d.Kind));
        Tiles[id] = list.ToArray();
    }

    public static void ClearTile(TileId id) => Tiles.Remove(id);

    /// <summary>The origin moved (#185): every door is now somewhere else in world space.</summary>
    public static void Shift(Core.OriginShift shift)
    {
        foreach (var doors in Tiles.Values)
            for (int i = 0; i < doors.Length; i++)
                doors[i] = doors[i] with { World = shift.Point(doors[i].World), Outward = shift.Direction(doors[i].Outward) };
    }

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
    /// The doors of one kind that are drawn, nearest first, each with what rules it out from
    /// <paramref name="at"/> (null: nothing but the caller's reach). For a probe that found no
    /// door and has to say why, rather than waiting in silence (#507).
    /// </summary>
    public static IEnumerable<(Entry Door, float Distance, string? Rejected)> Candidates(Vector3 at, BuildingKind? kind) =>
        All().Where(e => kind == null || e.Kind == kind)
            .Select(e =>
            {
                var (distance, rejected) = Measure(e, at);
                return (Door: e, Distance: distance, Rejected: rejected);
            })
            .OrderBy(c => c.Distance);

    /// <summary>
    /// The nearest door a player on foot enters an interior by. <paramref name="deeper"/> gives a
    /// door extra reach straight out in front, where its open leaves stand.
    /// </summary>
    public static Entry? NearestEntrance(Vector3 at, float reach, Func<Entry, float>? deeper = null) =>
        Nearest(at, reach, _ => true, deeper);

    /// <summary>
    /// The nearest door a vehicle drives through (<see cref="BuildingFootprint.VehicleDoor"/>)
    /// within <paramref name="reach"/> in front of it, at most <paramref name="halfAngle"/>
    /// radians off square: a vehicle heading at a garage or a barn, not driving past one.
    /// </summary>
    public static Entry? VehicleDoorAhead(Vector3 at, Vector3 heading, float reach, float halfAngle)
    {
        var h = new Vector2(heading.X, heading.Z);
        if (h.LengthSquared() < 1e-6f) return null;
        h = h.Normalized();
        float cos = Mathf.Cos(halfAngle);
        return Nearest(at, reach, e =>
        {
            if (!BuildingFootprint.VehicleDoor(e.Kind)) return false;
            var into = new Vector2(-e.Outward.X, -e.Outward.Z);
            if (h.Dot(into) < cos) return false;
            // and aimed at the opening, not at the wall beside it: where the heading meets the facade
            var rel = new Vector2(at.X - e.World.X, at.Z - e.World.Z);
            float outward = -rel.Dot(into);
            if (outward < 0.5f) return false;
            var t = new Vector2(-into.Y, into.X);
            float along = rel.Dot(t) + outward / h.Dot(into) * h.Dot(t);
            return Mathf.Abs(along) < e.Width / 2 + 1f;
        });
    }

    private static Entry? Nearest(Vector3 at, float reach, Func<Entry, bool> wanted, Func<Entry, float>? deeper = null)
    {
        Entry? best = null;
        float bestD = reach;
        foreach (var doors in Tiles.Values)
            foreach (var e in doors)
            {
                if (!wanted(e)) continue;
                var (d, rejected) = Measure(e, at, deeper);
                if (rejected != null || d >= bestD) continue;
                bestD = d;
                best = e;
            }
        return best;
    }

    /// <summary>A door more than this far above or below the player is a different storey's.</summary>
    private const float MaxRise = 2.5f;

    /// <summary>
    /// How far <paramref name="at"/> is from a door's opening, and what rules the door out from
    /// there: it is a storey away, or the player stands behind its wall. A null reason means the
    /// door is usable and only the caller's reach can still rule it out. Shared by
    /// <see cref="Nearest(Vector3, float, Func{Entry, bool}, Func{Entry, float})"/> and
    /// <see cref="Candidates"/>, so a probe explains a miss by the rule that caused it.
    /// </summary>
    private static (float Distance, string? Rejected) Measure(Entry e, Vector3 at, Func<Entry, float>? deeper = null)
    {
        var rel = at - e.World;
        var t = new Vector3(-e.Outward.Z, 0, e.Outward.X);
        float outward = rel.X * e.Outward.X + rel.Z * e.Outward.Z;
        float along = Mathf.Max(0, Mathf.Abs(rel.X * t.X + rel.Z * t.Z) - e.Width / 2);
        float depth = Mathf.Max(0, outward - (deeper?.Invoke(e) ?? 0f));
        float d = Mathf.Sqrt(along * along + depth * depth);
        // rel is the player relative to the door, so a positive Y means the door is the lower one
        if (Mathf.Abs(rel.Y) > MaxRise) return (d, $"{Mathf.Abs(rel.Y):F0} m {(rel.Y > 0 ? "below" : "above")} us");
        if (outward < -0.3f) return (d, "behind its wall");
        return (d, null);
    }
}
