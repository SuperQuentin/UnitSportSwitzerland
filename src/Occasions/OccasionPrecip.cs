using Godot;
using UnitSport.Core;

namespace UnitSport.Occasions;

/// <summary>
/// Falling snow around the camera while the top occasion's <see cref="OccasionAtmosphere.Snowfall"/>
/// is above zero (Christmas). One static mesh animated entirely by <c>ps1_snowfall.gdshader</c>;
/// this node only builds it once, sets its density — heavier at altitude — and hides it indoors.
/// Client only.
/// </summary>
public partial class OccasionPrecip : Node3D
{
    private const int Flakes = 4000;
    private const float Box = 60f;

    private readonly Func<bool> _indoors;
    private MeshInstance3D _snow = null!;
    private ShaderMaterial _material = null!;

    public OccasionPrecip(Func<bool> indoors)
    {
        Name = "OccasionPrecip";
        _indoors = indoors;
    }

    public OccasionPrecip() : this(() => false) { }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ps1_snowfall.gdshader") };
        _material.SetShaderParameter("box", Box);
        FogUniforms.Apply(_material);
        _snow = new MeshInstance3D
        {
            Name = "Snowfall",
            Mesh = BuildMesh(),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_snow);
    }

    public override void _Process(double delta)
    {
        float snowfall = OccasionManager.Instance?.Atmosphere?.Atmosphere.Snowfall ?? 0f;
        bool show = snowfall > 0f && !_indoors();
        _snow.Visible = show;
        if (!show) return;
        float altitude = GetViewport()?.GetCamera3D()?.GlobalPosition.Y ?? 0f;
        _material.SetShaderParameter("density", snowfall * Mathf.Lerp(0.45f, 1f, Mathf.SmoothStep(700f, 1800f, altitude)));
    }

    /// <summary>Every flake a quad whose corners carry its home, its corner and its hash; placed by the shader.</summary>
    private static ArrayMesh BuildMesh()
    {
        var rng = new Random(1225);
        var verts = new Vector3[Flakes * 4];
        var uv = new Vector2[Flakes * 4];
        var uv2 = new Vector2[Flakes * 4];
        var idx = new int[Flakes * 6];
        Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        for (int f = 0; f < Flakes; f++)
        {
            var home = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * Box;
            float hash = (float)rng.NextDouble();
            for (int c = 0; c < 4; c++)
            {
                verts[f * 4 + c] = home;
                uv[f * 4 + c] = corners[c];
                uv2[f * 4 + c] = new Vector2(hash, 0);
            }
            int[] quad = [0, 1, 2, 0, 2, 3];
            for (int k = 0; k < 6; k++) idx[f * 6 + k] = f * 4 + quad[k];
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // the shader moves every flake to wherever the camera is: never cull it
        mesh.CustomAabb = new Aabb(new Vector3(-1e5f, -1e4f, -1e5f), new Vector3(2e5f, 2e4f, 2e5f));
        return mesh;
    }
}
