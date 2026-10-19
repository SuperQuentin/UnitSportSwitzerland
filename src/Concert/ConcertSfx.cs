using Godot;
using UnitSport.Audio;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Concert;

/// <summary>
/// A concert's special effects (#885), read off their DMX like every other fixture: flame projectors
/// and CO2 jets for as long as they are fired, a gerb's four-second fountain, a confetti cannon's shot
/// and an airburst's launch and burst on each rising edge, low fog while its pump runs. Nothing fires
/// disarmed. Particles are the game's usual unshaded billboards; the sounds are synthesised here
/// (static banks, so they show in the sound player) and the burst is the explosion's boom.
/// </summary>
public partial class ConcertSfx : Node3D
{
    // ---- the sounds ----------------------------------------------------------------------------------

    private static SfxBank? _flame, _co2, _pop, _crackle;

    /// <summary>A flame projector's roar: low noise slammed open, a tail with crackle in it.</summary>
    public static SfxBank FlameBank => _flame ??= SfxBank.Build("concert flame", 4, 0.9f, 885, (rng, n) =>
    {
        var roar = LowPass(Noise(rng, n), 0.06f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate, env = Mathf.Min(1f, t / 0.03f) * Mathf.Exp(-2.6f * t);
            float crackle = rng.NextDouble() < 0.004 ? (float)(rng.NextDouble() * 2 - 1) * 0.8f : 0f;
            s[i] = roar[i] * 6f * env + crackle * env;
        }
        return s;
    });

    /// <summary>A CO2 jet: a hard hiss that stops dead.</summary>
    public static SfxBank Co2Bank => _co2 ??= SfxBank.Build("concert co2", 4, 1.0f, 886, (rng, n) =>
    {
        var hiss = HighPass(Noise(rng, n), 0.3f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate, env = Mathf.Min(1f, t / 0.015f) * (t < 0.75f ? 1f : Mathf.Exp(-30f * (t - 0.75f)));
            s[i] = hiss[i] * 1.6f * env;
        }
        return s;
    });

    /// <summary>A confetti cannon: a thump and the paper's rustle.</summary>
    public static SfxBank PopBank => _pop ??= SfxBank.Build("concert confetti", 4, 0.6f, 887, (rng, n) =>
    {
        var paper = BandPass(Noise(rng, n), 0.08f, 0.4f);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(110f, 45f, Mathf.Min(1f, t / 0.12f)) / Rate;
            s[i] = Mathf.Sin(phase) * 1.4f * Mathf.Exp(-18f * t) + paper[i] * 1.2f * Mathf.Exp(-5f * t);
        }
        return s;
    });

    /// <summary>A gerb's sparks: dry crackles, thick and then thinning.</summary>
    public static SfxBank CrackleBank => _crackle ??= SfxBank.Build("concert sparks", 4, 1.6f, 888, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate, density = 0.02f * Mathf.Exp(-1.2f * t);
            if (rng.NextDouble() < density)
            {
                float amp = (float)rng.NextDouble() * 0.9f + 0.1f;
                for (int k = 0; k < 40 && i + k < n; k++) s[i + k] += amp * (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-k / 8f);
            }
        }
        return HighPass(s, 0.1f);
    });

    // ---- the emitters ----------------------------------------------------------------------------------

    private readonly DmxPatch _patch;
    private readonly ConcertEvent _e;
    private Fixture[] _fx = Array.Empty<Fixture>();
    private CpuParticles3D[] _emit = Array.Empty<CpuParticles3D>();
    private CpuParticles3D?[] _burst = Array.Empty<CpuParticles3D?>();
    private bool[] _was = Array.Empty<bool>();
    private double[] _until = Array.Empty<double>(), _burstAt = Array.Empty<double>();
    private OmniLight3D _flameLight = null!;
    private readonly Random _rng = new(885);

    public ConcertSfx(DmxPatch patch, ConcertEvent e)
    {
        _patch = patch;
        _e = e;
        Name = "Effects";
    }

    public ConcertSfx() : this(new DmxPatch(), ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 1)) { }

    private static readonly Texture2D Puff = new GradientTexture2D
    {
        Width = 32, Height = 32, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0) } },
    };

    private static StandardMaterial3D Mat(bool additive, bool billboard = true) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
        BillboardMode = billboard ? BaseMaterial3D.BillboardModeEnum.Particles : BaseMaterial3D.BillboardModeEnum.Disabled,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoTexture = billboard ? Puff : null,
        DisableFog = true,
    };

    private static Gradient Ramp(params Color[] c) => new() { Colors = c, Offsets = Enumerable.Range(0, c.Length).Select(i => i / (float)(c.Length - 1)).ToArray() };

    public override void _Ready()
    {
        _fx = _patch.Fixtures.Where(f => f.Type.Kind is FixtureKind.Flame or FixtureKind.Co2 or FixtureKind.Confetti
            or FixtureKind.Gerb or FixtureKind.Airburst or FixtureKind.LowFog).ToArray();
        _emit = new CpuParticles3D[_fx.Length];
        _burst = new CpuParticles3D?[_fx.Length];
        _was = new bool[_fx.Length];
        _until = new double[_fx.Length];
        _burstAt = Enumerable.Repeat(double.MaxValue, _fx.Length).ToArray();
        var pal = _e.Palette;
        for (int i = 0; i < _fx.Length; i++)
        {
            var f = _fx[i];
            var p = f.Type.Kind switch
            {
                FixtureKind.Flame => new CpuParticles3D
                {
                    Amount = 50, Lifetime = 0.8f, Direction = Vector3.Up, Spread = 5f, InitialVelocityMin = 11f, InitialVelocityMax = 15f,
                    Gravity = new Vector3(0, 2f, 0), ScaleAmountMin = 1.6f, ScaleAmountMax = 3.2f,
                    ColorRamp = Ramp(new Color(1f, 0.95f, 0.6f, 1f), new Color(1f, 0.5f, 0.1f, 0.9f), new Color(0.6f, 0.1f, 0.02f, 0f)),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: true) },
                },
                FixtureKind.Co2 => new CpuParticles3D
                {
                    Amount = 60, Lifetime = 1.2f, Direction = f.Home, Spread = 9f, InitialVelocityMin = 14f, InitialVelocityMax = 18f,
                    DampingMin = 6f, DampingMax = 9f, Gravity = Vector3.Zero, ScaleAmountMin = 1.5f, ScaleAmountMax = 3.5f,
                    ColorRamp = Ramp(new Color(1f, 1f, 1f, 0.75f), new Color(0.9f, 0.92f, 0.95f, 0f)),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: false) },
                },
                FixtureKind.Gerb => new CpuParticles3D
                {
                    Amount = 140, Lifetime = 1.1f, Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = 6f, InitialVelocityMax = 9f,
                    Gravity = new Vector3(0, -9.8f, 0), ScaleAmountMin = 0.12f, ScaleAmountMax = 0.25f,
                    ColorRamp = Ramp(new Color(1f, 1f, 0.85f, 1f), new Color(1f, 0.75f, 0.3f, 1f), new Color(1f, 0.5f, 0.1f, 0f)),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: true) },
                },
                FixtureKind.Confetti => new CpuParticles3D
                {
                    Amount = 360, Lifetime = 10f, OneShot = true, Explosiveness = 0.85f, Direction = f.Home, Spread = 22f,
                    InitialVelocityMin = 14f, InitialVelocityMax = 20f, Gravity = new Vector3(0, -1.4f, 0), DampingMin = 0.6f, DampingMax = 1.2f,
                    AngularVelocityMin = -360f, AngularVelocityMax = 360f, ParticleFlagRotateY = true,
                    ColorInitialRamp = Ramp(pal[0], pal[1 % pal.Length], Colors.White, pal[2 % pal.Length], new Color(1f, 0.85f, 0.2f)),
                    Mesh = new QuadMesh { Size = new Vector2(0.28f, 0.18f), Material = Mat(additive: false, billboard: false) },
                },
                FixtureKind.Airburst => new CpuParticles3D
                {
                    // the launch: a spark climbing to the burst
                    Amount = 24, Lifetime = 1.3f, OneShot = true, Explosiveness = 0.2f, Direction = f.Home, Spread = 2f,
                    InitialVelocityMin = 18f, InitialVelocityMax = 22f, Gravity = new Vector3(0, -2f, 0), ScaleAmountMin = 0.2f, ScaleAmountMax = 0.35f,
                    ColorRamp = Ramp(new Color(1f, 0.9f, 0.6f, 1f), new Color(1f, 0.6f, 0.2f, 0f)),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: true) },
                },
                _ => new CpuParticles3D
                {
                    // low fog: heavy, rolling out over the deck's edge
                    Amount = 36, Lifetime = 9f, Direction = f.Home with { Y = 0 }, Spread = 30f, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.4f,
                    Gravity = new Vector3(0, -0.05f, 0), ScaleAmountMin = 4f, ScaleAmountMax = 7f,
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(4f, 0.2f, 1f),
                    ColorRamp = Ramp(new Color(0.85f, 0.87f, 0.9f, 0f), new Color(0.85f, 0.87f, 0.9f, 0.3f), new Color(0.85f, 0.87f, 0.9f, 0f)),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: false) },
                },
            };
            p.Name = $"{f.Type.Kind}{f.Id}";
            p.Position = f.Position;
            p.Emitting = false;
            p.LocalCoords = false;
            AddChild(p);
            _emit[i] = p;
            if (f.Type.Kind == FixtureKind.Airburst)
            {
                // the burst: a ball of stars in the show's colours
                float rise = f.Home.Y > 0.05f ? (24f - f.Position.Y) / f.Home.Y : 20f;
                var b = new CpuParticles3D
                {
                    Name = $"Burst{f.Id}", Position = f.Position + f.Home * rise,
                    Amount = 240, Lifetime = 2.6f, OneShot = true, Explosiveness = 1f, Direction = Vector3.Up, Spread = 180f,
                    InitialVelocityMin = 9f, InitialVelocityMax = 12f, Gravity = new Vector3(0, -2.5f, 0), DampingMin = 1f, DampingMax = 1.6f,
                    ScaleAmountMin = 0.25f, ScaleAmountMax = 0.45f,
                    ColorInitialRamp = Ramp(pal[0], pal[1 % pal.Length], Colors.White),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = Mat(additive: true) },
                    Emitting = false, LocalCoords = false,
                };
                AddChild(b);
                _burst[i] = b;
            }
        }
        // the heat of the flames on everything round them
        var flames = _fx.Where(f => f.Type.Kind == FixtureKind.Flame).ToArray();
        var at = flames.Length > 0 ? flames.Aggregate(Vector3.Zero, (a, f) => a + f.Position) / flames.Length : Vector3.Zero;
        _flameLight = new OmniLight3D { Position = at + Vector3.Up * 3f, OmniRange = 28f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, ShadowEnabled = false };
        AddChild(_flameLight);
    }

    /// <summary>One frame: the effects as their channels say.</summary>
    public void Step(double now)
    {
        int burning = 0;
        for (int i = 0; i < _fx.Length; i++)
        {
            var f = _fx[i];
            var p = _emit[i];
            if (f.Type.Kind == FixtureKind.LowFog)
            {
                bool fog = _patch.Get01(f, Attr.Pump) > 0.2f;
                if (p.Emitting != fog) p.Emitting = fog;
                continue;
            }
            bool armed = _patch.Get01(f, Attr.Arm) > 0.5f, fire = armed && _patch.Get01(f, Attr.Fire) > 0.5f;
            bool rising = fire && !_was[i];
            _was[i] = fire;
            switch (f.Type.Kind)
            {
                case FixtureKind.Flame:
                case FixtureKind.Co2:
                    if (p.Emitting != fire) p.Emitting = fire;
                    if (rising) Sound(f.Position, f.Type.Kind == FixtureKind.Flame ? FlameBank : Co2Bank, 4f);
                    if (fire && f.Type.Kind == FixtureKind.Flame) burning++;
                    break;
                case FixtureKind.Gerb:
                    if (rising)
                    {
                        _until[i] = now + 4.0;
                        Sound(f.Position, CrackleBank, 0f);
                    }
                    bool on = now < _until[i];
                    if (p.Emitting != on) p.Emitting = on;
                    break;
                case FixtureKind.Confetti:
                    if (rising)
                    {
                        p.Restart();
                        Sound(f.Position, PopBank, 6f);
                    }
                    break;
                case FixtureKind.Airburst:
                    if (rising)
                    {
                        p.Restart();
                        _burstAt[i] = now + 1.2;
                        Sound(f.Position, SfxSynth.WhooshBank, 2f);
                    }
                    if (now >= _burstAt[i] && _burst[i] is { } b)
                    {
                        _burstAt[i] = double.MaxValue;
                        b.Restart();
                        Sound(b.Position, SfxSynth.BoomBank, 4f);
                    }
                    break;
            }
        }
        _flameLight.LightEnergy = Mathf.Lerp(_flameLight.LightEnergy, Math.Min(2.2f, burning * 0.3f), 0.4f);
    }

    /// <summary>A one-shot sound where an effect fires, heard across the bowl.</summary>
    private void Sound(Vector3 at, SfxBank bank, float db)
    {
        if (DisplayServer.GetName() == "headless") return;
        var (stream, pitch, vol) = bank.Pick(_rng);
        var s = new AudioStreamPlayer3D
        {
            Stream = stream, PitchScale = pitch, VolumeDb = vol + db, UnitSize = 18f, MaxDistance = 600f, Bus = SfxBus.Name, Position = at,
        };
        AddChild(s);
        s.Finished += s.QueueFree;
        s.Play();
    }
}
