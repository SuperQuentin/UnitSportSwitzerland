using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors;

/// <summary>
/// A bowling lane's synthesised sounds (#1323), each built once (static: the sound player lists them by
/// itself): the ball's rumble on the wood (a loop, pitched by its speed), the crash of the pins, the
/// gutter's hollow rattle, the pinsetter's clatter, and the thunk of a ball coming up into the hood.
/// </summary>
public static class BowlingSounds
{
    private static AudioStreamWav? _roll, _crash, _light, _gutter, _machine, _thunk;

    /// <summary>The ball rolling on maple: a low rumble with the wood's knock in it, seamless as a loop.</summary>
    public static AudioStreamWav Roll => _roll ??= Dsp.Loop(1.0f, 13231, (rng, n) =>
    {
        var low = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(160f));
        var wood = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(300f), Dsp.Coef(900f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            // the boards' seams under the ball: a soft tick about 30 times a second
            float seam = 0.5f + 0.5f * Mathf.Sin(Mathf.Tau * 31f * t);
            s[i] = low[i] * 2.2f + wood[i] * 0.35f * seam * seam + Mathf.Sin(Mathf.Tau * 55f * t) * 0.15f;
        }
        return s;
    });

    /// <summary>A ball into a full rack: the first crack, then the pins' wooden clatter falling away.</summary>
    public static AudioStreamWav Crash => _crash ??= OneShot(Dsp.OneShot(1.6f, 13232, (rng, n) => Clatter(rng, n, 14, 1f)));

    /// <summary>A few pins: a lighter knock and a short clatter.</summary>
    public static AudioStreamWav Light => _light ??= OneShot(Dsp.OneShot(0.9f, 13233, (rng, n) => Clatter(rng, n, 4, 0.6f)));

    /// <summary>A ball in the gutter: a hollow, rattling roll.</summary>
    public static AudioStreamWav Gutter => _gutter ??= Dsp.Loop(1.0f, 13234, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(180f), Dsp.Coef(700f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float rattle = 0.6f + 0.4f * Mathf.Sin(Mathf.Tau * 17f * t) * Mathf.Sin(Mathf.Tau * 6f * t);
            s[i] = s[i] * 1.8f * rattle + Mathf.Sin(Mathf.Tau * 95f * t) * 0.2f;
        }
        return s;
    });

    /// <summary>The pinsetter: the sweep's clank down, a motor's whir, the table's knock setting the pins.</summary>
    public static AudioStreamWav Machine => _machine ??= OneShot(Dsp.OneShot(3.2f, 13235, (rng, n) =>
    {
        var s = new float[n];
        var hum = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(400f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float motor = Mathf.Min(1, t * 3) * Mathf.Min(1, (3.2f - t) * 3);
            s[i] = (Mathf.Sin(Mathf.Tau * 118f * t) * 0.25f + hum[i] * 0.8f) * motor * 0.6f;
        }
        Knock(s, 0.05f, 210f, 0.9f);
        Knock(s, 1.6f, 160f, 0.7f);
        Knock(s, 2.9f, 240f, 0.6f);
        return s;
    }));

    /// <summary>A ball pushed up into the hood: a deep thunk and the roll onto the rack.</summary>
    public static AudioStreamWav Thunk => _thunk ??= OneShot(Dsp.OneShot(0.8f, 13236, (rng, n) =>
    {
        var s = new float[n];
        Knock(s, 0f, 70f, 1f, 14f);
        var roll = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(250f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.12f) s[i] += roll[i] * 1.5f * Mathf.Exp(-(t - 0.12f) * 5f);
        }
        return s;
    }));

    /// <summary><paramref name="hits"/> pin knocks over a falling-away tail, the first the loudest.</summary>
    private static float[] Clatter(Random rng, int n, int hits, float amp)
    {
        var s = new float[n];
        var noise = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(600f));
        for (int i = 0; i < Math.Min(n, Dsp.Rate / 8); i++) s[i] += noise[i] * amp * (1 - i / (float)(Dsp.Rate / 8));
        float at = 0f;
        for (int h = 0; h < hits; h++)
        {
            // a hollow maple pin: a knock round 700-1300 Hz with a short ring
            Knock(s, at, 700f + (float)rng.NextDouble() * 600f, amp * (h == 0 ? 1f : 0.7f * Mathf.Exp(-at * 2.2f)), 40f);
            at += 0.02f + (float)rng.NextDouble() * 0.09f * (1 + at);
        }
        return s;
    }

    private static void Knock(float[] s, float at, float f, float amp, float decay = 30f)
    {
        int from = (int)(at * Dsp.Rate);
        for (int i = from; i < s.Length; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1, t * 3000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += (Mathf.Sin(Mathf.Tau * f * t) + 0.35f * Mathf.Sin(Mathf.Tau * f * 2.3f * t)) * env * amp;
        }
    }

    private static AudioStreamWav OneShot(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
