using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>What a tile draws, for the debug menu's layer switches (#339).</summary>
[Flags]
public enum TileLayers
{
    None = 0,
    Ground = 1,
    Roads = 2,
    Buildings = 4,
    Trees = 8,
    Water = 16,
}

/// <summary>
/// Scene-side representation of one terrain tile: a MeshInstance3D and optionally a
/// StaticBody3D with a HeightMapShape3D. Positioned at the tile's NW corner in world space.
/// </summary>
public partial class ChunkNode : Node3D
{
    private MeshInstance3D? _meshInstance;

    /// <summary>The tile's ground mesh, once built.</summary>
    public MeshInstance3D? Ground => _meshInstance;
    private MeshInstance3D? _roadInstance;
    private StaticBody3D? _body;

    /// <summary>The layers the debug menu hid (<see cref="ChunkManager.SetDebugHidden"/>); nothing in play.</summary>
    private TileLayers _hidden;

    private bool Shows(TileLayers layer) => (_hidden & layer) == 0;

    /// <summary>
    /// Hides the selected layers of this tile and shows the others, now and for the meshes it
    /// builds later. Trees go through their instance count, which their own visibility already
    /// answers for (an empty slot is hidden).
    /// </summary>
    public void HideLayers(TileLayers hidden)
    {
        if (hidden == _hidden) return;
        _hidden = hidden;
        if (_meshInstance != null) _meshInstance.Visible = Shows(TileLayers.Ground);
        if (_roadInstance != null) _roadInstance.Visible = Shows(TileLayers.Roads);
        if (_lamps != null) _lamps.Visible = Shows(TileLayers.Roads);
        if (_buildingInstance != null) _buildingInstance.Visible = Shows(TileLayers.Buildings);
        if (_siteInstance != null) _siteInstance.Visible = Shows(TileLayers.Buildings);
        if (_cellInstances != null)
            foreach (var cell in _cellInstances) cell.Visible = Shows(TileLayers.Buildings);
        if (_waterInstance != null) _waterInstance.Visible = Shows(TileLayers.Water);
        ApplyTreeDensity();
    }

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

    /// <param name="paint">The v3 paint layer (<see cref="RoadPaintBuilder"/>), a second surface.</param>
    public static ArrayMesh ToArrayMesh(RoadMeshBuilder.MeshData data, Material material,
        RoadMeshBuilder.MeshData? paint = null)
    {
        using var main = RoadArrays(data);
        var mesh = Finish(main, material);
        if (paint != null)
        {
            using var arrays = RoadArrays(paint);
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(1, material);
        }
        return mesh;
    }

    /// <summary>
    /// The tile's piers (#377) as one more surface of its roads mesh, with their own (prop)
    /// material; a new mesh when the tile has no roads drawn.
    /// </summary>
    /// <summary>Vertex-coloured triangles with the prop material: the piers' (#377) or the building sites' (#608).</summary>
    public static ArrayMesh ToPropMesh(PierMeshBuilder.MeshData data, Material material) => WithPiers(null, data, material);

    public static ArrayMesh WithPiers(ArrayMesh? roads, PierMeshBuilder.MeshData data, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        if (roads == null) return Finish(arrays, material);
        roads.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        roads.SurfaceSetMaterial(roads.GetSurfaceCount() - 1, material);
        return roads;
    }

    private static Godot.Collections.Array RoadArrays(RoadMeshBuilder.MeshData data)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2s;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        return arrays;
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
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = Finish(arrays, material);
        // the shader lifts crests above the still surface (#299): grow the bounds by the most a wave moves
        mesh.CustomAabb = mesh.GetAabb().Grow(WaterMeshBuilder.WaveMargin);
        return mesh;
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
            // the ground, its roads and its water never cast sun shadows (lit styles): millions
            // of triangles under the cascades, for shade the cel light already gives the slopes
            _meshInstance = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = Shows(TileLayers.Ground) };
            AddChild(_meshInstance);
        }
        Swap(_meshInstance, mesh);
    }

    public void SetRoads(ArrayMesh mesh)
    {
        if (_roadInstance == null)
        {
            _roadInstance = new MeshInstance3D { Name = "Roads", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = Shows(TileLayers.Roads) };
            AddChild(_roadInstance);
        }
        Swap(_roadInstance, mesh);
    }

    private SignalLamps? _lamps;

    /// <summary>The traffic lights' lenses (#350), replaced with the roads they stand beside; null removes them.</summary>
    public void SetSignalLamps(SignalBuilder.Lamps? lamps)
    {
        _lamps?.QueueFree();
        _lamps = null;
        if (lamps == null) return;
        _lamps = SignalLamps.Create(lamps);
        _lamps.Visible = Shows(TileLayers.Roads);
        AddChild(_lamps);
    }

    private MeshInstance3D? _buildingInstance;
    private StaticBody3D? _buildingBody;

    public void SetBuildings(ArrayMesh mesh)
    {
        ClearCells();
        if (_buildingInstance == null)
        {
            _buildingInstance = new MeshInstance3D { Name = "Buildings", Visible = Shows(TileLayers.Buildings) };
            AddChild(_buildingInstance);
        }
        Swap(_buildingInstance, mesh);
    }

    private MeshInstance3D? _siteInstance;

    /// <summary>
    /// The tile's building sites (#608): their half-built shells, one mesh with the prop material,
    /// drawn and dropped with the buildings. Null clears them.
    /// </summary>
    public void SetSites(ArrayMesh? mesh)
    {
        if (mesh == null)
        {
            if (_siteInstance?.Mesh is { } old)
            {
                _siteInstance.Mesh = null;
                old.Dispose();
            }
            return;
        }
        if (_siteInstance == null)
        {
            _siteInstance = new MeshInstance3D { Name = "Sites", Visible = Shows(TileLayers.Buildings) };
            AddChild(_siteInstance);
        }
        Swap(_siteInstance, mesh);
    }

    /// <summary>
    /// The tile has left the building ring (#553): its building mesh goes. The trees and the water,
    /// committed with the buildings, stay: they are drawn far past it.
    /// </summary>
    public void ClearBuildings()
    {
        ClearCells();
        SetSites(null);
        if (_buildingInstance?.Mesh is not { } mesh) return;
        _buildingInstance.Mesh = null;
        mesh.Dispose();
    }

    private MeshInstance3D[]? _cellInstances;
    private OccluderInstance3D? _occluder;

    /// <summary>
    /// The buildings cut into cells, with the tile's occluders (#553, <see cref="BuildingOcclusion"/>):
    /// what a tile round the camera draws, so a block hidden behind a row of houses is culled.
    /// Replaces the one-mesh buildings; <see cref="SetBuildings"/> replaces these.
    /// </summary>
    public void SetBuildingCells(ArrayMesh?[] cells, Vector3[]? occluderVertices, int[]? occluderIndices)
    {
        ClearCells();
        if (_buildingInstance?.Mesh is { } whole)
        {
            _buildingInstance.Mesh = null;
            whole.Dispose();
        }
        var list = new List<MeshInstance3D>();
        for (int c = 0; c < cells.Length; c++)
        {
            if (cells[c] is not { } mesh) continue;
            var instance = new MeshInstance3D { Name = $"Buildings{c}", Mesh = mesh, Visible = Shows(TileLayers.Buildings) };
            AddChild(instance);
            list.Add(instance);
        }
        _cellInstances = list.ToArray();
        if (occluderVertices != null && occluderIndices != null)
        {
            var occluder = new ArrayOccluder3D();
            occluder.SetArrays(occluderVertices, occluderIndices);
            _occluder = new OccluderInstance3D { Name = "Occluder", Occluder = occluder };
            AddChild(_occluder);
        }
    }

    private void ClearCells()
    {
        if (_cellInstances != null)
            foreach (var cell in _cellInstances)
            {
                var mesh = cell.Mesh;
                cell.Mesh = null;
                mesh?.Dispose();
                cell.QueueFree();
            }
        _cellInstances = null;
        _occluder?.QueueFree();
        _occluder = null;
    }

    /// <summary>The tile has left the road ring (#553): its road mesh (piers, signs and paint with it) and its signal lenses go.</summary>
    public void ClearRoads()
    {
        SetSignalLamps(null);
        if (_roadInstance?.Mesh is not { } mesh) return;
        _roadInstance.Mesh = null;
        mesh.Dispose();
    }

    /// <summary>The tile's building collision, once built: a player in an open doorway is let through it.</summary>
    public StaticBody3D? BuildingBody => _buildingBody;

    /// <summary>The buildings' collision shape — one place, so <c>--hitboxcheck</c> tests exactly what the world gets.</summary>
    public static ConcavePolygonShape3D BuildingShape(Vector3[] faces) => new() { Data = faces };

    private CollisionShape3D?[]? _buildingCells;

    /// <summary>
    /// Sorts collision triangles into the ground's 4×4 cells by centroid, on the build worker:
    /// a town tile's one BVH was up to 56 ms on the main thread, a cell's is a fraction of it.
    /// </summary>
    public static Vector3[][] SplitByCell(Vector3[] faces)
    {
        var cells = new List<Vector3>[CollisionCellCount];
        for (int c = 0; c < CollisionCellCount; c++) cells[c] = new List<Vector3>();
        float spacing = (float)ChunkFormat.SpacingM;
        for (int t = 0; t + 2 < faces.Length; t += 3)
        {
            var mid = (faces[t] + faces[t + 1] + faces[t + 2]) / 3f;
            var list = cells[CollisionCell((int)(mid.X / spacing), (int)(mid.Z / spacing))];
            list.Add(faces[t]); list.Add(faces[t + 1]); list.Add(faces[t + 2]);
        }
        var result = new Vector3[CollisionCellCount][];
        for (int c = 0; c < CollisionCellCount; c++) result[c] = cells[c].ToArray();
        return result;
    }

    /// <summary>Builds (or clears, when empty) one cell of the building collision.</summary>
    public void SetBuildingCell(Vector3[] faces, int cell)
    {
        if (_buildingBody == null)
        {
            _buildingBody = new StaticBody3D { Name = "BuildingBody" };
            AddChild(_buildingBody);
        }
        _buildingCells ??= new CollisionShape3D?[CollisionCellCount];
        _buildingCells[cell]?.QueueFree();
        _buildingCells[cell] = null;
        if (faces.Length == 0) return;
        _buildingCells[cell] = new CollisionShape3D { Shape = BuildingShape(faces) };
        _buildingBody.AddChild(_buildingCells[cell]);
    }

    private StaticBody3D? _roadBody;

    /// <summary>
    /// Bridge deck collision — the one piece of road geometry a heightfield cannot represent
    /// (a deck floats above terrain, at a different height than the ground it crosses). Every
    /// other at-grade road/path already stands on terrain collision blended toward it; see
    /// <c>TerrainMeshBuilder.ComputeRoadBlend</c>.
    /// </summary>
    /// <remarks>
    /// Road collision (decks, walls, railings, islands, sign poles, kerbs) is cut into the same
    /// 4×4 cells as the ground (<see cref="SplitByCell"/>) and committed one cell per frame.
    /// </remarks>
    public void SetRoadCell(Vector3[] faces, int cell)
    {
        _roadCells ??= new CollisionShape3D?[CollisionCellCount];
        _roadCells[cell]?.QueueFree();
        _roadCells[cell] = null;
        if (faces.Length == 0) return;

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
        _roadCells[cell] = new CollisionShape3D { Shape = shape };
        _roadBody.AddChild(_roadCells[cell]);
    }

    private CollisionShape3D?[]? _roadCells;

    private MultiMeshInstance3D? _coniferInstance;
    private MultiMeshInstance3D? _broadleafInstance;
    private MultiMeshInstance3D? _coniferFarInstance;
    private MultiMeshInstance3D? _broadleafFarInstance;

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

        // Instances go out in a shuffled order, so any prefix is an even thinning of the whole
        // tile: a far tile draws only the first VisibleInstanceCount (SetTreeDensity). Seeded,
        // so every peer and every rebuild thins the same trees.
        var slot = new int[count];
        for (int k = 0; k < count; k++) slot[k] = k;
        new Random(count).Shuffle(slot);

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
            int o = slot[i] * FloatsPerInstance;
            buffer[o + 0] = radius; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = t.X;
            buffer[o + 4] = 0; buffer[o + 5] = t.Height; buffer[o + 6] = 0; buffer[o + 7] = t.Y;
            buffer[o + 8] = 0; buffer[o + 9] = 0; buffer[o + 10] = radius; buffer[o + 11] = t.Z;
            buffer[o + 12] = tint.R; buffer[o + 13] = tint.G; buffer[o + 14] = tint.B; buffer[o + 15] = tint.A;
            i++;
        }
        return (buffer, count);
    }

    /// <summary>
    /// The MultiMeshes of a tile, built on the worker; null where a tile has none. The far pair
    /// holds the same trees as billboards, when the style has them (<see cref="Styles.StyleKit.TreeLod"/>).
    /// </summary>
    /// <para>
    /// With <see cref="Styles.MeshDetail.High"/> trees and billboards, a tile builds no 3D trees:
    /// <see cref="Near"/> hands its instances to <see cref="NearTrees"/>, which draws the ones in
    /// range from every tile, so the heavier trees cost only what is near the camera.
    /// </para>
    public sealed record TreeMeshes(MultiMesh? Conifers, MultiMesh? Broadleaves,
        MultiMesh? ConifersFar = null, MultiMesh? BroadleavesFar = null, TreeBuffers? Near = null)
    {
        /// <summary>Frees a build that is thrown away before it reached a tile.</summary>
        public void Dispose()
        {
            Conifers?.Dispose();
            Broadleaves?.Dispose();
            ConifersFar?.Dispose();
            BroadleavesFar?.Dispose();
        }
    }

    /// <summary>
    /// Builds the MultiMesh resources off the main thread. The bounds are given rather than
    /// computed: assigning a buffer makes the RenderingServer walk every instance for an AABB,
    /// 16 ms for a 60k-tree tile on the main thread, unless a custom one is already set.
    /// </summary>
    /// <param name="detail">The visual style's mesh detail: PS1's 20-triangle trees, or Cartoon's
    /// tiered conifers and puffy broadleaves (<see cref="HighDetailTrees"/>).</param>
    public static TreeMeshes BuildTreeMeshes(TreeBuffers trees, Material material, Aabb bounds,
        Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
        var far = Styles.StyleKit.TreeFarMaterial;
        MultiMesh? conifersFar = null, broadleavesFar = null;
        if (far != null)
        {
            // the same instances again as billboards: the shaders crossfade the two per tree
            conifersFar = Make(trees.Conifers, trees.ConiferCount, UnitMesh(far, 2), bounds);
            broadleavesFar = Make(trees.Broadleaves, trees.BroadleafCount, UnitMesh(far, 3), bounds);
        }
        bool high = detail == Styles.MeshDetail.High;
        if (high && far != null)
            return new TreeMeshes(null, null, conifersFar, broadleavesFar, Near: trees);
        return new TreeMeshes(Make(trees.Conifers, trees.ConiferCount, UnitMesh(material, high ? 4 : 0), bounds),
            Make(trees.Broadleaves, trees.BroadleafCount, UnitMesh(material, high ? 5 : 1), bounds),
            conifersFar, broadleavesFar);
    }

    private static readonly Dictionary<(Material, int), ArrayMesh> UnitMeshes = new();

    /// <summary>
    /// The unit tree meshes, shared by every tile: 0 cone, 1 crown, 2/3 conifer/broadleaf
    /// billboard, 4/5 <see cref="Styles.MeshDetail.High"/>'s tiered cone and puffy crown. They used
    /// to be built per tile build and never freed (a MultiMesh does not own its mesh), four
    /// RenderingServer meshes leaked with every tree tile.
    /// </summary>
    private static ArrayMesh UnitMesh(Material material, int kind)
    {
        lock (UnitMeshes)
        {
            if (!UnitMeshes.TryGetValue((material, kind), out var mesh))
                UnitMeshes[(material, kind)] = mesh = kind switch
                {
                    0 => ConeMesh(material),
                    1 => CrownMesh(material),
                    4 => TieredConeMesh(material),
                    5 => PuffCrownMesh(material),
                    _ => BillboardMesh(material, kind - 2),
                };
            return mesh;
        }
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
        Fill(ref _coniferFarInstance, "TreesFar", trees.ConifersFar);
        Fill(ref _broadleafFarInstance, "BroadleavesFar", trees.BroadleavesFar);
        // a ray-traced billboard is traced from the rendering camera, and a shadow pass would
        // trace it from the light: far trees cast no shadow (they are past the shadow range anyway)
        foreach (var farNode in new[] { _coniferFarInstance, _broadleafFarInstance })
            if (farNode != null) farNode.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (trees.Near != null) NearTrees.Register(this, trees.Near);
        else NearTrees.Unregister(this);
        // a tile too far for any of its trees to be 3D skips the 3D MultiMeshes outright: the
        // shader collapses each far tree, but the GPU would still run every vertex
        bool billboards = trees.ConifersFar != null || trees.BroadleavesFar != null;
        foreach (var near in new[] { _coniferInstance, _broadleafInstance })
            if (near?.Multimesh is { } multi)
                near.VisibilityRangeEnd = billboards ? Styles.StyleKit.TreeNearRange(multi.CustomAabb) : 0f;
        ApplyTreeDensity();
    }

    private float _treeDensity = 1f;

    /// <summary>
    /// The share of this tile's trees drawn, 0..1 (<see cref="LodPolicy.TreeDensity"/>): the
    /// first that many of the shuffled instances, so a far forest thins evenly, never in patches.
    /// </summary>
    public void SetTreeDensity(float density)
    {
        if (density == _treeDensity) return;
        _treeDensity = density;
        ApplyTreeDensity();
    }

    private void ApplyTreeDensity()
    {
        foreach (var node in new[] { _coniferInstance, _broadleafInstance, _coniferFarInstance, _broadleafFarInstance })
            if (node?.Multimesh is { } multi)
                multi.VisibleInstanceCount = !Shows(TileLayers.Trees) ? 0
                    : _treeDensity >= 1f ? -1 : (int)Math.Ceiling(multi.InstanceCount * _treeDensity);
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

    /// <summary>
    /// Cartoon's trees (<see cref="Styles.MeshDetail.High"/>), unit height, origin at the base,
    /// with smooth normals for the cel light. The far billboards trace the same shapes
    /// (<c>shaders/body/tree.gdshaderinc</c>, <c>hit_tier</c> and <c>hit_puff</c>): change both together.
    /// </summary>
    public static (ArrayMesh Conifer, ArrayMesh Broadleaf) HighDetailTrees(Material material) =>
        (UnitMesh(material, 4), UnitMesh(material, 5));

    /// <summary>Conifer: three stacked 7-sided tiers on a trunk. 54 triangles.</summary>
    private static ArrayMesh TieredConeMesh(Material material)
    {
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        Trunk(v, n, 0.06f, 0.3f);
        (float Base, float Top, float R)[] tiers = { (0.22f, 0.62f, 1.0f), (0.45f, 0.82f, 0.75f), (0.66f, 1.0f, 0.5f) };
        const int sides = 7;
        foreach (var (b, t, r) in tiers)
        {
            float slope = r / (t - b);
            Vector3 Side(float a) => new Vector3(Mathf.Cos(a), slope, Mathf.Sin(a)).Normalized();
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides, am = (a0 + a1) * 0.5f;
                var p0 = new Vector3(Mathf.Cos(a0) * r, b, Mathf.Sin(a0) * r);
                var p1 = new Vector3(Mathf.Cos(a1) * r, b, Mathf.Sin(a1) * r);
                v.Add(new Vector3(0, t, 0)); n.Add(Side(am));
                v.Add(p0); n.Add(Side(a0));
                v.Add(p1); n.Add(Side(a1));
                // the underside: a shallow dent up into the tier
                v.Add(p1); n.Add(Vector3.Down);
                v.Add(p0); n.Add(Vector3.Down);
                v.Add(new Vector3(0, b + 0.04f, 0)); n.Add(Vector3.Down);
            }
        }
        return BuildMesh(v, material, n);
    }

    /// <summary>Broadleaf: three overlapping ellipsoid puffs on a trunk. 120 triangles.</summary>
    private static ArrayMesh PuffCrownMesh(Material material)
    {
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        Trunk(v, n, 0.07f, 0.5f);
        Puff(v, n, new Vector3(0f, 0.70f, 0f), new Vector3(0.72f, 0.28f, 0.72f));
        Puff(v, n, new Vector3(0.34f, 0.56f, 0.18f), new Vector3(0.52f, 0.22f, 0.52f));
        Puff(v, n, new Vector3(-0.30f, 0.58f, -0.22f), new Vector3(0.55f, 0.23f, 0.55f));
        return BuildMesh(v, material, n);
    }

    /// <summary>A 6-sided trunk prism, corners at radius <paramref name="r"/>, from 0 to <paramref name="top"/>.</summary>
    private static void Trunk(List<Vector3> v, List<Vector3> n, float r, float top)
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
        }
    }

    /// <summary>A low-poly ellipsoid, 6 around and 4 down, with smooth normals.</summary>
    private static void Puff(List<Vector3> v, List<Vector3> n, Vector3 c, Vector3 r)
    {
        const int seg = 6, rings = 4;
        static Vector3 P(int i, int j)
        {
            float th = Mathf.Pi * j / rings, ph = Mathf.Tau * i / seg;
            return new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
        }
        void Add(Vector3 unit)
        {
            v.Add(c + unit * r);
            n.Add(new Vector3(unit.X / r.X, unit.Y / r.Y, unit.Z / r.Z).Normalized());
        }
        for (int j = 0; j < rings; j++)
            for (int i = 0; i < seg; i++)
            {
                Vector3 a = P(i, j), b = P(i + 1, j), cc = P(i, j + 1), d = P(i + 1, j + 1);
                if (j > 0) { Add(a); Add(b); Add(cc); }
                if (j < rings - 1) { Add(b); Add(d); Add(cc); }
            }
    }

    private static ArrayMesh BuildMesh(List<Vector3> verts, Material material, List<Vector3> normals)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    /// <summary>
    /// A unit quad for the billboard trees: UV = (across, up), UV2.x = kind (0 conifer, 1
    /// broadleaf). The shader rebuilds the positions around each instance, facing the camera.
    /// </summary>
    private static ArrayMesh BillboardMesh(Material material, float kind)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // these only bound the quad: the shader places it
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
            _waterInstance = new MeshInstance3D { Name = "Water", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = Shows(TileLayers.Water) };
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
        ClearCells();
        NearTrees.Unregister(this);
        foreach (var instance in new[] { _meshInstance, _roadInstance, _buildingInstance, _siteInstance, _waterInstance })
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

    // ---- Height-field collision, in cells ------------------------------------------------
    //
    // One 1001² HeightMapShape3D took ~80 ms on the main thread. The tile's ground is cut into
    // 4×4 cells of 251² samples (neighbours share their edge row), committed one per frame by
    // ChunkManager, nearest a body first.

    private const int CellsPerSide = 4;
    private const int CellQuads = (ChunkFormat.GridSize - 1) / CellsPerSide;   // 250
    public const int CollisionCellCount = CellsPerSide * CellsPerSide;
    public const int AllCollisionCells = (1 << CollisionCellCount) - 1;
    public const float TileSizeM = (float)((ChunkFormat.GridSize - 1) * ChunkFormat.SpacingM);
    private CollisionShape3D?[]? _cells;

    /// <summary>The cell holding grid sample (col, row); row 0 is the tile's north edge.</summary>
    public static int CollisionCell(int col, int row) =>
        Math.Clamp(row / CellQuads, 0, CellsPerSide - 1) * CellsPerSide
        + Math.Clamp(col / CellQuads, 0, CellsPerSide - 1);

    /// <summary>A cell's extent in tile-local metres: x east, y = z south, from the NW corner.</summary>
    public static Rect2 CollisionCellRect(int cell)
    {
        float size = (float)(CellQuads * ChunkFormat.SpacingM);
        return new Rect2(cell % CellsPerSide * size, cell / CellsPerSide * size, size, size);
    }

    /// <summary>Builds (or replaces) one cell of the ground collision from the tile's full map.</summary>
    public void SetCollisionCell(float[] collisionMap, int cell)
    {
        const int side = CellQuads + 1;
        int cx = cell % CellsPerSide, cz = cell / CellsPerSide;
        var data = new float[side * side];
        for (int r = 0; r < side; r++)
            Array.Copy(collisionMap, (cz * CellQuads + r) * ChunkFormat.GridSize + cx * CellQuads, data, r * side, side);

        float spacing = (float)ChunkFormat.SpacingM;
        var collisionShape = new CollisionShape3D
        {
            Shape = new HeightMapShape3D { MapWidth = side, MapDepth = side, MapData = data },
            // HeightMapShape3D cells are 1 unit and the shape is XZ-centred: scale to the grid
            // spacing and move to the cell's centre
            Position = new Vector3((cx * CellQuads + CellQuads / 2f) * spacing, 0f, (cz * CellQuads + CellQuads / 2f) * spacing),
            Scale = new Vector3(spacing, 1f, spacing),
        };

        if (_body == null)
        {
            _body = new StaticBody3D();
            AddChild(_body);
        }
        _cells ??= new CollisionShape3D?[CollisionCellCount];
        _cells[cell]?.QueueFree();
        _cells[cell] = collisionShape;
        _body.AddChild(collisionShape);
    }
}
