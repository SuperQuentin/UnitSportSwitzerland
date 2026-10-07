using Godot;
using UnitSport.Audio.Cd;

namespace UnitSport.Items;

/// <summary>
/// Where a playing CD is in its music right now (#725), from the shared clock and the analysis
/// burnt with it (<see cref="CdAnalysis"/>): the beat, the bar, how loud it is, its kicks and the
/// section it is in. The same on every screen with nothing on the wire, like the beat itself.
/// What the sparkles, the bounce and the panel's cassette follow; later the dancers too.
/// A CD burnt before the analysis (or still being re-analysed) falls back to the beat grid.
/// </summary>
public readonly struct RadioGroove
{
    /// <summary>The CD is known here, has a tempo and has not ended.</summary>
    public bool Beating { get; init; }
    /// <summary>0..1 within the beat.</summary>
    public float Phase { get; init; }
    public int Beat { get; init; }
    /// <summary>0 on a bar's first beat .. 3.</summary>
    public int BeatInBar { get; init; }
    /// <summary>The song's loudness now, 0..1 (0.8 without an envelope).</summary>
    public float Level { get; init; }
    /// <summary>The hit now, 0..1: the low-band onsets, else a decay from each beat.</summary>
    public float Kick { get; init; }
    /// <summary>1 on a bar's first beat, decaying through it; 0 on the other three.</summary>
    public float BarKick { get; init; }
    /// <summary>The section's size, 0 calm .. 1 chorus.</summary>
    public float Peak { get; init; }
    /// <summary>1 as a new section starts, decaying over about a second and a half.</summary>
    public float Burst { get; init; }
    /// <summary>The CD has its loudness envelope (not just the beat grid).</summary>
    public bool HasEnvelope { get; init; }

    public static readonly RadioGroove Silent = new() { Level = 0f, Peak = 0.5f };

    /// <summary>The groove of CD <paramref name="cdId"/> started at <paramref name="startedAt"/>, at <paramref name="serverNow"/>.</summary>
    public static RadioGroove Of(int cdId, double startedAt, double serverNow)
    {
        if (!RadioBody.BeatOf(cdId, startedAt, serverNow, out float phase, out int beat, out _, out _)
            || CdLibrary.Instance?.Find(cdId) is not { } cd)
            return Silent;
        double t = serverNow - startedAt;
        int inBar = ((beat % 4) + 4) % 4;   // BeatOf already counts from the bar's one (#728)
        float beatKick = Mathf.Exp(-phase * 6f);
        bool env = CdAnalysisRuntime.Sample(cd, t, out float level, out float kick);
        var kind = CdAnalysisRuntime.SectionAt(cd, t, out int section);
        float burst = 0f;
        if (section > 0 && cd.Analysis is { SectionStarts: { } starts } && section < starts.Length)
        {
            double since = t - starts[section];
            if (since >= 0 && since < 2.0) burst = Mathf.Exp(-(float)since * 2f);
        }
        return new RadioGroove
        {
            Beating = true,
            Phase = phase,
            Beat = beat,
            BeatInBar = inBar,
            HasEnvelope = env,
            Level = env ? level : 0.8f,
            // the onsets carry the feel; the grid keeps a little pulse under them so it never goes slack
            Kick = env ? Mathf.Max(kick, 0.35f * beatKick * level) : beatKick,
            BarKick = inBar == 0 ? Mathf.Exp(-phase * 5f) : 0f,
            Peak = kind switch { SectionKind.Calm => 0.1f, SectionKind.Peak => 0.85f, SectionKind.Chorus => 1f, _ => 0.5f },
            Burst = burst,
        };
    }

    /// <summary>How hard a radio bounces to it: softer in quiet parts, harder on a bar's first beat and in a chorus.</summary>
    public float BounceScale => HasEnvelope
        ? (0.5f + 0.5f * Level + 0.25f * Peak) * (1f + 0.35f * BarKick)
        : 1f + 0.25f * BarKick;
}
