using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// The time of day, and everything it colours.
///
/// <para>
/// Every world shader is unshaded and lights itself, so there is no scene light to rotate.
/// Instead this writes four global shader uniforms once a frame (declared in
/// <c>project.godot</c>'s <c>[shader_globals]</c>): the sun's direction, the colour of the
/// light, the sky the distance fades into, and a 0..1 night factor that lights windows and
/// lamps. One write reaches every tile, road and tree the streamer has loaded or will load —
/// no material is touched. The environment's background and ambient follow the same sky,
/// because the avatars and vehicles are standard materials lit by that ambient alone.
/// </para>
///
/// <para>
/// The sun is a simple model of a Swiss summer day rather than an ephemeris: up at 6, due
/// south and 62° high at noon, down at 18. What matters in a game is that dawn, dusk and night
/// look right and come round at a pace you can play through — 24 real minutes to the day by
/// default (<see cref="GameSettings.DayLengthMinutes"/>, 0 stops the clock).
/// </para>
/// </summary>
public partial class DayNight : Node
{
    public static DayNight? Instance { get; private set; }

    /// <summary>Hours, 0..24.</summary>
    public double Hour { get; set; } = 10.0;

    public float SunElevationDeg { get; private set; }

    /// <summary>0 in daylight, 1 at night. Headlights, lamps and windows read this.</summary>
    public float Night { get; private set; }

    private Godot.Environment? _environment;

    private const float NoonElevation = 62f;

    public DayNight(Godot.Environment? environment)
    {
        Name = "DayNight";
        _environment = environment;
        Hour = GameSettings.Current.StartHour;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Apply();
    }

    public override void _Process(double delta)
    {
        float minutes = GameSettings.Current.DayLengthMinutes;
        if (minutes > 0) Hour = (Hour + delta * 24.0 / (minutes * 60.0)) % 24.0;
        Apply();
    }

    /// <summary>"14:05", for the HUD and chat.</summary>
    public string Clock => $"{(int)Hour:00}:{(int)(Hour % 1 * 60):00}";

    private void Apply()
    {
        // azimuth measured clockwise from north; world +X is east and -Z is north
        float h = (float)Hour;
        float az = Mathf.DegToRad(90f + (h - 6f) * 15f);
        SunElevationDeg = NoonElevation * Mathf.Sin((h - 6f) / 12f * Mathf.Pi);
        float el = Mathf.DegToRad(SunElevationDeg);
        var sun = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));

        // Below the horizon the shading follows the moon instead — opposite the sun and never
        // lower than ~25°, so a night-time mountain still has a lit side and a dark one rather
        // than going flat.
        var shade = SunElevationDeg > 3f ? sun
            : new Vector3(-sun.X, Mathf.Max(0.42f, -sun.Y), -sun.Z).Normalized();
        if (SunElevationDeg > 0f && SunElevationDeg <= 3f)
            shade = shade with { Y = Mathf.Max(shade.Y, 0.05f) };

        var (tint, sky) = Palette(SunElevationDeg);
        Night = Mathf.SmoothStep(4f, -7f, SunElevationDeg);

        RenderingServer.GlobalShaderParameterSet("world_sun_dir", shade.Normalized());
        // The palette is authored as colours you would see; the shaders multiply LINEAR
        // values, so it is converted like the sky. Unconverted, night's 0.13 displayed as
        // 0.40 — a dull dusk, with every wall still pale.
        var tintLinear = tint.SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet("world_tint", new Vector3(tintLinear.R, tintLinear.G, tintLinear.B));
        var skyLinear = sky.SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet("world_sky", new Vector3(skyLinear.R, skyLinear.G, skyLinear.B));
        RenderingServer.GlobalShaderParameterSet("world_night", Night);

        if (_environment != null)
        {
            _environment.BackgroundColor = sky;
            _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightColor = sky.Lerp(new Color(tint.R, tint.G, tint.B), 0.5f);
            _environment.AmbientLightEnergy = Mathf.Lerp(1.0f, 0.55f, Night);
        }
    }

    /// <summary>
    /// Light and sky by sun elevation, keyed like a colour script: night, blue hour, the orange
    /// of a low sun, and plain daylight. Linear between keys.
    /// </summary>
    private static (Color Tint, Color Sky) Palette(float elevation)
    {
        (float El, Color Tint, Color Sky)[] keys =
        {
            (-90f, new Color(0.30f, 0.33f, 0.50f), new Color(0.03f, 0.04f, 0.09f)),
            (-10f, new Color(0.30f, 0.33f, 0.50f), new Color(0.03f, 0.04f, 0.09f)),
            (-4f,  new Color(0.52f, 0.48f, 0.66f), new Color(0.22f, 0.20f, 0.35f)),   // blue hour
            (0f,   new Color(0.78f, 0.50f, 0.42f), new Color(0.88f, 0.52f, 0.40f)),   // sunset
            (6f,   new Color(1.00f, 0.78f, 0.58f), new Color(0.92f, 0.72f, 0.58f)),   // golden hour
            (18f,  new Color(1.00f, 0.96f, 0.90f), new Color(0.76f, 0.80f, 0.86f)),
            (35f,  new Color(1.00f, 1.00f, 1.00f), new Color(0.72f, 0.78f, 0.86f)),   // the old fixed look
            (90f,  new Color(1.00f, 1.00f, 1.00f), new Color(0.72f, 0.78f, 0.86f)),
        };
        for (int i = 1; i < keys.Length; i++)
        {
            if (elevation > keys[i].El) continue;
            var a = keys[i - 1];
            var b = keys[i];
            float t = Mathf.Clamp((elevation - a.El) / (b.El - a.El), 0f, 1f);
            return (a.Tint.Lerp(b.Tint, t), a.Sky.Lerp(b.Sky, t));
        }
        return (keys[^1].Tint, keys[^1].Sky);
    }
}
