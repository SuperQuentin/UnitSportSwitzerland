using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// The Böögg's pyre, drawn and heard (client, #1358, <c>docs/notes/occasions/festival-sechselaeuten.md</c>): the 10 m pyre with the Böögg on top while the
/// Sechseläuten runs, the flames and smoke once the guilds light it, the head's blast when the server says it burst, then the embers. It follows the
/// round's view (<see cref="FestivalService.SechseView"/>) and the shared clock, never anything else: <b>the flames are the same whatever the burn
/// will last</b> (a fixed ramp over the first 15 seconds), so a watcher learns nothing before the bang. Built once per site, nothing is rebuilt per
/// frame: the flames and the smoke are one MultiMesh of billboards each animated by <c>sechse_flame.gdshader</c> from one <c>ramp</c> uniform that is set
/// only when its quantised value changes; the head is a mesh that is hidden at the bang; the blast is a pooled one-shot particle burst, a flash light and a sound.
/// Placed from LV95 when it is built (the node is an origin container), like the Schwing ring.
/// </summary>
public partial class BooggStage : Node3D, IOriginContainer
{
    public const string NodeName = "BooggStage";

    private const int Flames = 44, Smokes = 16;

    /// <summary>Seconds the flames take to reach their full height after the light (the same for every burn, so it says nothing of the burn's length).</summary>
    public const double RampSeconds = 15;

    /// <summary>How long a bang is still "fresh" (played as a blast) for a peer that hears of it late, s.</summary>
    public const double FreshSeconds = 6;

    public static BooggStage? Instance { get; private set; }

    private readonly WorldOrigin _origin;
    private readonly Func<Vector3, float?> _ground;
    private readonly Random _rng = new(1358);
    private (double E, double N) _at = (double.NaN, double.NaN);
    private bool _groundOk;
    private Node3D? _root, _figure;
    private MeshInstance3D? _pyre, _body, _head, _remains;
    private MultiMeshInstance3D? _fire, _smoke;
    private ShaderMaterial? _fireMat, _smokeMat;
    private OmniLight3D? _light;
    private CpuParticles3D? _blast;
    private AudioStreamPlayer3D? _voice, _crackVoice;
    private double _look, _nextCrackle, _flash;
    private (int Round, int Phase) _state = (-1, -1);
    private int _ramp = -1;
    private float _groundY = float.NaN;

    /// <summary>The flames' height now, 0 to 1 (checks).</summary>
    public float Ramp => Math.Max(_ramp, 0) / 50f;

    /// <summary>The pyre stands (a Sechseläuten runs here).</summary>
    public bool Built => _root != null;

    /// <summary>The head is still on the Böögg (checks).</summary>
    public bool HeadOn => _head is { Visible: true };

    /// <summary>The embers lie where the pyre stood (checks).</summary>
    public bool EmbersShown => _remains is { Visible: true };

    /// <summary>Blasts played, lights played, crackles played since the node began (checks).</summary>
    public int BlastsPlayed { get; private set; }

    public int LightsPlayed { get; private set; }

    public int CracklesPlayed { get; private set; }

    /// <summary>The pyre's position in the world (checks), or null before it is built.</summary>
    public Vector3? PyrePosition => _root?.Position;

    private static double Clock => ClockSync.SimNow;

    public BooggStage(WorldOrigin origin, Func<Vector3, float?> ground)
    {
        Name = NodeName;
        _origin = origin;
        _ground = ground;
    }

    public BooggStage() : this(WorldOrigin.SwissDefault(), _ => null) { }

    public static BooggStage Create(Node world, ChunkManager? chunks, WorldOrigin origin)
    {
        var s = new BooggStage(origin, p => chunks != null && chunks.TryGetHeight(p, out float h) ? h : null);
        world.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Process(double delta)
    {
        _look -= delta;
        if (_look <= 0)
        {
            _look = 1.0;
            Refresh();
        }
        if (_root == null) return;
        Animate(delta);
    }

    // ---- building ---------------------------------------------------------------------------------------

    private void Refresh()
    {
        if (FestivalService.Instance?.SechseNow() is not { } m)
        {
            if (_root != null) Clear();
            return;
        }
        var site = m.Site;
        var at = SechselaeutenRules.PyreAt(site);
        if (_root != null && _at == at)
        {
            if (!_groundOk) PlaceOnGround();   // the terrain arrived after the pyre was built
            return;
        }
        Clear();
        _at = at;
        Build();
        PlaceOnGround();
    }

    private void Clear()
    {
        _root?.QueueFree();
        _root = null;
        _figure = null;
        _pyre = _body = _head = _remains = null;
        _fire = _smoke = null;
        _light = null;
        _blast = null;
        _voice = _crackVoice = null;
        _at = (double.NaN, double.NaN);
        _state = (-1, -1);
        _ramp = -1;
        _groundOk = false;
        _groundY = float.NaN;
    }

    private void PlaceOnGround()
    {
        if (_root == null) return;
        var probe = _origin.ToWorld(_at.E, _at.N, 0);
        float? h = _ground(probe);
        _groundOk = h != null;
        float y = h ?? GetViewport().GetCamera3D()?.GlobalPosition.Y - 1.7f ?? 400f;
        if (float.IsNaN(_groundY) || Math.Abs(y - _groundY) > 0.01f)
        {
            _groundY = y;
            _root.Position = new Vector3(probe.X, y, probe.Z);
        }
    }

    private static ShaderMaterial PropMaterial()
    {
        var m = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        m.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(m);
        return m;
    }

    private void Build()
    {
        var root = _root = new Node3D { Name = "Stage" };
        var prop = PropMaterial();
        root.AddChild(_pyre = new MeshInstance3D { Name = "Pyre", Mesh = SechselaeutenModels.Pyre(), MaterialOverride = prop });
        _figure = new Node3D { Name = "Boogg", Position = new Vector3(0, 7.3f, 0) };
        root.AddChild(_figure);
        _figure.AddChild(_body = new MeshInstance3D { Name = "Body", Mesh = SechselaeutenModels.BooggBody(), MaterialOverride = prop });
        _figure.AddChild(_head = new MeshInstance3D { Name = "Head", Mesh = SechselaeutenModels.BooggHead(), MaterialOverride = prop, Position = new Vector3(0, SechselaeutenModels.BooggHeadHeight, 0) });
        root.AddChild(_remains = new MeshInstance3D { Name = "Embers", Mesh = SechselaeutenModels.Remains(), MaterialOverride = prop, Visible = false });

        var shader = GD.Load<Shader>("res://shaders/sechse_flame.gdshader");
        _fireMat = new ShaderMaterial { Shader = shader };
        _fireMat.SetShaderParameter("smoke_mix", 0f);
        _fireMat.SetShaderParameter("rise", 5.5f);
        _fireMat.SetShaderParameter("speed", 0.9f);
        _fireMat.SetShaderParameter("ramp", 0f);
        _smokeMat = new ShaderMaterial { Shader = shader };
        _smokeMat.SetShaderParameter("smoke_mix", 1f);
        _smokeMat.SetShaderParameter("rise", 16f);
        _smokeMat.SetShaderParameter("speed", 0.18f);
        _smokeMat.SetShaderParameter("ramp", 0f);
        var rng = new Random(1358);
        _fire = Puffs("Flames", Flames, _fireMat, rng, i =>
        {
            // spread over the pyre's footprint, more at the foot than at the top: a flame is born anywhere on the crib
            float h = 0.4f + (float)Math.Pow(rng.NextDouble(), 1.6) * 7f;
            float spread = 2.2f * (1f - 0.35f * h / 7f);
            return new Vector3(((float)rng.NextDouble() * 2 - 1) * spread, h, ((float)rng.NextDouble() * 2 - 1) * spread);
        }, 1.7f, 3.2f);
        _smoke = Puffs("Smoke", Smokes, _smokeMat, rng, i => new Vector3(((float)rng.NextDouble() * 2 - 1) * 1.8f, 6.5f + (float)rng.NextDouble() * 2f, ((float)rng.NextDouble() * 2 - 1) * 1.8f), 3f, 5f);
        root.AddChild(_fire);
        root.AddChild(_smoke);
        _fire.Visible = _smoke.Visible = false;

        _light = new OmniLight3D { Name = "Glow", Position = new Vector3(0, 5f, 0), OmniRange = 45f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, Visible = false, ShadowEnabled = false };
        root.AddChild(_light);
        _blast = Blast();
        _blast.Position = _figure.Position + new Vector3(0, SechselaeutenModels.BooggHeadHeight, 0);
        root.AddChild(_blast);
        _voice = new AudioStreamPlayer3D { Name = "Voice", Bus = SfxBus.Name, MaxDistance = 600f, UnitSize = 25f, Position = new Vector3(0, 6f, 0) };
        root.AddChild(_voice);
        _crackVoice = new AudioStreamPlayer3D { Name = "Crackle", Bus = SfxBus.Name, MaxDistance = 250f, UnitSize = 12f, Position = new Vector3(0, 2f, 0) };
        root.AddChild(_crackVoice);
        AddChild(root);
        // touch the banks now, so the first bang does not stall the frame it is heard in
        _ = SechselaeutenSounds.HeadBlast;
        _ = SechselaeutenSounds.PyreWhoosh;
        _ = SechselaeutenSounds.FireCrackle;
    }

    private static MultiMeshInstance3D Puffs(string name, int count, ShaderMaterial material, Random rng, Func<int, Vector3> at, float sizeMin, float sizeMax)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = count,
        };
        for (int i = 0; i < count; i++)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, at(i)));
            mm.SetInstanceCustomData(i, new Color((float)rng.NextDouble(), sizeMin + (float)rng.NextDouble() * (sizeMax - sizeMin), (float)rng.NextDouble(), 0f));
        }
        return new MultiMeshInstance3D
        {
            Name = name, Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-10, -2, -10), new Vector3(20, 34, 20)),
        };
    }

    private static ImageTexture? _disc;

    /// <summary>A round soft spot (white, fading to clear at the rim) for the blast's sparks, made once.</summary>
    private static ImageTexture SoftDisc()
    {
        if (_disc != null) return _disc;
        const int N = 64;
        var image = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = new Vector2(x + 0.5f - N / 2f, y + 0.5f - N / 2f).Length() / (N / 2f);
                float a = Mathf.Clamp(1f - d, 0f, 1f);
                image.SetPixel(x, y, new Color(1, 1, 1, a * a * (3f - 2f * a)));
            }
        return _disc = ImageTexture.CreateFromImage(image);
    }

    private static CpuParticles3D Blast()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(1f, 0.95f, 0.7f, 1f));
        gradient.SetColor(1, new Color(1f, 0.25f, 0.05f, 0f));
        gradient.AddPoint(0.35f, new Color(1f, 0.6f, 0.15f, 0.9f));
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = SoftDisc(),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            DisableFog = true,
        };
        return new CpuParticles3D
        {
            Name = "Blast", Emitting = false, OneShot = true, Amount = 90, Lifetime = 1.8, Explosiveness = 1f, Randomness = 0.3f,
            Mesh = new QuadMesh { Size = new Vector2(0.9f, 0.9f), Material = mat },
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.4f,
            Direction = Vector3.Up, Spread = 180f, InitialVelocityMin = 7f, InitialVelocityMax = 20f,
            Gravity = new Vector3(0, -7f, 0), DampingMin = 1.5f, DampingMax = 3f,
            ScaleAmountMin = 0.6f, ScaleAmountMax = 2.2f, ColorRamp = gradient,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // ---- the round ------------------------------------------------------------------------------------

    private void Animate(double delta)
    {
        var view = FestivalService.Instance?.SechseView;
        int round = view?.Round ?? 0, phase = view?.Phase ?? 0;
        double sim = Clock;
        if (_state != (round, phase))
        {
            var was = _state;
            _state = (round, phase);
            OnState(view, was, sim);
        }
        // the flames' height: a fixed ramp after the light, full until the bang, then falling to embers; quantised so the uniform is set rarely
        double target = 0;
        if (view != null && phase == (int)BooggPhase.Burning) target = Math.Clamp((sim - view.LitAt) / RampSeconds, 0, 1);
        else if (view != null && phase == (int)BooggPhase.Done) target = Math.Clamp(0.55 - (sim - view.DoneAt) / (SechselaeutenRules.DoneHold * 1.2) * 0.5, 0.05, 0.55);
        int q = (int)Math.Round(target * 50);
        if (q != _ramp) ApplyRamp(q);
        if (_flash > 0 && _light != null)
        {
            _flash = Math.Max(0, _flash - delta * 2.5);
            _light.LightEnergy = (float)(_ramp / 50.0 * 6.0 + _flash * 60.0);
            _light.Visible = _light.LightEnergy > 0.05f;
        }
        // the fire's pops while it burns (and for the first half minute of the embers)
        bool burning = phase == (int)BooggPhase.Burning || (phase == (int)BooggPhase.Done && view != null && sim - view.DoneAt < 30);
        if (burning && sim >= _nextCrackle && _voice != null)
        {
            _nextCrackle = sim + 0.5 + _rng.NextDouble() * 1.2;
            Play(SechselaeutenSounds.FireCrackle, -8f, _crackVoice);
            CracklesPlayed++;
        }
    }

    private void OnState(BooggView? view, (int Round, int Phase) was, double sim)
    {
        int phase = view?.Phase ?? 0;
        bool burst = phase == (int)BooggPhase.Done;
        if (_head != null) _head.Visible = !burst;
        if (_body != null) _body.Visible = !burst;
        if (_pyre != null) _pyre.Visible = !burst;
        if (_remains != null) _remains.Visible = burst;
        if (_figure != null) _figure.Visible = !burst;
        if (view == null) return;
        if (phase == (int)BooggPhase.Burning && was.Phase == (int)BooggPhase.Betting && sim - view.LitAt < FreshSeconds)
        {
            Play(SechselaeutenSounds.PyreWhoosh, -4f);
            LightsPlayed++;
        }
        if (burst && view.DoneAt > 0 && sim - view.DoneAt < FreshSeconds) Bang();
    }

    /// <summary>The head bursts: the particles, the flash, the boom.</summary>
    private void Bang()
    {
        _blast?.Restart();
        if (_blast != null) _blast.Emitting = true;
        _flash = 1;
        Play(SechselaeutenSounds.HeadBlast, 2f);
        BlastsPlayed++;
    }

    private void ApplyRamp(int q)
    {
        _ramp = q;
        float r = q / 50f;
        _fireMat?.SetShaderParameter("ramp", r);
        _smokeMat?.SetShaderParameter("ramp", r > 0.02f ? Math.Min(1f, r * 1.4f) : 0f);
        if (_fire != null) _fire.Visible = q > 0;
        if (_smoke != null) _smoke.Visible = q > 0;
        if (_light != null && _flash <= 0)
        {
            _light.LightEnergy = r * 6f;
            _light.Visible = r > 0.02f;
        }
    }

    private void Play(SfxBank bank, float db, AudioStreamPlayer3D? voice = null)
    {
        var v = voice ?? _voice;
        if (v == null) return;
        var (stream, pitch, jitter) = bank.Pick(_rng);
        v.Stream = stream;
        v.PitchScale = pitch;
        v.VolumeDb = db + jitter;
        v.Play();
    }
}
