using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// Stadiums on the cube leaves (#1723, G2 D2). A stadium is placed once on the Earth (<see cref="Placement"/>): its centre
/// spot on the ground the leaves draw there, its axes the pitch's true azimuth in the east-up-south at the spot. Every
/// leaf it reaches sweeps the whole bowl in its own anchor frame and keeps the triangles whose middle lies on it (the
/// cube's own <see cref="CubeKey.FromEcef"/>), so each triangle belongs to exactly one leaf: a bowl across a leaf edge is
/// neither cut off nor drawn twice (the pits' #1685). The data's buildings and trees inside a facade are dropped as a
/// leaf is read (<see cref="Clip"/>), as <see cref="StadiumClip"/> does for an LV95 tile. Any thread.
/// </summary>
public sealed class StadiumLeaves
{
    /// <summary>How far a leaf's anchor lies from any point of it at most, m (a level-14 leaf is ~610 m a side).</summary>
    private const double LeafReach = 600;

    /// <summary>The ground's ellipsoidal height under an Earth point (its own height ignored); null where nothing is known.</summary>
    private readonly Func<EcefPos, double?> _groundAt;

    private readonly ConditionalWeakTable<StadiumSpec, Placement> _placed = new();

    public StadiumLeaves(Func<EcefPos, double?> groundAt) => _groundAt = groundAt;

    /// <summary>On no ground: every stadium's pitch at sea level (the check's, and before any leaf is read).</summary>
    public static readonly StadiumLeaves Level = new(_ => null);

    /// <summary>
    /// The ground from the leaves' own drawn meshes (<see cref="CubeGeometry.MeshHeightAt"/>), read as the leaf holding the
    /// point has it. Only asked while a leaf's collision is prepared, after its content is made, so it never waits on itself.
    /// </summary>
    public static StadiumLeaves OnLeaves(Func<CubeKey, CubeLeafContent?> leaves)
    {
        var meshes = new ConcurrentDictionary<CubeKey, Lazy<(float[]? V, int N)>>();
        var order = new ConcurrentQueue<CubeKey>();
        return new StadiumLeaves(p =>
        {
            var key = CubeKey.FromEcef(p, LeafFrames.LeafLevel);
            bool added = false;
            var lazy = meshes.GetOrAdd(key, k =>
            {
                added = true;
                return new Lazy<(float[]?, int)>(() => leaves(k)?.Heights is { } h
                    ? (CubeGeometry.Vertices(k, h.Bits, h.Heights), (1 << h.Bits) + 1)
                    : (null, 0));
            });
            if (added)
            {
                // a stadium reaches a handful of leaves: keep the last few meshes, not every one ever asked
                order.Enqueue(key);
                while (meshes.Count > 8 && order.TryDequeue(out var old)) meshes.TryRemove(old, out _);
            }
            var (v, n) = lazy.Value;
            if (v == null) return null;
            var anchor = key.Anchor;
            var (x, _, z) = anchor.ToLocal(p);
            var hint = (n / 2, n / 2);
            double y = CubeGeometry.MeshHeightAt(v, n, x, z, ref hint);
            return double.IsNaN(y) ? null : Ellipsoid.Wgs84.ToGeodetic(anchor.ToEcef(x, y, z)).H;
        });
    }

    /// <summary>A stadium on the Earth: its pitch's centre spot on the ground and its frame's axes there, in ECEF.</summary>
    public sealed class Placement
    {
        public required StadiumSpec Spec { get; init; }

        /// <summary>The centre spot on the ground.</summary>
        public required EcefPos Origin { get; init; }

        /// <summary>Unit axes: u across the pitch (east when the true azimuth is 0), v along it, up.</summary>
        public required EcefPos U { get; init; }
        public required EcefPos V { get; init; }
        public required EcefPos Up { get; init; }

        /// <summary>The ground round the bowl over the pitch, at a point of the stadium frame (the gates' passages).</summary>
        public Func<Vector2, float>? Outside { get; init; }

        /// <summary>A point of the stadium frame on the Earth.</summary>
        public EcefPos At(Vector2 uv, float y) => new(
            Origin.X + U.X * uv.X + V.X * uv.Y + Up.X * y,
            Origin.Y + U.Y * uv.X + V.Y * uv.Y + Up.Y * y,
            Origin.Z + U.Z * uv.X + V.Z * uv.Y + Up.Z * y);

        /// <summary>An Earth point in the stadium frame: (u, y, v).</summary>
        public Vector3 Local(EcefPos p)
        {
            double dx = p.X - Origin.X, dy = p.Y - Origin.Y, dz = p.Z - Origin.Z;
            return new Vector3((float)(dx * U.X + dy * U.Y + dz * U.Z), (float)(dx * Up.X + dy * Up.Y + dz * Up.Z),
                (float)(dx * V.X + dy * V.Y + dz * V.Z));
        }

        /// <summary>The stadium frame in a leaf's anchor frame (x east, y up, z south), worked in doubles.</summary>
        public Func<Vector2, float, Vector3> In(EnuFrame anchor)
        {
            var (ox, oy, oz) = anchor.ToLocal(Origin);
            var (ux, uy, uz) = anchor.DirectionToLocal(U);
            var (vx, vy, vz) = anchor.DirectionToLocal(V);
            var (wx, wy, wz) = anchor.DirectionToLocal(Up);
            return (uv, y) => new Vector3((float)(ox + ux * uv.X + vx * uv.Y + wx * y), (float)(oy + uy * uv.X + vy * uv.Y + wy * y),
                (float)(oz + uz * uv.X + vz * uv.Y + wz * y));
        }

        /// <summary>The stadium frame as a transform of a leaf's anchor frame (the rig's, the occluder's).</summary>
        public Transform3D TransformIn(EnuFrame anchor)
        {
            var f = In(anchor);
            var o = f(Vector2.Zero, 0);
            return new Transform3D(new Basis(f(Vector2.Right, 0) - o, f(Vector2.Zero, 1) - o, f(Vector2.Down, 0) - o), o);
        }
    }

    /// <summary>A stadium's placement on this ground, worked out once.</summary>
    public Placement Place(StadiumSpec s) => _placed.GetValue(s, Make);

    private Placement Make(StadiumSpec s)
    {
        var spot = s.Centre;
        var g = spot.Geodetic;
        double h = _groundAt(spot.ToEcef()) ?? g.H;
        var enu = new EnuFrame(g with { H = h });
        double a = s.AzimuthDeg * Math.PI / 180;
        // east-up-south: u = east cos a + south sin a, v = −east sin a + south cos a (south = −north)
        double c = Math.Cos(a), sn = Math.Sin(a);
        var u = new EcefPos(enu.East.X * c - enu.North.X * sn, enu.East.Y * c - enu.North.Y * sn, enu.East.Z * c - enu.North.Z * sn);
        var v = new EcefPos(-enu.East.X * sn - enu.North.X * c, -enu.East.Y * sn - enu.North.Y * c, -enu.East.Z * sn - enu.North.Z * c);
        var outside = new ConcurrentDictionary<Vector2, float>();
        Placement? p = null;
        p = new Placement
        {
            Spec = s, Origin = enu.Origin, U = u, V = v, Up = enu.Up,
            // the same answer on every leaf: asked once a point, of the same ground
            Outside = uv => outside.GetOrAdd(uv, at => _groundAt(p!.At(at, 0)) is { } gh ? (float)(gh - h) : 0f),
        };
        return p;
    }

    /// <summary>Whether a stadium can reach a leaf at all (its farthest part from the centre spot is under <see cref="StadiumCatalog.Reach"/>).</summary>
    public static bool Reaches(StadiumSpec s, CubeKey leaf)
    {
        // the level placement's spot: worked out once a stadium, its ground never read
        var c = Level.Place(s).Origin;
        var o = leaf.Anchor.Origin;
        double dx = c.X - o.X, dy = c.Y - o.Y, dz = c.Z - o.Z;
        double r = StadiumCatalog.Reach + LeafReach;
        return dx * dx + dy * dy + dz * dz < r * r;
    }

    /// <summary>Whether a point of a leaf's anchor frame lies on that leaf.</summary>
    public static bool OnLeaf(CubeKey leaf, EnuFrame anchor, Vector3 p) =>
        CubeKey.FromEcef(anchor.ToEcef(p.X, p.Y, p.Z), leaf.Level) == leaf;

    /// <summary>
    /// The stadiums' parts on a leaf, in its anchor frame: the triangles (drawn and collided) whose middle is on it.
    /// Null when no stadium reaches it or none of one lies on it.
    /// </summary>
    public StadiumBuilder.StadiumBuild? Build(CubeKey leaf, bool mesh, bool collision, StadiumSpec? only = null)
    {
        if (!mesh && !collision) return null;
        var anchor = leaf.Anchor;
        List<Vector3>? verts = null, faces = null;
        List<Color>? colors = null;
        List<int>? indices = null;
        int capacity = 0;
        foreach (var s in StadiumCatalog.All)
        {
            if (only != null && s != only || !Reaches(s, leaf)) continue;
            var p = Place(s);
            var built = StadiumBuilder.BuildIn(s, p.In(anchor), p.Outside, mesh, collision);
            if (OnLeaf(leaf, anchor, p.In(anchor)(Vector2.Zero, 0))) capacity += built.Capacity;
            if (built.Mesh is { } m)
            {
                verts ??= new(); colors ??= new(); indices ??= new();
                var remap = new Dictionary<int, int>();
                for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                {
                    int i0 = m.Indices[k], i1 = m.Indices[k + 1], i2 = m.Indices[k + 2];
                    if (!OnLeaf(leaf, anchor, (m.Vertices[i0] + m.Vertices[i1] + m.Vertices[i2]) / 3f)) continue;
                    foreach (int i in (ReadOnlySpan<int>)[i0, i1, i2])
                    {
                        if (!remap.TryGetValue(i, out int j))
                        {
                            j = verts.Count;
                            remap[i] = j;
                            verts.Add(m.Vertices[i]);
                            colors.Add(m.Colors[i]);
                        }
                        indices.Add(j);
                    }
                }
            }
            faces ??= new();
            var f = built.Faces;
            for (int k = 0; k + 2 < f.Length; k += 3)
                if (OnLeaf(leaf, anchor, (f[k] + f[k + 1] + f[k + 2]) / 3f))
                {
                    faces.Add(f[k]); faces.Add(f[k + 1]); faces.Add(f[k + 2]);
                }
        }
        bool anyMesh = indices is { Count: > 0 }, anyFaces = faces is { Count: > 0 };
        if (!anyMesh && !anyFaces) return null;
        return new StadiumBuilder.StadiumBuild(anyMesh ? new PierMeshBuilder.MeshData(verts!.ToArray(), colors!.ToArray(), indices!.ToArray()) : null,
            faces?.ToArray() ?? [], capacity);
    }

    /// <summary>
    /// A leaf's content without the data's buildings and trees inside a stadium's facade (anchor frame), as
    /// <see cref="StadiumClip"/> leaves an LV95 tile: the buildings in their order (an index names the same solid), an open
    /// air's only its trees. New lists where something goes, the content's own otherwise; its ground never read, so a leaf
    /// being read never waits on another. On the level placement: a stadium's outline does not move with its ground's height.
    /// </summary>
    public static void Clip(CubeLeafContent leaf)
    {
        if (leaf.Buildings is null && leaf.Trees is null) return;
        EnuFrame? anchor = null;
        foreach (var s in StadiumCatalog.All)
        {
            if (!Reaches(s, leaf.Key)) continue;
            anchor ??= leaf.Key.Anchor;
            var frame = Level.Place(s).In(anchor.Value);
            var outline = StadiumBuilder.OutlineUv(s).Select(uv => { var q = frame(uv, 0); return new Vector2(q.X, q.Z); }).ToArray();
            var (min, max) = StadiumClip.Bounds(outline);
            if (s.Kind != VenueKind.OpenAir && leaf.Buildings is { } tile)
            {
                List<Building>? clipped = null;
                for (int i = 0; i < tile.Buildings.Count; i++)
                    if (StadiumClip.Clip(tile.Buildings[i], outline, min, max) is { } kept)
                    {
                        clipped ??= new List<Building>(tile.Buildings);
                        clipped[i] = kept;
                    }
                if (clipped != null) leaf.Buildings = new BuildingTile { Id = tile.Id, Buildings = clipped };
            }
            if (leaf.Trees is { } trees && trees.Any(t => In(t)))
                leaf.Trees = trees.Where(t => !In(t)).ToList();

            bool In(TreeInstance t) => t.X >= min.X && t.X <= max.X && t.Z >= min.Y && t.Z <= max.Y && StadiumClip.Inside(outline, new Vector2(t.X, t.Z));
        }
    }
}
