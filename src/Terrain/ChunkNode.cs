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

    public void SetMesh(TerrainMeshBuilder.MeshData data, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);

        if (_meshInstance == null)
        {
            _meshInstance = new MeshInstance3D();
            AddChild(_meshInstance);
        }
        _meshInstance.Mesh = mesh;
    }

    public void SetRoads(RoadMeshBuilder.MeshData data, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2s;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);

        if (_roadInstance == null)
        {
            _roadInstance = new MeshInstance3D { Name = "Roads" };
            AddChild(_roadInstance);
        }
        _roadInstance.Mesh = mesh;
    }

    private MeshInstance3D? _buildingInstance;
    private StaticBody3D? _buildingBody;

    public void SetBuildings(BuildingMeshBuilder.MeshData data, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2s;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);

        if (_buildingInstance == null)
        {
            _buildingInstance = new MeshInstance3D { Name = "Buildings" };
            AddChild(_buildingInstance);
        }
        _buildingInstance.Mesh = mesh;
    }

    public void SetBuildingCollision(Vector3[] faces)
    {
        var shape = new ConcavePolygonShape3D { Data = faces };
        if (_buildingBody == null)
        {
            _buildingBody = new StaticBody3D { Name = "BuildingBody" };
            AddChild(_buildingBody);
        }
        foreach (Node child in _buildingBody.GetChildren())
            child.QueueFree();
        _buildingBody.AddChild(new CollisionShape3D { Shape = shape });
    }

    private MultiMeshInstance3D? _coniferInstance;
    private MultiMeshInstance3D? _broadleafInstance;

    /// <summary>
    /// Trees as MultiMeshes per tile — 8k+ instances per tile makes individual nodes
    /// impossible. There are two, because a MultiMesh carries exactly one mesh: conifers
    /// and shrubs share a spire, while fruit trees and TLM's surveyed single trees are
    /// broadleaves standing in the open and need a round crown to read as such.
    /// </summary>
    public void SetTrees(IReadOnlyList<TreeInstance> trees, Material material)
    {
        // Kind: 0 conifer, 1 shrub, 2 fruit tree, 3 surveyed solitary broadleaf
        Fill(ref _coniferInstance, "Trees", trees, t => t.Kind is 0 or 1, ConeMesh(material));
        Fill(ref _broadleafInstance, "Broadleaves", trees, t => t.Kind is 2 or 3, CrownMesh(material));
    }

    private void Fill(ref MultiMeshInstance3D? node, string name, IReadOnlyList<TreeInstance> trees,
        Func<TreeInstance, bool> wanted, ArrayMesh mesh)
    {
        var picked = new List<TreeInstance>();
        foreach (var t in trees) if (wanted(t)) picked.Add(t);

        if (picked.Count == 0)
        {
            if (node != null) node.Visible = false;
            return;
        }

        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = picked.Count,
        };

        for (int i = 0; i < picked.Count; i++)
        {
            var t = picked[i];
            // scale the shared unit mesh to this tree's height; girth separates the kinds
            float slenderness = t.Kind switch
            {
                1 => 0.42f,   // shrub
                2 => 0.34f,   // fruit tree — small and dense
                3 => 0.40f,   // solitary broadleaf — wide, nothing crowding it
                _ => 0.26f,   // conifer
            };
            float radius = t.Height * slenderness;
            var basis = new Basis(
                new Vector3(radius, 0, 0),
                new Vector3(0, t.Height, 0),
                new Vector3(0, 0, radius));
            multi.SetInstanceTransform(i, new Transform3D(basis, new Vector3(t.X, t.Y, t.Z)));

            // vary tone per tree so a forest is not one flat mass
            float v = (i * 0.6180339f) % 1f;
            var tint = t.Kind switch
            {
                1 => new Color(0.30f, 0.36f, 0.20f),
                2 => new Color(0.28f + v * 0.06f, 0.40f + v * 0.07f, 0.18f + v * 0.04f),
                3 => new Color(0.21f + v * 0.08f, 0.35f + v * 0.10f, 0.16f + v * 0.05f),
                _ => new Color(0.13f + v * 0.07f, 0.24f + v * 0.09f, 0.12f + v * 0.05f),
            };
            multi.SetInstanceColor(i, tint.SrgbToLinear());
        }

        if (node == null)
        {
            node = new MultiMeshInstance3D { Name = name };
            AddChild(node);
        }
        node.Visible = true;
        node.Multimesh = multi;
    }

    /// <summary>Unit-height 5-sided cone, origin at the base.</summary>
    private static ArrayMesh ConeMesh(Material material)
    {
        const int sides = 5;
        var verts = new List<Vector3>();
        var apex = new Vector3(0, 1, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides;
            float a1 = Mathf.Tau * (i + 1) / sides;
            var p0 = new Vector3(Mathf.Cos(a0), 0.15f, Mathf.Sin(a0));
            var p1 = new Vector3(Mathf.Cos(a1), 0.15f, Mathf.Sin(a1));
            verts.Add(apex); verts.Add(p0); verts.Add(p1);
            verts.Add(p1); verts.Add(p0); verts.Add(new Vector3(0, 0, 0));
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

    private static ArrayMesh BuildMesh(List<Vector3> verts, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    private MeshInstance3D? _waterInstance;

    public void SetWater(WaterMeshBuilder.MeshData data, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);

        if (_waterInstance == null)
        {
            _waterInstance = new MeshInstance3D { Name = "Water" };
            AddChild(_waterInstance);
        }
        _waterInstance.Mesh = mesh;
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
            // HeightMapShape3D cells are 1 unit and the shape is XZ-centered; scale to the
            // 2 m grid and move to the tile center. Verified against the height sampler in M3.
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
