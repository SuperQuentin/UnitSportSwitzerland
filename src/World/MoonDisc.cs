using Godot;

namespace UnitSport.World;

/// <summary>
/// The real moon in the sky (#1440): a sphere at its ephemeris place (<see cref="DayNight.Sky"/>),
/// its face from NASA's LROC map turned by its own axes and lit by the real sun
/// (<c>shaders/moon.gdshader</c>), so its phase and libration are right in the telescope as
/// with the naked eye. Nothing under the fixed sun.
///
/// <para>
/// The moon is 384,000 km away and the camera sees 70 km, so it is drawn just inside the far plane
/// at its true angular size, placed each frame before drawing (as <see cref="Styles.CameraGlobal"/>
/// does), for whichever camera is current: a far ridge still hides it. A mesh built once; only its
/// transform and two shader parameters move.
/// </para>
/// </summary>
public partial class MoonDisc : MeshInstance3D
{
    public const double RadiusMetres = 1_737_400;

    /// <summary>The map: NASA SVS CGI Moon Kit (4720), LROC WAC colour, 4096 x 2048, centred on 0° E.</summary>
    public const string MapPath = "res://assets/space/moon_lroc_4k.jpg";

    /// <summary>The least angular radius drawn, as a share of the view's vertical field: 24 px across at 1080p and 70°.</summary>
    public const float MinScreenShare = 0.011f;

    private static readonly StringName SunDir = "sun_dir", Face = "face", Brightness = "brightness";

    private readonly ShaderMaterial _material;

    public MoonDisc()
    {
        Name = "MoonDisc";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        GIMode = GIModeEnum.Disabled;
        Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 96, Rings = 48 };
        _material = NewMaterial();
        MaterialOverride = _material;
    }

    /// <summary>The moon's material; the model viewer and the telescope's checks use it too.</summary>
    public static ShaderMaterial NewMaterial()
    {
        // drawn before the stars, which its depth then hides behind it (moon.gdshader)
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/moon.gdshader"), RenderPriority = -1 };
        material.SetShaderParameter("albedo", GD.Load<Texture2D>(MapPath));
        return material;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Place;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Place;

    private void Place()
    {
        if (DayNight.Instance is not { Sky: { } sky } day || GetViewport()?.GetCamera3D() is not { } cam)
        {
            Visible = false;
            return;
        }
        // just inside the far plane, at its true angular radius, but never smaller than MinScreenShare of the view's
        // height (#1798): true, it was 7 px across at a normal field of view, a star among stars, and a crescent a
        // sliver of one pixel; the eye sees the moon bigger than that. The telescope's narrow views keep the true size.
        float distance = cam.Far * 0.9f;
        float angular = Mathf.Max((float)(RadiusMetres / sky.MoonDistance), Mathf.DegToRad(cam.Fov) * MinScreenShare);
        float radius = distance * angular;
        GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * radius), cam.GlobalPosition + sky.Moon * distance);
        Visible = true;
        _material.SetShaderParameter(SunDir, sky.Sun);
        _material.SetShaderParameter(Face, sky.MoonFace);
        // above the air the sky behind it is black whatever the hour below: the night's brightness (#1798)
        _material.SetShaderParameter(Brightness, Mathf.Lerp(0.5f, 1.8f, Mathf.Max(day.Night, Atmosphere.Space)));
    }
}
