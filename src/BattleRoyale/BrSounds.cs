using Godot;
using UnitSport.Audio;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The match's stings (#231), synthesised like every other sound in the game: short two-voice chip
/// phrases (<see cref="ChipTune"/>) for the moments everyone should notice, and a heartbeat for
/// standing outside the zone. Original phrases; built once, on first use.
/// </summary>
public static class BrSounds
{
    private static AudioStreamWav? _doors, _closing, _final, _win, _out, _heart, _ping;

    private static AudioStreamWav Tune(ChipTune.Note[] lead, ChipTune.Note[] bass, float bpm) =>
        Dsp.Encode(Dsp.Normalise(ChipTune.Render(lead, bass, bpm), 0.8f));

    private static ChipTune.Note N(int midi, float beats) => ChipTune.N(midi, beats);

    /// <summary>The plane's doors open: two bright rising notes.</summary>
    public static AudioStreamWav Doors => _doors ??= Tune(
        new[] { N(72, 0.5f), N(79, 1.5f) },
        new[] { N(48, 2f) }, 170f);

    /// <summary>The zone starts to close: a falling minor arpeggio over a held bass.</summary>
    public static AudioStreamWav Closing => _closing ??= Tune(
        new[] { N(76, 0.5f), N(72, 0.5f), N(69, 0.5f), N(64, 1.5f) },
        new[] { N(45, 1.5f), N(40, 1.5f) }, 160f);

    /// <summary>The last circle closes: a fast trill, then a low held note.</summary>
    public static AudioStreamWav Final => _final ??= Tune(
        new[] { N(81, 0.25f), N(80, 0.25f), N(81, 0.25f), N(80, 0.25f), N(81, 0.25f), N(80, 0.25f), N(77, 2f) },
        new[] { N(41, 1.5f), N(40, 2f) }, 150f);

    /// <summary>The win: a short fanfare.</summary>
    public static AudioStreamWav Win => _win ??= Tune(
        new[] { N(67, 0.5f), N(72, 0.5f), N(76, 0.5f), N(79, 1f), N(76, 0.5f), N(79, 2.5f) },
        new[] { N(48, 1.5f), N(55, 1.5f), N(48, 2.5f) }, 150f);

    /// <summary>Out of the match: three falling notes.</summary>
    public static AudioStreamWav Out => _out ??= Tune(
        new[] { N(67, 0.75f), N(63, 0.75f), N(60, 2f) },
        new[] { N(43, 1.5f), N(36, 2f) }, 120f);

    /// <summary>One heartbeat: two low thumps, "lub-dub".</summary>
    public static AudioStreamWav Heart => _heart ??= Dsp.OneShot(0.5f, 61, (_, n) =>
    {
        var s = new float[n];
        foreach (var (start, hz, gain) in new[] { (0f, 52f, 1f), (0.17f, 46f, 0.7f) })
            for (int i = (int)(start * Dsp.Rate); i < n; i++)
            {
                float t = i / (float)Dsp.Rate - start;
                s[i] += Mathf.Sin(Mathf.Tau * hz * t) * Mathf.Exp(-t * 22f) * gain;
            }
        return s;
    });

    /// <summary>A team-mate's ping (#469): two quick high blips, a fifth apart.</summary>
    public static AudioStreamWav Ping => _ping ??= Dsp.OneShot(0.3f, 62, (_, n) =>
    {
        var s = new float[n];
        foreach (var (start, hz) in new[] { (0f, 1318.5f), (0.11f, 1975.5f) })
            for (int i = (int)(start * Dsp.Rate); i < n; i++)
            {
                float t = i / (float)Dsp.Rate - start;
                s[i] += Mathf.Sin(Mathf.Tau * hz * t) * Mathf.Exp(-t * 30f) * 0.5f;
            }
        return s;
    });

    /// <summary>All of them, for <c>--brcheck</c>: built, non-silent, short.</summary>
    public static IEnumerable<(string Name, AudioStreamWav Stream)> All() => new[]
    {
        ("doors", Doors), ("closing", Closing), ("final", Final), ("win", Win), ("out", Out), ("heart", Heart), ("ping", Ping),
    };
}
