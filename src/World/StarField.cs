using System.Text.Json;
using Godot;

namespace UnitSport.World;

/// <summary>
/// The stars (#1444): the Yale Bright Star Catalogue's 9,096 stars to magnitude 6.5
/// (<c>assets/space/stars_bsc5.json</c>), each a quad at its J2000 direction in one mesh built once,
/// turned each frame by the celestial sphere's axes (<see cref="DayNight.RealSky.Stars"/>: sidereal
/// time at the camera's place) in <c>shaders/sky_point.gdshader</c>. Brighter stars are bigger and
/// whiter; the colour follows B-V. They come out with the night and sink into the horizon's haze.
/// Only under the real sun.
/// </summary>
public partial class StarField : MeshInstance3D
{
    public const string CatalogPath = "res://assets/space/stars_bsc5.json";

    private static readonly StringName Sky = "sky", Distance = "distance";

    private readonly ShaderMaterial _material;
    private float _distance = -1f;

    /// <summary>How many stars the catalogue gave (0 when it could not be read).</summary>
    public int Count { get; private set; }

    public StarField()
    {
        Name = "StarField";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;   // the sky: depth hides it, never the occlusion buffer (SkyBodies)
        GIMode = GIModeEnum.Disabled;
        // placed by the shader at the far plane: never culled by where the node is
        CustomAabb = new Aabb(-Vector3.One * 1e7f, Vector3.One * 2e7f);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky_point.gdshader") };
        MaterialOverride = _material;
        Mesh = Build(Load(), out int count);
        Count = count;
    }

    /// <summary>The catalogue: J2000 right ascension and declination (degrees), V magnitude, B-V.</summary>
    public static float[][] Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(CatalogPath));
            return doc.RootElement.GetProperty("stars").EnumerateArray()
                .Select(s => s.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stars] {CatalogPath}: {e.Message}");
            return [];
        }
    }

    /// <summary>A star's radius in pixels and its brightness, by magnitude: Sirius 4 px, a 6th-magnitude star a faint 1.2.</summary>
    public static (float Pixels, float Alpha) Look(float magnitude) =>
        (Mathf.Clamp(3.4f - 0.4f * magnitude, 1.2f, 4.6f), Mathf.Clamp(Mathf.Pow(10f, -0.4f * (magnitude - 2.5f)), 0.25f, 1f));

    /// <summary>A star's tint from its B-V index: blue-white to orange.</summary>
    public static Color Tint(float bv)
    {
        float t = Mathf.Clamp((bv + 0.3f) / 2.0f, 0f, 1f);
        var blue = new Color(0.72f, 0.82f, 1f);
        var white = new Color(1f, 0.97f, 0.92f);
        var orange = new Color(1f, 0.72f, 0.5f);
        return t < 0.45f ? blue.Lerp(white, t / 0.45f) : white.Lerp(orange, (t - 0.45f) / 0.55f);
    }

    private static ArrayMesh Build(float[][] stars, out int count)
    {
        count = stars.Length;
        var verts = new Vector3[count * 4];
        var uv = new Vector2[count * 4];
        var uv2 = new Vector2[count * 4];
        var colors = new Color[count * 4];
        var index = new int[count * 6];
        var corners = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        for (int i = 0; i < count; i++)
        {
            float ra = Mathf.DegToRad(stars[i][0]), dec = Mathf.DegToRad(stars[i][1]);
            var dir = new Vector3(Mathf.Cos(dec) * Mathf.Cos(ra), Mathf.Cos(dec) * Mathf.Sin(ra), Mathf.Sin(dec));
            var (px, alpha) = Look(stars[i][2]);
            var c = Tint(stars[i][3]) with { A = alpha };
            for (int k = 0; k < 4; k++)
            {
                verts[i * 4 + k] = dir;
                uv[i * 4 + k] = corners[k];
                uv2[i * 4 + k] = new Vector2(px, 0);
                colors[i * 4 + k] = c;
            }
            int b = i * 4, j = i * 6;
            index[j] = b; index[j + 1] = b + 1; index[j + 2] = b + 2;
            index[j + 3] = b; index[j + 4] = b + 2; index[j + 5] = b + 3;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = index;
        var mesh = new ArrayMesh();
        if (count > 0) mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Place;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Place;

    /// <summary>A debugging aid (<c>--shuttlefilm drive,stardist=0.5</c>): the stars' distance, as a share of the usual.</summary>
    public static float DistanceScale = 1f;

    private void Place()
    {
        if (Count == 0 || DayNight.Instance is not { Sky: { } sky } || GetViewport()?.GetCamera3D() is not { } cam)
        {
            Visible = false;
            return;
        }
        Visible = true;
        _material.SetShaderParameter(Sky, sky.Stars);
        float distance = cam.Far * 0.95f * DistanceScale;
        if (distance != _distance)
        {
            _distance = distance;
            _material.SetShaderParameter(Distance, distance);
        }
    }
}
