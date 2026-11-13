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

    /// <summary>
    /// The least angular radius drawn, as a share of the view's vertical field (the sun's too). 0: always the true
    /// size (#1842), its brightness shown by its halo as a star's is, never by a bigger disc. It was 0.011 (#1798,
    /// 24 px across at 1080p and 70°): a boost zooming took back, so the moon shrank against the land as one zoomed.
    /// <c>--sky-disc-share</c> sets it for comparison shots.
    /// </summary>
    public static float ScreenShare = Core.CmdArgs.Float("--sky-disc-share") ?? 0f;

    /// <summary>The halo's inner falloff, as a share of the view's vertical field (its outer one is four times it).</summary>
    public const float HaloShare = 0.004f;

    /// <summary>The halo's peak, added onto the sky, for the full moon at night.</summary>
    public const float HaloStrength = 0.6f;

    /// <summary><c>--moon-halo 0</c>: no halo (the comparison shots' "before").</summary>
    private static readonly bool HaloOn = Core.CmdArgs.Float("--moon-halo") is not 0f;

    private static readonly StringName SunDir = "sun_dir", Face = "face", Brightness = "brightness",
        DiscR = "disc_r", Reach = "reach", Width = "width", Strength = "strength", Centre = "centre";

    private readonly ShaderMaterial _material, _haloMaterial;
    private readonly MeshInstance3D _halo;
    private (float Disc, float Width, float Strength, Vector2 Centre) _haloShown = (-1, -1, -1, Vector2.Zero);

    public MoonDisc()
    {
        Name = "MoonDisc";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        // never occlusion-culled: depth hides it behind a ridge; the buildings' coarse occlusion buffer culled it when
        // looked at head-on (it showed at the screen's edge and went near the middle)
        IgnoreOcclusionCulling = true;
        GIMode = GIModeEnum.Disabled;
        Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 96, Rings = 48 };
        _material = NewMaterial();
        MaterialOverride = _material;
        // its brightness: a glow round the disc, its size a share of the screen as a star's (#1842)
        _haloMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/moon_halo.gdshader") };
        _halo = new MeshInstance3D
        {
            Name = "MoonHalo",
            TopLevel = true,
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = _haloMaterial,
            CastShadow = ShadowCastingSetting.Off,
            GIMode = GIModeEnum.Disabled,
            IgnoreOcclusionCulling = true,
            Visible = false,
        };
        AddChild(_halo);
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
            _halo.Visible = false;
            return;
        }
        // just inside the far plane at its true angular radius (#1842; at least ScreenShare of the view's height when set)
        float distance = cam.Far * 0.9f;
        float fov = Mathf.DegToRad(Core.CameraLens.FovOf(cam));
        float angular = Mathf.Max((float)(RadiusMetres / sky.MoonDistance), fov * ScreenShare);
        float radius = distance * angular;
        GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * radius), cam.GlobalPosition + sky.Moon * distance);
        Visible = true;
        _material.SetShaderParameter(SunDir, sky.Sun);
        _material.SetShaderParameter(Face, sky.MoonFace);
        // above the air the sky behind it is black whatever the hour below: the night's brightness (#1798)
        float night = Mathf.Max(day.Night, Atmosphere.Space);
        _material.SetShaderParameter(Brightness, Mathf.Lerp(0.5f, 1.8f, night));
        PlaceHalo(cam, sky, distance, angular, fov, night);
    }

    private void PlaceHalo(Camera3D cam, DayNight.RealSky sky, float distance, float angular, float fov, float night)
    {
        float strength = HaloOn ? HaloStrength * night * PhaseGlow(sky.Sun, sky.Moon) * (1f - Mathf.SmoothStep(0.03f, 0.1f, angular / fov)) : 0f;
        _halo.Visible = strength > 0.002f;
        if (!_halo.Visible) return;
        float width = fov * HaloShare;
        float reach = angular + width * 16f;
        // a quad square to the moon's direction, its up from the camera's; drawn over the disc too (moon_halo.gdshader)
        var toMoon = sky.Moon.Normalized();
        var up = cam.GlobalBasis.Y;
        if (Mathf.Abs(up.Dot(toMoon)) > 0.99f) up = cam.GlobalBasis.X;
        var right = up.Cross(toMoon).Normalized();
        up = toMoon.Cross(right).Normalized();
        float half = distance * Mathf.Tan(reach);
        _halo.GlobalTransform = new Transform3D(new Basis(right * half, up * half, -toMoon), cam.GlobalPosition + toMoon * distance);
        // the glow comes from the lit part: off the disc's centre toward the sun as the phase thins (a crescent's dark
        // disc ringed by a centred glow read as an eclipse)
        var toSun = sky.Sun.Normalized();
        float lit = (1f - toSun.Dot(toMoon)) * 0.5f;
        var side = new Vector2(toSun.Dot(right), toSun.Dot(up));
        var centre = side.LengthSquared() > 1e-8f ? side.Normalized() * angular * 0.6f * (1f - lit) : Vector2.Zero;
        var shown = (angular, width, strength, centre);
        if (shown == _haloShown) return;
        _haloShown = shown;
        _haloMaterial.SetShaderParameter(DiscR, angular);
        _haloMaterial.SetShaderParameter(Reach, reach);
        _haloMaterial.SetShaderParameter(Width, width);
        _haloMaterial.SetShaderParameter(Strength, strength);
        _haloMaterial.SetShaderParameter(Centre, centre);
    }

    /// <summary>
    /// The halo's share by the phase, 0.25 to 1: the moon's magnitude below the full moon's (Allen's phase law, 2.6 at a
    /// quarter, 6 at a thin crescent) mapped as the eye takes brightness, by magnitude, so a crescent still glows.
    /// </summary>
    public static float PhaseGlow(Vector3 sun, Vector3 moon)
    {
        float phase = 180f - Mathf.RadToDeg(sun.Normalized().AngleTo(moon.Normalized()));
        float fainter = 0.026f * phase + 4e-9f * phase * phase * phase * phase;
        return Mathf.Lerp(0.25f, 1f, Mathf.Clamp(1f - fainter / 6f, 0f, 1f));
    }
}
