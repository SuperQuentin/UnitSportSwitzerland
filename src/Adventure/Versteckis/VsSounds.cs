using Godot;
using UnitSport.Audio;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// Versteckis's sounds (#1268), synthesised and in the sound player: the props' voices for Moo (a hay bale's rustle, a
/// woodpile's knock, a milk can's clank, a gnome's squeak, a bench's creak, a flower box's bees; the cow moos with the
/// herds' own voice), the poof of a disguise, the thumps of a disguised sprint, the church bell's toll (the ambience's
/// bell), the seeker's counting tick and a catch's "Gfunde!" chime. Static banks (the player lists them by themselves),
/// plus a set over <see cref="VsVoice"/> so a new voice shows by itself.
/// </summary>
public static class VsSounds
{
    private static float Env(float t, float attack, float decay) => t < attack ? t / attack : Mathf.Exp(-(t - attack) * decay);

    /// <summary>Dry hay pushed about: band-passed noise in three short bursts.</summary>
    public static SfxBank Rustle => _rustle ??= SfxBank.Build("vs rustle", 3, 0.9f, 12680, (rng, n) =>
    {
        var noise = BandPass(Noise(rng, n), Coef(1800f), Coef(7000f));
        var s = new float[n];
        for (int burst = 0; burst < 3; burst++)
        {
            float at = burst * 0.22f + (float)rng.NextDouble() * 0.05f;
            for (int i = (int)(at * Rate); i < n; i++) s[i] += noise[i] * Env(i / (float)Rate - at, 0.02f, 18f) * 0.7f;
        }
        return s;
    });

    /// <summary>Two knocks on a log: a hollow wooden resonance.</summary>
    public static SfxBank Knock => _knock ??= SfxBank.Build("vs knock", 3, 0.7f, 12681, (rng, n) => Strikes(rng, n, 2, 0.24f, 310f, 26f, 0.5f));

    /// <summary>A milk can's lid and side: a bright metallic clank.</summary>
    public static SfxBank Clank => _clank ??= SfxBank.Build("vs clank", 3, 0.9f, 12682, (rng, n) => Strikes(rng, n, 2, 0.18f, 1280f, 9f, 0.35f, metal: true));

    /// <summary>A rubber gnome's squeak: a quick rising whistle.</summary>
    public static SfxBank Squeak => _squeak ??= SfxBank.Build("vs squeak", 3, 0.45f, 12683, (rng, n) =>
    {
        var s = new float[n];
        double phase = 0;
        float f0 = 1400f * (0.9f + (float)rng.NextDouble() * 0.2f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            phase += Mathf.Tau * (f0 + 1600f * t) / Rate;
            s[i] = Mathf.Sin((float)phase) * Env(t, 0.03f, 9f) * 0.5f;
        }
        return s;
    });

    /// <summary>A bench's slats creaking: a slow rasp of clicks under a woody tone.</summary>
    public static SfxBank Creak => _creak ??= SfxBank.Build("vs creak", 3, 1.0f, 12684, (rng, n) =>
    {
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float rate = 32f + 18f * Mathf.Sin(t * 5f);
            phase += Mathf.Tau * rate / Rate;
            float click = Mathf.Pow(Mathf.Max(0f, Mathf.Sin((float)phase)), 30f);
            s[i] = (click * 0.8f + Mathf.Sin(Mathf.Tau * 180f * t) * 0.15f) * Env(t, 0.08f, 3f);
        }
        return s;
    });

    /// <summary>Bees round a flower box: a buzzing swarm swelling and fading.</summary>
    public static SfxBank Buzz => _buzz ??= SfxBank.Build("vs buzz", 2, 1.6f, 12685, (rng, n) =>
    {
        var s = new float[n];
        var phases = new double[4];
        var pitch = new float[4];
        for (int b = 0; b < 4; b++) pitch[b] = 210f + (float)rng.NextDouble() * 60f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float v = 0;
            for (int b = 0; b < 4; b++)
            {
                phases[b] += Mathf.Tau * pitch[b] * (1f + 0.03f * Mathf.Sin(t * (7f + b))) / Rate;
                v += Mathf.Sign(Mathf.Sin((float)phases[b])) * 0.12f;
            }
            s[i] = v * Mathf.Sin(Mathf.Pi * Mathf.Min(1f, t / 1.6f));
        }
        return LowPass(s, Coef(1600f));
    });

    /// <summary>A disguise put on or a decoy tagged: a soft puff of air with a little pop.</summary>
    public static SfxBank Poof => _poof ??= SfxBank.Build("vs poof", 3, 0.5f, 12686, (rng, n) =>
    {
        var noise = LowPass(Noise(rng, n), Coef(900f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            s[i] = noise[i] * Env(t, 0.01f, 9f) * 1.6f + Mathf.Sin(Mathf.Tau * 160f * t) * Env(t, 0.002f, 40f) * 0.5f;
        }
        return s;
    });

    /// <summary>A disguised sprint: heavy thumps, four a second.</summary>
    public static SfxBank Thump => _thump ??= SfxBank.Build("vs thump", 2, 1.0f, 12687, (rng, n) => Strikes(rng, n, 4, 0.25f, 70f, 22f, 0.9f));

    /// <summary>The church bell rung by a hider: the ambience's large bell (prime 230 Hz), a few seconds.</summary>
    public static AudioStreamWav Toll => _toll ??= Encode(Normalise(AmbienceDsp.ChurchBell(new Random(12688), 230f), 0.85f));
    private static AudioStreamWav? _toll;

    /// <summary>The seeker counting, one tick a second while blind.</summary>
    public static AudioStreamWav Tick => _tick ??= OneShot(0.12f, 12689, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = Mathf.Sin(Mathf.Tau * 880f * i / Rate) * Env(i / (float)Rate, 0.004f, 40f) * 0.4f;
        return s;
    });
    private static AudioStreamWav? _tick;

    /// <summary>"Gfunde!": a hider caught, two rising notes.</summary>
    public static AudioStreamWav Found => _found ??= OneShot(0.5f, 12690, (_, n) =>
    {
        var s = new float[n];
        foreach (var (at, hz) in new[] { (0f, 784f), (0.14f, 1175f) })
            for (int i = (int)(at * Rate); i < n; i++)
            {
                float t = i / (float)Rate - at;
                s[i] += Mathf.Sin(Mathf.Tau * hz * t) * Env(t, 0.005f, 9f) * 0.45f;
            }
        return s;
    });
    private static AudioStreamWav? _found;

    private static SfxBank? _rustle, _knock, _clank, _squeak, _creak, _buzz, _poof, _thump;

    /// <summary>Struck resonances: <paramref name="count"/> hits <paramref name="gap"/> s apart at <paramref name="hz"/>.</summary>
    private static float[] Strikes(Random rng, int n, int count, float gap, float hz, float decay, float gain, bool metal = false)
    {
        var s = new float[n];
        var noise = Noise(rng, n);
        for (int k = 0; k < count; k++)
        {
            float at = k * gap + (float)rng.NextDouble() * 0.02f, f = hz * (0.95f + (float)rng.NextDouble() * 0.1f);
            for (int i = (int)(at * Rate); i < n; i++)
            {
                float t = i / (float)Rate - at;
                float e = Mathf.Exp(-t * decay);
                float v = Mathf.Sin(Mathf.Tau * f * t) + (metal ? 0.6f * Mathf.Sin(Mathf.Tau * f * 2.76f * t) + 0.3f * Mathf.Sin(Mathf.Tau * f * 5.4f * t) : 0.3f * Mathf.Sin(Mathf.Tau * f * 1.5f * t));
                s[i] += (v * 0.5f + noise[i] * Mathf.Exp(-t * 90f)) * e * gain;
            }
        }
        return s;
    }

    /// <summary>A voice's bank (the cow's is the herds' moo); null for the bell, which is one stream.</summary>
    public static SfxBank? Of(VsVoice v) => v switch
    {
        VsVoice.Rustle => Rustle,
        VsVoice.Knock => Knock,
        VsVoice.Clank => Clank,
        VsVoice.Squeak => Squeak,
        VsVoice.Creak => Creak,
        VsVoice.Moo => CowSounds.Moo,
        VsVoice.Buzz => Buzz,
        VsVoice.Poof => Poof,
        VsVoice.Thump => Thump,
        _ => null,
    };

    /// <summary>The seven props' voices, by prop (the sound player's set: a new prop shows by itself).</summary>
    [SoundShowcase("Versteckis")]
    public static IEnumerable<(string Category, string Name, Func<float[]> Make)> Voices()
    {
        foreach (var p in VsProps.All)
        {
            var voice = p.Voice;
            yield return ("Versteckis", $"{p.Name}: {voice}", () => Samples(voice));
        }
    }

    private static float[] Samples(VsVoice v)
    {
        var stream = Of(v)?.Variants[0] ?? Toll;
        var data = stream.Data;
        var s = new float[data.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(data[2 * i] | data[2 * i + 1] << 8) / 32768f;
        return s;
    }
}
