using Godot;

namespace UnitSport.Audio;

/// <summary>
/// What a venue's guests sound like (#1244), synthesised once each (static: the sound player lists them by itself):
/// the chatter of a room of people and the cutlery under it, as loops a room plays as loud as it is full
/// (<see cref="Interiors.Patrons.VenuePatrons"/>); the café's grinder and steamer, the bar's tap, the cinema's popcorn
/// machine, an arcade's attract jingles and coin drops; the clicks, thuds and rattles of pool, darts and table football,
/// and a settee's cheer, as one-shots on the guests' own moves.
/// </summary>
public static class VenueAmbience
{
    private const int R = Dsp.Rate;

    private static AudioStreamWav? _chatter, _cutlery, _popcorn, _foosball, _grinder, _steamer, _tap, _cheer;
    private static SfxBank? _jingles, _coins, _pool, _darts;

    /// <summary>
    /// A room of people talking: seven voices, each a glottal buzz at its own pitch through a moving pair of formants,
    /// in syllables four to six a second, in phrases with pauses between. No words: a murmur, as through a room.
    /// </summary>
    public static AudioStreamWav Chatter => _chatter ??= Dsp.Loop(8f, 12441, (rng, n) =>
    {
        var s = new float[n];
        for (int v = 0; v < 7; v++)
        {
            float pitch = 95f + (float)rng.NextDouble() * 140f;
            float syll = 4f + (float)rng.NextDouble() * 2f;
            float pan = 0.5f + 0.5f * (float)rng.NextDouble();
            var breath = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
            float phase = 0f, f1 = 500f, f2 = 1500f;
            double phraseEnd = rng.NextDouble() * 2, pauseEnd = 0;
            float lp1 = 0, bp1 = 0, lp2 = 0, bp2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)R;
                // phrases of 1-3 s, pauses of 0.3-1.5 s
                if (t > phraseEnd)
                {
                    pauseEnd = t + 0.3 + rng.NextDouble() * 1.2;
                    phraseEnd = pauseEnd + 1 + rng.NextDouble() * 2;
                }
                float on = t < pauseEnd ? 0f : 1f;
                float sy = MathF.Max(0, MathF.Sin(MathF.Tau * syll * (float)t + v));
                // a new vowel each syllable: the formants glide
                if (i % (R / 8) == 0) (f1, f2) = (300f + 500f * (float)rng.NextDouble(), 900f + 1500f * (float)rng.NextDouble());
                phase += (pitch * (1f + 0.08f * MathF.Sin(MathF.Tau * 1.3f * (float)t + v))) / R;
                phase -= MathF.Floor(phase);
                float buzz = (2f * phase - 1f) * 0.6f + breath[i] * 0.4f;
                // two state-variable band passes: the vowel
                (lp1, bp1) = Svf(buzz, f1, lp1, bp1);
                (lp2, bp2) = Svf(buzz, f2, lp2, bp2);
                s[i] += (bp1 + 0.6f * bp2) * sy * sy * on * pan;
            }
        }
        // a room: the highs taken off, a little blur
        return Dsp.LowPass(s, Dsp.Coef(2600f));
    });

    /// <summary>Forks on plates, a glass set down: bright inharmonic clinks and a few low knocks, a dozen in a loop.</summary>
    public static AudioStreamWav Cutlery => _cutlery ??= Dsp.Loop(6f, 12442, (rng, n) =>
    {
        var s = new float[n];
        for (int k = 0; k < 14; k++)
        {
            float at = (float)rng.NextDouble() * n / R;
            if (rng.NextDouble() < 0.75) Ring(s, at, 2600f + 2400f * (float)rng.NextDouble(), 0.3f + 0.4f * (float)rng.NextDouble(), 55f);
            else Ring(s, at, 700f + 400f * (float)rng.NextDouble(), 0.4f, 70f);
        }
        return s;
    });

    /// <summary>A popcorn machine: kernels popping over the kettle's hum.</summary>
    public static AudioStreamWav Popcorn => _popcorn ??= Dsp.Loop(4f, 12443, (rng, n) =>
    {
        var s = new float[n];
        var hum = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(200f));
        for (int i = 0; i < n; i++) s[i] = hum[i] * 0.6f + MathF.Sin(MathF.Tau * 100f * i / R) * 0.04f;
        var crack = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(800f), Dsp.Coef(3000f));
        for (int k = 0; k < 26; k++)
        {
            int from = (int)(rng.NextDouble() * (n - R / 20));
            float amp = 0.5f + 0.8f * (float)rng.NextDouble();
            for (int i = 0; i < R / 25; i++) s[from + i] += crack[from + i] * amp * 4f * MathF.Exp(-i / (R * 0.006f));
        }
        return s;
    });

    /// <summary>Table football: rods slammed, the ball knocked about the box, a rattle.</summary>
    public static AudioStreamWav Foosball => _foosball ??= Dsp.Loop(3f, 12444, (rng, n) =>
    {
        var s = new float[n];
        for (int k = 0; k < 16; k++)
            Ring(s, (float)rng.NextDouble() * n / R, 300f + 500f * (float)rng.NextDouble(), 0.4f + 0.6f * (float)rng.NextDouble(), 45f);
        var roll = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(150f), Dsp.Coef(600f));
        for (int i = 0; i < n; i++) s[i] += roll[i] * 0.5f;
        return s;
    });

    /// <summary>A coffee grinder: the burrs' buzz rising to speed, the beans crackling, winding down.</summary>
    public static AudioStreamWav Grinder => _grinder ??= Quiet(Dsp.OneShot(3f, 12445, (rng, n) =>
    {
        var s = new float[n];
        var grit = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1500f), Dsp.Coef(5000f));
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)R, up = MathF.Min(1, t * 4) * MathF.Min(1, (3f - t) * 3);
            phase += (190f * (0.7f + 0.3f * up)) / R;
            phase -= MathF.Floor(phase);
            s[i] = ((2 * phase - 1) * 0.35f + grit[i] * (0.6f + 0.4f * MathF.Sin(MathF.Tau * 23 * t))) * up;
        }
        return s;
    }));

    /// <summary>The milk steamer: a hiss with the jug's gurgle under it.</summary>
    public static AudioStreamWav Steamer => _steamer ??= Quiet(Dsp.OneShot(2.5f, 12446, (rng, n) =>
    {
        var s = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(3000f));
        var gurgle = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(500f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)R, env = MathF.Min(1, t * 6) * MathF.Min(1, (2.5f - t) * 4);
            s[i] = (s[i] * 0.7f + gurgle[i] * 1.5f * (0.5f + 0.5f * MathF.Sin(MathF.Tau * 9 * t))) * env;
        }
        return s;
    }));

    /// <summary>A beer pulled at the tap: the hiss of the first gush, the pour filling up.</summary>
    public static AudioStreamWav Tap => _tap ??= Quiet(Dsp.OneShot(3f, 12447, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(400f), Dsp.Coef(4000f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)R, env = MathF.Min(1, t * 10) * MathF.Min(1, (3f - t) * 3);
            // the pitch of the pour rises as the glass fills: a band that slides
            float fill = 0.6f + 0.4f * MathF.Sin(MathF.Tau * (6 + 3 * t) * t);
            s[i] *= env * fill;
        }
        return s;
    }));

    /// <summary>A settee's cheer after a strike: a few voices on an open "ah", up and away.</summary>
    public static AudioStreamWav Cheer => _cheer ??= Quiet(Dsp.OneShot(1.8f, 12448, (rng, n) =>
    {
        var s = new float[n];
        for (int v = 0; v < 5; v++)
        {
            float pitch = 160f + 180f * (float)rng.NextDouble(), phase = 0, lp1 = 0, bp1 = 0, lp2 = 0, bp2 = 0;
            float delay = 0.08f * (float)rng.NextDouble();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)R - delay;
                if (t < 0) continue;
                float env = MathF.Min(1, t * 8) * MathF.Exp(-t * 1.6f);
                phase += pitch * (1 + 0.25f * MathF.Min(1, t * 3)) / R;
                phase -= MathF.Floor(phase);
                float buzz = 2 * phase - 1;
                (lp1, bp1) = Svf(buzz, 800f, lp1, bp1);
                (lp2, bp2) = Svf(buzz, 1250f, lp2, bp2);
                s[i] += (bp1 + 0.7f * bp2) * env;
            }
        }
        return s;
    }));

    /// <summary>An arcade cabinet's attract jingle: a square-wave arpeggio up a chord, three of them.</summary>
    public static SfxBank Jingles => _jingles ??= SfxBank.Build("Arcade jingles", 3, 1.2f, 12450, (rng, n) =>
    {
        var s = new float[n];
        float root = 220f * MathF.Pow(2, rng.Next(0, 7) / 12f);
        int[] steps = rng.Next(2) == 0 ? new[] { 0, 4, 7, 12, 16, 12, 7, 19 } : new[] { 0, 3, 7, 10, 12, 15, 19, 24 };
        int per = n / steps.Length;
        float phase = 0;
        for (int k = 0; k < steps.Length; k++)
        {
            float f = root * MathF.Pow(2, steps[k] / 12f);
            for (int i = 0; i < per; i++)
            {
                phase += f / R;
                phase -= MathF.Floor(phase);
                s[k * per + i] = (phase < 0.5f ? 0.3f : -0.3f) * MathF.Exp(-i / (float)per * 2f);
            }
        }
        return s;
    });

    /// <summary>A coin into a slot: two or three bright metal clinks and a rattle down the chute.</summary>
    public static SfxBank Coins => _coins ??= SfxBank.Build("Coin drops", 3, 0.6f, 12451, (rng, n) =>
    {
        var s = new float[n];
        float at = 0;
        for (int k = 0; k < 2 + rng.Next(2); k++)
        {
            Ring(s, at, 3200f + 1800f * (float)rng.NextDouble(), 0.8f * MathF.Exp(-k * 0.5f), 35f);
            at += 0.06f + 0.05f * (float)rng.NextDouble();
        }
        var rattle = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1500f), Dsp.Coef(4000f));
        for (int i = (int)(at * R); i < n; i++) s[i] += rattle[i] * 0.4f * MathF.Exp(-(i / (float)R - at) * 9f);
        return s;
    });

    /// <summary>A pool shot: the cue's tip, a beat later the balls' hard click.</summary>
    public static SfxBank Pool => _pool ??= SfxBank.Build("Pool shots", 3, 0.5f, 12452, (rng, n) =>
    {
        var s = new float[n];
        Ring(s, 0f, 900f, 0.4f, 80f);
        Ring(s, 0.08f + 0.1f * (float)rng.NextDouble(), 2600f + 900f * (float)rng.NextDouble(), 1f, 90f);
        if (rng.Next(2) == 0) Ring(s, 0.3f, 2400f, 0.45f, 90f);
        return s;
    });

    /// <summary>A dart into the board: a dull thud with the sisal's short tick.</summary>
    public static SfxBank Darts => _darts ??= SfxBank.Build("Dart hits", 3, 0.3f, 12453, (rng, n) =>
    {
        var s = new float[n];
        Ring(s, 0f, 170f + 40f * (float)rng.NextDouble(), 1f, 40f);
        Ring(s, 0f, 1900f, 0.25f, 120f);
        return s;
    });

    /// <summary>A single sound as a bank's pick: its own pitch and level.</summary>
    public static (AudioStreamWav Stream, float Pitch, float VolumeDb) Unit(AudioStreamWav w) => (w, 1f, 0f);

    /// <summary>A struck thing ringing at <paramref name="f"/> Hz (an inharmonic partial over it), from <paramref name="at"/> s.</summary>
    private static void Ring(float[] s, float at, float f, float amp, float decay)
    {
        int from = (int)(at * R);
        for (int i = Math.Max(0, from); i < s.Length; i++)
        {
            float t = (i - from) / (float)R;
            float env = MathF.Exp(-t * decay) * MathF.Min(1, t * 4000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += (MathF.Sin(MathF.Tau * f * t) + 0.4f * MathF.Sin(MathF.Tau * f * 2.76f * t)) * env * amp;
        }
    }

    /// <summary>One step of a state-variable filter's band pass at <paramref name="f"/> Hz (Q about 3).</summary>
    private static (float Low, float Band) Svf(float x, float f, float low, float band)
    {
        float k = 2f * MathF.Sin(MathF.PI * f / R);
        low += k * band;
        float high = x - low - 0.35f * band;
        band += k * high;
        return (low, band);
    }

    /// <summary>A one-shot that does not loop.</summary>
    private static AudioStreamWav Quiet(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
