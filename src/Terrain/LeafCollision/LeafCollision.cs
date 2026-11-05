using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A leaf's collision, ready to cut into patches (#1511, G2 C2; G1 found a separate height field misses 2 cm, so
/// collision is the mesh's own triangles). A patch is a level-<see cref="PatchLevel"/> cube cell, 128 × 128 lattice
/// cells (~150 m), a sixteenth of a leaf: its ground triangles (the leaf's, split as the drawn mesh is, along
/// (i, j)–(i+1, j+1), front faces up; hole quads left out) clamped to the road blend (<see cref="LeafRoadBlend"/>,
/// clearance 0), plus every road and building face whose middle lies in it: the same builders the LV95 tiles' collision
/// uses (bridge decks, walls, railings, islands, signs, masts, car parks, signals, sidewalks and kerbs; buildings'
/// shells). Faces are in the leaf's anchor frame (x east, y up, z south). Garage links, stoops and offices are left to
/// C1 (they need the doors the ground grid gives).
/// </summary>
public sealed class LeafCollision
{
    /// <summary>A patch's level: two below the leaf's, 128 of its 512 cells a side.</summary>
    public const int PatchLevel = CollisionPatches.PatchLevel;

    /// <summary>Patches a leaf side.</summary>
    public const int PerSide = 1 << (PatchLevel - LeafFrames.LeafLevel);

    private readonly int _n, _cells;
    private readonly float[] _vertices;
    private readonly HashSet<int>? _holes;
    private readonly List<Vector3>[] _faces;

    public CubeKey Leaf { get; }

    private LeafCollision(CubeKey leaf, int n, float[] vertices, HashSet<int>? holes, List<Vector3>[] faces)
    {
        Leaf = leaf;
        (_n, _cells, _vertices, _holes, _faces) = (n, n - 1, vertices, holes, faces);
    }

    /// <summary>The lattice vertices as the patches hold them (blended), x y z a vertex, row-major from the south-west.</summary>
    public ReadOnlySpan<float> Vertices => _vertices;

    /// <summary>Vertices a side.</summary>
    public int Side => _n;

    /// <summary>
    /// A leaf's collision from its content (heights needed; roads, holes and buildings when it has them) and its
    /// neighbours' roads (for the blend at its edges; null: its own only).
    /// </summary>
    public static LeafCollision Prepare(CubeLeafContent leaf, Func<CubeKey, RoadTile?>? neighbourRoads, LeafFrames? frames = null)
    {
        var heights = leaf.Heights ?? throw new ArgumentException($"leaf {leaf.Key} has no heights");
        int n = (1 << heights.Bits) + 1;
        var vertices = CubeGeometry.Vertices(leaf.Key, heights.Bits, heights.Heights);
        if (LeafRoadBlend.For(leaf.Key, leaf.Roads, neighbourRoads) is { } bands)
            LeafRoadBlend.Apply(vertices, bands, 0);

        var faces = new List<Vector3>[PerSide * PerSide];
        for (int p = 0; p < faces.Length; p++) faces[p] = new();
        var all = new List<Vector3>();
        if (leaf.Roads is { } roads)
            all.AddRange([.. RoadMeshBuilder.BuildBridgeCollisionFaces(roads), .. RoadWallBuilder.BuildCollisionFaces(roads),
                .. RailingBuilder.BuildCollisionFaces(roads), .. IslandBuilder.BuildCollisionFaces(roads),
                .. RoadSignBuilder.BuildCollisionFaces(roads), .. CatenaryBuilder.BuildCollisionFaces(roads),
                .. ParkingBuilder.BuildCollisionFaces(roads), .. SignalBuilder.BuildCollisionFaces(roads),
                .. RoadStreetBuilder.BuildCollisionFaces(roads)]);
        if (leaf.Buildings is { Buildings.Count: > 0 } buildings)
            all.AddRange(BuildingMeshBuilder.BuildCollisionFaces(buildings));
        if (all.Count > 0)
        {
            var t = (frames ?? LeafFrames.Swiss).TransformOf(leaf.Key);
            int cells = n - 1, per = cells / PerSide;
            for (int k = 0; k + 2 < all.Count; k += 3)
            {
                var mid = (all[k] + all[k + 1] + all[k + 2]) / 3f;
                var (i, j, _) = t.LocalToLattice(mid.X, mid.Y, mid.Z);
                int pi = Math.Clamp((int)Math.Floor(i / per), 0, PerSide - 1), pj = Math.Clamp((int)Math.Floor(j / per), 0, PerSide - 1);
                var list = faces[pi + pj * PerSide];
                list.Add(all[k]);
                list.Add(all[k + 1]);
                list.Add(all[k + 2]);
            }
        }
        return new LeafCollision(leaf.Key, n, vertices, leaf.Holes is { Length: > 0 } h ? h.ToHashSet() : null, faces);
    }

    /// <summary>Which patch of its leaf a patch key is (0..<see cref="PerSide"/> − 1 along u and v).</summary>
    public static (CubeKey Leaf, int I, int J) Split(CubeKey patch)
    {
        if (patch.Level != PatchLevel) throw new ArgumentException($"{patch} is not a level-{PatchLevel} patch");
        return (patch.AncestorAt(LeafFrames.LeafLevel), patch.X & (PerSide - 1), patch.Y & (PerSide - 1));
    }

    /// <summary>The patches of a leaf.</summary>
    public static IEnumerable<CubeKey> PatchesOf(CubeKey leaf)
    {
        for (int j = 0; j < PerSide; j++)
            for (int i = 0; i < PerSide; i++)
                yield return new CubeKey(leaf.Face, PatchLevel, leaf.X * PerSide + i, leaf.Y * PerSide + j);
    }

    /// <summary>
    /// A patch's triangles (three vertices each, front faces up), in the leaf's anchor frame; the ground's alone without
    /// <paramref name="features"/> (a ray test against the mesh).
    /// </summary>
    public Vector3[] Patch(CubeKey patch, bool features = true)
    {
        var (leaf, pi, pj) = Split(patch);
        if (leaf != Leaf) throw new ArgumentException($"{patch} is not in {Leaf}");
        int per = _cells / PerSide, i0 = pi * per, j0 = pj * per;
        var extra = features ? _faces[pi + pj * PerSide] : [];
        var faces = new List<Vector3>(per * per * 6 + extra.Count);
        var v = _vertices;
        for (int j = j0; j < j0 + per; j++)
            for (int i = i0; i < i0 + per; i++)
            {
                if (_holes?.Contains(i + (j << Bits)) == true) continue;
                int a = j * _n + i, b = a + 1, c = a + _n, d = c + 1;
                if (float.IsNaN(v[a * 3 + 1]) || float.IsNaN(v[b * 3 + 1]) || float.IsNaN(v[c * 3 + 1]) || float.IsNaN(v[d * 3 + 1])) continue;
                // clockwise seen from above, as the drawn mesh: Godot's front faces, and a trimesh collides with those only
                faces.Add(V(a)); faces.Add(V(d)); faces.Add(V(b));
                faces.Add(V(a)); faces.Add(V(c)); faces.Add(V(d));
            }
        faces.AddRange(extra);
        return faces.ToArray();

        Vector3 V(int k) => new(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
    }

    private int Bits => System.Numerics.BitOperations.Log2((uint)_cells);
}

/// <summary>
/// The patch builder over leaves (#1511): a patch's leaf prepared once (<see cref="LeafCollision.Prepare"/>, its
/// neighbours' roads for the blend at its edges) and kept for the last <see cref="KeptLeaves"/> leaves, then cut. The
/// leaves come from whoever holds them (C1's earth streamer; a region's <c>.cubl</c> in the harness).
/// </summary>
public sealed class LeafCollisionSource(Func<CubeKey, CubeLeafContent?> leaves, LeafFrames? frames = null)
{
    public const int KeptLeaves = 32;

    private readonly ConcurrentDictionary<CubeKey, Lazy<LeafCollision?>> _prepared = new();

    /// <summary>A patch's faces; null when its leaf has no ground.</summary>
    public Vector3[]? Build(CubeKey patch)
    {
        var (leaf, _, _) = LeafCollision.Split(patch);
        return Prepared(leaf)?.Patch(patch);
    }

    public LeafCollision? Prepared(CubeKey leaf)
    {
        if (_prepared.Count > KeptLeaves) _prepared.Clear();
        return _prepared.GetOrAdd(leaf, k => new Lazy<LeafCollision?>(() =>
            leaves(k) is { Heights: not null } content ? LeafCollision.Prepare(content, n => leaves(n)?.Roads, frames) : null)).Value;
    }
}
