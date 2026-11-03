using Godot;
using UnitSport.Audio;

namespace UnitSport.Phone;

/// <summary>
/// The phone's sounds (#889), synthesised once: in the sound player under "PhoneSounds"
/// (<c>docs/notes/audio/sound-player.md</c>).
/// </summary>
public static class PhoneSounds
{
    private static AudioStreamWav? _draw, _hinge, _tap, _buzz, _shutter;

    /// <summary>Out of the pocket: a short cloth rustle.</summary>
    public static AudioStreamWav Draw => _draw ??= Dsp.OneShot(0.28f, 8891, (rng, n) =>
    {
        var s = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 11f) * (0.6f + 0.4f * Mathf.Sin(t * 70f));
            lp += ((float)rng.NextDouble() * 2f - 1f - lp) * 0.25f;
            s[i] = lp * env * 0.55f;
        }
        return s;
    });

    /// <summary>The hinge: a soft mechanical slide that ends in a click as it seats.</summary>
    public static AudioStreamWav Hinge => _hinge ??= Dsp.OneShot(0.32f, 8893, (rng, n) =>
    {
        var s = new float[n];
        float lp = 0f;
        const float seat = 0.24f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            lp += ((float)rng.NextDouble() * 2f - 1f - lp) * 0.08f;
            float slide = t < seat ? Mathf.Sin(Mathf.Pi * t / seat) * 0.35f : 0f;
            float ct = t - seat;
            float click = ct >= 0f ? Mathf.Exp(-ct * 260f) * (Mathf.Sin(Mathf.Tau * 2300f * ct) + 0.5f * Mathf.Sin(Mathf.Tau * 4100f * ct)) : 0f;
            s[i] = lp * slide + click * 0.45f;
        }
        return s;
    });

    /// <summary>A screen tap: a tiny, high click.</summary>
    public static AudioStreamWav Tap => _tap ??= Dsp.OneShot(0.05f, 8897, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 1800f * t) * Mathf.Exp(-t * 140f) * 0.35f;
        }
        return s;
    });

    /// <summary>The camera app's shutter: a phone's synthetic click, two quick ticks with a breath of air.</summary>
    public static AudioStreamWav Shutter => _shutter ??= Dsp.OneShot(0.16f, 8903, (rng, n) =>
    {
        var s = new float[n];
        float lp = 0f;
        foreach (float at in new[] { 0f, 0.055f })
            for (int i = (int)(at * Dsp.Rate); i < n; i++)
            {
                float t = i / (float)Dsp.Rate - at;
                lp += ((float)rng.NextDouble() * 2f - 1f - lp) * 0.5f;
                s[i] += (lp * 0.6f + Mathf.Sin(Mathf.Tau * 2600f * t) * 0.4f) * Mathf.Exp(-t * 120f) * 0.45f;
            }
        return s;
    });

    /// <summary>A call in the pocket: two buzzes of the vibration motor, muffled by cloth.</summary>
    public static AudioStreamWav Buzz => _buzz ??= Dsp.OneShot(0.9f, 8899, (rng, n) =>
    {
        var s = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            bool on = t < 0.32f || t is > 0.5f and < 0.82f;
            float local = t < 0.32f ? t : t - 0.5f;
            float env = on ? Mathf.Min(1f, local * 80f) * Mathf.Min(1f, (0.32f - local) * 80f) : 0f;
            // an eccentric mass at ~170 Hz rattling the pocket: a buzz rich in harmonics, low-passed
            float raw = Mathf.Sign(Mathf.Sin(Mathf.Tau * 170f * t)) * 0.6f + ((float)rng.NextDouble() * 2f - 1f) * 0.15f;
            lp += (raw - lp) * 0.12f;
            s[i] = lp * env * 0.6f;
        }
        return s;
    });
}
