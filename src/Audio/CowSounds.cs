using Godot;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Audio;

/// <summary>
/// The herds' sounds (#863), synthesised and found by the sound player by themselves (static
/// banks): a moo (a nasal "mmm" opening into a long "ooh", a low voice sliding up and back down,
/// with breath), a higher, rising moo for a cow in a tractor beam, and the clink of a cowbell as a
/// cow takes a step or tugs at the grass (the alp bell of <see cref="Ambience"/>, short and soft).
/// </summary>
public static class CowSounds
{
    /// <summary>A contented moo, 1.6-2.3 s. Four cows' voices.</summary>
    public static SfxBank Moo => _moo ??= SfxBank.Build("moo", 4, 2.4f, 8631, (rng, n) => Voice(rng, n, alarmed: false));
    private static SfxBank? _moo;

    /// <summary>A cow lifted off its feet: higher, louder, rising at the end.</summary>
    public static SfxBank Alarmed => _alarmed ??= SfxBank.Build("moo alarmed", 3, 1.6f, 8632, (rng, n) => Voice(rng, n, alarmed: true));
    private static SfxBank? _alarmed;

    /// <summary>One clink of a cowbell: a cow's step or a tug at the grass.</summary>
    public static SfxBank Bell => _bell ??= new SfxBank("cowbell clink",
        Enumerable.Range(0, 4).Select(v => Encode(Normalise(Clink(new Random(8640 + v * 37)), 0.8f))).ToArray())
    { PitchJitter = 0.03f, VolumeJitterDb = 2f };
    private static SfxBank? _bell;

    private static float[] Voice(Random rng, int n, bool alarmed)
    {
        float J(float a) => 1f + ((float)rng.NextDouble() * 2 - 1) * a;
        float f0 = (alarmed ? 190f : 118f) * J(0.12f);
        float length = (alarmed ? 1.25f : 1.6f + (float)rng.NextDouble() * 0.6f);
        float onset = alarmed ? 0.12f : 0.32f;   // the closed-mouth "mmm"
        var breath = BandPass(Noise(rng, n), Coef(300f), Coef(1800f));
        var raw = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            if (t > length) break;
            float u = t / length;
            // the pitch: up a fifth into the vowel, then sagging away (an alarmed one rises again)
            float bend = alarmed
                ? 1f + 0.35f * Mathf.Sin(Mathf.Pi * u) + 0.25f * u * u
                : 1f + 0.18f * Mathf.Sin(Mathf.Pi * Mathf.Min(1f, u * 1.4f)) - 0.12f * u * u;
            float vib = 1f + 0.012f * Mathf.Sin(Mathf.Tau * 5.5f * t);
            phase += Mathf.Tau * f0 * bend * vib / Rate;
            // a buzzy source: a soft saw from a few harmonics, falling off with height
            float src = 0f;
            for (int h = 1; h <= 9; h++) src += Mathf.Sin((float)(phase * h)) / (h * 0.8f + 0.4f);
            float env = Mathf.SmoothStep(0f, 0.06f, t) * (1f - Mathf.SmoothStep(length - 0.35f, length, t));
            raw[i] = src * env + breath[i] * 0.6f * env;
        }
        // the mouth: closed (low only) through the "mmm", opening to a dark "ooh"
        var closed = LowPass(raw, Coef(280f));
        var open = BandPass(raw, Coef(180f), Coef(alarmed ? 1400f : 900f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float mouth = Mathf.SmoothStep(onset * 0.6f, onset + 0.15f, t);
            s[i] = Mathf.Lerp(closed[i] * 1.6f, open[i] * 2.2f, mouth);
        }
        return s;
    }

    /// <summary>The alp bell (<c>AmbienceDsp.CowBell</c>) cut short and damped: one knock of the clapper.</summary>
    private static float[] Clink(Random rng)
    {
        var bell = AmbienceDsp.CowBell(rng);
        int n = Math.Min(bell.Length, (int)(Rate * 0.9f));
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = bell[i] * Mathf.Exp(-3.5f * i / (float)Rate);
        return s;
    }
}
