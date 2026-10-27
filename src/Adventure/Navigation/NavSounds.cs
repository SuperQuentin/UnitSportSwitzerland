using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The navigation plan's synthesised sounds (#1226), each a static <see cref="AudioStreamWav"/> built once on first use,
/// so the sound player (<c>--sounds</c>) lists them by itself: the guiding gust and its chime, and the arrival
/// curtain's four chimes (the SBB three-note gong, the PostAuto's three-tone horn C♯-E-A, a ship's bell, a ropeway's
/// bell).
/// </summary>
public static class NavSounds
{
    private static AudioStreamWav? _gust, _guideChime, _sbb, _postauto, _shipBell, _ropewayBell;

    /// <summary>A warm Föhn gust: band-passed noise swelling over a second and dying in three.</summary>
    public static AudioStreamWav Gust => _gust ??= Dsp.OneShot(4.2f, 1226, (rng, n) =>
    {
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), 180f, 1400f);
        var s = new float[n];
        float wobble = (float)rng.NextDouble() * Mathf.Tau;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = t < 1.1f ? Mathf.SmoothStep(0f, 1f, t / 1.1f) : Mathf.Exp(-(t - 1.1f) * 1.1f);
            float gusting = 0.75f + 0.25f * Mathf.Sin(t * 2.3f + wobble) * Mathf.Sin(t * 5.1f);
            s[i] = noise[i] * env * gusting;
        }
        return s;
    });

    /// <summary>A soft two-note wind chime when the gust starts.</summary>
    public static AudioStreamWav GuideChime => _guideChime ??= Dsp.OneShot(1.6f, 1227, (_, n) => Bells(n, new[] { (1318.5f, 0f), (1760f, 0.12f) }, 2.6f));

    /// <summary>The SBB station gong: three bell notes rising (the familiar "ding-dang-dong").</summary>
    public static AudioStreamWav SbbChime => _sbb ??= Dsp.OneShot(2.4f, 1228, (_, n) => Bells(n, new[] { (659.3f, 0f), (784f, 0.42f), (1046.5f, 0.84f) }, 1.8f));

    /// <summary>The PostAuto's three-tone horn, C♯5-E5-A4 (from Rossini's William Tell), reedy and loud.</summary>
    public static AudioStreamWav PostautoHorn => _postauto ??= Dsp.OneShot(2.1f, 1229, (_, n) =>
    {
        var s = new float[n];
        (float Hz, float From, float To)[] notes = { (554.4f, 0f, 0.55f), (659.3f, 0.6f, 1.15f), (440f, 1.2f, 2.0f) };
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            foreach (var (hz, from, to) in notes)
            {
                if (t < from || t > to) continue;
                float lt = t - from, env = Mathf.Min(1f, lt * 40f) * Mathf.Min(1f, (to - t) * 30f);
                // three horns a little apart, odd harmonics: the brass of a coach's horn
                for (int h = 1; h <= 7; h += 2)
                    s[i] += env * Mathf.Sin(Mathf.Tau * hz * h * t) / h * (h == 1 ? 1f : 0.55f);
            }
        }
        return s;
    });

    /// <summary>A ship's bell: two strokes, inharmonic partials.</summary>
    public static AudioStreamWav ShipBell => _shipBell ??= Dsp.OneShot(2.8f, 1230, (_, n) => Bells(n, new[] { (523.3f, 0f), (523.3f, 0.5f) }, 1.4f, inharmonic: true));

    /// <summary>A ropeway's departure bell: a quick electric ring.</summary>
    public static AudioStreamWav RopewayBell => _ropewayBell ??= Dsp.OneShot(1.4f, 1231, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float strike = Mathf.Abs(Mathf.Sin(Mathf.Pi * 18f * t));
            float env = t < 0.9f ? 1f : Mathf.Exp(-(t - 0.9f) * 9f);
            s[i] = env * strike * (Mathf.Sin(Mathf.Tau * 1975f * t) * 0.6f + Mathf.Sin(Mathf.Tau * 2960f * t) * 0.3f);
        }
        return s;
    });

    /// <summary>The arrival chime of a kind of travel point.</summary>
    public static AudioStreamWav ChimeOf(TravelKind kind) => kind switch
    {
        TravelKind.Bus => PostautoHorn,
        TravelKind.Ship => ShipBell,
        TravelKind.CableCar => RopewayBell,
        _ => SbbChime,
    };

    /// <summary>Struck bells: each (frequency, start) decays at <paramref name="decay"/> per second.</summary>
    private static float[] Bells(int n, (float Hz, float At)[] notes, float decay, bool inharmonic = false)
    {
        var s = new float[n];
        float[] partials = inharmonic ? new[] { 1f, 2.76f, 5.4f } : new[] { 1f, 2f, 3f };
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            foreach (var (hz, at) in notes)
            {
                if (t < at) continue;
                float lt = t - at, env = Mathf.Min(1f, lt * 600f) * Mathf.Exp(-lt * decay);
                for (int p = 0; p < partials.Length; p++)
                    s[i] += env * Mathf.Sin(Mathf.Tau * hz * partials[p] * lt) * (p == 0 ? 1f : 0.35f / p) * Mathf.Exp(-lt * decay * p);
            }
        }
        return s;
    }
}
