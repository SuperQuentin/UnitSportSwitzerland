using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A leaf's collision, ready to cut into patches (#1511, G2 C2; G1 found a separate height field misses 2 cm, so
/// collision is the mesh's own triangles). A patch is a level-<see cref="PatchLevel"/> cube cell, 128 × 128 lattice
/// cells (~150 m), a sixteenth of a leaf: its ground triangles (the leaf's, split as the drawn mesh is, along
/// (i, j)–(i+1, j+1), front faces up; hole quads left out) clamped to the road blend (<see cref="LeafRoadBlend"/>,
/// clearance 0), plus every road and building face whose middle lies in it: the same builders the LV95 tiles' collision
/// uses (bridge decks, walls, railings, islands, signs, masts, car parks, signals, sidewalks and kerbs; buildings'
/// shells and footings; since #1510 the doors' garage links and stoops and the building sites' shells and offices, from
/// the leaf's stand-in tile, <see cref="LeafStand"/>). Faces are in the leaf's anchor frame (x east, y up, z south).
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
    /// <summary>The cells reaching into a building site's pit: their ground comes as faces, cut (#1653).</summary>
    private readonly HashSet<int>? _pitCells;
    private readonly List<Vector3>[] _faces;
    /// <summary>
    /// Per patch, the buildings' faces (shells, footings, garage links, stoops, the sites' shells and offices, #1704): a body
    /// of their own, as a flat tile's <c>BuildingBody</c>, so an open doorway lets a walker through them and not the ground.
    /// </summary>
    private readonly List<Vector3>[] _buildings;

    public CubeKey Leaf { get; }

    private LeafCollision(CubeKey leaf, int n, float[] vertices, HashSet<int>? holes, HashSet<int>? pitCells, List<Vector3>[] faces,
        List<Vector3>[] buildings, LeafRoadBlend.Bands? bands, LeafStand stand, float[] bare)
    {
        Leaf = leaf;
        (_n, _cells, _vertices, _holes, _pitCells, _faces, _buildings) = (n, n - 1, vertices, holes, pitCells, faces, buildings);
        (Bands, Stand, Bare) = (bands, stand, bare);
    }

    /// <summary>The road blend's bands (null: no road reaches the leaf): the drawn ground takes them at the visual clearance.</summary>
    public LeafRoadBlend.Bands? Bands { get; }

    /// <summary>The leaf as its stand-in tile: grid, records, sites and doors (#1510).</summary>
    public LeafStand Stand { get; }

    /// <summary>The lattice vertices before the blend.</summary>
    public float[] Bare { get; }

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
        var bare = CubeGeometry.Vertices(leaf.Key, heights.Bits, heights.Heights);
        var stand = LeafStand.Make(leaf, bare, n);
        var vertices = (float[])bare.Clone();
        var bands = LeafRoadBlend.For(leaf.Key, leaf.Roads, neighbourRoads);
        if (bands != null) LeafRoadBlend.Apply(vertices, bands, 0);

        var faces = new List<Vector3>[PerSide * PerSide];
        var buildingFaces = new List<Vector3>[PerSide * PerSide];
        for (int p = 0; p < faces.Length; p++) (faces[p], buildingFaces[p]) = (new(), new());
        var all = new List<Vector3>();
        if (leaf.Roads is { } roads)
            all.AddRange([.. RoadMeshBuilder.BuildBridgeCollisionFaces(roads), .. RoadWallBuilder.BuildCollisionFaces(roads),
                .. RailingBuilder.BuildCollisionFaces(roads), .. IslandBuilder.BuildCollisionFaces(roads),
                .. RoadSignBuilder.BuildCollisionFaces(roads), .. CatenaryBuilder.BuildCollisionFaces(roads),
                .. ParkingBuilder.BuildCollisionFaces(roads), .. SignalBuilder.BuildCollisionFaces(roads),
                .. RoadStreetBuilder.BuildCollisionFaces(roads)]);
        // the buildings' shells with their footings on the grid, and what C2 left to the doors: in the stand-in tile, moved back
        var tileFaces = new List<Vector3>(stand.ExtraFaces);
        if (stand.Buildings != null) tileFaces.AddRange(BuildingMeshBuilder.BuildCollisionFaces(stand.Buildings, stand.Grid));
        var shells = new List<Vector3>(tileFaces.Count);
        foreach (var p in tileFaces) shells.Add(LeafStand.ToAnchor(p));
        // the sites' slabs, stairs and walkways stay with the ground: underfoot at a raised door
        foreach (var p in stand.SiteFaces) all.Add(LeafStand.ToAnchor(p));
        // the building sites' pits (#619): the cells reaching into one leave the lattice, their ground outside the pit
        // and the pit's lining come back as faces (#1653), as the drawn mesh is cut
        var holes = leaf.Holes is { Length: > 0 } h ? h.ToHashSet() : null;
        HashSet<int>? pitCells = null;
        if (stand.Pits.Count > 0)
        {
            var (cells, pitFaces) = PitFaces(vertices, n, holes, stand.PitsInAnchor());
            pitCells = cells;
            all.AddRange(pitFaces);
        }
        if (all.Count > 0 || shells.Count > 0)
        {
            var t = (frames ?? LeafFrames.Swiss).TransformOf(leaf.Key);
            int cells = n - 1, per = cells / PerSide;
            Split(all, faces);
            Split(shells, buildingFaces);

            void Split(List<Vector3> from, List<Vector3>[] into)
            {
                for (int k = 0; k + 2 < from.Count; k += 3)
                {
                    var mid = (from[k] + from[k + 1] + from[k + 2]) / 3f;
                    var (i, j, _) = t.LocalToLattice(mid.X, mid.Y, mid.Z);
                    int pi = Math.Clamp((int)Math.Floor(i / per), 0, PerSide - 1), pj = Math.Clamp((int)Math.Floor(j / per), 0, PerSide - 1);
                    var list = into[pi + pj * PerSide];
                    list.Add(from[k]);
                    list.Add(from[k + 1]);
                    list.Add(from[k + 2]);
                }
            }
        }
        return new LeafCollision(leaf.Key, n, vertices, holes, pitCells, faces, buildingFaces, bands, stand, bare);
    }

    /// <summary>
    /// The lattice cells (i + (j &lt;&lt; bits), as the holes) whose box meets a pit's, and their triangles with the pits cut out
    /// and lined (<see cref="PitTerrain.CutFaces"/>), front faces up, in the anchor frame.
    /// </summary>
    public static (HashSet<int> Cells, List<Vector3> Faces) PitFaces(float[] v, int n, HashSet<int>? holes, IReadOnlyList<PitCut> pits)
    {
        int bits = System.Numerics.BitOperations.Log2((uint)(n - 1));
        var bounds = pits.Select(p => PitTerrain.Bounds(p.Rect)).ToArray();
        var cells = new HashSet<int>();
        var soup = new List<Vector3>();
        for (int j = 0; j + 1 < n; j++)
            for (int i = 0; i + 1 < n; i++)
            {
                int key = i + (j << bits);
                if (holes?.Contains(key) == true) continue;
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                float x0 = Math.Min(Math.Min(v[a * 3], v[b * 3]), Math.Min(v[c * 3], v[d * 3]));
                float x1 = Math.Max(Math.Max(v[a * 3], v[b * 3]), Math.Max(v[c * 3], v[d * 3]));
                float z0 = Math.Min(Math.Min(v[a * 3 + 2], v[b * 3 + 2]), Math.Min(v[c * 3 + 2], v[d * 3 + 2]));
                float z1 = Math.Max(Math.Max(v[a * 3 + 2], v[b * 3 + 2]), Math.Max(v[c * 3 + 2], v[d * 3 + 2]));
                bool near = false;
                foreach (var (bx0, bz0, bx1, bz1) in bounds)
                    if (x1 > bx0 && x0 < bx1 && z1 > bz0 && z0 < bz1) { near = true; break; }
                if (!near) continue;
                if (float.IsNaN(v[a * 3 + 1]) || float.IsNaN(v[b * 3 + 1]) || float.IsNaN(v[c * 3 + 1]) || float.IsNaN(v[d * 3 + 1])) continue;
                cells.Add(key);
                soup.Add(V(a)); soup.Add(V(d)); soup.Add(V(b));
                soup.Add(V(a)); soup.Add(V(c)); soup.Add(V(d));
            }
        return (cells, PitTerrain.CutFaces(soup, pits));

        Vector3 V(int k) => new(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
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
    /// <paramref name="features"/> (a ray test against the mesh); without <paramref name="buildings"/>, all but the
    /// buildings' (<see cref="Buildings"/>: their own body).
    /// </summary>
    public Vector3[] Patch(CubeKey patch, bool features = true, bool buildings = true)
    {
        var (leaf, pi, pj) = Split(patch);
        if (leaf != Leaf) throw new ArgumentException($"{patch} is not in {Leaf}");
        int per = _cells / PerSide, i0 = pi * per, j0 = pj * per;
        var extra = features ? _faces[pi + pj * PerSide] : [];
        var shells = features && buildings ? _buildings[pi + pj * PerSide] : [];
        var faces = new List<Vector3>(per * per * 6 + extra.Count + shells.Count);
        var v = _vertices;
        for (int j = j0; j < j0 + per; j++)
            for (int i = i0; i < i0 + per; i++)
            {
                if (_holes?.Contains(i + (j << Bits)) == true || _pitCells?.Contains(i + (j << Bits)) == true) continue;
                int a = j * _n + i, b = a + 1, c = a + _n, d = c + 1;
                if (float.IsNaN(v[a * 3 + 1]) || float.IsNaN(v[b * 3 + 1]) || float.IsNaN(v[c * 3 + 1]) || float.IsNaN(v[d * 3 + 1])) continue;
                // clockwise seen from above, as the drawn mesh: Godot's front faces, and a trimesh collides with those only
                faces.Add(V(a)); faces.Add(V(d)); faces.Add(V(b));
                faces.Add(V(a)); faces.Add(V(c)); faces.Add(V(d));
            }
        faces.AddRange(extra);
        faces.AddRange(shells);
        return faces.ToArray();

        Vector3 V(int k) => new(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
    }

    /// <summary>A patch's buildings' faces (anchor frame), for a body of their own (#1704).</summary>
    public Vector3[] Buildings(CubeKey patch)
    {
        var (leaf, pi, pj) = Split(patch);
        if (leaf != Leaf) throw new ArgumentException($"{patch} is not in {Leaf}");
        return _buildings[pi + pj * PerSide].ToArray();
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
    public PatchFaces? Build(CubeKey patch)
    {
        var (leaf, _, _) = LeafCollision.Split(patch);
        return Prepared(leaf) is { } c ? new PatchFaces(c.Patch(patch, buildings: false), c.Buildings(patch)) : null;
    }

    /// <summary>A leaf prepared, kept; a leaf that gave nothing (a streamed one not come yet, #1655) is asked again next time.</summary>
    public LeafCollision? Prepared(CubeKey leaf)
    {
        if (_prepared.Count > KeptLeaves) _prepared.Clear();
        var lazy = _prepared.GetOrAdd(leaf, k => new Lazy<LeafCollision?>(() =>
            leaves(k) is { Heights: not null } content ? LeafCollision.Prepare(content, n => leaves(n)?.Roads, frames) : null));
        var value = lazy.Value;
        if (value == null) _prepared.TryRemove(new KeyValuePair<CubeKey, Lazy<LeafCollision?>>(leaf, lazy));
        return value;
    }

    /// <summary>Every prepared leaf dropped (#1655: the server's leaves changed what they were prepared from).</summary>
    public void Forget() => _prepared.Clear();
}
