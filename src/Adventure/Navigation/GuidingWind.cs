using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The guiding gust (#1226, navigation plan 4), Ghost of Tsushima's wind as the Alpine Föhn: on the <c>guide</c> action
/// (N; the quick menu's Guide wedge on a pad; VR the open left hand palm up, or the wrist menu) a 6 s gust from behind
/// the player blows toward the tracked target, carrying leaves (larch needles and streaks; dark slate and red above
/// 2 000 m, never white on snow), with a gust sound from the way to go. Local and cosmetic, never replicated.
/// <para>
/// One <see cref="GpuParticles3D"/> (96, 2.2 s, world coordinates) 3 m ahead of the body (8 m when mounted) and one
/// <see cref="ParticleProcessMaterial"/> whose direction is set only when the bearing moves by more than 2°, tilted by
/// the slope so the gust climbs a hill instead of entering it; the shapes are the fragment shader's
/// (<c>guide_wind.gdshader</c>, styled per <c>styles/style-kit</c>), no mesh built after <c>_Ready</c>. Settings: on
/// demand, always (a soft stream while something is tracked), arrow only, off; "High-contrast guide" shows the ground
/// chevron (<see cref="NavMeshBuilder.GuideArrow"/>) instead: amber with a dark outline, turned per frame.
/// </para>
/// </summary>
public partial class GuidingWind : Node3D
{
    public const double GustSeconds = 6;
    private const float StreakSpeed = 25f, SnowLine = 2000f;

    public static GuidingWind? Instance { get; private set; }

    /// <summary>The local body (null in the fly camera), set by the client world.</summary>
    public static Func<Player.FootPlayer?>? Body { get; set; }

    private GpuParticles3D _particles = null!;
    private ParticleProcessMaterial _process = null!;
    private ShaderMaterial _shader = null!;
    private AudioStreamPlayer3D _gust = null!;
    private AudioStreamPlayer _chime = null!;
    private MeshInstance3D _arrow = null!;
    private double _until;
    private float _bearing = float.NaN, _tilt;
    private int _palette = -1;
    private float _streaks = -1;

    /// <summary>Gusts summoned (checks), and whether one blows now.</summary>
    public int Gusts { get; private set; }
    public bool Blowing => GameClock.Now < _until;
    /// <summary>The bearing the gust blows toward now, degrees (checks).</summary>
    public float Bearing => _bearing;
    public bool ArrowShown => _arrow.Visible;
    public int Palette => _palette;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "GuidingWind";
        _shader = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Adventure/Navigation/guide_wind.gdshader") };
        _shader.SetShaderParameter("style", Styles.StyleKit.Style switch { Styles.VisualStyle.Ps1 => 0, Styles.VisualStyle.Cartoon => 1, _ => 2 });
        _process = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, -1),
            Spread = 10f,
            InitialVelocityMin = 7f,
            InitialVelocityMax = 10f,
            Gravity = new Vector3(0, -0.4f, 0),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -160f,
            AngularVelocityMax = 160f,
            ScaleMin = 0.07f,
            ScaleMax = 0.14f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(2.5f, 1.2f, 2.5f),
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.6f,
        };
        _particles = new GpuParticles3D
        {
            Name = "Leaves",
            Amount = 96,
            Lifetime = 2.2,
            LocalCoords = false,
            Emitting = false,
            ProcessMaterial = _process,
            DrawPass1 = new QuadMesh { Size = new Vector2(1, 1), Material = _shader },
            VisibilityAabb = new Aabb(new Vector3(-30, -10, -30), new Vector3(60, 25, 60)),
        };
        AddChild(_particles);
        _gust = new AudioStreamPlayer3D { Name = "Gust", Stream = NavSounds.Gust, Bus = SfxBus.Name, UnitSize = 12f, VolumeDb = -4f };
        AddChild(_gust);
        _chime = new AudioStreamPlayer { Name = "GuideChime", Stream = NavSounds.GuideChime, Bus = SfxBus.Name, VolumeDb = -12f };
        AddChild(_chime);
        _arrow = new MeshInstance3D
        {
            Name = "GuideArrow",
            Mesh = NavMeshBuilder.GuideArrow(),
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_arrow);
    }

    /// <summary>Whether the guide can blow now: something tracked, adventuring, the setting not off.</summary>
    public static bool Can => NavTarget.Current != null && AdventureLog.Adventuring && !Permissions.InMatch && GameSettings.Current.GuideWind != 3;

    /// <summary>Summons a gust (N, the quick menu, the palm). False, and a hint, when there is nothing to guide to.</summary>
    public bool Summon()
    {
        if (!Can)
        {
            GD.Print("[guide] nothing tracked");
            Hint?.Invoke(GameSettings.Current.GuideWind == 3 ? "The guiding wind is off (Settings > Gameplay)." : "Nothing tracked: pick a place, a pin or a quest to follow (M, J).");
            return false;
        }
        _until = GameClock.Now + GustSeconds;
        Gusts++;
        _bearing = float.NaN;
        _chime.Play();
        if (!ArrowMode) _gust.Play();
        if (XR.XrSession.Active) XR.XrSession.Rig?.PulseLeft(0.2f, 0.08f);
        GD.Print($"[guide] gust toward {NavTarget.Current?.Label}");
        return true;
    }

    /// <summary>A line for the player (the client world's toast).</summary>
    public static Action<string>? Hint { get; set; }

    private static bool ArrowMode => GameSettings.Current.GuideWind == 2 || GameSettings.Current.GuideHighContrast;

    public override void _Process(double delta)
    {
        var body = Body?.Invoke();
        var target = NavTarget.Current;
        int mode = GameSettings.Current.GuideWind;
        bool always = mode == 1 && target != null && Can;
        bool active = body != null && IsInstanceValid(body) && target != null && (Blowing || always) && Can;
        if (!active)
        {
            if (_particles.Emitting) _particles.Emitting = false;
            if (_arrow.Visible) _arrow.Visible = false;
            if (_gust.Playing && !Blowing) _gust.Stop();
            return;
        }
        var me = body!.Global;
        var t = target!.Value;
        var toward = new Vector2((float)(t.E - me.E), (float)(t.N - me.N));
        float bearing = CompassStrip.Bearing(toward);
        // the world's way: east +X, north -Z
        var flat = new Vector3(toward.X, 0, -toward.Y).Normalized();
        bool mounted = body.RideKindId != 0;
        float speed = body.Velocity.Length();
        float ahead = mounted ? 8f : 3f;
        var origin = body.GlobalPosition + new Vector3(0, mounted ? 1.2f : 1.0f, 0);
        if (float.IsNaN(_bearing) || Mathf.Abs(Mathf.Wrap(bearing - _bearing, -180f, 180f)) > 2f)
        {
            _bearing = bearing;
            // the slope along the way: the gust climbs a hill rather than entering it
            float here = NavTarget.GroundAt?.Invoke(me.E, me.N) ?? (float)me.Alt;
            double aheadE = me.E + toward.X / Math.Max(toward.Length(), 1) * 20, aheadN = me.N + toward.Y / Math.Max(toward.Length(), 1) * 20;
            float there = NavTarget.GroundAt?.Invoke(aheadE, aheadN) ?? here;
            _tilt = Mathf.Clamp((there - here) / 20f, -0.6f, 0.6f);
            _process.Direction = new Vector3(flat.X, _tilt + 0.05f, flat.Z).Normalized();
        }
        // dark leaves above the snow line, the streaks at speed: set on change only
        int palette = me.Alt > SnowLine ? 1 : 0;
        if (palette != _palette) { _palette = palette; _shader.SetShaderParameter("palette", palette); }
        float streaks = speed > StreakSpeed ? 1f : 0f;
        if (streaks != _streaks) { _streaks = streaks; _shader.SetShaderParameter("streaks", streaks); }

        if (ArrowMode)
        {
            if (_particles.Emitting) _particles.Emitting = false;
            // the chevron 4 m ahead on the ground, turned toward the target; hidden when the target is behind the view
            var cam = GetViewport()?.GetCamera3D();
            bool behind = cam != null && (-cam.GlobalBasis.Z with { Y = 0 }).Normalized().Dot(flat) < Mathf.Cos(Mathf.DegToRad(120));
            _arrow.Visible = !behind;
            float ground = NavTarget.GroundAt?.Invoke(me.E + flat.X * 4, me.N - flat.Z * 4) is { } g && body.Origin is { } o
                ? o.ToWorld(0, 0, g).Y + 0.05f : body.GlobalPosition.Y + 0.05f;
            var at = body.GlobalPosition + flat * 4f;
            _arrow.GlobalTransform = new Transform3D(Basis.LookingAt(flat, Vector3.Up), new Vector3(at.X, ground, at.Z));
            return;
        }
        _arrow.Visible = false;
        _particles.GlobalPosition = origin + flat * ahead;
        _particles.AmountRatio = Blowing ? 1f : 0.35f;
        if (!_particles.Emitting) _particles.Emitting = true;
        // the gust is heard from the way to go
        _gust.GlobalPosition = origin + flat * 8f;
    }
}
