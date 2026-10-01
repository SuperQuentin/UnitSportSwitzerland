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

    /// <summary>
    /// The same, lit like a room: for any camera in the interiors' space under the terrain (see
    /// <see cref="EnvironmentAt"/>). Null on a server, which has no environment.
    /// </summary>
    private Godot.Environment? _indoor;

    private const float NoonElevation = 62f;

    /// <summary>Daylight through the windows, as seen: noon's outdoor ambient, whatever the hour.</summary>
    private static readonly Color RoomDaylight = new(0.86f, 0.89f, 0.93f);

    /// <summary>Ceiling lamps, as seen: what lights a character indoors at night.</summary>
    private static readonly Color RoomLamp = new(0.96f, 0.90f, 0.78f);

    public DayNight(Godot.Environment? environment)
    {
        Name = "DayNight";
        _environment = environment;
        _indoor = environment?.Duplicate() as Godot.Environment;
        Hour = GameSettings.Current.StartHour;
    }

    /// <summary>
    /// The environment a camera at this point sees by. Rooms are lit (<c>ps1_interior</c> never
    /// darkens), so a character in one is too, by daylight through the windows or by the lamps at
    /// night; avatars and vehicles are standard materials lit by the ambient alone. It goes by
    /// where the <i>camera</i> is, not the character: a portal camera looking into a room from the
    /// street stands in the interiors' space, one looking out of a room stands in the world. Null =
    /// the world's own.
    /// </summary>
    public static Godot.Environment? EnvironmentAt(Vector3 at) =>
        at.Y < Interiors.InteriorManager.InteriorBaseY + 1000f ? Instance?._indoor : null;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Apply(0);
    }

    /// <summary>
    /// Real minutes per day when <c>/time speed</c> or the server's world clock has decided it;
    /// null = this player's setting (<see cref="GameSettings.DayLengthMinutes"/>).
    /// </summary>
    public float? DayLengthOverride { get; set; }

    public float MinutesPerDay => DayLengthOverride ?? GameSettings.Current.DayLengthMinutes;

    public override void _Process(double delta)
    {
        Hour = TimeCommand.Advance(Hour, delta, MinutesPerDay);
        Apply((float)delta);
        if (GetViewport()?.GetCamera3D() is { } cam) cam.Environment = EnvironmentAt(cam.GlobalPosition);
    }

    /// <summary>"14:05", for the HUD and chat.</summary>
    public string Clock => $"{(int)Hour:00}:{(int)(Hour % 1 * 60):00}";

    /// <summary>Ground height at a world point, if loaded — for sitting an occasion's mist on the valley floor.</summary>
    public Func<Vector3, float?>? GroundHeight { get; set; }

    private void Apply(float delta)
    {
        // A running occasion may move the sun (a short winter day) and re-grade the colours.
        var occasion = Occasions.OccasionManager.Instance?.Atmosphere;
        var atmo = occasion?.Atmosphere;
        float rise = atmo?.Sunrise ?? 6f, set = atmo?.Sunset ?? 18f, noon = atmo?.NoonElevation ?? NoonElevation;
        float dayLength = set - rise;

        // How far round the sun is: 0 at sunrise, 1 at sunset, 2 at the next sunrise. Day and
        // night each take half the circle however long they last, so the default 6 / 18 reduces to
        // the old (h - 6) / 12 exactly.
        float h = (float)Hour;
        float phase = h >= rise && h <= set
            ? (h - rise) / dayLength
            : 1f + Mathf.PosMod(h - set, 24f) / (24f - dayLength);

        // azimuth measured clockwise from north; world +X is east and -Z is north
        float az = Mathf.DegToRad(90f + phase * 180f);
        SunElevationDeg = noon * Mathf.Sin(phase * Mathf.Pi);
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
        if (occasion is { Owner: var owner }) (tint, sky) = owner.Grade(SunElevationDeg, tint, sky);
        Night = Mathf.SmoothStep(4f, -7f, SunElevationDeg);

        RenderingServer.GlobalShaderParameterSet(GSunDir, shade.Normalized());
        // The palette is authored as colours you would see; the shaders multiply LINEAR
        // values, so it is converted like the sky. Unconverted, night's 0.13 displayed as
        // 0.40 — a dull dusk, with every wall still pale.
        var tintLinear = tint.SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet(GTint, new Vector3(tintLinear.R, tintLinear.G, tintLinear.B));
        var skyLinear = sky.SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet(GSky, new Vector3(skyLinear.R, skyLinear.G, skyLinear.B));
        RenderingServer.GlobalShaderParameterSet(GNight, Night);
        ApplyOccasionGlobals(atmo, tintLinear, delta);

        // the palette is flat through most of the day and night: only write when it moved (#221).
        // Whatever swaps an environment in must reset _applied, or the new one waits for a change.
        bool moved = (sky, tint, Night) != _applied;
        _applied = (sky, tint, Night);
        if (moved && _environment != null)
        {
            _environment.BackgroundColor = sky;
            _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightColor = sky.Lerp(new Color(tint.R, tint.G, tint.B), 0.5f);
            _environment.AmbientLightEnergy = Mathf.Lerp(1.0f, 0.55f, Night);
        }
        if (moved && _indoor != null)
        {
            _indoor.BackgroundColor = sky;
            _indoor.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _indoor.AmbientLightColor = RoomDaylight.Lerp(RoomLamp, Night);
            _indoor.AmbientLightEnergy = 1.0f;
        }
    }

    // set every frame: a string would convert to a new StringName each call (#221)
    private static readonly StringName GLights = "world_lights", GMistColor = "world_mist_color",
        GMistDensity = "world_mist_density", GMistTop = "world_mist_top", GNight = "world_night", GSky = "world_sky",
        GSnow = "world_snow", GSunDir = "world_sun_dir", GTint = "world_tint";
    private (Color, Color, float)? _applied;

    // ---- occasion globals (shaders/world_occasion.gdshaderinc) ------------------------------------

    private float _mistTop;
    private bool _mistTopKnown;
    private float _mistSampleIn;

    /// <summary>
    /// Snow, lights and valley mist. All zero with no occasion running, which the shader include
    /// treats as "change nothing".
    /// </summary>
    private void ApplyOccasionGlobals(Occasions.OccasionAtmosphere? atmo, Color tintLinear, float delta)
    {
        RenderingServer.GlobalShaderParameterSet(GSnow, atmo?.Snow ?? 0f);
        RenderingServer.GlobalShaderParameterSet(GLights, atmo?.Lights ?? 0f);

        float density = atmo == null ? 0f : Mathf.Lerp(atmo.MistDay, atmo.MistNight, Night);
        if (density > 0f && TrackMistTop(atmo!.MistHeight, delta))
        {
            // the mist is lit like everything else, so it darkens with the evening
            var mist = atmo.MistColor.SrgbToLinear();
            RenderingServer.GlobalShaderParameterSet(GMistColor,
                new Vector3(mist.R * tintLinear.R, mist.G * tintLinear.G, mist.B * tintLinear.B));
            RenderingServer.GlobalShaderParameterSet(GMistTop, _mistTop);
        }
        else
        {
            density = 0f;
        }
        RenderingServer.GlobalShaderParameterSet(GMistDensity, density);
    }

    /// <summary>
    /// Keeps the mist's ceiling a fixed height over the <i>lowest</i> ground within ~1.5 km of the
    /// camera, so it pools in the valley rather than following the camera up a mountain. Sampled
    /// twice a second and eased, so crossing a ridge lifts or drops it gently. False until a height
    /// is known.
    /// </summary>
    private bool TrackMistTop(float height, float delta)
    {
        _mistSampleIn -= delta;
        if (_mistSampleIn <= 0f && GroundHeight != null && GetViewport()?.GetCamera3D() is { } cam)
        {
            _mistSampleIn = 0.5f;
            var c = cam.GlobalPosition;
            float? lowest = null;
            for (int i = -1; i < 8; i++)
            {
                var at = i < 0 ? c : c + new Vector3(Mathf.Cos(i * Mathf.Pi / 4f), 0, Mathf.Sin(i * Mathf.Pi / 4f)) * 1500f;
                if (GroundHeight(at) is { } g && (lowest == null || g < lowest)) lowest = g;
            }
            if (lowest is { } floor)
            {
                float target = floor + height;
                _mistTop = _mistTopKnown ? Mathf.Lerp(_mistTop, target, 0.25f) : target;
                _mistTopKnown = true;
            }
        }
        return _mistTopKnown;
    }

    /// <summary>
    /// Light and sky by sun elevation, keyed like a colour script: night, blue hour, the orange
    /// of a low sun, and plain daylight. Linear between keys.
    /// </summary>
    private static (Color Tint, Color Sky) Palette(float elevation)
    {
        var keys = PaletteKeys;
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

    private static readonly (float El, Color Tint, Color Sky)[] PaletteKeys =
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
}
