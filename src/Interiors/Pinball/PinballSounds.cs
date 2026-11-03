using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A pinball machine's synthesised sounds (#1325), each built once (static: the sound player lists them):
/// the mechanics (flipper coils, pop bumpers, slingshots, targets, drop targets, the spinner, the
/// plunger, a scoop's kick, the drain, the knocker, the ball rolling on wood and on a wire form), the
/// electro-mechanical table's chimes and score reels, and the solid-state tables' jingles.
/// </summary>
public static class PinballSounds
{
    private static AudioStreamWav? _flip, _flipDown, _bumper, _sling, _target, _drop, _spin, _plunge, _kick, _drain,
        _knock, _roll, _wire, _chime10, _chime100, _chime1000, _reel, _jingle, _fanfare, _tilt, _save;

    /// <summary>A flipper's coil: a hard clack and the bat's rubber.</summary>
    public static AudioStreamWav Flip => _flip ??= One(0.16f, 13251, (rng, n) => Clack(rng, n, 180f, 1f, 2800f));

    /// <summary>A flipper falling back: a softer knock.</summary>
    public static AudioStreamWav FlipDown => _flipDown ??= One(0.1f, 13252, (rng, n) => Clack(rng, n, 140f, 0.45f, 1800f));

    /// <summary>A pop bumper: the coil's thwack and the ring's ping.</summary>
    public static AudioStreamWav Bumper => _bumper ??= One(0.3f, 13253, (rng, n) =>
    {
        var s = Clack(rng, n, 120f, 1f, 3500f);
        Tone(s, 0.004f, 820f, 0.35f, 18f);
        return s;
    });

    /// <summary>A slingshot: a sharp snap of rubber.</summary>
    public static AudioStreamWav Sling => _sling ??= One(0.18f, 13254, (rng, n) => Clack(rng, n, 210f, 0.9f, 4500f));

    /// <summary>A stand-up target: a dull tick.</summary>
    public static AudioStreamWav Target => _target ??= One(0.12f, 13255, (rng, n) =>
    {
        var s = new float[n];
        Tone(s, 0, 540f, 0.6f, 60f);
        Tone(s, 0, 1300f, 0.25f, 90f);
        return s;
    });

    /// <summary>A drop target falling: a clack and the thud under the playfield.</summary>
    public static AudioStreamWav Drop => _drop ??= One(0.22f, 13256, (rng, n) =>
    {
        var s = Clack(rng, n, 160f, 0.7f, 2500f);
        Tone(s, 0.03f, 95f, 0.6f, 25f);
        return s;
    });

    /// <summary>One turn of a spinner: a light metal tick (played per turn, faster as it spins).</summary>
    public static AudioStreamWav Spin => _spin ??= One(0.05f, 13257, (rng, n) =>
    {
        var s = new float[n];
        Tone(s, 0, 2400f, 0.4f, 120f);
        Tone(s, 0, 3700f, 0.2f, 160f);
        return s;
    });

    /// <summary>The plunger let go: the spring's whang and the ball's knock.</summary>
    public static AudioStreamWav Plunge => _plunge ??= One(0.35f, 13258, (rng, n) =>
    {
        var s = Clack(rng, n, 150f, 0.8f, 2000f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] += Mathf.Sin(Mathf.Tau * (310f - 90f * t) * t) * 0.25f * Mathf.Exp(-t * 9f);
        }
        return s;
    });

    /// <summary>A scoop or a subway kicking the ball out.</summary>
    public static AudioStreamWav Kick => _kick ??= One(0.25f, 13259, (rng, n) =>
    {
        var s = Clack(rng, n, 100f, 1f, 1500f);
        Tone(s, 0, 70f, 0.5f, 14f);
        return s;
    });

    /// <summary>The ball dropping through the outhole: a hollow clunk and a roll away.</summary>
    public static AudioStreamWav Drain => _drain ??= One(0.6f, 13260, (rng, n) =>
    {
        var s = new float[n];
        Tone(s, 0, 85f, 0.9f, 10f);
        Tone(s, 0.0f, 210f, 0.3f, 22f);
        var roll = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(300f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.08f) s[i] += roll[i] * 1.2f * Mathf.Exp(-(t - 0.08f) * 6f);
        }
        return s;
    });

    /// <summary>The knocker: a free game or an extra ball, a loud knock on the cabinet.</summary>
    public static AudioStreamWav Knock => _knock ??= One(0.5f, 13261, (rng, n) =>
    {
        var s = Clack(rng, n, 60f, 1f, 1200f);
        Tone(s, 0, 52f, 1f, 9f);
        Tone(s, 0, 160f, 0.4f, 16f);
        return s;
    });

    /// <summary>The ball rolling on the playfield's wood: a soft rumble, looped and pitched by speed.</summary>
    public static AudioStreamWav Roll => _roll ??= Dsp.Loop(1.0f, 13262, (rng, n) =>
    {
        var low = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(220f));
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = low[i] * 2.4f;
        return s;
    });

    /// <summary>The ball on a wire form: a metallic rattle, looped.</summary>
    public static AudioStreamWav Wire => _wire ??= Dsp.Loop(1.0f, 13263, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1800f), Dsp.Coef(5200f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] *= 1.4f * (0.6f + 0.4f * Mathf.Sin(Mathf.Tau * 23f * t));
        }
        return s;
    });

    /// <summary>The electro-mechanical table's chimes: 10, 100 and 1000 points, three bells.</summary>
    public static AudioStreamWav Chime10 => _chime10 ??= One(1.2f, 13264, (_, n) => Bell(n, 1568f));

    public static AudioStreamWav Chime100 => _chime100 ??= One(1.4f, 13265, (_, n) => Bell(n, 1175f));

    public static AudioStreamWav Chime1000 => _chime1000 ??= One(1.6f, 13266, (_, n) => Bell(n, 784f));

    /// <summary>A score reel stepping: a small relay click.</summary>
    public static AudioStreamWav Reel => _reel ??= One(0.06f, 13267, (rng, n) => Clack(rng, n, 300f, 0.5f, 6000f));

    /// <summary>The solid-state tables' short jingle (a mission done, a lock).</summary>
    public static AudioStreamWav Jingle => _jingle ??= One(0.9f, 13268, (_, n) => Notes(n, new[] { 523f, 659f, 784f, 1047f }, 0.12f));

    /// <summary>Multiball and the jackpot: a fanfare.</summary>
    public static AudioStreamWav Fanfare => _fanfare ??= One(1.6f, 13269, (_, n) => Notes(n, new[] { 392f, 523f, 659f, 784f, 659f, 784f, 1047f }, 0.16f));

    /// <summary>Tilt: a low buzzer.</summary>
    public static AudioStreamWav Tilt => _tilt ??= One(1.0f, 13270, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float saw = 2f * (t * 110f % 1f) - 1f;
            s[i] = saw * 0.45f * Mathf.Min(1, t * 40) * Mathf.Min(1, (1f - t) * 8);
        }
        return s;
    });

    /// <summary>The ball saved: a quick rising chirp.</summary>
    public static AudioStreamWav Save => _save ??= One(0.4f, 13271, (_, n) => Notes(n, new[] { 880f, 1175f, 1568f }, 0.08f));

    // ---- building blocks -------------------------------------------------------------------------

    /// <summary>A solenoid's clack: a body knock at <paramref name="body"/> Hz and a burst of filtered noise.</summary>
    private static float[] Clack(Random rng, int n, float body, float amp, float brightness)
    {
        var s = new float[n];
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(400f), Dsp.Coef(brightness));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = noise[i] * amp * 2.2f * Mathf.Exp(-t * 70f);
        }
        Tone(s, 0, body, amp * 0.8f, 30f);
        return s;
    }

    private static void Tone(float[] s, float at, float f, float amp, float decay)
    {
        int from = (int)(at * Dsp.Rate);
        for (int i = from; i < s.Length; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1, t * 3000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += Mathf.Sin(Mathf.Tau * f * t) * env * amp;
        }
    }

    /// <summary>A struck bell: the fundamental and its inharmonic partials, a long ring.</summary>
    private static float[] Bell(int n, float f)
    {
        var s = new float[n];
        Tone(s, 0, f, 0.5f, 3.2f);
        Tone(s, 0, f * 2.76f, 0.22f, 5f);
        Tone(s, 0, f * 5.4f, 0.1f, 8f);
        Tone(s, 0, f * 0.5f, 0.12f, 4f);
        return s;
    }

    /// <summary>Square-ish notes one after the other, the solid-state sound chips' way.</summary>
    private static float[] Notes(int n, float[] freqs, float each)
    {
        var s = new float[n];
        for (int k = 0; k < freqs.Length; k++)
        {
            int from = (int)(k * each * Dsp.Rate);
            for (int i = from; i < n; i++)
            {
                float t = (i - from) / (float)Dsp.Rate;
                float env = Mathf.Exp(-t * (k == freqs.Length - 1 ? 3f : 9f));
                if (env < 1e-3f) break;
                float sq = Mathf.Sin(Mathf.Tau * freqs[k] * t);
                s[i] += (sq > 0 ? 0.22f : -0.22f) * env + sq * 0.1f * env;
            }
        }
        return s;
    }

    private static AudioStreamWav One(float seconds, int seed, Func<Random, int, float[]> make)
    {
        var w = Dsp.OneShot(seconds, seed, make);
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
