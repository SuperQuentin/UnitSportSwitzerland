using System.Text.Json;
using Godot;

using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// The stars (#1444): the Yale Bright Star Catalogue's 9,096 stars to magnitude 6.5
/// (<c>assets/space/stars_bsc5.json</c>), each a quad at its J2000 direction in one mesh built once,
/// turned each frame by the celestial sphere's axes (<see cref="DayNight.RealSky.Stars"/>: sidereal
/// time at the camera's place) in <c>shaders/sky_point.gdshader</c>. Brighter stars are bigger and
/// whiter; the colour follows B-V. They come out with the night and sink into the horizon's haze.
/// Only under the real sun.
/// </summary>
public partial class StarField : MeshInstance3D, IOriginShiftAware
{
    public const string CatalogPath = "res://assets/space/stars_bsc5.json";

    private static readonly StringName Sky = "sky", Distance = "distance", Dazzle = "dazzle";

    /// <summary>The cull box's least half side, m: round the node, which keeps near the camera (<see cref="FollowM"/>).</summary>
    public const float BoxM = 1e7f;

    /// <summary>
    /// The cull box's half side for a camera with this far plane (#2035): past the far plane, so the box always reaches past
    /// the near plane into the view. 5 AU out the near plane is 7.5e7 m: a 1e7 m box round the camera lay wholly in front
    /// of it, out of the frustum, and every star was culled ("far enough, all stars disappear").
    /// </summary>
    public static float BoxFor(float far) => Mathf.Max(BoxM, far * 1.5f);

    private float _box = BoxM;

    /// <summary>The node goes to the camera once it is this far from it, m (#2035).</summary>
    public const float FollowM = 1e6f;

    private readonly ShaderMaterial _material;
    private float _distance = -1f, _dazzle = -1f;

    /// <summary>
    /// The node stays at the origin (#1989): the shader places every star from the camera, but the cull box is the
    /// node's, 1e7 m round it. An origin shift moves every top-level node; the shift to the Moon's tangent frame left the
    /// field ~4e8 m away, and every star was culled (as after ~10,000 km of flight on the Earth).
    /// </summary>
    public void OnOriginShifted(OriginShift shift) => GlobalTransform = Transform3D.Identity;

    /// <summary>
    /// How dazzled the eye is (#1989): 0 at night or with air (the sky's own light hides the stars there), 1 with the sun
    /// up over an airless ground. Then only the brightest stars show (<c>sky_point.gdshader</c>'s <c>dazzle</c>): the
    /// eye on the sunlit ground of the Moon does not see the faint ones, as a camera's short exposure does not.
    /// </summary>
    public static float DazzleOf(bool airless, float sunElevationDeg, float nearGround = 1f) =>
        airless ? Mathf.SmoothStep(-2f, 4f, sunElevationDeg) * nearGround : 0f;

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
        CustomAabb = new Aabb(-Vector3.One * BoxM, Vector3.One * 2 * BoxM);
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
        // the cull box goes with the camera (#2035): the shader places every star from the camera, but Godot culls the node
        // by its box. At the spectator's top speed (#2027) the camera flies 2e7 m from the origin between re-anchors (in
        // Space the origin does not follow it vertically) and out of the box: "when moving toward a planet, all stars
        // disappear". Moved when the camera is a tenth of the box's half side away, not every frame
        if (GlobalPosition.DistanceSquaredTo(cam.GlobalPosition) > FollowM * FollowM) GlobalPosition = cam.GlobalPosition;
        float box = BoxFor(cam.Far);
        if (box > _box * 1.05f || box < _box * 0.5f)
        {
            _box = box;
            CustomAabb = new Aabb(-Vector3.One * box, Vector3.One * 2 * box);
        }
        _material.SetShaderParameter(Sky, sky.Stars);
        // only near the ground (Atmosphere.NearGround): 68,000 km off the Moon the field was thin by day (#1989 follow-up)
        float dazzle = DazzleOf(Atmosphere.Airless, DayNight.Instance.SunElevationDeg, Atmosphere.NearGround);
        if (dazzle != _dazzle) _material.SetShaderParameter(Dazzle, _dazzle = dazzle);
        float distance = cam.Far * 0.95f * DistanceScale;
        if (distance != _distance)
        {
            _distance = distance;
            _material.SetShaderParameter(Distance, distance);
        }
    }
}
