using Godot;
using UnitSport.Core;

namespace UnitSport.Audio;

/// <summary>A car engine's layout, which is most of what it sounds like.</summary>
public enum EngineLayout { Inline4, Inline4Turbo, Rotary, RotaryTurbo, Boxer4Turbo, Inline6Turbo, V6, V6Turbo, V8, Crossplane4, VTwin90, ParallelTwin270, VTwin52, Diesel6 }

/// <summary>How an engine is built, as far as its sound is concerned.</summary>
public sealed record EngineProfile
{
    public bool Turbine { get; init; }
    /// <summary>A turbofan (#414): the fan's whine over the jet's roar, both from the spool; no rotor.</summary>
    public bool Jet { get; init; }
    /// <summary>
    /// A steam engine (#380): each "firing" is a puff of exhaust steam up the funnel, and nothing
    /// burns or breathes in between. <see cref="MaxRpm"/> then counts beats (four a turn for two
    /// double-acting cylinders), so the beat is rpm / 60 Hz.
    /// </summary>
    public bool Steam { get; init; }
    /// <summary>
    /// Turboprops (#420): <see cref="Engines"/> constant-speed propellers at <see cref="IdleRpm"/>..
    /// <see cref="MaxRpm"/> with <see cref="PropBlades"/> blades (<see cref="TurbopropTone"/>), their
    /// buzz loudening with the blades' load, beating against each other, over a gas turbine's whine.
    /// </summary>
    public bool IsTurboprop { get; init; }
    /// <summary>A turboprop's engine count: the props that beat against each other.</summary>
    public int Engines { get; init; } = 1;
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
    /// <summary>
    /// Crank degrees from each firing to the next, summing to 720; null fires evenly. What makes a
    /// crossplane four or a V-twin sound like one is the gaps, not the cylinder count.
    /// </summary>
    public float[]? Firing { get; init; }

    /// <summary>A light aircraft's flat-four with a two-blade prop.</summary>
    public static readonly EngineProfile PistonAero = new() { Cylinders = 4, IdleRpm = 700, MaxRpm = 2700, PipeM = 0.9f, PropBlades = 2 };

    /// <summary>A light helicopter's turboshaft and two-blade rotor.</summary>
    public static readonly EngineProfile Turboshaft = new() { Turbine = true };
    /// <summary>An airliner's high-bypass turbofans (#414): fan tone 700 Hz at idle to 2.4 kHz at take-off.</summary>
    public static readonly EngineProfile Turbofan = new() { Jet = true, WhineIdleHz = 700f, WhineMaxHz = 2400f };
    /// <summary>
    /// A military freighter's four turboprops (#420), C-130-like: four-blade props governed at
    /// 1,020 rpm (68 Hz blade pass), 740 at ground idle; the core's whine 1.1-1.65 kHz.
    /// </summary>
    public static readonly EngineProfile Turboprop = new()
    {
        IsTurboprop = true, Engines = 4, PropBlades = 4, IdleRpm = 740f, MaxRpm = 1020f, WhineIdleHz = 1100f, WhineMaxHz = 1650f,
    };

    /// <summary>A high-revving naturally aspirated four with a short pipe: 900 to 7,800 rpm.</summary>
    public static readonly EngineProfile Inline4Na = new() { Cylinders = 4, IdleRpm = 900, MaxRpm = 7800, PipeM = 0.7f };

    /// <summary>A two-rotor Wankel: fires like a four, but smooth and buzzy, 850 to 8,000 rpm.</summary>
    public static readonly EngineProfile Rotary = new() { Cylinders = 4, IdleRpm = 850, MaxRpm = 8000, PipeM = 0.6f, Unevenness = 0.25f };

    /// <summary>A turbo flat-four with unequal-length headers: the uneven burble, 850 to 7,000 rpm.</summary>
    public static readonly EngineProfile Boxer4Turbo = new() { Cylinders = 4, IdleRpm = 850, MaxRpm = 7000, PipeM = 1.0f, Unevenness = 3.5f };

    /// <summary>
    /// Yamaha's crossplane inline four (R1): the crank pins sit at 90°, so it fires 270-180-90-180
    /// like a V8 cut in half — the uneven, V-twin-ish growl of a four. 1,300 to 14,000 rpm.
    /// </summary>
    public static readonly EngineProfile Crossplane4 = new()
    {
        Cylinders = 4, IdleRpm = 1300, MaxRpm = 14000, PipeM = 0.75f, Unevenness = 0.6f,
        Firing = new[] { 270f, 180f, 90f, 180f },
    };

    /// <summary>A 90° V-twin (Ducati Testastretta): fires 270-450, the potato-potato lope. 1,350 to 10,500 rpm.</summary>
    public static readonly EngineProfile VTwin90 = new()
    {
        Cylinders = 2, IdleRpm = 1350, MaxRpm = 10500, PipeM = 0.9f, Unevenness = 1.2f,
        Firing = new[] { 270f, 450f },
    };

    /// <summary>
    /// A parallel twin with its crank pins 270° apart (Honda CRF1000L / CRF1100L Africa Twin): the
    /// same 270-450 firing as a 90° V-twin, so it lopes like one; a longer 2-into-1 pipe.
    /// </summary>
    public static readonly EngineProfile ParallelTwin270 = VTwin90 with { PipeM = 1.05f, Unevenness = 1.1f };

    /// <summary>
    /// Honda's 52° V-twin with an offset dual-pin crank (XRV650 / XRV750 Africa Twin). Assumed: the
    /// pins at the 76° usually quoted, which is the offset (180 − 2·52) that gives a 90° twin's
    /// primary balance; the cylinders then fire 128° of crank apart, 232-488 — a wider lope than a 90°.
    /// </summary>
    public static readonly EngineProfile VTwin52 = VTwin90 with { PipeM = 1.0f, Unevenness = 1.3f, Firing = new[] { 232f, 488f } };

    /// <summary>A car's engine: the layout's voice, at that car's own idle and redline.</summary>
    public static EngineProfile For(EngineLayout layout, float idleRpm, float redline) => (layout switch
    {
        EngineLayout.Rotary or EngineLayout.RotaryTurbo => Rotary,
        EngineLayout.Boxer4Turbo => Boxer4Turbo,
        // a six fires half as often again, smoother; a V6 lopes a little more than an inline
        EngineLayout.Inline6Turbo => Inline4Na with { Cylinders = 6, PipeM = 0.9f, Unevenness = 0.5f },
        EngineLayout.V6 or EngineLayout.V6Turbo => Inline4Na with { Cylinders = 6, PipeM = 0.85f, Unevenness = 1.4f },
        EngineLayout.V8 => Inline4Na with { Cylinders = 8, PipeM = 1.1f, Unevenness = 2f },
        EngineLayout.Crossplane4 => Crossplane4,
        EngineLayout.VTwin90 => VTwin90,
        EngineLayout.ParallelTwin270 => ParallelTwin270,
        EngineLayout.VTwin52 => VTwin52,
        // a truck or bus diesel: a big inline six at 550-2,200 rpm, fired evenly, through a long
        // pipe — the low, smooth drone of a Scania or an OM 470, not a car's rasp
        EngineLayout.Diesel6 => Inline4Na with { Cylinders = 6, PipeM = 2.4f, Unevenness = 0.3f },
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
    private float _quietFor;

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
    private float _prop2Phase;
    private float _propPhase, _whinePhase, _whine2Phase, _subPhase, _tipLp, _tipHp;
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
            // non-positional is always the local body's own machine: the Player bus, inside the cabin glass
            _player = new AudioStreamPlayer { Stream = gen, Bus = SfxBus.Player };
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

        // A parked engine is silent but would still render and push a buffer every frame (#221).
        // Stop the player once the fade-out has played through the buffer; start it again when
        // the level rises: the per-sample ramp still starts from silence, so neither end clicks.
        bool quiet = _tLevel < 1e-4f && _level < 1e-4f;
        _quietFor = quiet ? _quietFor + (float)delta : 0f;
        AudioStreamPlayer3D? p3 = _player3D;
        bool playing = p3?.Playing ?? _player!.Playing;
        if (playing && _quietFor > 2f * BufferSeconds)
        {
            if (p3 != null) p3.Stop(); else _player!.Stop();
            return;
        }
        if (!playing)
        {
            if (quiet) return;
            if (p3 != null) p3.Play(); else _player!.Play();
            _playback = (AudioStreamGeneratorPlayback)(p3 != null ? p3.GetStreamPlayback() : _player!.GetStreamPlayback());
        }

        int frames = _playback.GetFramesAvailable();
        if (frames <= 0) return;
        if (_push.Length < frames) _push = new Vector2[frames];
        const float vol = 1f;   // the Sfx bus carries the slider (SfxBus.ApplyVolumes)
        for (int i = 0; i < frames; i++)
        {
            float s = NextSample(vol);
            _push[i] = new Vector2(s, s);
        }
        _playback.PushBuffer(new ReadOnlySpan<Vector2>(_push, 0, frames));
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
            Level = _level * volume, Turbine = Profile.Turbine, Steam = Profile.Steam,
        };
        if (f.Level < 1e-4f && _tLevel < 1e-4f) return 0f;   // silent: skip the model

        if (Profile.IsTurboprop) TurbopropVoice(ref f);
        else if (Profile.Jet) Jet(ref f);
        else if (Profile.Turbine) Turbine(ref f);
        else if (Profile.Steam) SteamBeat(ref f);
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
            // an uneven firing order stretches or shortens the gap to the next pulse
            float gap = p.Firing is { } firing ? firing[_cyl % firing.Length] * p.Cylinders / 720f : 1f;
            _period = gap * (1f + WhiteNoise() * slop);
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

        // A propeller is its own voice, and the one that says "aeroplane". Only amplitude-
        // modulating the exhaust at blade-pass rate made a slow 23-90 Hz throb — below what laptop
        // speakers reproduce, and at low throttle the very chop of a helicopter rotor. A real prop
        // is a harmonic-rich buzz at blade-pass frequency (a sawtooth: each blade's pressure pulse)
        // plus the rasp of the tips, which climbs steeply with tip speed and blade loading.
        float prop = 1f, propVoice = 0f;
        if (p.PropBlades > 0)
        {
            float bpf = rpm / 60f * p.PropBlades;
            _propPhase = (_propPhase + bpf / Dsp.Rate) % 1f;
            prop = 1f + 0.08f * Mathf.Sin(Mathf.Tau * _propPhase);
            // band-limited sawtooth: 8 harmonics reach ~700 Hz at cruise, where speakers live
            float buzz = 0f;
            for (int k = 1; k <= 8; k++) buzz += Mathf.Sin(Mathf.Tau * _propPhase * k) / k;
            // tip noise, 700-3500 Hz, pulsing once per blade pass
            _tipLp += Dsp.Coef(3500f) * (n - _tipLp);
            _tipHp += Dsp.Coef(700f) * (_tipLp - _tipHp);
            float tipSpeed = 0.25f + 0.75f * _rpm;
            float tip = (_tipLp - _tipHp) * (0.55f + 0.45f * Mathf.Cos(Mathf.Tau * _propPhase))
                * tipSpeed * tipSpeed * (0.45f + 0.55f * _load);
            propVoice = buzz * (0.18f + 0.3f * _rpm) * (0.5f + 0.5f * _load) + tip * 2.2f;
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
        if (p.PropBlades > 0) f.Core = Mathf.Tanh(f.Core * 0.75f + propVoice) * 0.85f;
    }

    /// <summary>
    /// A steam engine's exhaust (#380): a chuff a beat, a burst of hiss with a sharp onset that dies
    /// away before the next, coloured by the funnel (the pipe comb), over the faint leak of the
    /// glands. Measured before (the piston model): at full ahead the beat was a 2 dB dip in a steady
    /// roar of intake noise, a petrol engine idling at 3 Hz. The chip voices get the funnel's note as
    /// their tone (the beat itself, 0.3-3 Hz, is below anything an oscillator can play).
    /// </summary>
    private void SteamBeat(ref EngineFrame f)
    {
        var p = Profile;
        float rpm = Mathf.Lerp(p.IdleRpm, p.MaxRpm, _rpm);
        float beatHz = rpm / 60f * p.Cylinders / 2f;
        float funnel = 343f / (2f * p.PipeM);
        f.ToneHz = funnel * 2f;
        f.SubHz = funnel;

        _phase += 1f / (_period * Dsp.Rate / beatHz);
        if (_phase >= 1f)
        {
            _phase -= 1f;
            _cyl = (_cyl + 1) % _cylGain.Length;
            // a double-acting cylinder's two ends do not quite match: alternate beats differ
            _period = 1f + WhiteNoise() * 0.02f;
            _cylAmp = _cylGain[_cyl] * (_cyl % 2 == 0 ? 1f : 0.82f) * (1f + WhiteNoise() * 0.08f);
            f.PulseStart = 1f;
        }
        // seconds since the beat: a 6 ms onset, then a decay short enough to leave a gap before the next
        float since = _phase / beatHz;
        float tau = Mathf.Clamp(0.32f / beatHz, 0.035f, 0.15f);
        _pulseEnv = Mathf.Min(1f, since / 0.006f) * Mathf.Exp(-since / tau);
        f.Pulse = _pulseEnv;
        float puff = _pulseEnv * _cylAmp;

        // the steam's hiss, 250 Hz - 4 kHz, brighter the harder it works
        float n = WhiteNoise();
        _intakeLp += Dsp.Coef(2500f + 1500f * _load) * (n - _intakeLp);
        _intakeHp += Dsp.Coef(250f) * (_intakeLp - _intakeHp);
        float hiss = _intakeLp - _intakeHp;
        // the funnel's whoomp: the puff's low part ringing in the stack
        _bodyLp += Dsp.Coef(300f) * (n - _bodyLp);
        float echo = _comb[_combPos];
        float x = _bodyLp * puff * 3f + echo * 0.6f;
        _comb[_combPos] = x;
        _combPos = (_combPos + 1) % _comb.Length;
        float leak = hiss * 0.05f;
        f.Noise = 0.85f;

        f.Core = Mathf.Tanh((hiss * puff * (1.4f + 0.8f * _load) + x * 0.8f + leak) * 1.6f) * 0.7f;
    }

    /// <summary>
    /// A turbofan: the fan's blade-pass tone and its second harmonic rising with the spool, a low
    /// rumble, and the jet's broadband roar, which grows far faster than the tone (with the thrust:
    /// <c>_load</c>). Idle is mostly whine; take-off mostly roar.
    /// </summary>
    private void Jet(ref EngineFrame f)
    {
        var p = Profile;
        float spool = _rpm;
        float toneHz = Mathf.Lerp(p.WhineIdleHz, p.WhineMaxHz, spool);
        f.ToneHz = toneHz;
        f.SubHz = 38f;
        float n = WhiteNoise();
        // the roar: noise low-passed higher as it grows, and a second, darker band under it
        _bodyLp += Dsp.Coef(220f + 700f * _load) * (n - _bodyLp);
        _tipLp += Dsp.Coef(90f) * (n - _tipLp);
        _whinePhase = (_whinePhase + toneHz / Dsp.Rate) % 1f;
        _whine2Phase = (_whine2Phase + toneHz * 2.01f / Dsp.Rate) % 1f;
        _subPhase = (_subPhase + 38f / Dsp.Rate) % 1f;
        float whine = Mathf.Sin(Mathf.Tau * _whinePhase) + 0.4f * Mathf.Sin(Mathf.Tau * _whine2Phase);
        float roar = _load * _load * 0.85f + spool * 0.12f;
        f.Noise = 0.4f + 0.5f * _load;
        f.Pulse = 0f;
        f.Core = _bodyLp * 4.5f * roar
               + _tipLp * 6f * roar * 0.6f
               + Mathf.Sin(Mathf.Tau * _subPhase) * 0.12f * spool
               + whine * 0.09f * (0.25f + 0.75f * spool);
        f.Core = Mathf.Tanh(f.Core * 1.2f) * 0.8f;
    }

    /// <summary>
    /// Turboprops (#420): two props a hair apart in rpm (the fleet's beating, <see cref="TurbopropTone.Detune"/>),
    /// each a band-limited sawtooth at blade pass with the tips' rasp pulsing on it, both louder and
    /// brighter as the blades take load (<c>_load</c>, the thrust); the governor holds the rpm, so the
    /// note barely moves with the levers, only its weight does. Under it the core's whine and a
    /// little exhaust roar.
    /// </summary>
    private void TurbopropVoice(ref EngineFrame f)
    {
        var p = Profile;
        float spool = _rpm;
        float rpm = TurbopropTone.PropRpm(spool, p.IdleRpm, p.MaxRpm);
        float bpf = TurbopropTone.BladePassHz(rpm, p.PropBlades);
        float whineHz = Mathf.Lerp(p.WhineIdleHz, p.WhineMaxHz, spool);
        f.ToneHz = bpf * 2f;
        f.SubHz = bpf;
        int engines = Mathf.Max(1, p.Engines);
        _propPhase = (_propPhase + bpf * TurbopropTone.Detune(0, engines) / Dsp.Rate) % 1f;
        _prop2Phase = (_prop2Phase + bpf * TurbopropTone.Detune(engines - 1, engines) / Dsp.Rate) % 1f;
        float buzz = 0f;
        for (int k = 1; k <= 10; k++)
            buzz += (Mathf.Sin(Mathf.Tau * _propPhase * k) + Mathf.Sin(Mathf.Tau * _prop2Phase * k)) / k;
        float loud = TurbopropTone.BladeLoudness(_load);
        float n = WhiteNoise();
        _tipLp += Dsp.Coef(2800f) * (n - _tipLp);
        _tipHp += Dsp.Coef(500f) * (_tipLp - _tipHp);
        float tip = (_tipLp - _tipHp) * (0.5f + 0.5f * Mathf.Cos(Mathf.Tau * _propPhase)) * loud * loud;
        _bodyLp += Dsp.Coef(180f + 400f * _load) * (n - _bodyLp);
        _whinePhase = (_whinePhase + whineHz / Dsp.Rate) % 1f;
        _whine2Phase = (_whine2Phase + whineHz * 2.02f / Dsp.Rate) % 1f;
        float whine = Mathf.Sin(Mathf.Tau * _whinePhase) + 0.3f * Mathf.Sin(Mathf.Tau * _whine2Phase);
        float running = Mathf.Clamp(spool * 3f, 0f, 1f);
        f.Noise = 0.35f + 0.45f * _load;
        f.Pulse = 0.5f + 0.5f * Mathf.Sin(Mathf.Tau * _propPhase);
        f.Core = buzz * 0.11f * loud * running
               + tip * 2.4f * running
               + _bodyLp * 2.2f * (0.15f + 0.5f * _load * _load)
               + whine * 0.06f * (0.3f + 0.7f * spool);
        f.Core = Mathf.Tanh(f.Core * 1.3f) * 0.8f;
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
/// The mix (#375), created in code at boot so the project needs no bus layout resource:
/// <code>
/// Master  [hard limiter, -1 dB ceiling]
///  |- Sfx     the world: other bodies, vehicles, ambience, impacts   [cabin low-pass, reverb]
///  |- Player  this body: own steps, landings, own engine and foley  [reverb, drier]
///  '- Music   radios, car stereos, live stations                     [reverb, drier]
/// </code>
/// <see cref="ReverbZones"/> eases the three reverbs toward the room the ears are in;
/// <see cref="Ears.Shut"/> closes the cabin filter over the world when sitting in a car, so the
/// street goes dull behind the glass while one's own engine and stereo stay clear. The Player
/// bus is the Sfx slider too.
/// </summary>
public static class SfxBus
{
    public const string Name = "Sfx";

    /// <summary>The local body's own sounds (#375): never behind the cabin glass, less room on them.</summary>
    public const string Player = "Player";

    /// <summary>
    /// The music bus (#261): radios, car stereos, live stations. Its own slider in Settings, and its
    /// own reverb kept in step with the Sfx one by <see cref="ReverbZones"/>, so a boombox in a
    /// tunnel rings like the footsteps beside it.
    /// </summary>
    public const string Music = "Music";

    /// <summary>The bus's reverb, or null before <see cref="Ensure"/>.</summary>
    public static AudioEffectReverb? Reverb { get; private set; }

    /// <summary>The music bus's reverb, or null before <see cref="Ensure"/>.</summary>
    public static AudioEffectReverb? MusicReverb { get; private set; }

    /// <summary>The player bus's reverb, or null before <see cref="Ensure"/>.</summary>
    public static AudioEffectReverb? PlayerReverb { get; private set; }

    /// <summary>The world bus's cabin low-pass (20 kHz open), or null before <see cref="Ensure"/>.</summary>
    public static AudioEffectLowPassFilter? Cabin { get; private set; }

    /// <summary>
    /// A volume slider position as decibels. Hearing is logarithmic: a LINEAR 5 % is only -26 dB,
    /// which still fills a room — that is why "even at 5 % it is too loud". Square law (40·log10)
    /// puts 50 % at -12 dB and 5 % at -52 dB, and 0 is silence.
    /// </summary>
    public static float SliderDb(float v) => v <= 0.001f ? -80f : 40f * Mathf.Log(v) / Mathf.Log(10f);

    /// <summary>The same curve as a linear gain, for the few sounds that scale themselves.</summary>
    public static float SliderGain(float v) => v * v;

    /// <summary>
    /// The one place volume is applied: Master for everything, Sfx (and Player) for the effects and
    /// ambience routed through them. Per-sound code no longer multiplies by the settings, so
    /// nothing can escape the slider or be scaled twice.
    /// </summary>
    public static void ApplyVolumes()
    {
        var s = Core.GameSettings.Current;
        AudioServer.SetBusVolumeDb(0, SliderDb(s.MasterVolume));
        AudioServer.SetBusMute(0, s.MasterVolume <= 0.001f);
        foreach (var (bus, v) in new[] { (Name, s.SfxVolume), (Player, s.SfxVolume), (Music, s.MusicVolume) })
        {
            int idx = AudioServer.GetBusIndex(bus);
            if (idx < 0) continue;
            AudioServer.SetBusVolumeDb(idx, SliderDb(v) + (bus == Name ? _cabinDb : 0f));
            AudioServer.SetBusMute(idx, v <= 0.001f);
        }
    }

    private static bool _subscribed;
    private static float _cabinHz = 20000f, _cabinDb;

    /// <summary>Creates the buses once (idempotent). Call before creating any player.</summary>
    public static void Ensure()
    {
        // the master only catches peaks: a stack of engines, a boom and a radio must not clip
        if (Find<AudioEffectHardLimiter>(0) == null)
            AudioServer.AddBusEffect(0, new AudioEffectHardLimiter { CeilingDb = -1f, PreGainDb = 0f, Release = 0.1f });

        int sfx = Bus(Name);
        if (Find<AudioEffectLowPassFilter>(sfx) == null)
            AudioServer.AddBusEffect(sfx, new AudioEffectLowPassFilter { CutoffHz = 20000f, Resonance = 0.5f }, 0);
        Cabin = Find<AudioEffectLowPassFilter>(sfx);
        Reverb = Find<AudioEffectReverb>(sfx) ?? AddReverb(sfx, 0.1f);
        PlayerReverb = Find<AudioEffectReverb>(Bus(Player)) ?? AddReverb(Bus(Player), 0.12f);
        MusicReverb = Find<AudioEffectReverb>(Bus(Music)) ?? AddReverb(Bus(Music), 0.15f);
        // the filter costs nothing while it is off
        AudioServer.SetBusEffectEnabled(sfx, 0, _cabinHz < 19000f);
        ApplyVolumes();
        if (!_subscribed) { _subscribed = true; Core.GameSettings.Changed += ApplyVolumes; }
    }

    /// <summary>
    /// How far shut in a cabin the ears are (0 open .. 1 closed car): the world loses its highs and
    /// ~8 dB, the Player and Music buses do not. Cheap to call every frame: it writes only on change.
    /// </summary>
    public static void SetCabin(float shut)
    {
        if (Cabin == null) return;
        // log-space sweep from open air to the ~1.4 kHz of a closed car
        float hz = shut <= 0.001f ? 20000f : Mathf.Exp(Mathf.Lerp(Mathf.Log(20000f), Mathf.Log(1400f), shut));
        float db = -8f * shut;
        if (Mathf.Abs(hz - _cabinHz) < 20f && Mathf.Abs(db - _cabinDb) < 0.05f) return;
        _cabinHz = hz; _cabinDb = db;
        Cabin.CutoffHz = hz;
        int sfx = AudioServer.GetBusIndex(Name);
        if (sfx < 0) return;
        AudioServer.SetBusEffectEnabled(sfx, 0, hz < 19000f);
        AudioServer.SetBusVolumeDb(sfx, SliderDb(Core.GameSettings.Current.SfxVolume) + db);
    }

    private static int Bus(string name)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx >= 0) return idx;
        AudioServer.AddBus();
        idx = AudioServer.BusCount - 1;
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, "Master");
        return idx;
    }

    private static AudioEffectReverb AddReverb(int bus, float hipass)
    {
        var r = new AudioEffectReverb { RoomSize = 0.2f, Damping = 0.7f, Wet = 0f, Dry = 1f, Spread = 1f, Hipass = hipass };
        AudioServer.AddBusEffect(bus, r);
        return r;
    }

    private static T? Find<T>(int bus) where T : AudioEffect
    {
        for (int e = 0; e < AudioServer.GetBusEffectCount(bus); e++)
            if (AudioServer.GetBusEffect(bus, e) is T t) return t;
        return null;
    }
}
