using System.Diagnostics;
using Godot;
using UnitSport.Audio;
using UnitSport.Terrain.Stadiums;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Concert;

/// <summary>
/// The room a concert plays in and the crowd in it (#984), for this machine's ears. The PA's hangs
/// (<see cref="ConcertSpeaker"/>) play into the <see cref="BusName"/> bus (under Music): a slap off the
/// stands' fronts (<see cref="AudioEffectDelay"/>, two taps from <see cref="ConcertAcoustics.Echoes"/>)
/// and the venue's reverb (a stadium short and sparse, an arena longer and denser, an open air nearly
/// dry), its wet rising with the distance from the PA. The crowd is two stereo loops on the
/// <see cref="CrowdBus"/> bus (under Sfx, so it goes dull in a closed car): a roar and an applause that
/// follow the show as the ears hear it (<see cref="ConcertAcoustics.Crowd"/>), muffled outside the bowl.
/// Effects are made once and only written when a value moves; the nearest concert owns the bus.
/// </summary>
public partial class ConcertSound : Node3D
{
    public const string BusName = "Concert", CrowdBus = "Crowd";
    private static readonly StringName BusId = BusName, CrowdId = CrowdBus;

    /// <summary>The crowd's full level and the room's poll, s.</summary>
    private const float CrowdBaseDb = -9f, RoomPoll = 0.2f;

    private static AudioEffectDelay? _slap;
    private static AudioEffectReverb? _room;
    private static AudioEffectLowPassFilter? _muffle;
    private static ConcertSound? _owner;

    private readonly ConcertEvent _e;
    private readonly ConcertMusic _music;
    private readonly RoomTone _tone;
    private readonly Vector2 _front, _field, _outer;
    private Vector3 _paCentre, _paFacing;
    private AudioStreamPlayer _roar = null!, _clap = null!;
    private float _roarDb = -80f, _clapDb = -80f, _distance = float.MaxValue, _poll;
    private bool _effectsOn;
    private RoomTone? _toneSet;

    /// <summary>For the probes: what the room and the crowd are set to now.</summary>
    public float Wet { get; private set; }
    public Echo Slap1 { get; private set; }
    public Echo Slap2 { get; private set; }
    public int Slaps { get; private set; }
    public CrowdMix CrowdTarget { get; private set; }
    public CrowdMix CrowdNow => new(_roarDb, _clapDb);
    public float CrowdPlaceDb { get; private set; }
    public float CrowdCutoff { get; private set; } = 20000f;
    public bool OwnsBus => _owner == this;
    public ConcertMusic Music => _music;
    public RoomTone Tone => _tone;

    /// <summary>The main thread's time in this and the PA's speakers, for the probe (Stopwatch ticks, frames).</summary>
    public static long CostTicks, CostFrames;

    public ConcertSound(ConcertEvent e, ConcertMusic music)
    {
        _e = e;
        _music = music;
        Name = "Sound";
        var s = e.Stadium;
        _tone = s.Kind switch { VenueKind.Arena => ConcertAcoustics.Arena, VenueKind.OpenAir => ConcertAcoustics.OpenAir, _ => ConcertAcoustics.Stadium };
        _field = new Vector2(s.FrontHalfU, s.FrontHalfV);
        // no stands round an open air: nothing to slap back
        _front = s.Kind == VenueKind.OpenAir ? Vector2.Zero : _field;
        _outer = s.Kind == VenueKind.OpenAir ? Vector2.Zero : ConcertMusic.OuterHalf(s);
    }

    public ConcertSound() : this(ConcertPlanner.Plan(StadiumCatalog.Geneva, 1), new ConcertMusic()) { }

    /// <summary>The Concert bus (under Music) with its slap and reverb, and the Crowd bus (under Sfx) with its muffle; idempotent.</summary>
    public static void EnsureBuses()
    {
        SfxBus.Ensure();
        int pa = Bus(BusName, SfxBus.Music);
        if (_slap == null || AudioServer.GetBusEffectCount(pa) == 0)
        {
            _slap = new AudioEffectDelay { Dry = 1f, Tap1Active = false, Tap2Active = false, FeedbackActive = false };
            _room = new AudioEffectReverb { Dry = 1f, Wet = 0f, Spread = 0.9f };
            AudioServer.AddBusEffect(pa, _slap, 0);
            AudioServer.AddBusEffect(pa, _room, 1);
            AudioServer.SetBusEffectEnabled(pa, 0, false);
            AudioServer.SetBusEffectEnabled(pa, 1, false);
        }
        int crowd = Bus(CrowdBus, SfxBus.Name);
        if (_muffle == null || AudioServer.GetBusEffectCount(crowd) == 0)
        {
            _muffle = new AudioEffectLowPassFilter { CutoffHz = 20000f, Resonance = 0.5f };
            AudioServer.AddBusEffect(crowd, _muffle, 0);
            AudioServer.SetBusEffectEnabled(crowd, 0, false);
        }
    }

    private static int Bus(string name, string sendTo)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx >= 0) return idx;
        AudioServer.AddBus();
        idx = AudioServer.BusCount - 1;
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, sendTo);
        return idx;
    }

    public override void _Ready()
    {
        EnsureBuses();
        // the PA as one source for the room: the middle of its hangs, facing their way (the round's cancel out: all round)
        var hangs = _music.Hangs;
        foreach (var h in hangs) { _paCentre += h.Hang; _paFacing += h.Facing; }
        if (hangs.Count > 0) _paCentre /= hangs.Count;
        _paFacing = _paFacing.LengthSquared() > 0.1f ? _paFacing.Normalized() : Vector3.Zero;
        _roar = new AudioStreamPlayer { Name = "Roar", Stream = CrowdRoar, Bus = CrowdBus, VolumeDb = -80f };
        _clap = new AudioStreamPlayer { Name = "Applause", Stream = CrowdApplause, Bus = CrowdBus, VolumeDb = -80f };
        AddChild(_roar);
        AddChild(_clap);
        if (ConcertSoundProbe.Requested) ConcertSoundProbe.Start(this);
    }

    public override void _ExitTree()
    {
        if (_owner != this) return;
        _owner = null;
        Effects(false);
    }

    public override void _Process(double delta)
    {
        long t0 = Stopwatch.GetTimestamp();
        float dt = (float)delta;
        if (Ears.FrameOf(this) is not { } ear) return;
        var at = ToLocal(ear.Origin);
        _distance = new Vector2(at.X, at.Z).Length();
        // the nearest concert owns the room's bus (a few metres' margin: no flip-flop between two)
        if (_owner != this && (_owner == null || !IsInstanceValid(_owner) || !_owner.IsInsideTree() || _distance < _owner._distance - 10f))
        {
            // another concert wrote the bus: write it all again
            _owner = this;
            _toneSet = null;
            Wet = -1f;
            Slaps = -1;
            Slap1 = Slap2 = new Echo(-1e4f, 0f, 0f);
            _effectsOn = _music.Song == null;
            CrowdCutoff = -1f;
        }

        // how late the ears hear the show: the nearest hang's delay
        float delay = float.MaxValue, directDb = -80f;
        foreach (var s in _music.Speakers)
        {
            delay = MathF.Min(delay, s.Heard.Delay);
            directDb = MathF.Max(directDb, s.Heard.Db);
        }
        if (delay == float.MaxValue) delay = 0f;
        Crowd(at, delay, dt);

        if (_owner == this)
        {
            bool playing = _music.Song != null;
            Effects(playing);
            _poll -= dt;
            if (playing && _poll <= 0f)
            {
                _poll = RoomPoll;
                Room(at, GlobalBasis.Inverse() * ear.Basis.X, directDb);
            }
        }
        CostTicks += Stopwatch.GetTimestamp() - t0;
        CostFrames++;
    }

    private void Effects(bool on)
    {
        if (on == _effectsOn) return;
        _effectsOn = on;
        int pa = AudioServer.GetBusIndex(BusId);
        if (pa < 0) return;
        AudioServer.SetBusEffectEnabled(pa, 0, on && Slaps > 0);
        AudioServer.SetBusEffectEnabled(pa, 1, on);
        if (!on) _toneSet = null;
    }

    /// <summary>The slap and the reverb's wet for the ears at <paramref name="at"/> (stadium frame), written when they move.</summary>
    private void Room(Vector3 at, Vector3 earRight, float directDb)
    {
        if (_slap == null || _room == null) return;
        if (_toneSet != _tone)
        {
            _toneSet = _tone;
            _room.RoomSize = _tone.Size;
            _room.Damping = _tone.Damping;
            _room.PredelayMsec = _tone.PredelayMs;
            _room.PredelayFeedback = _tone.PredelayFeedback;
            _room.Hipass = _tone.Hipass;
        }
        float wet = ConcertAcoustics.Wet(_tone, directDb);
        if (MathF.Abs(wet - Wet) > 0.004f) _room.Wet = Wet = wet;

        Span<Echo> echoes = stackalloc Echo[4];
        int found = ConcertAcoustics.Echoes(_paCentre, _paFacing, at, _front, _tone.ReflectDb, earRight, echoes);
        int n = ConcertAcoustics.Taps(echoes[..found], out var first, out var second);
        // too quiet to hear under the reverb: off
        if (n > 1 && second.Db < -30f) n = 1;
        if (n > 0 && first.Db < -30f) { first = second; n--; }
        if (n != Slaps)
        {
            int pa = AudioServer.GetBusIndex(BusId);
            if (pa >= 0) AudioServer.SetBusEffectEnabled(pa, 0, n > 0);
            Slaps = n;
            _slap.Tap1Active = n > 0;
            _slap.Tap2Active = n > 1;
        }
        if (n > 0 && Moved(first, Slap1))
        {
            Slap1 = first;
            _slap.Tap1DelayMs = Math.Clamp(first.DelayMs, 1f, 1500f);
            _slap.Tap1LevelDb = first.Db;
            _slap.Tap1Pan = Math.Clamp(first.Pan, -1f, 1f);
        }
        if (n > 1 && Moved(second, Slap2))
        {
            Slap2 = second;
            _slap.Tap2DelayMs = Math.Clamp(second.DelayMs, 1f, 1500f);
            _slap.Tap2LevelDb = second.Db;
            _slap.Tap2Pan = Math.Clamp(second.Pan, -1f, 1f);
        }
    }

    private static bool Moved(Echo a, Echo b) => MathF.Abs(a.DelayMs - b.DelayMs) > 3f || MathF.Abs(a.Db - b.Db) > 0.5f || MathF.Abs(a.Pan - b.Pan) > 0.08f;

    /// <summary>The crowd's two loops: the show as heard here, where the ears stand, eased.</summary>
    private void Crowd(Vector3 at, float delay, float dt)
    {
        var cue = Cue(delay);
        CrowdTarget = ConcertAcoustics.Crowd(cue);
        var (placeDb, cutoff) = ConcertAcoustics.CrowdAt(at, _field, _outer);
        CrowdPlaceDb = placeDb;
        Ease(ref _roarDb, CrowdTarget.RoarDb, dt);
        Ease(ref _clapDb, CrowdTarget.ApplauseDb, dt);
        Voice(_roar, _roarDb + placeDb);
        Voice(_clap, _clapDb + placeDb);
        if (_owner == this && _muffle != null && MathF.Abs(cutoff - CrowdCutoff) > CrowdCutoff * 0.02f)
        {
            CrowdCutoff = cutoff;
            _muffle.CutoffHz = cutoff;
            int bus = AudioServer.GetBusIndex(CrowdId);
            if (bus >= 0) AudioServer.SetBusEffectEnabled(bus, 0, cutoff < 19000f);
        }
    }

    /// <summary>The show's moment shifted by the ears' delay: the crowd round them reacts to what they hear.</summary>
    private CrowdCue Cue(float delay)
    {
        var m = _music.Moment;
        int section = m?.Section switch { ShowSection.Calm => 0, ShowSection.Peak => 2, ShowSection.Chorus => 3, _ => 1 };
        float songTime = (float)_music.SongTime - delay, gapTime = (float)_music.GapTime, gapLeft = (float)_music.GapLeft;
        if (_music.GapTime >= 0) { gapTime -= delay; gapLeft += delay; }
        if (_music.SongTime >= 0 && songTime < 0f)
        {
            // the song has started at the stage, not yet here: the end of the gap
            gapTime = (float)ConcertMusic.Gap + songTime;
            gapLeft = -songTime;
        }
        else if (_music.GapTime >= 0 && gapTime < 0f)
        {
            // the song has ended at the stage, its last notes still on the way
            gapTime = -1f;
            songTime = 1e4f;
        }
        else if (_music.SongTime < 0 && _music.GapTime < 0) gapTime = -1f;
        float sinceDrop = m is { HasBands: true } b ? b.SinceDrop - delay : float.PositiveInfinity;
        if (sinceDrop < 0f) sinceDrop = float.PositiveInfinity;
        bool show = _music.ShowOn && (_music.Song != null || _music.GapTime >= 0);
        return new CrowdCue(show, songTime, gapTime, gapLeft, _music.Finale, section, m?.Level ?? 0f, m?.Build ?? 0f, sinceDrop);
    }

    private static void Ease(ref float db, float target, float dt)
    {
        // up fast (a roar), down slow (it dies away)
        float k = 1f - MathF.Exp(-dt / (target > db ? 0.12f : 0.9f));
        db += (target - db) * k;
    }

    private static void Voice(AudioStreamPlayer p, float db)
    {
        float vol = CrowdBaseDb + db;
        if (vol < -62f)
        {
            if (p.Playing) p.Stop();
            return;
        }
        if (MathF.Abs(p.VolumeDb - vol) > 0.1f) p.VolumeDb = vol;
        // somewhere in the loop: two concerts' crowds never in step
        if (!p.Playing && DisplayServer.GetName() != "headless") p.Play((float)(GD.Randf() * p.Stream.GetLength()));
    }

    // ---- the crowd's sounds (static: in the sound player) -------------------------------------------

    private static AudioStreamWav? _roarWav, _clapWav;

    /// <summary>
    /// A stadium crowd's roar, 8 s stereo loop: a low hubbub, sixty yells a side ("woo", "yeah": a
    /// voice's buzz through two vowel formants, gliding), and a few whistles; each side its own people.
    /// </summary>
    public static AudioStreamWav CrowdRoar => _roarWav ??= StereoLoop(8f, 984, (rng, n) =>
    {
        var s = new float[n];
        var hub = BandPass(Noise(rng, n), Coef(160f), Coef(1100f));
        for (int i = 0; i < n; i++) s[i] = hub[i] * 0.7f;
        for (int v = 0; v < 60; v++)
        {
            int len = (int)(Rate * (0.4f + 1.2f * (float)rng.NextDouble())), start = rng.Next(n);
            float f0 = 170f + 260f * (float)rng.NextDouble(), glide = 0.85f + 0.4f * (float)rng.NextDouble();
            float f1 = 520f + 330f * (float)rng.NextDouble(), f2 = 950f + 800f * (float)rng.NextDouble();
            float amp = 0.15f + 0.5f * (float)rng.NextDouble(), breath = 0.3f + 0.4f * (float)rng.NextDouble();
            var yell = new float[len];
            float ph = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / len;
                ph = (ph + f0 * (1f + (glide - 1f) * t) / Rate) % 1f;
                yell[i] = (2f * ph - 1f) + breath * (float)(rng.NextDouble() * 2 - 1);
            }
            var voiced = new float[len];
            Resonate(yell, voiced, f1, 6f);
            Resonate(yell, voiced, f2, 8f);
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / len, env = MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.6f)) * MathF.Min(1f, (1f - t) * 4f);
                s[(start + i) % n] += voiced[i] * amp * env;
            }
        }
        for (int w = 0; w < 3; w++)
        {
            int len = (int)(Rate * (0.4f + 0.5f * (float)rng.NextDouble())), start = rng.Next(n);
            float f = 2200f + 1200f * (float)rng.NextDouble(), ph = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / len;
                ph += MathF.Tau * f * (1f + 0.12f * MathF.Sin(MathF.PI * t) + 0.01f * MathF.Sin(MathF.Tau * 6f * t * len / Rate)) / Rate;
                s[(start + i) % n] += MathF.Sin(ph) * 0.12f * MathF.Sin(MathF.PI * t);
            }
        }
        return s;
    });

    /// <summary>
    /// A stadium's applause, 8 s stereo loop: a dozen clappers close by (each clapping ~4 a second at
    /// their own hands' pitch) over a thousand claps a second further off, a dense hiss with grain in it.
    /// </summary>
    public static AudioStreamWav CrowdApplause => _clapWav ??= StereoLoop(8f, 985, (rng, n) =>
    {
        var far = new float[n];
        int claps = (int)(n / (float)Rate * 1000f);
        for (int c = 0; c < claps; c++) Clap(far, rng.Next(n), 0.3f + 0.7f * (float)rng.NextDouble(), 0.004f, rng);
        var s = BandPass(far, Coef(500f), Coef(4500f));
        for (int i = 0; i < n; i++) s[i] *= 0.5f;
        for (int p = 0; p < 12; p++)
        {
            var hands = new float[n];
            float rate = 3.3f + 1.4f * (float)rng.NextDouble(), amp = 0.4f + 0.8f * (float)rng.NextDouble();
            for (float t = (float)rng.NextDouble() / rate; t * Rate < n; t += (1f + 0.08f * ((float)rng.NextDouble() * 2 - 1)) / rate)
                Clap(hands, (int)(t * Rate), amp, 0.009f, rng);
            Resonate(hands, s, 900f + 1300f * (float)rng.NextDouble(), 2.5f);
        }
        return s;
    });

    /// <summary>One clap into <paramref name="s"/>: a noise burst with a sharp attack and a short decay.</summary>
    private static void Clap(float[] s, int at, float amp, float decay, Random rng)
    {
        int len = (int)(decay * 6f * Rate);
        for (int k = 0; k < len; k++)
            s[(at + k) % s.Length] += amp * (float)(rng.NextDouble() * 2 - 1) * MathF.Exp(-k / (decay * Rate)) * MathF.Min(1f, k / 8f);
    }

    /// <summary>Adds <paramref name="x"/> band-passed at <paramref name="hz"/> (RBJ biquad, peak gain 1) into <paramref name="y"/>.</summary>
    private static void Resonate(float[] x, float[] y, float hz, float q)
    {
        float w = MathF.Tau * hz / Rate, alpha = MathF.Sin(w) / (2f * q), a0 = 1f + alpha;
        float b0 = alpha / a0, a1 = -2f * MathF.Cos(w) / a0, a2 = (1f - alpha) / a0;
        float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            float o = b0 * x[i] - b0 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = o;
            y[i] += o;
        }
    }

    /// <summary>A seamless stereo loop: each side its own render, its tail crossfaded into its head, both normalised together.</summary>
    private static AudioStreamWav StereoLoop(float seconds, int seed, Func<Random, int, float[]> make)
    {
        int fade = Rate / 4, n = (int)(seconds * Rate);
        var sides = new float[2][];
        float peak = 1e-6f;
        for (int c = 0; c < 2; c++)
        {
            var raw = make(new Random(seed + 101 * c), n + fade);
            var s = new float[n];
            Array.Copy(raw, s, n);
            for (int i = 0; i < fade; i++)
            {
                float t = (float)i / fade;
                s[i] = raw[n + i] * (1 - t) + raw[i] * t;
            }
            foreach (float v in s) peak = MathF.Max(peak, MathF.Abs(v));
            sides[c] = s;
        }
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 2; c++)
            {
                short v = (short)Math.Clamp((int)(sides[c][i] * 0.8f / peak * 32767f), short.MinValue, short.MaxValue);
                bytes[i * 4 + c * 2] = (byte)(v & 0xFF);
                bytes[i * 4 + c * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
        return new AudioStreamWav
        {
            Data = bytes, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = true,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }
}
