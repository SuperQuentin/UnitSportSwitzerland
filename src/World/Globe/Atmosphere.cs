using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The air seen from high up (#1430 H4; docs/plans/ellipsoid-handover.md §6): a scattering shell
/// round the globe (<c>shaders/atmosphere.gdshader</c>: the blue limb, the haze over the Earth, the
/// dark sky above) fading in as the camera climbs from <see cref="FromM"/> to <see cref="FullM"/>,
/// while the style's own sky (a procedural gradient, or PS1's flat colour) dims to black under it:
/// above the air there is no sky to draw, only what the shell scatters and the stars, which come out by
/// day there (the <c>world_space</c> global, read by <c>sky_point.gdshader</c>). Client only, with the
/// globe.
/// </summary>
public partial class Atmosphere : Node3D
{
    /// <summary>The shell starts showing, and the sky dimming, above this camera altitude, metres.</summary>
    public const double FromM = 15_000;

    /// <summary>All shell, no sky, above this one.</summary>
    public const double FullM = 40_000;

    /// <summary>The top of the shell above the ground, metres.</summary>
    public const double ThicknessM = 100_000;

    public WorldOrigin? Origin { get; set; }

    private static readonly StringName PlanetCentre = "planet_centre", PlanetR = "planet_r", AtmoR = "atmo_r",
        SunDir = "sun_dir", Intensity = "intensity", WorldSpace = "world_space", TrueSunParam = "world_true_sun";

    private MeshInstance3D _shell = null!;
    private ShaderMaterial _material = null!;
    private float _space = -1, _skyEnergy = 1, _radius;
    private Vector3 _centre = new(float.NaN, 0, 0), _sun;
    private Color _bgBase, _bgWritten;
    private Godot.Environment? _env;

    /// <summary>How far into space a camera altitude is: 0 in the air, 1 above it (smooth).</summary>
    public static float SpaceAt(double altitude) =>
        (float)Mathf.SmoothStep(FromM, FullM, altitude);

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/atmosphere.gdshader") };
        float r = (float)(Ellipsoid.Wgs84.A + ThicknessM);
        _shell = new MeshInstance3D
        {
            Name = "Shell",
            // a quad the shader lays over the whole screen (atmosphere.gdshader), never culled: the air is marched
            // per pixel from the screen and the depth; a sphere's clamped back faces were clipped round the view (#1528)
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            CustomAabb = new Aabb(new Vector3(-r, -r, -r), new Vector3(2 * r, 2 * r, 2 * r)),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_shell);
    }

    /// <summary>The last <c>world_true_sun</c> written, in world axes (zero before the first): <c>--fly --flyterminator</c> reads it.</summary>
    public static Vector3 TrueSunWritten { get; private set; }

    private Vector3 _trueSun = Vector3.Up, _fixedSun = Vector3.Up;
    private EcefPos _sunEcef;
    private bool _realSun, _sunWritten;

    /// <summary>
    /// Toward the real sun, not the scene's light: at night that is the moon. Under the fixed sun of checks and
    /// shots there is no real one: the light it is. The ephemeris is read every frame as an Earth-fixed direction
    /// and turned into <paramref name="axes"/>, the axes the globe's tiles are placed in (#1798). Read twice a
    /// second in world axes, the terminator lagged twice over: it stayed behind each re-anchor while the globe
    /// turned under it (20 km a turn in Space), and the game's day, faster than the wall clock, moved it 13 km a
    /// read. A few trigonometric calls, no allocation; written every frame the game's clock runs.
    /// </summary>
    private Vector3 TrueSun(FrameAxes axes)
    {
        if (DayNight.Instance is { Sky: { } sky })
        {
            // from the Earth's centre: the parallax of a place on it is 4e-5 rad
            var s = Ephemeris.Ecef(Core.SkyBody.Sun, SwissTime.ToUtc(sky.Date, sky.Hour));
            double l = s.Length;
            (_sunEcef, _realSun) = (new EcefPos(s.X / l, s.Y / l, s.Z / l), true);
        }
        else
        {
            _realSun = false;
            if (DayNight.Instance?.Sun is { } light && IsInstanceValid(light)) _fixedSun = light.GlobalBasis.Z.Normalized();
        }
        var sun = _realSun
            ? new Vector3((float)FrameAxes.Dot(_sunEcef, axes.X), (float)FrameAxes.Dot(_sunEcef, axes.Y), (float)FrameAxes.Dot(_sunEcef, axes.Z))
            : _fixedSun;
        if (_sunWritten && sun == _trueSun) return _trueSun;
        _sunWritten = true;
        RenderingServer.GlobalShaderParameterSet(TrueSunParam, TrueSunWritten = _trueSun = sun);
        return _trueSun;
    }

    public override void _Process(double delta)
    {
        if (Origin == null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var c = cam.GlobalPosition;
        float space = Globe.Present ? SpaceAt(Origin.AltitudeOf(c)) : 0;
        // the globe's sun at every height the globe draws, in the axes it is placed in this frame (the
        // atmosphere runs after the globe, which places its tiles again at a re-anchor)
        var axes = Globe.Axes ?? Origin.EcefAxes;
        var sun = TrueSun(axes);
        DimSky(space);
        // the stars by day up here (sky_point.gdshader)
        if (space != _space) RenderingServer.GlobalShaderParameterSet(WorldSpace, space);
        _shell.Visible = space > 0;
        if (space <= 0)
        {
            _space = space;
            return;
        }

        // the planet's centre and its radius under the camera, in this world
        var (px, py, pz) = axes.FromEcef(new EcefPos(0, 0, 0));
        var centre = new Vector3((float)px, (float)py, (float)pz);
        _shell.GlobalPosition = centre;
        var eye = axes.ToEcef(c.X, c.Y, c.Z);
        var g = Ellipsoid.Wgs84.ToGeodetic(eye);
        var nadir = CubeSphere.OnEllipsoid(CubeSphere.NormalAt(g.LatDeg, g.LonDeg), 0);
        double r = Math.Sqrt(FrameAxes.Dot(nadir, nadir));

        // written on change only (perf-no-per-frame-allocations): the centre moves with a shift, the
        // radius and the sun slowly, the far plane with the altitude
        if (centre != _centre) _material.SetShaderParameter(PlanetCentre, _centre = centre);
        float radius = (float)r;
        if (Mathf.Abs(radius - _radius) > 1f)
        {
            _material.SetShaderParameter(PlanetR, _radius = radius);
            _material.SetShaderParameter(AtmoR, (float)(r + ThicknessM));
        }
        if (!sun.IsEqualApprox(_sun)) _material.SetShaderParameter(SunDir, _sun = sun);
        if (space != _space) _material.SetShaderParameter(Intensity, space);
        _space = space;
    }

    /// <summary>The style's sky, dimmed by <paramref name="space"/>; written only when it changes or the style rewrote it.</summary>
    private void DimSky(float space)
    {
        if (GetViewport()?.FindWorld3D()?.Environment is not { } env) return;
        float keep = 1 - space;
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial sky)
        {
            // the style sets the sky's energy to 1 on a palette change: rewrite it when it differs
            if (env != _env || Mathf.Abs(sky.SkyEnergyMultiplier - _skyEnergy) > 1e-4f || Mathf.Abs(_skyEnergy - keep) > 1e-3f)
            {
                sky.SkyEnergyMultiplier = keep;
                // its ground half (below the horizontal) is where, up here, the limb glows: gone sooner
                sky.GroundEnergyMultiplier = keep * keep * keep;
                _skyEnergy = keep;
            }
        }
        else if (env.BackgroundMode == Godot.Environment.BGMode.Color)
        {
            // the style writes its colour on a palette change: that is the new base
            if (env != _env || env.BackgroundColor != _bgWritten) _bgBase = env.BackgroundColor;
            var want = _bgBase * keep;
            want.A = 1;
            if (env.BackgroundColor != want) env.BackgroundColor = want;
            _bgWritten = want;
        }
        _env = env;
    }
}
