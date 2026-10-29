using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The Désalpe's synthesised sounds (#1269 part 7), baked once as static banks (so the sound player lists them): the Treichel in three
/// sizes (a big cylindrical bell, a deep clank with a long ring; the Leitkuh wears the biggest) and the herder's call ("Hoo-ooh!", a falling
/// shout with a rough edge) that starts a drive and urges the herd on.
/// </summary>
public static class DesalpeSounds
{
    private static SfxBank? _big, _middle, _small, _call;

    /// <summary>The Leitkuh's big Treichel: a deep clank (about 170 Hz) that rings for over a second.</summary>
    public static SfxBank TreichelBig => _big ??= Bell("treichel big", 170f, 1.6f, 19011);

    public static SfxBank TreichelMiddle => _middle ??= Bell("treichel middle", 250f, 1.2f, 19021);

    public static SfxBank TreichelSmall => _small ??= Bell("treichel small", 370f, 0.9f, 19031);

    /// <summary>The bank of a bell size 1-3.</summary>
    public static SfxBank Treichel(int size) => size switch { 3 => TreichelBig, 2 => TreichelMiddle, _ => TreichelSmall };

    private static SfxBank Bell(string name, float f0, float seconds, int seed) => new SfxBank(name,
        Enumerable.Range(0, 3).Select(v => Dsp.Encode(Dsp.Normalise(Clank(new Random(seed + v * 31), f0 * (1f + (v - 1) * 0.02f), seconds), 0.85f))).ToArray())
    { PitchJitter = 0.03f, VolumeJitterDb = 2f };

    /// <summary>A struck sheet-metal bell: a few inharmonic partials with their own decays, over a short knock of the clapper.</summary>
    private static float[] Clank(Random rng, float f0, float seconds)
    {
        int n = (int)(Dsp.Rate * seconds);
        float[] ratio = [1f, 2.01f, 2.76f, 4.1f, 5.4f];
        float[] amp = [1f, 0.55f, 0.7f, 0.3f, 0.2f];
        float[] decay = [2.6f, 3.4f, 4.5f, 7f, 9f];
        var knock = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(500f), Dsp.Coef(3500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float v = 0f;
            for (int p = 0; p < ratio.Length; p++)
                v += amp[p] * Mathf.Sin(Mathf.Tau * f0 * ratio[p] * t) * Mathf.Exp(-decay[p] * t);
            // the wobble of a bell that is not quite round
            v *= 1f + 0.06f * Mathf.Sin(Mathf.Tau * 4.5f * t);
            s[i] = v * 0.5f + knock[i] * Mathf.Exp(-t * 90f) * 0.8f;
        }
        return s;
    }

    /// <summary>"Hoo-ooh!": a man's shout from the chest, falling a fifth over 0.8 s, with breath on the edges.</summary>
    public static SfxBank HerderCall => _call ??= SfxBank.Build("herder call", 3, 1.0f, 19101, (rng, n) =>
    {
        float f0 = 190f + (float)rng.NextDouble() * 25f;
        var breath = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(500f), Dsp.Coef(2500f));
        var raw = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.85f) break;
            float u = t / 0.85f;
            float f = f0 * (1.25f - 0.45f * u * u);
            phase += Mathf.Tau * f / Dsp.Rate;
            float src = 0f;
            for (int h = 1; h <= 8; h++) src += Mathf.Sin((float)(phase * h)) / h;
            float env = Mathf.SmoothStep(0f, 0.06f, t) * (1f - Mathf.SmoothStep(0.5f, 0.85f, t));
            raw[i] = (src + breath[i] * 0.35f) * env;
        }
        // the vowel: "hoo" (dark) opening to "ooh" through two formant bands
        var dark = Dsp.BandPass(raw, Dsp.Coef(200f), Dsp.Coef(700f));
        var open = Dsp.BandPass(raw, Dsp.Coef(250f), Dsp.Coef(1100f));
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = Mathf.Lerp(dark[i], open[i], Mathf.SmoothStep(0.1f, 0.7f, i / (float)Dsp.Rate)) * 2.2f;
        float peak = 0;
        foreach (float v in s) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak > 1f) for (int i = 0; i < s.Length; i++) s[i] /= peak;
        return s;
    });
}
