using Godot;
using UnitSport.Audio;

namespace UnitSport.Vehicles;

/// <summary>
/// A vehicle going up: a fireball, a flash of light, a column of smoke and a 3D boom, plus the
/// consequences — shake and damage for whoever is close (<see cref="Blast"/>). Self-contained and
/// self-freeing, so a crash anywhere in the world only has to call <see cref="Spawn"/>.
///
/// <para>
/// Particles are unshaded billboards like the rest of the feel layer's; the fireball blends
/// additively so it glows over what is behind it instead of reading as orange cardboard.
/// </para>
/// </summary>
public partial class Explosion : Node3D
{
    /// <summary>Anyone this close takes damage, falling off with distance.</summary>
    public const float DamageRadius = 9f;

    /// <summary>Anyone this close feels it — shake and rumble, scaled by distance.</summary>
    public const float ShockRadius = 70f;

    /// <summary>
    /// Raised for every explosion, with its position. The local player listens and decides what
    /// it costs them — the client-authoritative model: each peer hurts only its own player.
    /// </summary>
    public static event Action<Vector3>? Blast;

    private double _age;
    private OmniLight3D _light = null!;

    public static Explosion Spawn(Node parent, Vector3 position)
    {
        var e = new Explosion { Name = "Explosion" };
        parent.AddChild(e);
        e.GlobalPosition = position;
        Blast?.Invoke(position);
        return e;
    }

    public override void _Ready()
    {
        TopLevel = true;

        AddChild(Burst("Fireball", 70, 0.9f, 0.9f, additive: true, explosiveness: 1f,
            velocity: (4f, 13f), gravity: 3f, spread: 180f,
            ramp: new[] { new Color(1f, 0.9f, 0.45f, 1f), new Color(1f, 0.4f, 0.05f, 0.9f), new Color(0.35f, 0.08f, 0.02f, 0.6f), new Color(0.1f, 0.05f, 0.03f, 0f) }));
        AddChild(Burst("Debris", 40, 1.6f, 0.18f, additive: false, explosiveness: 1f,
            velocity: (6f, 16f), gravity: -9.8f, spread: 70f,
            ramp: new[] { new Color(0.15f, 0.13f, 0.12f, 1f), new Color(0.1f, 0.1f, 0.1f, 1f) }));
        AddChild(Burst("Smoke", 40, 5f, 2.2f, additive: false, explosiveness: 0.3f,
            velocity: (1f, 3f), gravity: 2.5f, spread: 25f,
            ramp: new[] { new Color(0.25f, 0.22f, 0.2f, 0.85f), new Color(0.35f, 0.34f, 0.33f, 0f) }));

        _light = new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.25f), LightEnergy = 12f, OmniRange = 30f };
        AddChild(_light);

        var boom = new AudioStreamPlayer3D
        {
            Stream = SfxSynth.Boom,
            UnitSize = 25f,
            MaxDistance = 3000f,
            VolumeDb = Mathf.LinearToDb(Mathf.Max(0.01f, Core.GameSettings.Current.SfxVolume)),
            Autoplay = true,
            Bus = SfxBus.Name,
        };
        AddChild(boom);
    }

    public override void _Process(double delta)
    {
        _age += delta;
        _light.LightEnergy = Mathf.Max(0f, 12f * (1f - (float)_age / 0.45f));
        if (_age > 7) QueueFree();
    }

    private static GradientTexture2D? _puff;

    /// <summary>A radial white-to-clear disc, shared by every particle material.</summary>
    private static GradientTexture2D Puff => _puff ??= new GradientTexture2D
    {
        Width = 32,
        Height = 32,
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient
        {
            Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.8f), new Color(1, 1, 1, 0) },
            Offsets = new[] { 0f, 0.45f, 1f },
        },
    };

    private static CurveTexture? _grow;

    private static CurveTexture GrowCurve
    {
        get
        {
            if (_grow != null) return _grow;
            var curve = new Curve();
            curve.AddPoint(new Vector2(0f, 0.35f));
            curve.AddPoint(new Vector2(0.3f, 1f));
            curve.AddPoint(new Vector2(1f, 1.3f));
            return _grow = new CurveTexture { Curve = curve };
        }
    }

    /// <summary>A one-shot particle burst with a colour ramp over its life.</summary>
    internal static GpuParticles3D Burst(string name, int amount, float life, float size, bool additive,
        float explosiveness, (float Min, float Max) velocity, float gravity, float spread, Color[] ramp,
        bool oneShot = true)
    {
        var gradient = new Gradient();
        gradient.Colors = ramp;
        var offsets = new float[ramp.Length];
        for (int i = 0; i < ramp.Length; i++) offsets[i] = ramp.Length == 1 ? 0 : (float)i / (ramp.Length - 1);
        gradient.Offsets = offsets;

        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.8f,
            Direction = Vector3.Up,
            Spread = spread,
            InitialVelocityMin = velocity.Min,
            InitialVelocityMax = velocity.Max,
            Gravity = new Vector3(0, gravity, 0),
            DampingMin = 1f,
            DampingMax = 3f,
            ScaleMin = 0.6f,
            ScaleMax = 1.5f,
            // billows: small at birth, full size a third of the way through its life
            ScaleCurve = GrowCurve,
            ColorRamp = new GradientTexture1D { Gradient = gradient },
        };
        var p = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = life,
            OneShot = oneShot,
            Explosiveness = explosiveness,
            ProcessMaterial = mat,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(size, size),
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    VertexColorUseAsAlbedo = true,
                    // a soft disc, not a quad: square flat particles read as cardboard
                    AlbedoTexture = Puff,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                },
            },
            VisibilityAabb = new Aabb(new Vector3(-30, -10, -30), new Vector3(60, 60, 60)),
            Emitting = true,
        };
        return p;
    }
}
