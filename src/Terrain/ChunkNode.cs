using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Scene-side representation of one terrain tile: a MeshInstance3D and optionally a
/// StaticBody3D with a HeightMapShape3D. Positioned at the tile's NW corner in world space.
/// </summary>
public partial class ChunkNode : Node3D
{
    private MeshInstance3D? _meshInstance;
    private MeshInstance3D? _roadInstance;
    private StaticBody3D? _body;

    // ---- ArrayMesh construction ---------------------------------------------------------
    //
    // Building the ArrayMesh is the expensive half of a commit - a million-vertex tile is
    // 30 ms of packing and upload - and none of it needs the scene tree. Godot's
    // RenderingServer is thread-safe (calls from other threads are queued), so the build
    // worker creates the resource and the main thread only assigns it to a MeshInstance3D.

    public static ArrayMesh ToArrayMesh(TerrainMeshBuilder.MeshData data, Material material)
    {
        // disposed on return: AddSurfaceFromArrays has copied the data into the RenderingServer,
        // and the packed native copies inside this Array are tens of MB per tile that would
        // otherwise wait for a finalizer
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        return Finish(arrays, material);
    }

    public static ArrayMesh ToArrayMesh(RoadMeshBuilder.MeshData data, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2s;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        return Finish(arrays, material);
    }

    public static ArrayMesh ToArrayMesh(BuildingMeshBuilder.MeshData data, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2s;
        arrays[(int)Mesh.ArrayType.Custom0] = data.Frames;
        return Finish(arrays, material,
            (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
    }

    public static ArrayMesh ToArrayMesh(WaterMeshBuilder.MeshData data, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        return Finish(arrays, material);
    }

    /// <summary>
    /// Assigns a mesh and frees the one it replaces. Every tile mesh is owned by exactly one
    /// instance, so nothing else can still be holding the old one; left to the GC, a tile rebuilt
    /// at a finer stride kept its coarse mesh (and a moving camera a trail of them) alive.
    /// </summary>
    private static void Swap(MeshInstance3D instance, Mesh mesh)
    {
        var old = instance.Mesh;
        instance.Mesh = mesh;
        if (old != null && old != mesh) old.Dispose();
    }

    private static ArrayMesh Finish(Godot.Collections.Array arrays, Material material, Mesh.ArrayFormat flags = 0)
    {
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: flags);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    public void SetMesh(TerrainMeshBuilder.MeshData data, Material material) =>
        SetMesh(ToArrayMesh(data, material));

    public void SetMesh(ArrayMesh mesh)
    {
        if (_meshInstance == null)
        {
            _meshInstance = new MeshInstance3D { CastShadow = Core.VisualStyleKit.GroundShadows };
            AddChild(_meshInstance);
        }
        Swap(_meshInstance, mesh);
    }

    public void SetRoads(ArrayMesh mesh)
    {
        if (_roadInstance == null)
        {
            _roadInstance = new MeshInstance3D { Name = "Roads", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_roadInstance);
        }
        Swap(_roadInstance, mesh);
    }

    private MeshInstance3D? _buildingInstance;
    private StaticBody3D? _buildingBody;

    public void SetBuildings(ArrayMesh mesh)
    {
        if (_buildingInstance == null)
        {
            _buildingInstance = new MeshInstance3D { Name = "Buildings" };
            AddChild(_buildingInstance);
        }
        Swap(_buildingInstance, mesh);
    }

    /// <summary>The tile's building collision, once built: a player in an open doorway is let through it.</summary>
    public StaticBody3D? BuildingBody => _buildingBody;

    /// <summary>The buildings' collision shape — one place, so <c>--hitboxcheck</c> tests exactly what the world gets.</summary>
    public static ConcavePolygonShape3D BuildingShape(Vector3[] faces) => new() { Data = faces };

    public void SetBuildingCollision(Vector3[] faces)
    {
        var shape = BuildingShape(faces);
        if (_buildingBody == null)
        {
            _buildingBody = new StaticBody3D { Name = "BuildingBody" };
            AddChild(_buildingBody);
        }
        foreach (Node child in _buildingBody.GetChildren())
            child.QueueFree();
        _buildingBody.AddChild(new CollisionShape3D { Shape = shape });
    }

    private StaticBody3D? _roadBody;

    /// <summary>
    /// Bridge deck collision — the one piece of road geometry a heightfield cannot represent
    /// (a deck floats above terrain, at a different height than the ground it crosses). Every
    /// other at-grade road/path already stands on terrain collision blended toward it; see
    /// <c>TerrainMeshBuilder.ComputeRoadBlend</c>.
    /// </summary>
    public void SetRoadCollision(Vector3[] faces)
    {
        if (faces.Length == 0)
        {
            _roadBody?.QueueFree();
            _roadBody = null;
            return;
        }

        // BackfaceCollision: a bridge deck is walked on from above, but Godot's default (false)
        // makes a ConcavePolygonShape3D one-sided for exactly the queries a player's own
        // MoveAndSlide and a straight-down raycast both are - a ray from above that happens to
        // approach the "wrong" side of the triangle winding passes straight through as if the
        // deck were not there at all. Verified with a direct PhysicsRayQueryParameters3D probe
        // from above a real bridge: without this the ray landed on the terrain far below,
        // exactly the fall-through this collision exists to prevent.
        var shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true };
        if (_roadBody == null)
        {
            _roadBody = new StaticBody3D { Name = "RoadBody" };
            AddChild(_roadBody);
        }
        foreach (Node child in _roadBody.GetChildren())
            child.QueueFree();
        _roadBody.AddChild(new CollisionShape3D { Shape = shape });
    }

    private MultiMeshInstance3D? _coniferInstance;
    private MultiMeshInstance3D? _broadleafInstance;
    private MultiMeshInstance3D? _coniferFarInstance, _broadleafFarInstance;

    /// <summary>
    /// Trees as MultiMeshes per tile — 8k+ instances per tile makes individual nodes
    /// impossible. There are two, because a MultiMesh carries exactly one mesh: conifers
    /// and shrubs share a spire, while fruit trees and TLM's surveyed single trees are
    /// broadleaves standing in the open and need a round crown to read as such.
    /// </summary>
    /// <summary>
    /// Instance buffers for the two tree MultiMeshes, in Godot's packed layout (12 transform
    /// floats then 4 colour floats per instance). Pure arithmetic, so it runs on the build
    /// worker; the main thread then uploads each with one <c>Buffer</c> assignment instead of
    /// two native calls per tree — 60,000 trees a tile used to cost 30 ms of commit.
    /// </summary>
    public sealed record TreeBuffers(float[] Conifers, int ConiferCount, float[] Broadleaves, int BroadleafCount);

    public static TreeBuffers BuildTreeBuffers(IReadOnlyList<TreeInstance> trees)
    {
        // Kind: 0 conifer, 1 shrub, 2 fruit tree, 3 surveyed solitary broadleaf
        var (cone, coneCount) = Pack(trees, t => t.Kind is 0 or 1);
        var (crown, crownCount) = Pack(trees, t => t.Kind is 2 or 3);
        return new TreeBuffers(cone, coneCount, crown, crownCount);
    }

    private const int FloatsPerInstance = 16;

    private static (float[] Buffer, int Count) Pack(IReadOnlyList<TreeInstance> trees, Func<TreeInstance, bool> wanted)
    {
        int count = 0;
        foreach (var t in trees) if (wanted(t)) count++;
        var buffer = new float[count * FloatsPerInstance];

        int i = 0;
        foreach (var t in trees)
        {
            if (!wanted(t)) continue;
            // scale the shared unit mesh to this tree's height; girth separates the kinds
            float slenderness = t.Kind switch
            {
                1 => 0.42f,   // shrub
                2 => 0.34f,   // fruit tree — small and dense
                3 => 0.40f,   // solitary broadleaf — wide, nothing crowding it
                _ => 0.26f,   // conifer
            };
            float radius = t.Height * slenderness;

            // vary tone per tree so a forest is not one flat mass
            float v = (i * 0.6180339f) % 1f;
            var tint = (t.Kind switch
            {
                1 => new Color(0.30f, 0.36f, 0.20f),
                2 => new Color(0.28f + v * 0.06f, 0.40f + v * 0.07f, 0.18f + v * 0.04f),
                3 => new Color(0.21f + v * 0.08f, 0.35f + v * 0.10f, 0.16f + v * 0.05f),
                _ => new Color(0.13f + v * 0.07f, 0.24f + v * 0.09f, 0.12f + v * 0.05f),
            }).SrgbToLinear();

            // Transform3D as three rows of (basis column x, y, z, origin): a diagonal basis
            // of (radius, height, radius) with the tree's position as the last column.
            int o = i * FloatsPerInstance;
            buffer[o + 0] = radius; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = t.X;
            buffer[o + 4] = 0; buffer[o + 5] = t.Height; buffer[o + 6] = 0; buffer[o + 7] = t.Y;
            buffer[o + 8] = 0; buffer[o + 9] = 0; buffer[o + 10] = radius; buffer[o + 11] = t.Z;
            buffer[o + 12] = tint.R; buffer[o + 13] = tint.G; buffer[o + 14] = tint.B; buffer[o + 15] = tint.A;
            i++;
        }
        return (buffer, count);
    }

    /// <summary>The two MultiMeshes of a tile, built on the worker; null where a tile has none.</summary>
    public sealed record TreeMeshes(MultiMesh? Conifers, MultiMesh? Broadleaves,
        MultiMesh? ConifersFar = null, MultiMesh? BroadleavesFar = null, TreeBuffers? Near = null);

    /// <summary>
    /// Builds the MultiMesh resources off the main thread. The bounds are given rather than
    /// computed: assigning a buffer makes the RenderingServer walk every instance for an AABB,
    /// 16 ms for a 60k-tree tile on the main thread, unless a custom one is already set.
    /// </summary>
    public static TreeMeshes BuildTreeMeshes(TreeBuffers trees, Material material, Aabb bounds)
    {
        // prototype #181: the same instances twice, the 3D tree near and a billboard far (every
        // style, PS1 included); the shaders crossfade them per tree by distance
        bool high = Core.VisualStyleKit.Lit;
        var far = Core.VisualStyleKit.TreeFarMaterial;
        // lit styles: the 3D trees are culled per tree by NearTrees, the tile only
        // draws billboards and hands over its instance buffers
        if (Core.VisualStyleKit.NearTreeMeshes != null)
            return new TreeMeshes(null, null,
                Make(trees.Conifers, trees.ConiferCount, BillboardMesh(far, 0f), bounds),
                Make(trees.Broadleaves, trees.BroadleafCount, BillboardMesh(far, 1f), bounds),
                trees);
        return new TreeMeshes(
            Make(trees.Conifers, trees.ConiferCount,
                Core.RealTrees.Conifer ?? (high ? TieredConeMesh(material) : ConeMesh(material)), bounds),
            Make(trees.Broadleaves, trees.BroadleafCount,
                Core.RealTrees.Broadleaf ?? (high ? PuffCrownMesh(material) : CrownMesh(material)), bounds),
            Make(trees.Conifers, trees.ConiferCount, BillboardMesh(far, 0f), bounds),
            Make(trees.Broadleaves, trees.BroadleafCount, BillboardMesh(far, 1f), bounds));
    }

    private static MultiMesh? Make(float[] buffer, int count, ArrayMesh mesh, Aabb bounds)
    {
        if (count == 0) return null;
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            CustomAabb = bounds,
            InstanceCount = count,
        };
        multi.Buffer = buffer;
        return multi;
    }

    public void SetTrees(TreeMeshes trees)
    {
        Fill(ref _coniferInstance, "Trees", trees.Conifers);
        Fill(ref _broadleafInstance, "Broadleaves", trees.Broadleaves);
        if (trees.Near != null) NearTrees.Register(this, trees.Near);
        Fill(ref _coniferFarInstance, "TreesFar", trees.ConifersFar);
        Fill(ref _broadleafFarInstance, "BroadleavesFar", trees.BroadleavesFar);
        foreach (var far in new[] { _coniferFarInstance, _broadleafFarInstance })
            if (far != null) far.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        // whole tiles far enough that none of their trees can be 3D skip the near meshes
        foreach (var near in new[] { _coniferInstance, _broadleafInstance })
            if (near != null) near.VisibilityRangeEnd = Core.VisualStyleKit.TreeNearTileRange;
    }

    private void Fill(ref MultiMeshInstance3D? node, string name, MultiMesh? multi)
    {
        if (multi == null)
        {
            if (node != null) node.Visible = false;
            return;
        }
        if (node == null)
        {
            node = new MultiMeshInstance3D { Name = name };
            AddChild(node);
        }
        node.Visible = true;
        var previous = node.Multimesh;
        node.Multimesh = multi;
        // the MultiMesh is this tile's own; its shared unit tree mesh is not, so only the
        // wrapper is released, never previous.Mesh
        if (previous != null && previous != multi) previous.Dispose();
    }

    /// <summary>
    /// Unit-height conifer: a 5-sided cone on a bare trunk, origin at the base. The foliage
    /// used to reach down to 15% of the height, so a 25 m spruce was 13 m wide at eye level
    /// and walled in every forest trail; starting it at <c>trunkTop</c> leaves the silhouette
    /// from above unchanged and opens the ground-level view. 20 triangles, like the crown.
    /// </summary>
    private static ArrayMesh ConeMesh(Material material)
    {
        const int sides = 5;
        const float trunkTop = 0.28f, neck = 0.22f;
        var verts = new List<Vector3>();
        var apex = new Vector3(0, 1, 0);
        var under = new Vector3(0, neck, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides;
            float a1 = Mathf.Tau * (i + 1) / sides;
            var p0 = new Vector3(Mathf.Cos(a0), trunkTop, Mathf.Sin(a0));
            var p1 = new Vector3(Mathf.Cos(a1), trunkTop, Mathf.Sin(a1));
            verts.Add(apex); verts.Add(p0); verts.Add(p1);
            verts.Add(p1); verts.Add(p0); verts.Add(under);

            // trunk: the same thin prism the broadleaf stands on
            var t0 = new Vector3(Mathf.Cos(a0) * 0.10f, 0, Mathf.Sin(a0) * 0.10f);
            var t1 = new Vector3(Mathf.Cos(a1) * 0.10f, 0, Mathf.Sin(a1) * 0.10f);
            verts.Add(t0); verts.Add(new Vector3(t1.X, neck, t1.Z)); verts.Add(t1);
            verts.Add(t0); verts.Add(new Vector3(t0.X, neck, t0.Z));
            verts.Add(new Vector3(t1.X, neck, t1.Z));
        }
        return BuildMesh(verts, material);
    }

    /// <summary>
    /// Unit-height broadleaf: a 5-sided bipyramid crown on a stub trunk, origin at the
    /// base. 20 triangles — barely more than the cone, and the silhouette is what
    /// distinguishes an orchard from a plantation at any distance worth rendering.
    /// </summary>
    private static ArrayMesh CrownMesh(Material material)
    {
        const int sides = 5;
        const float trunkTop = 0.34f, waist = 0.62f;
        var verts = new List<Vector3>();
        var apex = new Vector3(0, 1, 0);
        var neck = new Vector3(0, trunkTop, 0);

        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides;
            float a1 = Mathf.Tau * (i + 1) / sides;
            var w0 = new Vector3(Mathf.Cos(a0), waist, Mathf.Sin(a0));
            var w1 = new Vector3(Mathf.Cos(a1), waist, Mathf.Sin(a1));
            verts.Add(apex); verts.Add(w0); verts.Add(w1);   // crown top
            verts.Add(neck); verts.Add(w1); verts.Add(w0);   // crown underside

            // trunk: a thin prism, wide enough not to vanish at the snap resolution
            var t0 = new Vector3(Mathf.Cos(a0) * 0.10f, 0, Mathf.Sin(a0) * 0.10f);
            var t1 = new Vector3(Mathf.Cos(a1) * 0.10f, 0, Mathf.Sin(a1) * 0.10f);
            verts.Add(t0); verts.Add(new Vector3(t1.X, trunkTop, t1.Z)); verts.Add(t1);
            verts.Add(t0); verts.Add(new Vector3(t0.X, trunkTop, t0.Z));
            verts.Add(new Vector3(t1.X, trunkTop, t1.Z));
        }
        return BuildMesh(verts, material);
    }

    // ---- prototype #181: the High detail level, for the lit styles -------------------------
    // Smooth normals (the lit shaders read them when use_mesh_normal is set) and UV.x = 1 on
    // the trunk, so the shader can colour it as bark instead of the instance's foliage colour.

    /// <summary>The High-detail unit trees, for NearTrees (prototype #181).</summary>
    public static (Mesh Conifer, Mesh Broadleaf) HighDetailTrees(Material material) =>
        (TieredConeMesh(material), PuffCrownMesh(material));

    /// <summary>Broadleaf, High detail: three overlapping blobs on a trunk. ~120 triangles.</summary>
    private static ArrayMesh PuffCrownMesh(Material material)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>();
        Trunk(v, n, uv, 0.07f, 0.5f);
        Blob(v, n, uv, new Vector3(0f, 0.70f, 0f), new Vector3(0.72f, 0.28f, 0.72f));
        Blob(v, n, uv, new Vector3(0.34f, 0.56f, 0.18f), new Vector3(0.52f, 0.22f, 0.52f));
        Blob(v, n, uv, new Vector3(-0.30f, 0.58f, -0.22f), new Vector3(0.55f, 0.23f, 0.55f));
        return BuildMesh(v, material, n, uv);
    }

    /// <summary>Conifer, High detail: three stacked 7-sided tiers on a trunk. ~54 triangles.</summary>
    private static ArrayMesh TieredConeMesh(Material material)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>();
        Trunk(v, n, uv, 0.06f, 0.3f);
        (float Base, float Top, float R)[] tiers = { (0.22f, 0.62f, 1.0f), (0.45f, 0.82f, 0.75f), (0.66f, 1.0f, 0.5f) };
        const int sides = 7;
        foreach (var (b, t, r) in tiers)
        {
            float slope = r / (t - b);
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides, am = (a0 + a1) * 0.5f;
                var p0 = new Vector3(Mathf.Cos(a0) * r, b, Mathf.Sin(a0) * r);
                var p1 = new Vector3(Mathf.Cos(a1) * r, b, Mathf.Sin(a1) * r);
                var apex = new Vector3(0, t, 0);
                Vector3 Side(float a) => new Vector3(Mathf.Cos(a), slope, Mathf.Sin(a)).Normalized();
                v.Add(apex); n.Add(Side(am)); v.Add(p0); n.Add(Side(a0)); v.Add(p1); n.Add(Side(a1));
                var under = new Vector3(0, b + 0.04f, 0);
                v.Add(p1); n.Add(Vector3.Down); v.Add(p0); n.Add(Vector3.Down); v.Add(under); n.Add(Vector3.Down);
                for (int k = 0; k < 6; k++) uv.Add(Vector2.Zero);
            }
        }
        return BuildMesh(v, material, n, uv);
    }

    /// <summary>A unit quad for the billboard trees: UV = (across, up), UV2.x = kind (0 conifer, 1 broadleaf).</summary>
    private static ArrayMesh BillboardMesh(Material material, float kind)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // positions are rebuilt in the shader (world_vertex_coords); these only bound the quad
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            new Vector3(-1, 0, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
        };
        arrays[(int)Mesh.ArrayType.TexUV] = new[]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
            new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1),
        };
        var k = new Vector2(kind, 0);
        arrays[(int)Mesh.ArrayType.TexUV2] = new[] { k, k, k, k, k, k };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    private static void Trunk(List<Vector3> v, List<Vector3> n, List<Vector2> uv, float r, float top)
    {
        const int sides = 6;
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
            var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            Vector3 b0 = d0 * r, b1 = d1 * r, t0 = b0 + Vector3.Up * top, t1 = b1 + Vector3.Up * top;
            v.Add(b0); n.Add(d0); v.Add(t1); n.Add(d1); v.Add(b1); n.Add(d1);
            v.Add(b0); n.Add(d0); v.Add(t0); n.Add(d0); v.Add(t1); n.Add(d1);
            for (int k = 0; k < 6; k++) uv.Add(Vector2.Right);
        }
    }

    /// <summary>A low-poly ellipsoid (6 around, 4 down) with smooth normals.</summary>
    private static void Blob(List<Vector3> v, List<Vector3> n, List<Vector2> uv, Vector3 c, Vector3 r)
    {
        const int seg = 6, rings = 4;
        Vector3 P(int i, int j)
        {
            float th = Mathf.Pi * j / rings, ph = Mathf.Tau * i / seg;
            return new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
        }
        void Add(Vector3 unit)
        {
            v.Add(c + unit * r);
            n.Add(new Vector3(unit.X / r.X, unit.Y / r.Y, unit.Z / r.Z).Normalized());
            uv.Add(Vector2.Zero);
        }
        for (int j = 0; j < rings; j++)
            for (int i = 0; i < seg; i++)
            {
                Vector3 a = P(i, j), b = P(i + 1, j), cc = P(i, j + 1), d = P(i + 1, j + 1);
                if (j > 0) { Add(a); Add(b); Add(cc); }
                if (j < rings - 1) { Add(b); Add(d); Add(cc); }
            }
    }

    private static ArrayMesh BuildMesh(List<Vector3> verts, Material material, List<Vector3> normals, List<Vector2> uvs)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    private static ArrayMesh BuildMesh(List<Vector3> verts, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    private MeshInstance3D? _waterInstance;

    public void SetWater(ArrayMesh mesh)
    {
        if (_waterInstance == null)
        {
            _waterInstance = new MeshInstance3D { Name = "Water", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_waterInstance);
        }
        Swap(_waterInstance, mesh);
    }

    /// <summary>
    /// Frees this tile's meshes now rather than when their managed wrappers are finalized.
    /// Call before QueueFree on unload: the memory is the RenderingServer's, so the GC sees a
    /// few bytes per tile and a fast flight left hundreds of MB of dead meshes waiting on it.
    /// </summary>
    public void ReleaseResources()
    {
        NearTrees.Unregister(this);
        foreach (var instance in new[] { _meshInstance, _roadInstance, _buildingInstance, _waterInstance })
        {
            var mesh = instance?.Mesh;
            if (mesh == null) continue;
            instance!.Mesh = null;
            mesh.Dispose();
        }
        foreach (var node in new[] { _coniferInstance, _broadleafInstance, _coniferFarInstance, _broadleafFarInstance })
        {
            var multi = node?.Multimesh;
            if (multi == null) continue;
            node!.Multimesh = null;
            multi.Dispose();
        }
    }

    public void ClearRoads()
    {
        _roadInstance?.QueueFree();
        _roadInstance = null;
    }

    public void SetCollision(float[] collisionMap)
    {
        var shape = new HeightMapShape3D
        {
            MapWidth = ChunkFormat.GridSize,
            MapDepth = ChunkFormat.GridSize,
            MapData = collisionMap,
        };
        var collisionShape = new CollisionShape3D
        {
            Shape = shape,
            // HeightMapShape3D cells are 1 unit and the shape is XZ-centered; scale to
            // ChunkFormat.SpacingM and move to the tile center. Verified against the height
            // sampler in M3. Position is half the 1000 m tile size, not the grid spacing —
            // unaffected by a spacing change.
            Position = new Vector3(500f, 0f, 500f),
            Scale = new Vector3((float)ChunkFormat.SpacingM, 1f, (float)ChunkFormat.SpacingM),
        };

        if (_body == null)
        {
            _body = new StaticBody3D();
            AddChild(_body);
        }
        foreach (Node child in _body.GetChildren())
            child.QueueFree();
        _body.AddChild(collisionShape);
    }

    public void ClearCollision()
    {
        _body?.QueueFree();
        _body = null;
    }
}
