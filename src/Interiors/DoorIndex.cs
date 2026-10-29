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
    public readonly record struct Entry(DoorKey Key, Vector3 World, Vector3 Outward, float Width, float Height, BuildingKind Kind)
    {
        /// <summary>How this door's leaf moves (<see cref="DoorSpot.Hang"/>, #498).</summary>
        public DoorHang Hang { get; init; }

        /// <summary>Whether a ground vehicle is driven through it (<see cref="DoorSpot.Vehicle"/>).</summary>
        public bool Vehicle { get; init; }

        /// <summary>The road link of a garage door (#558), <see cref="LinkKind.None"/> on every other door.</summary>
        public GarageLink Link { get; init; }

        /// <summary>The ramp behind a garage door (#694) and, along the facade, which way it descends in the plan's X.</summary>
        public GarageRule.RampKind Ramp { get; init; }
        public int RampDir { get; init; }

        /// <summary>The building the door is on: the key of its plan and of the space behind it.</summary>
        public BuildingKey Building => Key.Building;

        /// <summary>The shop behind it, if any (a farm co-op is found by it, #494).</summary>
        public Loot.ShopType Shop { get; init; }

        /// <summary>Storeys above its building's ground floor it opens onto (<see cref="DoorSpot.Level"/>, #827): for probes and a bot's plans; reach reads <see cref="World"/>.</summary>
        public int Level { get; init; }
    }

    private static readonly Dictionary<TileId, Entry[]> Tiles = new();

    /// <summary>
    /// Each tile's doors' extent in the ground plane (world X/Z), so a search within a reach skips the
    /// tiles it cannot touch: a city's thousands of doors were measured every frame, on a Quest's CPU (#1112).
    /// </summary>
    private static readonly Dictionary<TileId, Rect2> Bounds = new();

    /// <summary>A door can be this much nearer than its centre: half a wide door plus the leaves' reach out in front.</summary>
    private const float BoundsSlack = 8f;

    private static Rect2 BoundsOf(Entry[] doors)
    {
        if (doors.Length == 0) return new Rect2();
        var r = new Rect2(doors[0].World.X, doors[0].World.Z, 0, 0);
        foreach (var e in doors) r = r.Expand(new Vector2(e.World.X, e.World.Z));
        return r;
    }

    /// <summary>Whether a tile's doors may be within <paramref name="reach"/> of <paramref name="at"/>.</summary>
    private static bool MayReach(TileId id, Vector3 at, float reach) =>
        !Bounds.TryGetValue(id, out var r) || r.Grow(reach + BoundsSlack).HasPoint(new Vector2(at.X, at.Z));

    public static void SetTile(TileId id, Vector3 tileOrigin, DoorSpot[] doors)
    {
        var list = new List<Entry>(doors.Length);
        foreach (var d in doors)
            if (d.Width > 0)
                list.Add(new Entry(d.KeyIn(id), tileOrigin + d.Position, d.Outward, d.Width, d.Height, d.Kind)
                {
                    Hang = d.Hang, Vehicle = d.Vehicle, Link = d.Link, Ramp = d.Ramp, RampDir = d.RampDir, Shop = d.Shop, Level = d.Level,
                });
        Tiles[id] = list.ToArray();
        Bounds[id] = BoundsOf(Tiles[id]);
    }

    public static void ClearTile(TileId id)
    {
        Tiles.Remove(id);
        Bounds.Remove(id);
    }

    /// <summary>The origin moved (#185): every door is now somewhere else in world space.</summary>
    public static void Shift(Core.OriginShift shift)
    {
        foreach (var (id, doors) in Tiles)
        {
            for (int i = 0; i < doors.Length; i++)
                doors[i] = doors[i] with { World = shift.Point(doors[i].World), Outward = shift.Direction(doors[i].Outward) };
            Bounds[id] = BoundsOf(doors);
        }
    }

    /// <summary>A given door, if its tile is drawn and the building has it.</summary>
    public static Entry? Find(DoorKey key)
    {
        if (!Tiles.TryGetValue(key.Tile, out var doors)) return null;
        foreach (var e in doors)
            if (e.Key == key) return e;
        return null;
    }

    public static void Clear()
    {
        Tiles.Clear();
        Bounds.Clear();
    }

    /// <summary>Every door currently drawn (for probes).</summary>
    public static IEnumerable<Entry> All() => Tiles.Values.SelectMany(t => t);

    /// <summary>
    /// The nearest door within <paramref name="reach"/> of a point, measured to the doorway rather
    /// than its centre line, and only from the outside: a player standing behind a wall must not
    /// open the door on its far side.
    /// </summary>
    public static Entry? Nearest(Vector3 at, float reach) => Nearest(at, reach, _ => true);

    /// <summary>
    /// As <see cref="Nearest(Vector3, float)"/>, among buildings of one kind, <b>its main door
    /// for choice</b>. Asking for "a barn" or "a garage" means the door that makes it one — the
    /// pair or the roll-up door a vehicle goes through — rather than the pedestrian side door it
    /// also has since #498. Any door of the kind will do when no main one is in reach, so a
    /// caller looking for a building of a kind still finds it.
    /// </summary>
    public static Entry? Nearest(Vector3 at, float reach, BuildingKind kind) =>
        Nearest(at, reach, e => e.Kind == kind && e.Key.Slot == 0)
        ?? Nearest(at, reach, e => e.Kind == kind);

    /// <summary>
    /// A door of one kind to walk or drive to, however far off: the nearest by plain distance,
    /// that building's <b>main</b> door for choice (a barn's pair, a garage's roll-up door).
    ///
    /// <para>
    /// This is target selection, not reach, so none of <see cref="Nearest(Vector3, float)"/>'s
    /// rules apply — those only let a door be worked from outside and within 2.5 m of the
    /// player's own level, which for a target hundreds of metres off over sloping ground makes
    /// the answer a matter of luck. A probe asking for "the nearest barn" needs the barn.
    /// </para>
    /// </summary>
    /// <summary>The nearest door a vehicle fits through, of any kind of building (#531).</summary>
    public static Entry? NearestVehicle(Vector3 at, float reach) =>
        Nearest(at, reach, e => e.Vehicle);

    public static Entry? NearestOfKind(Vector3 at, float reach, BuildingKind kind, bool vehicleOnly = false, Func<Entry, bool>? also = null)
    {
        Entry? best = null;
        float bestScore = float.MaxValue;
        foreach (var doors in Tiles.Values)
            foreach (var e in doors)
            {
                if (e.Kind != kind) continue;
                // Driving somewhere wants a door a vehicle fits through, and on an industrial site
                // that is never the main one: a works' slot 0 is the office door and its bays are
                // the extras (#528). Preferring slot 0 would send a lorry at a 1 m pedestrian door.
                if (vehicleOnly && !e.Vehicle) continue;
                if (also != null && !also(e)) continue;
                float d = e.World.DistanceTo(at);
                if (d > reach) continue;
                // a side door only when no main door of the kind is anywhere in reach — but with
                // vehicleOnly every candidate is already a door to drive through, and a bay is as
                // good as its neighbour, so the nearest wins
                float score = vehicleOnly ? d : d + (e.Key.Slot == 0 ? 0f : reach);
                if (score < bestScore) { bestScore = score; best = e; }
            }
        return best;
    }

    /// <summary>
    /// The doors of one kind that are drawn, nearest first, each with what rules it out from
    /// <paramref name="at"/> (null: nothing but the caller's reach). For a probe that found no
    /// door and has to say why, rather than waiting in silence (#507).
    /// </summary>
    public static IEnumerable<(Entry Door, float Distance, string? Rejected)> Candidates(Vector3 at, BuildingKind? kind) =>
        All().Where(e => kind == null || e.Kind == kind)
            .Select(e =>
            {
                var (distance, rule) = Measure(e, at);
                // the text is made here, for a probe's log, not for every door of every search
                string? rejected = rule switch
                {
                    Rule.Storey => $"{Mathf.Abs((at - e.World).Y):F0} m {((at - e.World).Y > 0 ? "below" : "above")} us",
                    Rule.Behind => "behind its wall",
                    _ => null,
                };
                return (Door: e, Distance: distance, Rejected: rejected);
            })
            .OrderBy(c => c.Distance);

    /// <summary>As <see cref="Nearest(Vector3, float)"/>, only the doors of one type of shop (a farm co-op, #494).</summary>
    public static Entry? Nearest(Vector3 at, float reach, Loot.ShopType shop) => Nearest(at, reach, e => e.Shop == shop);

    /// <summary>
    /// The nearest door a player on foot enters an interior by. <paramref name="deeper"/> gives a
    /// door extra reach straight out in front, where its open leaves stand.
    /// </summary>
    public static Entry? NearestEntrance(Vector3 at, float reach, Func<Entry, float>? deeper = null) =>
        Nearest(at, reach, _ => true, deeper);

    /// <summary>As <see cref="NearestEntrance(Vector3, float, Func{Entry, float}?)"/>, among the doors <paramref name="wanted"/> keeps (a VR hand: the doors whose opening it is in).</summary>
    public static Entry? NearestEntrance(Vector3 at, float reach, Func<Entry, float>? deeper, Func<Entry, bool> wanted) =>
        Nearest(at, reach, wanted, deeper);

    /// <summary>
    /// How far a door's sill may be from the feet for the door to be at the player's level (#827):
    /// half a storey. Of the doors in reach, one at the player's level is taken before one that is
    /// not (<see cref="Nearest(Vector3, float, Func{Entry, bool}, Func{Entry, float})"/>,
    /// <see cref="FacedEntrance"/>), so a door stacked over another (a site office's upper container
    /// over the one below) is told from it by the floor the player stands on, not by
    /// <see cref="MaxRise"/> happening to be shorter than the storey.
    /// </summary>
    public const float LevelBand = 1.3f;

    /// <summary>Whether a door's sill is at the level of a player whose feet are at <paramref name="feet"/> (<see cref="LevelBand"/>).</summary>
    public static bool AtLevel(Entry e, Vector3 feet) => Mathf.Abs(feet.Y - e.World.Y) <= LevelBand;

    /// <summary>
    /// As <see cref="NearestEntrance"/>, but the nearest door whose facade the player faces
    /// (<paramref name="look"/>, flattened) wins over a nearer one at their back: two doors facing
    /// each other across a gap, a building site's toilet cabins a step in front of its office on a
    /// real site (#822). With no such door in reach, the nearest one.
    /// </summary>
    public static Entry? FacedEntrance(Vector3 at, Vector3 look, float reach, Func<Entry, float>? deeper = null)
    {
        // faced before not, and within each, at the player's level before not (#827)
        Entry? facedLevel = null, faced = null, level = null, any = null;
        float facedLevelD = reach, facedD = reach, levelD = reach, anyD = reach;
        foreach (var (id, doors) in Tiles)
        {
            if (!MayReach(id, at, reach)) continue;
            foreach (var e in doors)
            {
                var (d, rejected) = Measure(e, at, deeper);
                if (rejected != Rule.None) continue;
                bool atLevel = AtLevel(e, at);
                // facing into it: the look against the door's outward normal
                bool isFaced = look.X * e.Outward.X + look.Z * e.Outward.Z < 0f;
                if (d < anyD) { anyD = d; any = e; }
                if (atLevel && d < levelD) { levelD = d; level = e; }
                if (isFaced && d < facedD) { facedD = d; faced = e; }
                if (isFaced && atLevel && d < facedLevelD) { facedLevelD = d; facedLevel = e; }
            }
        }
        return facedLevel ?? faced ?? level ?? any;
    }

    /// <summary>
    /// The nearest door a vehicle drives through (<see cref="DoorSpot.Vehicle"/>)
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
            if (!e.Vehicle) return false;
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
        // a door at the player's level before one that is not (#827), the nearest of each
        Entry? best = null, off = null;
        float bestD = reach, offD = reach;
        foreach (var (id, doors) in Tiles)
        {
            if (!MayReach(id, at, reach)) continue;
            foreach (var e in doors)
            {
                if (!wanted(e)) continue;
                var (d, rejected) = Measure(e, at, deeper);
                if (rejected != Rule.None) continue;
                if (AtLevel(e, at))
                {
                    if (d >= bestD) continue;
                    bestD = d;
                    best = e;
                }
                else if (d < offD)
                {
                    offD = d;
                    off = e;
                }
            }
        }
        return best ?? off;
    }

    /// <summary>A door more than this far above or below the player is a different storey's.</summary>
    public const float MaxRise = 2.5f;

    /// <summary>
    /// How far <paramref name="at"/> is from a door's opening, and what rules the door out from
    /// there: it is a storey away, or the player stands behind its wall. <see cref="Rule.None"/> means the
    /// door is usable and only the caller's reach can still rule it out. Shared by
    /// <see cref="Nearest(Vector3, float, Func{Entry, bool}, Func{Entry, float})"/> and
    /// <see cref="Candidates"/>, so a probe explains a miss by the rule that caused it.
    /// </summary>
    private static (float Distance, Rule Rejected) Measure(Entry e, Vector3 at, Func<Entry, float>? deeper = null)
    {
        var rel = at - e.World;
        var t = new Vector3(-e.Outward.Z, 0, e.Outward.X);
        float outward = rel.X * e.Outward.X + rel.Z * e.Outward.Z;
        float along = Mathf.Max(0, Mathf.Abs(rel.X * t.X + rel.Z * t.Z) - e.Width / 2);
        float depth = Mathf.Max(0, outward - (deeper?.Invoke(e) ?? 0f));
        float d = Mathf.Sqrt(along * along + depth * depth);
        // rel is the player relative to the door, so a positive Y means the door is the lower one
        if (Mathf.Abs(rel.Y) > MaxRise) return (d, Rule.Storey);
        if (outward < -0.3f) return (d, Rule.Behind);
        return (d, Rule.None);
    }

    /// <summary>What rules a door out in <see cref="Measure"/>: a code, so the hot searches build no text.</summary>
    private enum Rule { None, Storey, Behind }
}
