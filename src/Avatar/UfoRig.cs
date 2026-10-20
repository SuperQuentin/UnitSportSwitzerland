using Godot;
using static UnitSport.Avatar.UfoMeshBuilder;

namespace UnitSport.Avatar;

/// <summary>
/// The drawn flying saucer (#861, <see cref="UfoMeshBuilder"/>): its moving parts as named
/// children, posed by <see cref="Show"/> from a <see cref="Look"/> (the flyer's state, the same four
/// numbers a remote copy gets). No <c>_Process</c> of its own: the rider's peer calls it from
/// <c>Ufo.AnimateFlight</c>, every other peer from <c>AnimateRemote</c>, a parked one from the
/// vehicle's copy. Materials change only when a lamp's level does (<c>perf-no-per-frame-allocations</c>).
/// </summary>
public partial class UfoRig : Node3D
{
    /// <summary>What the saucer shows.</summary>
    /// <param name="Hum">The engine, 0..1: the underside's glow and the lamps' chase speed.</param>
    /// <param name="Legs">0 folded .. 1 down.</param>
    /// <param name="Ramp">0 shut .. 1 open.</param>
    /// <param name="Beam">The tractor beam is on.</param>
    /// <param name="Lights">The rim lamps chase (off: dark lamps, a dim glow).</param>
    /// <param name="Landed">Standing on its legs: the thud as it touches down.</param>
    public readonly record struct Look(float Hum, float Legs, float Ramp, bool Beam, bool Lights, bool Landed = false);

    private static readonly Look ParkedLook = new(0f, 1f, 1f, false, true, true);

    private Node3D _hull = null!, _ramp = null!;
    private readonly Node3D[] _legs = new Node3D[3];
    private readonly MeshInstance3D[] _lamps = new MeshInstance3D[RimLamps];
    private readonly int[] _lampLevel = new int[RimLamps];
    private MeshInstance3D _beam = null!, _hatch = null!, _under = null!;
    private SpotLight3D _beamLight = null!;
    private OmniLight3D _glow = null!;
    private StandardMaterial3D _underMaterial = null!;
    private float _shownGlow = -1f, _shownGlowEnergy = -1f;
    private float _chase, _bob;
    private Look _shown = new(-1f, -1f, -1f, false, false);
    private AudioStreamPlayer3D? _humVoice, _beamVoice, _servoVoice, _thudVoice;
    private float _shownHum = -1f;
    private readonly System.Random _rng = new();

    /// <summary>The legs' angles round the axis, rad: clear of the ramp at the front.</summary>
    private static readonly float[] LegAngles = { Mathf.Pi / 3f, Mathf.Pi, 5f * Mathf.Pi / 3f };

    /// <summary>Lamp brightness steps: the chase head, two trailing, then the rest.</summary>
    private const int Levels = 4;

    public static UfoRig Create(HumanPalette? pilot)
    {
        var rig = new UfoRig { Name = "Ufo" };
        rig.Build(pilot);
        rig.Show(ParkedLook, 0f);
        return rig;
    }

    private void Build(HumanPalette? pilot)
    {
        _hull = new Node3D { Name = "Hull" };
        AddChild(_hull);
        var body = new MeshInstance3D { Name = "Body", Mesh = HullMesh };
        MeshScratch.Paint(body, HumanMeshBuilder.Material(), Glass);
        _hull.AddChild(body);

        if (pilot != null)
            _hull.AddChild(new MeshInstance3D
            {
                Name = "Pilot",
                Mesh = HumanMeshBuilder.Build(pilot, HumanPose.Hanging),
                MaterialOverride = HumanMeshBuilder.FigureMaterial(),
                Position = AircraftMeshBuilder.Flip(PilotSeat),
            });

        var legs = new Node3D { Name = "Legs" };
        _hull.AddChild(legs);
        for (int i = 0; i < 3; i++)
        {
            float a = LegAngles[i];
            var hinge = new Vector3(Mathf.Sin(a) * LegHingeR, Belly + 0.02f, Mathf.Cos(a) * LegHingeR);
            _legs[i] = new Node3D { Name = $"Leg{i}", Position = AircraftMeshBuilder.Flip(hinge) };
            _legs[i].AddChild(new MeshInstance3D { Mesh = LegMesh, MaterialOverride = HumanMeshBuilder.Material() });
            legs.AddChild(_legs[i]);
        }

        _ramp = new Node3D { Name = "Ramp", Position = AircraftMeshBuilder.Flip(new Vector3(0, RampHingeY, RampHingeZ)) };
        _ramp.AddChild(new MeshInstance3D { Mesh = RampMesh, MaterialOverride = HumanMeshBuilder.Material() });
        _ramp.AddChild(new MeshInstance3D { Name = "Lights", Mesh = RampLightMesh, MaterialOverride = WarmLamp });
        _hull.AddChild(_ramp);
        _hull.AddChild(_hatch = new MeshInstance3D { Name = "HatchGlow", Mesh = HatchMesh, MaterialOverride = WarmLamp });
        _hull.AddChild(new MeshInstance3D { Name = "Portholes", Mesh = PortholeMesh, MaterialOverride = PortholeLamp });

        _underMaterial = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _hull.AddChild(_under = new MeshInstance3D { Name = "UnderGlow", Mesh = UnderMesh, MaterialOverride = _underMaterial });

        var lamps = new Node3D { Name = "Lamps" };
        _hull.AddChild(lamps);
        for (int i = 0; i < RimLamps; i++)
        {
            float a = Mathf.Tau * i / RimLamps;
            var at = new Vector3(Mathf.Sin(a) * (RimRadius + 0.05f), RimY, Mathf.Cos(a) * (RimRadius + 0.05f));
            _lamps[i] = new MeshInstance3D { Name = $"Lamp{i}", Mesh = LampMesh, Position = AircraftMeshBuilder.Flip(at) };
            _lampLevel[i] = -1;
            lamps.AddChild(_lamps[i]);
        }

        // the beam hangs from the hatch: a cone of light, and a spot that pools it on the ground
        var apex = AircraftMeshBuilder.Flip(UfoMeshBuilder.Hatch);
        _hull.AddChild(_beam = new MeshInstance3D
        {
            Name = "Beam", Mesh = BeamMesh, MaterialOverride = BeamMaterial, Position = apex, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _hull.AddChild(_beamLight = new SpotLight3D
        {
            Name = "BeamLight", Position = apex, Rotation = new Vector3(-Mathf.Pi / 2f, 0, 0), Visible = false,
            LightColor = GlowColour, LightEnergy = 4f, SpotRange = Player.TractorBeamMath.Reach + 5f,
            SpotAngle = Mathf.RadToDeg(Player.TractorBeamMath.HalfAngle) + 3f, ShadowEnabled = false,
        });
        // its voices (none headless): the hum, the beam, the servo, the touchdown
        if (DisplayServer.GetName() != "headless")
        {
            _humVoice = Voice("Hum", Audio.UfoSounds.Hum, 16f);
            _beamVoice = Voice("BeamSound", Audio.UfoSounds.BeamDrone, 12f);
            _servoVoice = Voice("Servo", Audio.UfoSounds.Servo, 6f);
            _thudVoice = Voice("Thud", null, 10f);
        }

        // the underside's glow on whatever is below (a field at night)
        _hull.AddChild(_glow = new OmniLight3D
        {
            Name = "Glow", Position = new Vector3(0, Belly - 0.6f, 0), LightColor = GlowColour,
            OmniRange = 9f, LightEnergy = 0f, ShadowEnabled = false,
        });
    }

    private AudioStreamPlayer3D Voice(string name, AudioStream? stream, float unitSize)
    {
        var voice = new AudioStreamPlayer3D
        {
            Name = name, Stream = stream, UnitSize = unitSize, MaxDistance = 700f, Bus = Audio.SfxBus.Name,
            Position = new Vector3(0, Belly, 0),
        };
        _hull.AddChild(voice);
        return voice;
    }

    /// <summary>The voices from the look: the hum's pitch and level with the speed, the beam, the servo while a part moves, a thud on touchdown.</summary>
    private void Sound(in Look look, in Look was)
    {
        if (_humVoice == null || _beamVoice == null || _servoVoice == null || _thudVoice == null) return;
        // the first look (just built) is not a change: no thud, no servo for a model appearing
        if (was.Hum < 0f) return;
        float hum = Mathf.Snapped(look.Hum, 0.02f);
        if (!Mathf.IsEqualApprox(hum, _shownHum))
        {
            _shownHum = hum;
            bool on = hum > 0.04f;
            if (on != _humVoice.Playing) { if (on) _humVoice.Play(); else _humVoice.Stop(); }
            _humVoice.PitchScale = 0.75f + 0.7f * hum;
            _humVoice.VolumeDb = Mathf.LinearToDb(0.3f + 0.7f * hum) - 3f;
        }
        if (look.Beam != _beamVoice.Playing) { if (look.Beam) _beamVoice.Play(); else _beamVoice.Stop(); }
        bool moving = !Mathf.IsEqualApprox(look.Legs, was.Legs) || !Mathf.IsEqualApprox(look.Ramp, was.Ramp);
        if (moving != _servoVoice.Playing) { if (moving) _servoVoice.Play(); else _servoVoice.Stop(); }
        if (look.Landed && !was.Landed)
        {
            var (stream, pitch, db) = Audio.UfoSounds.LegsBank.Pick(_rng);
            _thudVoice.Stream = stream;
            _thudVoice.PitchScale = pitch;
            _thudVoice.VolumeDb = db;
            _thudVoice.Play();
        }
    }

    /// <summary>Where the pilot's figure stands, authored: its hip on the left seat's cushion.</summary>
    private static readonly Vector3 PilotSeat = new(-0.55f, DomeBase + 0.4f - 0.965f, -0.27f);

    /// <summary>The seats' hips, node space (−Z forward), pilot first: for the passengers (#158).</summary>
    public static readonly Vector3[] SeatHips =
    {
        AircraftMeshBuilder.Flip(new Vector3(-0.55f, DomeBase + 0.4f, -0.27f)),
        AircraftMeshBuilder.Flip(new Vector3(0.55f, DomeBase + 0.4f, -0.27f)),
    };

    /// <summary>Poses every moving part; <paramref name="dt"/> runs the chase and the hover bob.</summary>
    public void Show(in Look look, float dt)
    {
        _chase = Mathf.PosMod(_chase + dt * (5f + 14f * look.Hum), RimLamps);
        _bob += dt;

        if (!Mathf.IsEqualApprox(look.Legs, _shown.Legs))
            for (int i = 0; i < 3; i++)
            {
                float fold = 1f - look.Legs, tuck = Mathf.Lerp(1f, LegTuck, fold);
                _legs[i].Basis = new Basis(Vector3.Up, LegAngles[i]) * new Basis(Vector3.Right, LegFold * fold)
                    * Basis.FromScale(new Vector3(1f, tuck, tuck));
            }
        if (!Mathf.IsEqualApprox(look.Ramp, _shown.Ramp))
        {
            _ramp.Basis = new Basis(Vector3.Right, -RampOpenAngle * look.Ramp);
            _hatch.Visible = look.Ramp > 0.02f;
            _ramp.GetChild<Node3D>(1).Visible = look.Ramp > 0.5f;
        }
        if (look.Beam != _shown.Beam) _beam.Visible = _beamLight.Visible = look.Beam;
        Sound(look, _shown);
        _shown = look;

        // a hovering saucer bobs a little; on its legs it is still
        float hover = 1f - look.Legs;
        var y = hover > 0.01f ? Mathf.Sin(_bob * 2.1f) * 0.09f * hover : 0f;
        if (!Mathf.IsEqualApprox(_hull.Position.Y, y)) _hull.Position = new Vector3(0, y, 0);

        // the lamps: a head chasing round with two trailing, in their colours; dark when off
        int head = (int)_chase;
        for (int i = 0; i < RimLamps; i++)
        {
            int behind = (head - i + RimLamps) % RimLamps;
            int level = !look.Lights ? 0 : behind == 0 ? 3 : behind <= 2 ? 2 : 1;
            if (level == _lampLevel[i]) continue;
            _lampLevel[i] = level;
            _lamps[i].MaterialOverride = LampLevel(i % LampColours.Length, level);
        }

        // the underside glows with the hum, much dimmer with the lights off
        float glow = Mathf.Snapped((look.Lights ? 0.35f + 0.65f * look.Hum : 0.12f) + (look.Beam ? 0.3f : 0f), 0.02f);
        if (!Mathf.IsEqualApprox(glow, _shownGlow))
        {
            _shownGlow = glow;
            _underMaterial.AlbedoColor = (GlowColour * Mathf.Min(glow, 1.2f)) with { A = 1f };
        }
        float energy = look.Lights ? glow * 2.5f : 0f;
        if (!Mathf.IsEqualApprox(energy, _shownGlowEnergy))
        {
            _shownGlowEnergy = energy;
            _glow.LightEnergy = energy;
            _glow.Visible = energy > 0.01f;
        }
    }

    // ---- shared meshes and materials (one each for every saucer) -----------------------------

    private static ArrayMesh? _hullMesh, _legMesh, _rampMesh, _rampLightMesh, _hatchMesh, _underMesh, _lampMesh, _beamMesh;
    private static ArrayMesh HullMesh => _hullMesh ??= UfoMeshBuilder.Hull();
    private static ArrayMesh LegMesh => _legMesh ??= Leg();
    private static ArrayMesh RampMesh => _rampMesh ??= UfoMeshBuilder.Ramp();
    private static ArrayMesh RampLightMesh => _rampLightMesh ??= RampLights();
    private static ArrayMesh HatchMesh => _hatchMesh ??= HatchGlow();
    private static ArrayMesh UnderMesh => _underMesh ??= UnderGlow();
    private static ArrayMesh LampMesh => _lampMesh ??= UfoMeshBuilder.Lamp();
    private static ArrayMesh? _portholeMesh;
    private static ArrayMesh PortholeMesh => _portholeMesh ??= UfoMeshBuilder.Portholes();
    private static StandardMaterial3D? _porthole;
    /// <summary>The portholes' light: a cabin glow, a little green.</summary>
    private static StandardMaterial3D PortholeLamp => _porthole ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.75f, 1f, 0.85f),
    };
    private static ArrayMesh BeamMesh => _beamMesh ??= BeamCone();
    /// <summary>The beam cone every saucer shares (and the mothership, scaled up).</summary>
    public static ArrayMesh BeamMeshShared => BeamMesh;

    private static StandardMaterial3D? _glass, _warm;
    private static StandardMaterial3D Glass => _glass ??= CarRig.GlassMaterial();
    private static StandardMaterial3D WarmLamp => _warm ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(1f, 0.95f, 0.75f),
    };

    private static readonly StandardMaterial3D?[] _lampLevels = new StandardMaterial3D?[4 * Levels];

    /// <summary>A rim lamp's material: colour <paramref name="colour"/> at brightness <paramref name="level"/> (0 off .. 3 the chase head).</summary>
    private static StandardMaterial3D LampLevel(int colour, int level) =>
        _lampLevels[colour * Levels + level] ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = level == 0 ? new Color(0.22f, 0.24f, 0.28f)
                : (LampColours[colour] * (level switch { 1 => 0.75f, 2 => 1.1f, _ => 1.6f })) with { A = 1f },
        };

    private static ShaderMaterial? _beamMaterial;

    /// <summary>
    /// The beam's light: additive, unshaded, brighter at its edges than through its middle, fading
    /// down its length, with rings of light running up it. Depth-tested, so the ground cuts it off.
    /// </summary>
    public static ShaderMaterial BeamMaterial => _beamMaterial ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                uniform vec4 tint : source_color = vec4(0.45, 1.0, 0.75, 1.0);
                uniform float reach = 45.0;
                varying float depth;
                void vertex() { depth = -VERTEX.y; }
                void fragment() {
                    float fade = clamp(1.0 - depth / reach, 0.0, 1.0);
                    float rings = 0.5 + 0.5 * sin(depth * 1.3 + TIME * 7.0);
                    float edge = 1.0 - abs(dot(normalize(NORMAL), normalize(VIEW)));
                    ALBEDO = tint.rgb * fade * (0.10 + 0.22 * rings + 0.45 * edge * edge);
                }
                """,
        },
    };
}
