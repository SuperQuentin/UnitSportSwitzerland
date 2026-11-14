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
        PPole = "pole";

    private sealed class Entry
    {
        public SkyBody Id;
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
            var e = new Entry { Id = id, Tint = TintOf(id) };
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
    private static string? MapOf(SkyBody id) => id switch
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
    private static Color TintOf(SkyBody id) => id switch
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
            if (HiddenByGlobe.Contains(id)) { Hide(e); continue; }
            var parent = _entries.GetValueOrDefault(Ephemeris.ParentOf(id), e);
            bool zoomed = e.DiscPixels > 0.5f || parent.DiscPixels > 0.5f;
            if (now - e.ComputedAt > (zoomed ? 0.0 : 0.5))
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
            var face = stars * e.Face;

            // the body itself, once it is more than about a pixel wide
            float discFade = Mathf.SmoothStep(0.5f, 2f, e.DiscPixels);
            e.Disc.Visible = discFade > 0f;
            if (e.Disc.Visible)
            {
                e.Disc.GlobalTransform = new Transform3D(
                    new Basis(face.Column0 * e.Radius, face.Column1 * e.Radius, face.Column2 * (float)(rp * scale)), e.WorldPosition);
                e.DiscMaterial.SetShaderParameter(PSun, SunFrom(sunWorld, worldDir, e.Distance));
                e.DiscMaterial.SetShaderParameter(PBright, discFade * Mathf.Lerp(0.5f, 1.4f, night));
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
                    ringMaterial.SetShaderParameter(PSun, SunFrom(sunWorld, worldDir, e.Distance));
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
