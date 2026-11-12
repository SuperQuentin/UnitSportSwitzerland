using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions;

/// <summary>
/// Stateless hashing for placement. Every prop an occasion puts in the world is chosen from a
/// hash of <i>where</i> it is — a building key, a world-anchored grid cell — never from a shared
/// random stream, so every player sees the same pumpkin at the same door and a tile that unloads
/// and comes back is dressed exactly as it was.
/// </summary>
public static class OccasionHash
{
    public static uint Hash(int a, int b = 0, int c = 0, uint salt = 0)
    {
        unchecked
        {
            uint h = salt * 0x9E3779B9u ^ (uint)a * 0x85EBCA6Bu;
            h ^= (uint)b * 0xC2B2AE35u; h = (h << 13) | (h >> 19);
            h ^= (uint)c * 0x27D4EB2Fu; h *= 0x165667B1u;
            h ^= h >> 15; h *= 0x85EBCA77u; h ^= h >> 13; h *= 0xC2B2AE3Du; h ^= h >> 16;
            return h;
        }
    }

    /// <summary>0..1.</summary>
    public static float Unit(int a, int b = 0, int c = 0, uint salt = 0) =>
        (Hash(a, b, c, salt) & 0xFFFFFF) / 16777216f;
}

/// <summary>A mesh that occasions place many copies of, built once and shared by every tile.</summary>
public sealed class PropKind
{
    private readonly Func<ArrayMesh> _build;
    private ArrayMesh? _mesh;

    public PropKind(string name, Func<ArrayMesh> build, bool candle = true)
    {
        Name = name;
        _build = build;
        Candle = candle;
    }

    public string Name { get; }
    /// <summary>True: flickers like a flame. False: a steady bulb.</summary>
    public bool Candle { get; }
    public ArrayMesh Mesh => _mesh ??= _build();
}

/// <summary>A spot in the treat / gift hunt: tile-local, keyed so a claim survives an unload.</summary>
public readonly record struct HuntSpot(string Key, string OccasionId, Vector3 Local, string Label);

/// <summary>
/// What an occasion is given to dress one tile. Everything is main-thread data already loaded for
/// the tile: its doors, its roads, the height and land cover under a point, the towns nearby.
/// Positions handed back to <see cref="DecorBuilder"/> are <b>tile-local</b> — the frame of the
/// tile's node, of <see cref="DoorSpot.Position"/> and of <see cref="RoadSegment.Points"/>.
/// </summary>
public sealed class TileContext
{
    public required TileId Id { get; init; }
    /// <summary>World position of the tile node (its NW corner at altitude 0).</summary>
    public required Vector3 Origin { get; init; }
    public required WorldOrigin World { get; init; }
    public required DoorSpot[] Doors { get; init; }
    public required RoadTile? Roads { get; init; }
    public required Func<Vector3, float?> HeightAtWorld { get; init; }
    public required Func<Vector3, CoverClass?> CoverAtWorld { get; init; }
    public required IReadOnlyList<OccasionTowns.Town> Towns { get; init; }

    /// <summary>
    /// The tile node's whole frame in the world (#1729): on the earth world a leaf's stand-in tile stands in its
    /// anchor's frame (east, up and north there, the Earth's curve between it and the world's), so a tile-local point
    /// is not <see cref="Origin"/> plus itself. Unset: the flat tile's, a shift alone.
    /// </summary>
    public Transform3D? Frame { get; init; }
    /// <summary>The ground's tile (by its content name) under a world point, when the ground can say: the earth world's leaves.</summary>
    public Func<Vector3, TileId>? TileAtWorld { get; init; }

    private Transform3D? _toLocal;
    private Transform3D ToWorldFrame => Frame ?? new Transform3D(Basis.Identity, Origin);
    private Transform3D ToLocalFrame => _toLocal ??= ToWorldFrame.AffineInverse();

    /// <summary>The ground's height under a tile-local point, as a tile-local height.</summary>
    public float? HeightAt(Vector3 local)
    {
        var w = ToWorldFrame * (local with { Y = 0 });
        return HeightAtWorld(w) is { } h ? (ToLocalFrame * (w with { Y = h })).Y : null;
    }

    public CoverClass? CoverAt(Vector3 local) => CoverAtWorld(ToWorldFrame * (local with { Y = 0 }));

    /// <summary>LV95 of a tile-local point.</summary>
    public (double E, double N) Lv95(Vector3 local) => World.ToLv95(ToWorldFrame * local);

    /// <summary>Tile-local position of an LV95 point (altitude 0).</summary>
    public Vector3 Local(double e, double n) => ToLocalFrame * World.ToWorld(e, n, 0);

    /// <summary>
    /// Whether a tile-local point is on this tile, at least <paramref name="margin"/> inside it: by the ground's own
    /// tiles where it names them (a leaf spans ~611 m of its stand-in's square, its neighbours overlap it, #1729),
    /// else by the tile's square.
    /// </summary>
    public bool InTile(Vector3 local, float margin = 0f)
    {
        if (TileAtWorld is { } at)
        {
            if (at(ToWorldFrame * local) != Id) return false;
            if (margin <= 0f) return true;
            foreach (var o in Margins)
                if (at(ToWorldFrame * (local + o * margin)) != Id) return false;
            return true;
        }
        float size = (float)ChunkFormat.TileSizeM;
        return local.X >= margin && local.X < size - margin && local.Z > margin && local.Z <= size - margin;
    }

    private static readonly Vector3[] Margins = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];

    /// <summary>Whether an LV95 place lies on this tile (<see cref="InTile"/>), a leaf's stand-in included.</summary>
    public bool Holds(double e, double n) => InTile(Local(e, n));

    /// <summary>
    /// A stable identity for a door, for hashing and hunt keys. A building's extra doors (#498)
    /// are a million apart in the third number, well past any tile's building count, so a main
    /// door keeps exactly the identity it had when buildings had one door each — and the gift or
    /// the lantern at it stays where it was.
    /// </summary>
    public (int A, int B, int C) DoorKey(DoorSpot d) => (Id.E, Id.N, d.Index + d.Slot * 1_000_000);

    /// <summary>Distance in the ground plane from a tile-local point to the nearest drivable or walkable line.</summary>
    public float DistanceToRoad(Vector3 local, float giveUpBeyond = 50f)
    {
        if (Roads == null) return float.MaxValue;
        float best = giveUpBeyond;
        var p = new Vector2(local.X, local.Z);
        foreach (var s in Roads.Segments)
        {
            if (RoadFormat.IsAerial(s.Class)) continue;
            float half = s.Width * 0.5f;
            for (int i = 0; i + 1 < s.PointCount; i++)
            {
                var a = new Vector2(s.Points[i * 3], s.Points[i * 3 + 2]);
                var b = new Vector2(s.Points[i * 3 + 3], s.Points[i * 3 + 5]);
                var ab = b - a;
                float t = ab.LengthSquared() < 1e-6f ? 0 : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
                float d = (a + ab * t).DistanceTo(p) - half;
                if (d < best) best = d;
            }
        }
        return best;
    }

    /// <summary>
    /// A basis whose −Z points along <paramref name="facing"/> in the ground plane, turned by
    /// <paramref name="yawJitter"/> radians — a jack-o'-lantern looking out of its doorway. Built by
    /// hand rather than with LookAt, which errors on a degenerate direction (see CLAUDE.md).
    /// </summary>
    public static Basis Facing(Vector3 facing, float yawJitter = 0f, float scale = 1f)
    {
        var f = new Vector3(facing.X, 0, facing.Z);
        if (f.LengthSquared() < 1e-6f) f = Vector3.Forward;
        f = f.Normalized().Rotated(Vector3.Up, yawJitter);
        var z = -f;
        var x = Vector3.Up.Cross(z).Normalized();
        return new Basis(x, Vector3.Up, z).Scaled(new Vector3(scale, scale, scale));
    }
}

/// <summary>Collects one tile's props for <see cref="OccasionDecor"/> to turn into MultiMeshes.</summary>
public sealed class DecorBuilder
{
    internal readonly Dictionary<PropKind, List<(Transform3D Xf, float Seed, float Glow)>> Instances = new();
    internal readonly List<ArrayMesh> Meshes = new();
    internal readonly List<HuntSpot> Spots = new();
    internal readonly List<Rect2> Patches = new();

    public Func<string, bool> IsClaimed { get; init; } = _ => false;

    public void Add(PropKind kind, Transform3D local, float seed = 0f, float glow = 1f)
    {
        if (!Instances.TryGetValue(kind, out var list)) Instances[kind] = list = new();
        list.Add((local, seed, glow));
    }

    /// <summary>A one-off mesh in tile-local coordinates (a soil patch built from the terrain lattice).</summary>
    public void AddMesh(ArrayMesh mesh) => Meshes.Add(mesh);

    /// <summary>
    /// A hunt spot, drawn with <paramref name="kind"/> until this player claims it. Returns false
    /// (and draws nothing) when it has already been claimed.
    /// </summary>
    public bool AddHuntSpot(HuntSpot spot, PropKind kind, Transform3D local, float seed = 0f, float glow = 1f)
    {
        if (IsClaimed(spot.Key)) return false;
        Spots.Add(spot);
        Add(kind, local, seed, glow);
        return true;
    }

    /// <summary>A pumpkin patch, tile-local XZ, so gathering can tell a patch from a meadow.</summary>
    public void AddPatch(Rect2 localXz) => Patches.Add(localXz);
}

/// <summary>
/// Vertex colours for occasion props. <c>ps1_prop</c> reads vertex <b>alpha</b> as "this part is a
/// light" (dark by day, its own colour at night), and a plain <c>new Color(r, g, b)</c> has alpha
/// 1 — which lit the first jack-o'-lanterns up from stalk to base.
/// </summary>
public static class PropColors
{
    /// <summary>A surface that is not a light.</summary>
    public static Color Matte(float r, float g, float b) => new(r, g, b, 0f);

    /// <summary>A light: a candle, a bulb, a bauble catching the lights.</summary>
    public static Color Lamp(float r, float g, float b) => new(r, g, b, 1f);
}
