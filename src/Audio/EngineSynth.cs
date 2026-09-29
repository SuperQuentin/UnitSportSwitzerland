using Godot;
using UnitSport.Core;

namespace UnitSport.Audio;

/// <summary>A car engine's layout, which is most of what it sounds like.</summary>
public enum EngineLayout { Inline4, Inline4Turbo, Rotary, RotaryTurbo, Boxer4Turbo, Inline6Turbo, V6, V6Turbo, V8 }

/// <summary>How an engine is built, as far as its sound is concerned.</summary>
public sealed record EngineProfile
{
    public bool Turbine { get; init; }
    public int Cylinders { get; init; } = 4;
    public float IdleRpm { get; init; } = 800f;
    public float MaxRpm { get; init; } = 2700f;
    /// <summary>Exhaust pipe length, m: sets the comb resonance that gives an engine its "voice".</summary>
    public float PipeM { get; init; } = 1.1f;
    /// <summary>Propeller blades (0 = none): a slow beat at prop rpm × blades rides on the note.</summary>
    public int PropBlades { get; init; }
    /// <summary>Turboshaft: whine at idle and full spool, Hz.</summary>
    public float WhineIdleHz { get; init; } = 500f;
    public float WhineMaxHz { get; init; } = 1250f;
    /// <summary>Turboshaft: blade passes per second at full rotor speed.</summary>
    public float BladePassHz { get; init; } = 4.8f;
    /// <summary>How unlike each other the cylinders fire, 0 = identical, 1 = the default lope.</summary>
    public float Unevenness { get; init; } = 1f;

    /// <summary>A light aircraft's flat-four with a two-blade prop.</summary>
    public static readonly EngineProfile PistonAero = new() { Cylinders = 4, IdleRpm = 700, MaxRpm = 2700, PipeM = 0.9f, PropBlades = 2 };

    /// <summary>A light helicopter's turboshaft and two-blade rotor.</summary>
    public static readonly EngineProfile Turboshaft = new() { Turbine = true };

    /// <summary>A high-revving naturally aspirated four with a short pipe: 900 to 7,800 rpm.</summary>
    public static readonly EngineProfile Inline4Na = new() { Cylinders = 4, IdleRpm = 900, MaxRpm = 7800, PipeM = 0.7f };

    /// <summary>A two-rotor Wankel: fires like a four, but smooth and buzzy, 850 to 8,000 rpm.</summary>
    public static readonly EngineProfile Rotary = new() { Cylinders = 4, IdleRpm = 850, MaxRpm = 8000, PipeM = 0.6f, Unevenness = 0.25f };

    /// <summary>A turbo flat-four with unequal-length headers: the uneven burble, 850 to 7,000 rpm.</summary>
    public static readonly EngineProfile Boxer4Turbo = new() { Cylinders = 4, IdleRpm = 850, MaxRpm = 7000, PipeM = 1.0f, Unevenness = 3.5f };

    /// <summary>A car's engine: the layout's voice, at that car's own idle and redline.</summary>
    public static EngineProfile For(EngineLayout layout, float idleRpm, float redline) => (layout switch
    {
        EngineLayout.Rotary or EngineLayout.RotaryTurbo => Rotary,
        EngineLayout.Boxer4Turbo => Boxer4Turbo,
        // a six fires half as often again, smoother; a V6 lopes a little more than an inline
        EngineLayout.Inline6Turbo => Inline4Na with { Cylinders = 6, PipeM = 0.9f, Unevenness = 0.5f },
        EngineLayout.V6 or EngineLayout.V6Turbo => Inline4Na with { Cylinders = 6, PipeM = 0.85f, Unevenness = 1.4f },
        EngineLayout.V8 => Inline4Na with { Cylinders = 8, PipeM = 1.1f, Unevenness = 2f },
        _ => Inline4Na,
    }) with { IdleRpm = idleRpm, MaxRpm = redline };
}

/// <summary>
/// A live engine sound, synthesised sample by sample from what the engine is doing.
///
/// <para>
/// It replaces a baked loop pitch-shifted by the throttle — which is a tape speeding up, not an
/// engine revving: everything in the loop, the exhaust roar included, slid up together. Here the
/// firing frequency, the pulse sharpness (load), the exhaust resonance (fixed by the pipe, so it
/// does NOT move with rpm — that is exactly what makes an engine sound like one) and the decel
/// crackle are all computed per sample, then handed to an <see cref="IChipVoice"/> that decides
/// what the result sounds like: the model itself, or the model as a PS1, NES, SID or YM2612
/// would have played it.
/// </para>
///
/// <para>
/// Fed through an <see cref="AudioStreamGenerator"/> from <c>_Process</c> on the main thread:
/// about 370 samples a frame at 60 fps. Parameters are set once per frame and smoothed per
/// sample, so a throttle step never clicks. <see cref="Render"/> runs the same code offline for
/// <c>--soundcheck</c>.
/// </para>
/// </summary>
public partial class EngineSynth : Node3D
{
    private const float BufferSeconds = 0.1f;

    public EngineProfile Profile { get; }
    private readonly bool _spatial;
    private readonly Random _rng;
    private readonly int _seed;

    private AudioStreamPlayer? _player;
    private AudioStreamPlayer3D? _player3D;
    private AudioStreamGeneratorPlayback? _playback;
    private Vector2[] _push = [];

    private IChipVoice _voice;
    private EngineVoice _voiceKind;
    /// <summary>Forces a voice instead of following the setting (for <c>--soundcheck</c>).</summary>
    public EngineVoice? VoiceOverride { get; set; }

    // targets, set per frame; the per-sample state eases toward them
    private float _tRpm, _tThrottle, _tLoad, _tLevel;
    private float _rpm, _throttle, _load, _level;

    // model state
    private float _phase = 1f, _period = 1f, _cylAmp = 1f, _pulseEnv;
    private int _cyl;
    private readonly float[] _cylGain;
    private readonly float[] _comb;
    private int _combPos;
    private float _intakeLp, _intakeHp, _bodyLp, _dc;
    private float _propPhase, _whinePhase, _whine2Phase, _subPhase;
    private float _crackleEnv, _crackleRate;

    public EngineSynth(EngineProfile profile, bool spatial, int seed = 0)
    {
        Profile = profile;
        _spatial = spatial;
        _seed = seed;
        _rng = new Random(seed * 7919 + 17);
        // no two cylinders fire quite alike: that unevenness is the engine's "lope"
        _cylGain = new float[Math.Max(1, profile.Cylinders)];
        for (int i = 0; i < _cylGain.Length; i++)
        {
            float g = 0.9f + 0.2f * (float)_rng.NextDouble();
            _cylGain[i] = profile.Unevenness == 1f ? g : 1f + (g - 1f) * profile.Unevenness;
        }
        // exhaust comb: a pressure pulse bounces back from the open pipe end after 2L/c
        _comb = new float[Math.Max(2, (int)(Dsp.Rate * 2f * profile.PipeM / 343f))];
        _voiceKind = GameSettings.Current.EngineVoice;
        _voice = ChipVoices.Create(_voiceKind, seed);
        Name = "EngineSynth";
    }

    /// <summary>The 3D player, when spatial, for attenuation and doppler settings.</summary>
    public AudioStreamPlayer3D? Player3D => _player3D;

    /// <summary>
    /// Sets what the engine is doing. <paramref name="rpm01"/>: idle 0 .. redline 1 (a turboshaft's
    /// spool). <paramref name="level"/>: 0..1 loudness before the volume setting; 0 is silent.
    /// </summary>
    public void Set(float rpm01, float throttle, float load, float level)
    {
        _tRpm = Mathf.Clamp(rpm01, 0f, 1.1f);
        _tThrottle = Mathf.Clamp(throttle, 0f, 1f);
        _tLoad = Mathf.Clamp(load, 0f, 1f);
        _tLevel = Mathf.Max(0f, level);
    }

    public override void _Ready()
    {
        var gen = new AudioStreamGenerator { MixRate = Dsp.Rate, BufferLength = BufferSeconds };
        if (_spatial)
        {
            _player3D = new AudioStreamPlayer3D
            {
                Stream = gen, UnitSize = 12f, MaxDistance = 1500f,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.PhysicsStep,
                Bus = SfxBus.Name,
            };
            AddChild(_player3D);
            _player3D.Play();
            _playback = (AudioStreamGeneratorPlayback)_player3D.GetStreamPlayback();
        }
        else
        {
            _player = new AudioStreamPlayer { Stream = gen, Bus = SfxBus.Name };
            AddChild(_player);
            _player.Play();
            _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
        }
    }

    public override void _Process(double delta)
    {
        if (_playback == null) return;
        var want = VoiceOverride ?? GameSettings.Current.EngineVoice;
        if (want != _voiceKind) { _voiceKind = want; _voice = ChipVoices.Create(want, _seed); }

        int frames = _playback.GetFramesAvailable();
        if (frames <= 0) return;
        if (_push.Length != frames) _push = new Vector2[frames];
        float vol = GameSettings.Current.SfxVolume;
        for (int i = 0; i < frames; i++)
        {
            float s = NextSample(vol);
            _push[i] = new Vector2(s, s);
        }
        _playback.PushBuffer(_push);
    }

    /// <summary>
    /// Offline render into <paramref name="buffer"/>, with the parameters ramped linearly from
    /// their current values to <paramref name="rpm01"/> etc. over the buffer. For <c>--soundcheck</c>.
    /// </summary>
    public void Render(float[] buffer, float rpm01, float throttle, float load, float level)
    {
        if (VoiceOverride is { } v && v != _voiceKind) { _voiceKind = v; _voice = ChipVoices.Create(v, _seed); }
        Set(rpm01, throttle, load, level);
        for (int i = 0; i < buffer.Length; i++) buffer[i] = NextSample(1f);
    }

    // ------------------------------------------------------------------------------------
    // the model
    // ------------------------------------------------------------------------------------

    private float NextSample(float volume)
    {
        // ~25 ms parameter smoothing: fast enough to feel the throttle, slow enough not to click
        const float k = 1f / (0.025f * Dsp.Rate);
        float prevThrottle = _throttle;
        _rpm += (_tRpm - _rpm) * k * 0.6f;   // a flywheel: rpm lags the throttle
        _throttle += (_tThrottle - _throttle) * k;
        _load += (_tLoad - _load) * k;
        _level += (_tLevel - _level) * k;

        var f = new EngineFrame
        {
            Rpm01 = _rpm, Throttle = _throttle, Load = _load,
            Level = _level * volume, Turbine = Profile.Turbine,
        };
        if (f.Level < 1e-4f && _tLevel < 1e-4f) return 0f;   // silent: skip the model

        if (Profile.Turbine) Turbine(ref f);
        else Piston(ref f, prevThrottle);

        // DC blocker: pulse trains are one-sided and would otherwise sit off centre
        _dc += (f.Core - _dc) * 0.002f;
        f.Core -= _dc;

        float s = _voice.Next(f);
        return float.IsFinite(s) ? Mathf.Clamp(s, -1f, 1f) : 0f;
    }

    private float WhiteNoise() => (float)(_rng.NextDouble() * 2 - 1);

    private void Piston(ref EngineFrame f, float prevThrottle)
    {
        var p = Profile;
        float rpm = Mathf.Lerp(p.IdleRpm, p.MaxRpm, _rpm);
        // a four-stroke fires each cylinder every second revolution
        float firingHz = rpm / 60f * p.Cylinders / 2f;
        f.ToneHz = firingHz;
        f.SubHz = firingHz * 0.5f;

        _phase += 1f / (_period * Dsp.Rate / firingHz);
        if (_phase >= 1f)
        {
            _phase -= 1f;
            _cyl = (_cyl + 1) % _cylGain.Length;
            // combustion is not a clock: at idle and light load the spacing wanders
            float slop = 0.03f * (1f - _load) * (1.2f - _rpm);
            _period = 1f + WhiteNoise() * slop;
            _cylAmp = _cylGain[_cyl] * (1f + WhiteNoise() * 0.06f);
            f.PulseStart = 1f;
        }
        // load sharpens the pressure pulse: more harmonics, the note hardens
        float sharp = 5f + 16f * _load;
        _pulseEnv = Mathf.Exp(-_phase * sharp);
        f.Pulse = _pulseEnv;
        float pulse = _pulseEnv * _cylAmp;

        // exhaust resonance (fixed by the pipe, not by rpm)
        float echo = _comb[_combPos];
        float x = pulse + echo * 0.55f;
        _comb[_combPos] = x;
        _combPos = (_combPos + 1) % _comb.Length;

        // exhaust roughness rides on the pulse; intake noise opens with the throttle
        float n = WhiteNoise();
        _bodyLp += Dsp.Coef(900f) * (n - _bodyLp);
        _intakeLp += Dsp.Coef(3000f) * (n - _intakeLp);
        _intakeHp += Dsp.Coef(600f) * (_intakeLp - _intakeHp);
        float intake = (_intakeLp - _intakeHp) * (0.1f + 0.5f * _throttle);
        float exhaustNoise = _bodyLp * pulse * (0.6f + 0.8f * _load);
        f.Noise = Mathf.Clamp(0.25f + 0.35f * _throttle + 0.2f * _load, 0f, 1f);

        // a propeller beats against the exhaust note at blade-pass rate (prop on the crank)
        float prop = 1f;
        if (p.PropBlades > 0)
        {
            _propPhase = (_propPhase + rpm / 60f * p.PropBlades / Dsp.Rate) % 1f;
            prop = 1f + 0.25f * Mathf.Sin(Mathf.Tau * _propPhase);
        }

        // decel crackle: throttle snapped shut at high rpm pops in the exhaust
        float closing = (prevThrottle - _throttle) * Dsp.Rate;   // per second
        if (closing > 1.5f && _rpm > 0.45f) _crackleRate = Mathf.Min(1f, _crackleRate + 0.5f);
        _crackleRate = Mathf.Max(0f, _crackleRate - 0.8f / Dsp.Rate);
        if (_crackleRate > 0 && _rng.NextDouble() < _crackleRate * 14.0 / Dsp.Rate)
        {
            f.Crackle = 1f;
            _crackleEnv = 0.6f + 0.4f * (float)_rng.NextDouble();
        }
        _crackleEnv *= 0.9985f;
        float crackle = _crackleEnv * WhiteNoise() * _crackleEnv;

        float drive = 1.4f + 2.2f * _load;
        f.Core = Mathf.Tanh((x * 0.9f + exhaustNoise * 1.3f + intake) * drive) * 0.62f * prop + crackle * 0.35f;
    }

    private void Turbine(ref EngineFrame f)
    {
        var p = Profile;
        float spool = _rpm;
        float whineHz = Mathf.Lerp(p.WhineIdleHz, p.WhineMaxHz, spool);
        f.ToneHz = whineHz;
        f.SubHz = 42f;

        // rotor: a pressure pulse per blade pass, sharper when the blades are loaded (blade slap)
        float bladeHz = p.BladePassHz * spool;
        _phase += bladeHz / Dsp.Rate;
        if (_phase >= 1f) { _phase -= 1f; f.PulseStart = 1f; }
        float sharp = 8f + 12f * _load;
        float whop = Mathf.Exp(-_phase * sharp) * Mathf.Clamp(spool * 1.5f, 0f, 1f);
        f.Pulse = whop;

        float n = WhiteNoise();
        _bodyLp += Dsp.Coef(350f) * (n - _bodyLp);
        _subPhase = (_subPhase + 42f / Dsp.Rate) % 1f;
        _whinePhase = (_whinePhase + whineHz / Dsp.Rate) % 1f;
        _whine2Phase = (_whine2Phase + whineHz * 2.03f / Dsp.Rate) % 1f;
        float whine = Mathf.Sin(Mathf.Tau * _whinePhase) + 0.35f * Mathf.Sin(Mathf.Tau * _whine2Phase);
        f.Noise = 0.6f;

        f.Core = _bodyLp * 5f * (0.2f + whop)                              // rotor wash, chopped
               + Mathf.Sin(Mathf.Tau * _subPhase) * whop * 0.6f              // the thump's body
               + whine * 0.07f * (0.3f + 0.7f * spool);                      // turbine
        f.Core = Mathf.Tanh(f.Core * 1.3f) * 0.8f;
    }
}

/// <summary>
/// The "Sfx" audio bus every game sound is routed through, with a reverb on it whose settings
/// <see cref="ReverbZones"/> eases toward the surroundings. Created in code at boot so the
/// project needs no bus layout resource.
/// </summary>
public static class SfxBus
{
    public const string Name = "Sfx";

    /// <summary>The bus's reverb, or null before <see cref="Ensure"/>.</summary>
    public static AudioEffectReverb? Reverb { get; private set; }

    /// <summary>Creates the bus once (idempotent). Call before creating any player.</summary>
    public static void Ensure()
    {
        int idx = AudioServer.GetBusIndex(Name);
        if (idx < 0)
        {
            AudioServer.AddBus();
            idx = AudioServer.BusCount - 1;
            AudioServer.SetBusName(idx, Name);
            AudioServer.SetBusSend(idx, "Master");
            AudioServer.AddBusEffect(idx, new AudioEffectReverb
            {
                RoomSize = 0.3f, Damping = 0.5f, Wet = 0f, Dry = 1f, Spread = 1f, Hipass = 0.1f,
            });
        }
        for (int e = 0; e < AudioServer.GetBusEffectCount(idx); e++)
            if (AudioServer.GetBusEffect(idx, e) is AudioEffectReverb r) Reverb = r;
    }
}
