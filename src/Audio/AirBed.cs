using Godot;
using UnitSport.Interiors;

namespace UnitSport.Audio;

/// <summary>
/// The air of the open (#375): what "outside" sounds like when nothing in particular happens. A
/// wide, slowly gusting wind in stereo, and in woods the rustle of leaves riding the same gusts,
/// non-positional, around the ears. Without it every footstep and engine played in a silent void,
/// which read as a studio, not a mountainside.
///
/// <para>
/// Two baked stereo loops (16 s and 11 s, so their repeats never line up), left and right drawn
/// from different noise so they sound wide, sharing most of their gust envelope so it still
/// sounds like one wind. Levels follow what <see cref="Ambience"/> samples: louder and brighter
/// high up, leaves in woods, nothing in a building, half behind the glass of a car (the world
/// bus's cabin filter does the rest). Kept low on purpose: you should notice it when you stop.
/// </para>
/// </summary>
public partial class AirBed : Node
{
    private AudioStreamPlayer _wind = null!, _leaves = null!;
    private float _windGain, _leafGain;

    public override void _Ready()
    {
        Name = "AirBed";
        _wind = new AudioStreamPlayer { Name = "Wind", Bus = SfxBus.Name, VolumeDb = -80f, Stream = Wind };
        _leaves = new AudioStreamPlayer { Name = "Leaves", Bus = SfxBus.Name, VolumeDb = -80f, Stream = Leaves };
        AddChild(_wind);
        AddChild(_leaves);
    }

    /// <summary>
    /// Once a frame. <paramref name="wooded"/> 0..1 and <paramref name="altitude"/> (m) from the
    /// last environment sample; <paramref name="volume"/> the ambience setting.
    /// </summary>
    public void Step(double delta, Vector3 ears, float wooded, float altitude, float volume)
    {
        bool inside = InteriorManager.InInteriorSpace(ears);
        float high = Mathf.Clamp((altitude - 900f) / 1800f, 0f, 1f);
        float wind = inside ? 0f : (0.07f + 0.13f * high) * (1f - 0.6f * wooded);
        float leaves = inside ? 0f : 0.09f * wooded;
        float cabin = 1f - 0.5f * Ears.Shut;
        float k = 1f - Mathf.Exp(-(float)delta / 1.2f);
        _windGain += (wind * cabin * volume - _windGain) * k;
        _leafGain += (leaves * cabin * volume - _leafGain) * k;
        Drive(_wind, _windGain, 0.9f + 0.2f * high);
        Drive(_leaves, _leafGain, 1f);
    }

    private static void Drive(AudioStreamPlayer p, float gain, float pitch)
    {
        if (gain < 0.002f)
        {
            if (p.Playing) p.Stop();
            return;
        }
        p.VolumeDb = Mathf.LinearToDb(gain);
        p.PitchScale = pitch;
        if (!p.Playing) p.Play();
    }

    // ---- baking -------------------------------------------------------------------------------

    private static AudioStreamWav? _windWav, _leavesWav;

    /// <summary>16 s of stereo wind: low rumble and a faint whistle, gusting.</summary>
    public static AudioStreamWav Wind => _windWav ??= StereoLoop(16f, 4101, (rng, n, gust) =>
    {
        var body = Dsp.LowPass(Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(320f)), Dsp.Coef(500f));
        var whistle = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(650f), Dsp.Coef(1400f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float g = gust[i];
            // the whistle only comes up in the strong gusts, as it does round a ridge
            s[i] = body[i] * 6f * g + whistle[i] * 0.6f * g * g * g;
        }
        return s;
    });

    /// <summary>11 s of stereo leaves: bright fluttering hiss riding the gusts.</summary>
    public static AudioStreamWav Leaves => _leavesWav ??= StereoLoop(11f, 5203, (rng, n, gust) =>
    {
        var hiss = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1200f), Dsp.Coef(6500f));
        // flutter: thousands of leaves, each a tiny tick, bunched into a fast random swell
        var flutter = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(9f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float f = Mathf.Clamp(0.5f + flutter[i] * 6f, 0f, 1f);
            s[i] = hiss[i] * gust[i] * gust[i] * (0.4f + 0.6f * f);
        }
        return s;
    });

    /// <summary>
    /// A seamless stereo loop: one shared gust envelope, each side made from its own noise with
    /// a little of its own gust mixed in, both crossfaded tail-into-head like <see cref="Dsp.Loop"/>.
    /// </summary>
    private static AudioStreamWav StereoLoop(float seconds, int seed, Func<Random, int, float[], float[]> make)
    {
        int rate = Dsp.Rate, fade = rate / 2, n = (int)(seconds * rate), m = n + fade;
        var shared = Gusts(new Random(seed), m);
        var sides = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            var own = Gusts(new Random(seed + 31 + c), m);
            var gust = new float[m];
            for (int i = 0; i < m; i++) gust[i] = 0.75f * shared[i] + 0.25f * own[i];
            var raw = make(new Random(seed + 101 * (c + 1)), m, gust);
            var s = new float[n];
            for (int i = 0; i < n; i++) s[i] = raw[i];
            for (int i = 0; i < fade; i++)
            {
                float t = (float)i / fade;
                s[i] = raw[n + i] * (1 - t) + raw[i] * t;
            }
            sides[c] = s;
        }
        float peak = 1e-6f;
        foreach (var s in sides) foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
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
            Data = bytes, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = true,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    /// <summary>A slow gust envelope, 0.25..1: a few incommensurate swells under a random wander.</summary>
    private static float[] Gusts(Random rng, int n)
    {
        var wander = Dsp.LowPass(Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(0.4f)), Dsp.Coef(0.6f));
        float wmax = 1e-6f;
        foreach (float v in wander) wmax = Math.Max(wmax, Math.Abs(v));
        float p1 = (float)rng.NextDouble() * Mathf.Tau, p2 = (float)rng.NextDouble() * Mathf.Tau;
        var g = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Dsp.Rate;
            float swell = 0.5f * Mathf.Sin(Mathf.Tau * 0.071f * t + p1) + 0.3f * Mathf.Sin(Mathf.Tau * 0.173f * t + p2);
            float x = 0.5f + 0.25f * swell + 0.35f * wander[i] / wmax;
            g[i] = Mathf.Clamp(x, 0.25f, 1f);
        }
        return g;
    }
}
