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
    /// The least angular radius drawn, as a share of the view's vertical field (the sun's too): 12 px across at 1080p
    /// and 70°, 1.5 times the true 8 px; from a 47° view down the true size. Its brightness is its halo, as a star's.
    /// It was 0.011 (#1798, 24 px), a boost zooming took back, so the moon shrank against the land as one zoomed in;
    /// the user chose this halfway one over the true size (#1842, side by side in the game).
    /// <c>--sky-disc-share</c> sets it for comparison shots (0: the true size).
    /// </summary>
    public const float DefaultShare = 0.0055f;

    public static float ScreenShare = Core.CmdArgs.Float("--sky-disc-share") ?? DefaultShare;

    /// <summary>The halo's inner falloff, as a share of the view's vertical field (its outer one is four times it).</summary>
    public const float HaloShare = 0.004f;

    /// <summary>The halo's peak, added onto the sky, for the full moon at night.</summary>
    public const float HaloStrength = 0.6f;

    /// <summary>
    /// Out to here from the Earth's centre, metres, the moon keeps its enlargement and its halo (#2047): the Earth's sphere
    /// of influence, where the game counts places from the Earth. Past it they fade, gone at <see cref="BoostGoneM"/>; from
    /// there on (Mars, Saturn, 1 AU out) the moon has its true size and brightness, a point by its magnitude when it is
    /// smaller than a few pixels, as the planets are.
    /// </summary>
    public const double BoostFullM = 925_000_000;

    /// <summary>Past here from the Earth's centre, metres, nothing of the enlargement or halo is left (#2047).</summary>
    public const double BoostGoneM = 1_850_000_000;

    /// <summary>How much of the near-Earth enlargement and halo there is <paramref name="metres"/> from the Earth's centre, 1 to 0.</summary>
    public static float NearEarth(double metres) => 1f - Mathf.SmoothStep((float)BoostFullM, (float)BoostGoneM, (float)metres);

    /// <summary><c>--moon-halo 0</c>: no halo (the comparison shots' "before").</summary>
    private static readonly bool HaloOn = Core.CmdArgs.Float("--moon-halo") is not 0f;

    private static readonly StringName SunDir = "sun_dir", Face = "face", Brightness = "brightness",
        DiscR = "disc_r", Reach = "reach", Width = "width", Strength = "strength", Centre = "centre";

    private readonly ShaderMaterial _material, _haloMaterial;
    private readonly MeshInstance3D _halo, _point;
    private readonly ShaderMaterial _pointMaterial;
    private (float Disc, float Width, float Strength, Vector2 Centre) _haloShown = (-1, -1, -1, Vector2.Zero);
    private (float, float, float, float) _lookShown = (-1f, -1f, -1f, -1f);

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
        // far from the Earth, a point by its magnitude, as the planets' (#2047, SkyBodies)
        _pointMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky_point.gdshader") };
        _pointMaterial.SetShaderParameter("single", true);
        _point = new MeshInstance3D
        {
            Name = "MoonPoint",
            TopLevel = true,
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = _pointMaterial,
            CastShadow = ShadowCastingSetting.Off,
            GIMode = GIModeEnum.Disabled,
            IgnoreOcclusionCulling = true,
            Visible = false,
        };
        AddChild(_point);
    }

    /// <summary>The disc's angular radius, the halo's strength, the point's alpha and magnitude, the near-Earth share drawn this frame (#2047's check).</summary>
    public ShownMoon Shown { get; private set; }

    /// <summary>What the moon looked like in a frame: its angular radius (rad), halo strength, point alpha and magnitude, near-Earth share.</summary>
    public readonly record struct ShownMoon(float Angular, float Halo, float Point, float Magnitude, float Near);

    /// <summary>The moon's material; the model viewer and the telescope's checks use it too.</summary>
    public static ShaderMaterial NewMaterial()
    {
        // drawn before the stars, which its depth then hides behind it (moon.gdshader)
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/moon.gdshader"), RenderPriority = -1 };
        material.SetShaderParameter("albedo", GD.Load<Texture2D>(MapPath));
        // the colonies' lights as sparks on the dark part (#1946): each one's direction in the moon's own axes, its strength
        var colonies = new Vector4[8];
        int n = 0;
        foreach (var c in Terrain.Bodies.MoonColonies.All)
        {
            float strength = c.Staffed ? 1f : c.Theme == Terrain.Bodies.ColonyTheme.ColdWar ? 0.45f : 0f;
            if (strength <= 0 || n >= colonies.Length) continue;
            double la = c.Lat * Mathf.Pi / 180, lo = c.Lon * Mathf.Pi / 180;
            colonies[n++] = new Vector4((float)(Math.Cos(la) * Math.Cos(lo)), (float)(Math.Cos(la) * Math.Sin(lo)), (float)Math.Sin(la), strength);
        }
        material.SetShaderParameter("colonies", colonies);
        material.SetShaderParameter("colony_count", n);
        return material;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Place;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Place;

    /// <summary>The Moon's own globe draws it this frame (#1848, World/Bodies/BodyViews): the sky's picture steps aside.</summary>
    public static bool HiddenByGlobe { get; set; }

    private void Place()
    {
        if (HiddenByGlobe || DayNight.Instance is not { Sky: { } sky } day || GetViewport()?.GetCamera3D() is not { } cam)
        {
            Visible = false;
            _halo.Visible = false;
            _point.Visible = false;
            Shown = default;
            return;
        }
        // where it is and how it is lit: the sky's reading, or in space the frame tree's from the camera, as its globe has it
        // (#1998: the sky counts its observer from the Earth, 90° off for the Moon from 140,000 km out sunward)
        var moonDir = sky.Moon;
        double moonDistance = sky.MoonDistance;
        var sunDir = sky.Sun;
        var face = sky.MoonFace;
        if (Bodies.BodyLook.Seen(Core.BodyId.Moon, cam.GlobalPosition, out var seenDir, out double seenDistance, out var seenFace, out var seenSun))
            (moonDir, moonDistance, face, sunDir) = (seenDir, seenDistance, seenFace, seenSun);
        // just inside the far plane at its true angular radius, at least ScreenShare of the view's height (#1842) near the
        // Earth only (#2047): from other planets its true size
        float near = NearEarthOf(cam);
        float distance = cam.Far * 0.9f;
        float fov = Mathf.DegToRad(Core.CameraLens.FovOf(cam));
        float trueAngular = (float)(RadiusMetres / moonDistance);
        float angular = Mathf.Max(trueAngular, fov * ScreenShare * near);
        float radius = distance * angular;
        GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * radius), cam.GlobalPosition + moonDir * distance);
        Visible = true;
        _material.SetShaderParameter(SunDir, sunDir);
        _material.SetShaderParameter(Face, face);
        // above the air the sky behind it is black whatever the hour below: the night's brightness (#1798)
        float night = Mathf.Max(day.Night, Atmosphere.Space);
        _material.SetShaderParameter(Brightness, Mathf.Lerp(0.5f, 1.8f, night));
        // from space its globe's own look (#1998, Bodies.BodyLook): the globe takes it over past a size with no change
        float space = Bodies.BodyLook.InSpace;
        Bodies.BodyLook.Write(_material, Core.CelestialBody.Moon, space, ref _lookShown);
        float halo = PlaceHalo(cam, sunDir, moonDir, distance, angular, fov, night, space, near);
        float pixelsPerRadian = GetViewport().GetVisibleRect().Size.Y * 0.5f / Mathf.Tan(fov * 0.5f);
        var (point, magnitude) = PlacePoint(cam, sunDir, moonDir, moonDistance, distance, trueAngular * pixelsPerRadian, near);
        Shown = new ShownMoon(angular, halo, point, magnitude, near);
    }

    /// <summary>The share of the near-Earth look for this camera: 1 on a flat world or with no Earth to count from.</summary>
    private static float NearEarthOf(Camera3D cam)
    {
        if (Core.OriginShifter.Instance?.Origin is not { } origin || !Core.OriginShifter.CrossBodies) return 1f;
        var earth = origin.ToWorld(Core.GlobalPos.On(Core.BodyId.Earth, new Terrain.Format.EcefPos(0, 0, 0)));
        return NearEarth(earth.DistanceTo(cam.GlobalPosition));
    }

    /// <summary>
    /// Far from the Earth, the moon as the planets are when their disc is too small to read (#2047): a point sized and lit
    /// by its true magnitude from the camera (<see cref="StarField.Look"/>), gone as its disc takes over (1.5 to 4 px) and
    /// near the Earth, where the halo stands for its brightness.
    /// </summary>
    private (float Alpha, float Magnitude) PlacePoint(Camera3D cam, Vector3 sunDir, Vector3 moonDir, double moonDistance, float distance,
        float discPixels, float near)
    {
        float magnitude = float.NaN;
        float alpha = 0f;
        if (near < 1f && DayNight.Instance is { Sky: { } sky })
        {
            double sunAu = Core.Frames.Offset(Core.BodyId.Moon, Core.BodyId.Sun, sky.Moment).Length / Core.Ephemeris.AuMetres;
            magnitude = (float)SkyBodies.MagnitudeSeen(Core.SkyBody.Moon, sunAu, sunDir, moonDir, moonDistance, sky.Moment.Utc);
            var (px, a) = StarField.Look(magnitude);
            // fainter than the eye's 7th magnitude, none: the sky's planets' cut (SkyBodies)
            alpha = magnitude < 7f ? a * (1f - Mathf.SmoothStep(1.5f, 4f, discPixels)) * (1f - near) : 0f;
            if (alpha > 0.01f)
            {
                var toMoon = moonDir.Normalized();
                _point.GlobalPosition = cam.GlobalPosition + toMoon * distance;
                _pointMaterial.SetShaderParameter(PointSky, new Basis(toMoon, Vector3.Up, Vector3.Back));
                _pointMaterial.SetShaderParameter(PointDistance, distance);
                _pointMaterial.SetShaderParameter(PointColor, new Color(1f, 0.98f, 0.94f, alpha));
                _pointMaterial.SetShaderParameter(PointSize, px);
                _pointMaterial.SetShaderParameter(PointDaylight, 0f);
            }
        }
        _point.Visible = alpha > 0.01f;
        return (_point.Visible ? alpha : 0f, magnitude);
    }

    private static readonly StringName PointSky = "sky", PointDistance = "distance", PointColor = "point_color", PointSize = "point_size",
        PointDaylight = "daylight";

    private float PlaceHalo(Camera3D cam, Vector3 sunDir, Vector3 moonDir, float distance, float angular, float fov, float night, float space,
        float near)
    {
        // in space the halo stands for a disc too small to read; one a few pixels wide is the globe's look, no glow over it
        // (#1998: past the handover from the Moon it read as a glow, not the Moon)
        float resolved = Mathf.Lerp(1f, 1f - Mathf.SmoothStep(0.3f * Bodies.BodyLook.GlobeShare, 0.8f * Bodies.BodyLook.GlobeShare, angular / fov), space);
        // near the Earth only (#2047): far off the point by its magnitude stands for its brightness, as a planet's
        float strength = HaloOn ? HaloStrength * night * PhaseGlow(sunDir, moonDir) * (1f - Mathf.SmoothStep(0.03f, 0.1f, angular / fov)) * resolved * near : 0f;
        _halo.Visible = strength > 0.002f;
        if (!_halo.Visible) return 0f;
        float width = fov * HaloShare;
        float reach = angular + width * 16f;
        // a quad square to the moon's direction, its up from the camera's; drawn over the disc too (moon_halo.gdshader)
        var toMoon = moonDir.Normalized();
        var up = cam.GlobalBasis.Y;
        if (Mathf.Abs(up.Dot(toMoon)) > 0.99f) up = cam.GlobalBasis.X;
        var right = up.Cross(toMoon).Normalized();
        up = toMoon.Cross(right).Normalized();
        // in front of the disc's nearest point (its centre's distance less two radii), so the glow is drawn over the disc
        // and not under it (#1845): pushed 1 % in clip z it was ~0.1 % of the distance nearer, near the far plane, and the
        // sphere bulges by its angular radius (0.7 % at the least size): the disc's unlit part hid the glow, a black moon
        // in a bright ring, from space over the night side above all (the halo at full strength there)
        float at = distance * (1f - 2f * angular);
        float half = at * Mathf.Tan(reach);
        _halo.GlobalTransform = new Transform3D(new Basis(right * half, up * half, -toMoon), cam.GlobalPosition + toMoon * at);
        // the glow comes from the lit part: off the disc's centre toward the sun as the phase thins (a crescent's dark
        // disc ringed by a centred glow read as an eclipse)
        var toSun = sunDir.Normalized();
        float lit = (1f - toSun.Dot(toMoon)) * 0.5f;
        var side = new Vector2(toSun.Dot(right), toSun.Dot(up));
        var centre = side.LengthSquared() > 1e-8f ? side.Normalized() * angular * 0.6f * (1f - lit) : Vector2.Zero;
        var shown = (angular, width, strength, centre);
        if (shown == _haloShown) return strength;
        _haloShown = shown;
        _haloMaterial.SetShaderParameter(DiscR, angular);
        _haloMaterial.SetShaderParameter(Reach, reach);
        _haloMaterial.SetShaderParameter(Width, width);
        _haloMaterial.SetShaderParameter(Strength, strength);
        _haloMaterial.SetShaderParameter(Centre, centre);
        return strength;
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
