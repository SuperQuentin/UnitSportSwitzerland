using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// The planets and the big moons in the sky (#1444), as the moon is (<see cref="MoonDisc"/>): each at
/// its real place (<see cref="Ephemeris"/>), drawn just inside the far plane. By eye a point of light
/// sized by its magnitude (<c>sky_point.gdshader</c>; Venus shows by day); zoomed until it is more than
/// a pixel wide, the body itself (<c>planet.gdshader</c>): its map turned by its IAU axes and squashed
/// along its pole, lit by the sun, Saturn with its rings (<c>planet_rings.gdshader</c>), Jupiter's four
/// moons and Titan beside their planets at the same scale, hidden behind them when they pass behind.
///
/// <para>
/// The ephemeris is not cheap (the moon's series is in every planet's light time), so each body's
/// place is kept on the celestial sphere and only turned with it each frame (the stars' frame,
/// <see cref="DayNight.RealSky.Stars"/>); it is worked out again twice a second, or every frame for
/// what the camera is zoomed on. Meshes built once; only transforms and shader parameters move.
/// </para>
/// </summary>
public partial class SkyBodies : Node3D
{
    public static SkyBodies? Instance { get; private set; }

    public static readonly SkyBody[] Bodies =
    [
        SkyBody.Mercury, SkyBody.Venus, SkyBody.Mars, SkyBody.Jupiter, SkyBody.Saturn, SkyBody.Uranus, SkyBody.Neptune,
        SkyBody.Io, SkyBody.Europa, SkyBody.Ganymede, SkyBody.Callisto, SkyBody.Titan, SkyBody.Phobos, SkyBody.Deimos,
    ];

    /// <summary>Saturn's rings, inner and outer edge (m): the C ring's inside to the F ring.</summary>
    private const double RingInner = 74_500_000, RingOuter = 140_220_000;

    private static readonly StringName PSky = "sky", PDistance = "distance", PDaylight = "daylight", PSingle = "single",
        PColor = "point_color", PSize = "point_size", PSun = "sun_dir", PBright = "brightness", POccluder = "occluder",
        PPole = "pole", PFade = "fade";

    /// <summary>The body a sky body is (null for none).</summary>
    private static CelestialBody? BodyOf(SkyBody id)
    {
        foreach (var b in CelestialBody.All)
            if (b.Sky == id) return b;
        return null;
    }

    private sealed class Entry
    {
        public SkyBody Id;
        // the body it is (#1998): its globe's look from space (Bodies.BodyLook), written on change
        public CelestialBody? Body;
        public (float, float, float, float) LookShown = (-1f, -1f, -1f, -1f);
        // in space, from the frame tree (#1998): the Sun's direction at the body, world axes; null from the ground
        public Vector3? TreeSun;
        public MeshInstance3D Disc = null!, Point = null!;
        public ShaderMaterial DiscMaterial = null!, PointMaterial = null!;
        public MeshInstance3D? Rings;
        public ShaderMaterial? RingMaterial;
        public Color Tint;
        // on the celestial sphere: the direction, the body's own axes, its distance (m) and magnitude
        public Vector3 Direction;
        public Basis Face;
        public double Distance;
        public float Magnitude;
        public double ComputedAt = double.NegativeInfinity;
        // this frame, in world axes
        public Vector3 WorldPosition;
        // its distance from the Sun (AU) for its brightness from space, and when it was worked out
        public double SunAu = 1, SunAuAt = double.NegativeInfinity;
        public float Radius;
        public float DiscPixels;
    }

    private readonly Dictionary<SkyBody, Entry> _entries = new();

    // the sun on the celestial sphere and its distance (m): each planet is lit from where the sun is from it
    private Vector3 _sunSky;
    private double _sunDistance = Ephemeris.AuMetres, _sunAt = double.NegativeInfinity;

    public SkyBodies() => Name = "SkyBodies";

    public override void _EnterTree()
    {
        Instance = this;
        RenderingServer.FramePreDraw += Place;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        RenderingServer.FramePreDraw -= Place;
    }

    // placed in the frame too, where a headless run never draws (the checks, the telescope's lock); again
    // just before drawing, from where the camera ended up (the ephemeris is not worked out twice)
    public override void _Process(double delta) => Place();

    public override void _Ready()
    {
        TopLevel = true;
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 64, Rings = 32 };
        var quad = new QuadMesh { Size = Vector2.One * 2f };
        var discShader = GD.Load<Shader>("res://shaders/planet.gdshader");
        var pointShader = GD.Load<Shader>("res://shaders/sky_point.gdshader");
        foreach (var id in Bodies)
        {
            var e = new Entry { Id = id, Tint = TintOf(id), Body = BodyOf(id) };
            e.DiscMaterial = new ShaderMaterial { Shader = discShader };
            if (MapOf(id) is { } map)
                e.DiscMaterial.SetShaderParameter("albedo", GD.Load<Texture2D>(map));
            else e.DiscMaterial.SetShaderParameter("textured", false);
            e.DiscMaterial.SetShaderParameter("tint", e.Tint);
            e.DiscMaterial.SetShaderParameter("limb", id is SkyBody.Jupiter or SkyBody.Saturn or SkyBody.Uranus or SkyBody.Neptune ? 0.8f : 0.2f);
            e.Disc = Instance3D(sphere, e.DiscMaterial, $"{id}Disc");
            e.PointMaterial = new ShaderMaterial { Shader = pointShader };
            e.PointMaterial.SetShaderParameter(PSingle, true);
            e.Point = Instance3D(quad, e.PointMaterial, $"{id}Point");
            if (id == SkyBody.Saturn)
            {
                e.RingMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/planet_rings.gdshader") };
                e.RingMaterial.SetShaderParameter("rings", GD.Load<Texture2D>("res://assets/space/saturn_ring_2k.png"));
                e.RingMaterial.SetShaderParameter("inner", (float)(RingInner / RingOuter));
                e.Rings = Instance3D(quad, e.RingMaterial, "SaturnRings");
            }
            _entries[id] = e;
        }
        _earthPointMaterial = new ShaderMaterial { Shader = pointShader };
        _earthPointMaterial.SetShaderParameter(PSingle, true);
        _earthPoint = Instance3D(quad, _earthPointMaterial, "EarthPoint");
    }

    private MeshInstance3D Instance3D(Mesh mesh, Material material, string name)
    {
        var m = new MeshInstance3D
        {
            Name = name, Mesh = mesh, MaterialOverride = material, TopLevel = true, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // the sky is never occlusion-culled: depth hides it behind a ridge; the buildings' coarse buffer culled the
            // moon looked at head-on (#1835 follow-up)
            IgnoreOcclusionCulling = true,
        };
        AddChild(m);
        return m;
    }

    /// <summary>The map of a body (Solar System Scope, CC BY 4.0), or null for a plain tinted ball.</summary>
    internal static string? MapOf(SkyBody id) => id switch
    {
        SkyBody.Mercury => "res://assets/space/mercury_2k.jpg",
        SkyBody.Venus => "res://assets/space/venus_2k.jpg",
        SkyBody.Mars => "res://assets/space/mars_2k.jpg",
        SkyBody.Jupiter => "res://assets/space/jupiter_2k.jpg",
        SkyBody.Saturn => "res://assets/space/saturn_2k.jpg",
        SkyBody.Uranus => "res://assets/space/uranus_2k.jpg",
        SkyBody.Neptune => "res://assets/space/neptune_2k.jpg",
        _ => null,
    };

    /// <summary>A body's colour as a point, and a moon's whole colour.</summary>
    internal static Color TintOf(SkyBody id) => id switch
    {
        SkyBody.Mars => new Color(1f, 0.72f, 0.55f),
        SkyBody.Jupiter or SkyBody.Saturn => new Color(1f, 0.95f, 0.85f),
        SkyBody.Uranus or SkyBody.Neptune => new Color(0.75f, 0.9f, 1f),
        SkyBody.Io => new Color(1f, 0.93f, 0.6f),
        SkyBody.Europa => new Color(0.95f, 0.92f, 0.86f),
        SkyBody.Ganymede => new Color(0.8f, 0.75f, 0.7f),
        SkyBody.Callisto => new Color(0.6f, 0.56f, 0.5f),
        SkyBody.Titan => new Color(1f, 0.75f, 0.42f),
        SkyBody.Phobos or SkyBody.Deimos => new Color(0.55f, 0.5f, 0.46f),
        _ => new Color(1f, 0.98f, 0.94f),
    };

    /// <summary>Where a body is this frame, in world axes (the telescope's lock), if the sky has one.</summary>
    public Vector3? DirectionOf(SkyBody id) =>
        _entries.TryGetValue(id, out var e) && e.Disc.GetViewport()?.GetCamera3D() is { } cam && e.ComputedAt > double.NegativeInfinity
            ? (e.WorldPosition - cam.GlobalPosition).Normalized() : null;

    /// <summary>A body's magnitude as the sky has it this frame (#2047's check).</summary>
    internal float? MagnitudeOf(SkyBody id) => _entries.TryGetValue(id, out var e) && e.ComputedAt > double.NegativeInfinity ? e.Magnitude : null;

    /// <summary>Where a body was last placed, in world space (a check reads it as drawn, after the frame's last placing).</summary>
    internal Vector3? PlacedAt(SkyBody id) =>
        _entries.TryGetValue(id, out var e) && e.ComputedAt > double.NegativeInfinity ? e.WorldPosition : null;

    /// <summary>The body whose place is nearest <paramref name="direction"/>, and how far off it is, degrees.</summary>
    public (SkyBody Id, float OffDeg)? Nearest(Vector3 direction)
    {
        (SkyBody, float)? best = null;
        foreach (var e in _entries.Values)
        {
            if (DirectionOf(e.Id) is not { } d) continue;
            float off = Mathf.RadToDeg(d.AngleTo(direction));
            if (best == null || off < best.Value.Item2) best = (e.Id, off);
        }
        return best;
    }

    /// <summary>
    /// The bodies drawn as globes now (#1850, <c>World/Bodies/BodyViews</c>): their sky disc and point step aside, as the
    /// Moon's disc does (<see cref="MoonDisc.HiddenByGlobe"/>). Mars from 20,000 km is its globe, not a second disc.
    /// </summary>
    public static readonly HashSet<SkyBody> HiddenByGlobe = new();

    private void Place()
    {
        if (DayNight.Instance is not { Sky: { } sky } day || GetViewport()?.GetCamera3D() is not { } cam)
        {
            foreach (var e in _entries.Values) Hide(e);
            if (_earthPoint != null) _earthPoint.Visible = false;
            return;
        }
        double now = GameClock.Now;
        float far = cam.Far * 0.9f;
        float fov = CameraLens.FovOf(cam);
        float pixelsPerRadian = GetViewport().GetVisibleRect().Size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(fov) * 0.5f);
        var stars = sky.Stars;
        var toSky = stars.Transposed();
        var camPos = cam.GlobalPosition;
        // the sky's moment (#1850): the planets where they are at the bodies' time, the Earth turned by the hour
        var utc = sky.Moment.Utc;
        float night = day.Night;
        if (now - _sunAt > 0.5)
        {
            _sunSky = toSky * SunPath.Toward(SkyBody.Sun, sky.Moment, sky.At, out _, out _sunDistance);
            _sunAt = now;
        }
        var sunWorld = (stars * _sunSky).Normalized();

        foreach (var id in Bodies)
        {
            var e = _entries[id];
            var parent = _entries.GetValueOrDefault(Ephemeris.ParentOf(id), e);
            bool zoomed = e.DiscPixels > 0.5f || parent.DiscPixels > 0.5f;
            // in space every frame from the frame tree, as the body's globe has it (#1998): the sky's reading counts its
            // observer from the Earth, far off from the Moon's side of the system or out by Mars
            e.TreeSun = null;
            if (e.Body != null && World.Bodies.BodyLook.Seen(e.Body.Id, camPos, out var seenDir, out double seenDistance, out var seenFace, out var seenSun))
            {
                var fromWorld = stars.Inverse();
                e.Direction = fromWorld * seenDir;
                e.Face = fromWorld * seenFace;
                e.Distance = seenDistance;
                e.TreeSun = seenSun;
                // its brightness from here (#2047): the phase at the body and the distances from the Sun and the camera
                // (Jupiter from Saturn, the Earth's numbers were 4 AU off)
                if (now - e.SunAuAt > 0.5)
                {
                    e.SunAu = Frames.Offset(e.Body.Id, BodyId.Sun, sky.Moment).Length / Ephemeris.AuMetres;
                    e.SunAuAt = now;
                }
                e.Magnitude = (float)MagnitudeSeen(id, e.SunAu, seenSun, seenDir, seenDistance, utc);
                e.ComputedAt = now;
            }
            else if (now - e.ComputedAt > (zoomed ? 0.0 : 0.5))
            {
                var dir = SunPath.Toward(id, sky.Moment, sky.At, out _, out double distance);
                e.Direction = toSky * dir;
                e.Face = toSky * SunPath.Face(id, sky.Moment, sky.At);
                e.Distance = distance;
                e.Magnitude = (float)Ephemeris.Magnitude(id, utc);
                e.ComputedAt = now;
            }
            // the whole planetary system at one scale: its planet just inside the far plane
            double scale = far / parent.Distance;
            if (parent == e || parent.Distance <= 0) scale = far / e.Distance;
            var worldDir = (stars * e.Direction).Normalized();
            e.WorldPosition = camPos + worldDir * (float)(e.Distance * scale);
            var (re, rp) = Ephemeris.Radii(id);
            e.Radius = (float)(re * scale);
            e.DiscPixels = (float)(re / e.Distance) * pixelsPerRadian;
            // its globe draws it (#1998): the disc and the point step aside, but its place is still kept every frame. The
            // telescope follows it through DirectionOf, and skipped here it froze where the globe took over: zoomed in on a
            // planet from the Moon, the globe drifted off while the tube held still (#2028)
            if (HiddenByGlobe.Contains(id))
            {
                Hide(e);
                continue;
            }
            var face = stars * e.Face;

            // the body itself, once it is more than about a pixel wide
            float discFade = Mathf.SmoothStep(0.5f, 2f, e.DiscPixels);
            e.Disc.Visible = discFade > 0f;
            if (e.Disc.Visible)
            {
                e.Disc.GlobalTransform = new Transform3D(
                    new Basis(face.Column0 * e.Radius, face.Column1 * e.Radius, face.Column2 * (float)(rp * scale)), e.WorldPosition);
                e.DiscMaterial.SetShaderParameter(PSun, e.TreeSun ?? SunFrom(sunWorld, worldDir, e.Distance));
                e.DiscMaterial.SetShaderParameter(PBright, discFade * Mathf.Lerp(0.5f, 1.4f, night));
                // from space the body's globe's own look (#1998): the globe takes it over past a size with no change
                e.DiscMaterial.SetShaderParameter(PFade, discFade);
                if (e.Body != null) World.Bodies.BodyLook.Write(e.DiscMaterial, e.Body, World.Bodies.BodyLook.InSpace, ref e.LookShown);
                e.DiscMaterial.SetShaderParameter(POccluder, parent == e ? Vector4.Zero
                    : new Vector4(parent.WorldPosition.X, parent.WorldPosition.Y, parent.WorldPosition.Z, parent.Radius));
            }
            if (e.Rings is { } rings && e.RingMaterial is { } ringMaterial)
            {
                rings.Visible = e.Disc.Visible;
                if (rings.Visible)
                {
                    float outer = (float)(RingOuter * scale);
                    rings.GlobalTransform = new Transform3D(new Basis(face.Column0 * outer, face.Column1 * outer, face.Column2 * outer), e.WorldPosition);
                    ringMaterial.SetShaderParameter(PSun, e.TreeSun ?? SunFrom(sunWorld, worldDir, e.Distance));
                    ringMaterial.SetShaderParameter(PPole, face.Column2.Normalized());
                    ringMaterial.SetShaderParameter(PBright, discFade * Mathf.Lerp(0.5f, 1.4f, night));
                    ringMaterial.SetShaderParameter(POccluder, new Vector4(e.WorldPosition.X, e.WorldPosition.Y, e.WorldPosition.Z, e.Radius));
                }
            }

            // the point by eye, gone as the disc takes over
            var (px, alpha) = StarField.Look(e.Magnitude);
            alpha *= 1f - Mathf.SmoothStep(1.5f, 4f, e.DiscPixels);
            e.Point.Visible = alpha > 0.01f && e.Magnitude < 7f;
            if (e.Point.Visible)
            {
                e.Point.GlobalPosition = e.WorldPosition;
                e.PointMaterial.SetShaderParameter(PSky, new Basis(worldDir, Vector3.Up, Vector3.Back));
                e.PointMaterial.SetShaderParameter(PDistance, (float)(e.Distance * scale));
                e.PointMaterial.SetShaderParameter(PColor, e.Tint with { A = alpha });
                e.PointMaterial.SetShaderParameter(PSize, px);
                // Venus shows by day, the others with the stars (by eye nothing else shows in a blue sky: Jupiter and
                // Mercury dotted the shuttle's climb at noon, #1497)
                e.PointMaterial.SetShaderParameter(PDaylight, e.Magnitude < -3.5f ? 0.7f : 0f);
            }
        }
        PlaceEarth(camPos, far, pixelsPerRadian, sky.Moment);
    }

    // the Earth from space (#2047): no sky body of its own (its globe draws its disc), a point as the planets' once small
    private MeshInstance3D? _earthPoint;
    private ShaderMaterial? _earthPointMaterial;
    private double _earthSunAu = 1, _earthSunAt = double.NegativeInfinity;

    /// <summary>The Earth's magnitude from space this frame, NaN when it has no point (#2047's check).</summary>
    internal float EarthMagnitude { get; private set; } = float.NaN;

    /// <summary>The Earth's point's alpha this frame (#2047's check).</summary>
    internal float EarthPointAlpha { get; private set; }

    private static readonly Color EarthTint = new(0.8f, 0.88f, 1f);

    /// <summary>
    /// The Earth from space as the planets are from it (#2047): too small for its globe to read (under 1.5 to 4 px), a point
    /// sized and lit by its true magnitude from the camera (<see cref="Ephemeris.EarthMagnitudeAt"/>), none fainter than
    /// the 7th. From Mars it was not drawn at all, the Moon a glowing disc beside nothing.
    /// </summary>
    private void PlaceEarth(Vector3 camPos, float far, float pixelsPerRadian, SkyMoment m)
    {
        EarthMagnitude = float.NaN;
        EarthPointAlpha = 0f;
        if (_earthPoint == null || _earthPointMaterial == null) return;
        if (!World.Bodies.BodyLook.Seen(BodyId.Earth, camPos, out var dir, out double distance, out _, out var sunAtEarth))
        {
            _earthPoint.Visible = false;
            return;
        }
        double now = GameClock.Now;
        if (now - _earthSunAt > 0.5)
        {
            _earthSunAu = Frames.Offset(BodyId.Earth, BodyId.Sun, m).Length / Ephemeris.AuMetres;
            _earthSunAt = now;
        }
        double phase = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(sunAtEarth.Dot(-dir), -1f, 1f)));
        float magnitude = (float)Ephemeris.EarthMagnitudeAt(phase, _earthSunAu, distance / Ephemeris.AuMetres);
        float discPixels = (float)(CelestialBody.Earth.Shape.A / distance) * pixelsPerRadian;
        var (px, a) = StarField.Look(magnitude);
        float alpha = magnitude < 7f ? a * (1f - Mathf.SmoothStep(1.5f, 4f, discPixels)) : 0f;
        _earthPoint.Visible = alpha > 0.01f;
        EarthMagnitude = magnitude;
        if (!_earthPoint.Visible) return;
        EarthPointAlpha = alpha;
        _earthPoint.GlobalPosition = camPos + dir * far;
        _earthPointMaterial.SetShaderParameter(PSky, new Basis(dir, Vector3.Up, Vector3.Back));
        _earthPointMaterial.SetShaderParameter(PDistance, far);
        _earthPointMaterial.SetShaderParameter(PColor, EarthTint with { A = alpha });
        _earthPointMaterial.SetShaderParameter(PSize, px);
        _earthPointMaterial.SetShaderParameter(PDaylight, 0f);
    }

    /// <summary>
    /// A body's magnitude from the camera (#2047): <paramref name="sunAtBody"/> the Sun's direction at the body and
    /// <paramref name="toBody"/> the body's from the camera (any one set of axes), <paramref name="distance"/> metres off,
    /// <paramref name="sunAu"/> the body's distance from the Sun.
    /// </summary>
    public static double MagnitudeSeen(SkyBody id, double sunAu, Vector3 sunAtBody, Vector3 toBody, double distance, System.DateTime utc)
    {
        double r = sunAu;
        double phase = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(sunAtBody.Normalized().Dot(-toBody.Normalized()), -1f, 1f)));
        return Ephemeris.MagnitudeAt(id, phase, r, distance / Ephemeris.AuMetres, utc);
    }

    /// <summary>The direction to the sun from a body <paramref name="distance"/> metres away along <paramref name="direction"/>.</summary>
    private Vector3 SunFrom(Vector3 sun, Vector3 direction, double distance)
    {
        // in units of the sun's distance, so the floats stay sane
        var v = sun - direction * (float)(distance / _sunDistance);
        return v.Normalized();
    }

    private static void Hide(Entry e)
    {
        e.Disc.Visible = false;
        e.Point.Visible = false;
        if (e.Rings != null) e.Rings.Visible = false;
    }
}
