// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fasnacht.md).

using System.Globalization;
using System.Text;
using UnitSport.Concert;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>The three voices of a Guggenmusik, in the order players sit down: the drum keeps the beat, the trumpet plays the stabs between, the sousaphone the oompah.</summary>
public enum GuggenPart { Drum = 0, Trumpet = 1, Sousaphone = 2 }

/// <summary>
/// One note of a part: when it is due (beats from the song's start, the count-in included) and what it sounds like (the drum: 0 snare, 1 bass drum; the
/// trumpet and the sousaphone: semitones above the instrument's own base note).
/// </summary>
public readonly record struct GuggenNote(double Beat, int Pitch);

/// <summary>How a tap landed on its note.</summary>
public enum GuggenGrade : byte { Miss, Good, Perfect }

/// <summary>What a part scored: <see cref="Points"/> of <see cref="Max"/>, which notes were hit (for the sync between the voices) and the taps that hit nothing.</summary>
public sealed record GuggenScore(GuggenPart Part, int Points, int Max, int Hit, int Notes, int Perfects, int Extra, bool[] Matched)
{
    public double Accuracy => Max == 0 ? 0 : (double)Points / Max;
}

/// <summary>
/// A Guggenmusik song (#1357), a pure function of its seed: 120-140 BPM, four beats of count-in, eight bars in four-four. Three parts on the alphorn game's
/// rhythm engine (the pogo's timing windows, <see cref="PogoBeat"/>), one tap per note: no pitch to pick, the part's notes sound as they are tapped. A
/// Guggenmusik is loud and together, not consonant, so what is scored is how many voices land the same beats (<see cref="GuggenRules.Sync"/>).
/// </summary>
public sealed class GuggenChart
{
    public const int Bars = 8, BeatsPerBar = 4, LeadBeats = 4, MinBpm = 120, MaxBpm = 140;

    public int Seed { get; }

    public double Bpm { get; }

    public double SecondsPerBeat => 60.0 / Bpm;

    /// <summary>The whole song, count-in to the last bar's end, s.</summary>
    public double Seconds => (LeadBeats + Bars * BeatsPerBar) * SecondsPerBeat;

    /// <summary>The chord root of each bar in semitones over the key (the tune's I-IV-V turns).</summary>
    public int[] Roots { get; }

    private readonly GuggenNote[][] _parts;

    private GuggenChart(int seed)
    {
        Seed = seed;
        var rng = new Random(seed);
        Bpm = MinBpm + rng.Next(MaxBpm - MinBpm + 1);
        int[][] turns = [[0, 5, 7, 0], [0, 7, 5, 0], [0, 5, 0, 7], [0, 0, 5, 7]];
        var turn = turns[rng.Next(turns.Length)];
        Roots = new int[Bars];
        for (int b = 0; b < Bars; b++) Roots[b] = turn[b / 2 % turn.Length];
        var drum = new List<GuggenNote>();
        var trumpet = new List<GuggenNote>();
        var sousa = new List<GuggenNote>();
        int[] scale = [0, 4, 7, 9, 12];
        for (int b = 0; b < Bars; b++)
        {
            double at = LeadBeats + b * BeatsPerBar;
            for (int k = 0; k < BeatsPerBar; k++) drum.Add(new GuggenNote(at + k, k % 2 == 0 ? 1 : 0));
            drum.Add(new GuggenNote(at + (rng.Next(2) == 0 ? 1.5 : 3.5), 0));
            sousa.Add(new GuggenNote(at, Roots[b] - 12));
            sousa.Add(new GuggenNote(at + 2, Roots[b] - 12 + 7));
            // the trumpet's stabs on the off-beats: always the "and" of 1 and 3, now and then the "and" of 2 or 4
            trumpet.Add(new GuggenNote(at + 0.5, Roots[b] + scale[rng.Next(scale.Length)]));
            if (rng.Next(2) == 0) trumpet.Add(new GuggenNote(at + 1.5, Roots[b] + scale[rng.Next(scale.Length)]));
            trumpet.Add(new GuggenNote(at + 2.5, Roots[b] + scale[rng.Next(scale.Length)]));
            if (rng.Next(2) == 0) trumpet.Add(new GuggenNote(at + 3.5, Roots[b] + scale[rng.Next(scale.Length)]));
        }
        drum.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        _parts = [[.. drum], [.. trumpet], [.. sousa]];
    }

    public static GuggenChart Make(int seed) => new(seed);

    public GuggenNote[] Of(GuggenPart part) => _parts[(int)part];

    /// <summary>The note's due time in seconds from the song's start.</summary>
    public double TimeOf(GuggenNote note) => note.Beat * SecondsPerBeat;
}

/// <summary>The rules of the Guggenmusik game (#1357), pure: grading a part's taps, the sync between voices, pay, a bot's voice.</summary>
public static class GuggenRules
{
    public const int MaxVoices = 3;

    /// <summary>A perfect tap and a good one are worth this, a tap that hits nothing costs <see cref="ExtraCost"/>.</summary>
    public const int PerfectPoints = 10, GoodPoints = 6, ExtraCost = 3;

    /// <summary>The best din on every voice's score: all voices on the same beats at full strength.</summary>
    public const double MaxDin = 1.6;

    public const int BatzenPerSong = 20, BatzenMax = 32, DayCap = 80;

    public static GuggenGrade GradeOff(double offSeconds, double secondsPerBeat)
    {
        double w = PogoBeat.Window(secondsPerBeat), off = Math.Abs(offSeconds);
        return off > w ? GuggenGrade.Miss : off <= Math.Min(PogoBeat.PerfectSeconds, w * 0.5) ? GuggenGrade.Perfect : GuggenGrade.Good;
    }

    /// <summary>
    /// A part's score for <paramref name="taps"/> (seconds from the song's start): each tap takes the nearest note nothing earlier took, if it is inside the
    /// good window (10 perfect, 6 good); a tap that takes none is an extra and costs 3 (so holding the key down scores nothing). The most it can be is 10 a note.
    /// </summary>
    public static GuggenScore Grade(GuggenChart chart, GuggenPart part, IReadOnlyList<double> taps)
    {
        var notes = chart.Of(part);
        double spb = chart.SecondsPerBeat;
        var taken = new bool[notes.Length];
        int points = 0, hit = 0, perfects = 0, extra = 0;
        foreach (double t in taps.OrderBy(x => x))
        {
            int best = -1;
            double bestOff = double.MaxValue;
            for (int i = 0; i < notes.Length; i++)
            {
                if (taken[i]) continue;
                double off = Math.Abs(t - chart.TimeOf(notes[i]));
                if (off < bestOff) { best = i; bestOff = off; }
            }
            var g = best < 0 ? GuggenGrade.Miss : GradeOff(bestOff, spb);
            if (g == GuggenGrade.Miss) { extra++; continue; }
            taken[best] = true;
            hit++;
            if (g == GuggenGrade.Perfect) { points += PerfectPoints; perfects++; }
            else points += GoodPoints;
        }
        return new GuggenScore(part, Math.Max(0, points - extra * ExtraCost), notes.Length * PerfectPoints, hit, notes.Length, perfects, extra, taken);
    }

    /// <summary>
    /// How together the voices were, 0..1: of the beats on which two or more of the playing voices have notes, the share on which every one of those notes
    /// was hit. Chart-relative (each player runs their own clock), so nothing is aligned over the wire. Zero with one voice or no shared beat.
    /// </summary>
    public static double Sync(GuggenChart chart, IReadOnlyList<GuggenScore> scores)
    {
        if (scores.Count < 2) return 0;
        int eligible = 0, locked = 0;
        for (int beat = GuggenChart.LeadBeats; beat < GuggenChart.LeadBeats + GuggenChart.Bars * GuggenChart.BeatsPerBar; beat++)
        {
            int voices = 0;
            bool all = true;
            foreach (var s in scores)
            {
                var notes = chart.Of(s.Part);
                bool any = false;
                for (int i = 0; i < notes.Length; i++)
                {
                    if (Math.Floor(notes[i].Beat) != beat) continue;
                    any = true;
                    if (!s.Matched[i]) all = false;
                }
                if (any) voices++;
            }
            if (voices < 2) continue;
            eligible++;
            if (all) locked++;
        }
        return eligible == 0 ? 0 : (double)locked / eligible;
    }

    /// <summary>The multiplier on every voice's score: 1 alone, up to <see cref="MaxDin"/> with all three voices fully together (loud: more voices, tight: more sync).</summary>
    public static double Din(double sync, int voices) =>
        voices < 2 ? 1.0 : 1.0 + (MaxDin - 1.0) * Math.Clamp(sync, 0, 1) * (voices - 1) / (MaxVoices - 1);

    public static int Batzen(GuggenScore score, double din) =>
        score.Accuracy < 0.25 ? 0 : Math.Min(BatzenMax, (int)Math.Round(score.Accuracy * BatzenPerSong * din));

    public static int BoardPoints(GuggenScore score, double din) => (int)Math.Round(score.Points * din);

    /// <summary>What a bot taps for <paramref name="part"/>: every note with a timing error by <paramref name="skill"/> (0..1; 1 is within 25 ms), a missed one now and then at low skill.</summary>
    public static double[] BotTaps(GuggenChart chart, GuggenPart part, double skill, Random rng)
    {
        double jitter = 0.025 + (1 - Math.Clamp(skill, 0, 1)) * 0.12;
        var taps = new List<double>();
        foreach (var n in chart.Of(part))
        {
            if (rng.NextDouble() < (1 - skill) * 0.2) continue;
            taps.Add(Math.Max(0, chart.TimeOf(n) + (rng.NextDouble() * 2 - 1) * jitter));
        }
        return [.. taps];
    }
}

/// <summary>How a song's taps cross the wire: milliseconds from the song's start, comma-separated, invariant culture. Strict: a bad string is refused whole.</summary>
public static class GuggenWire
{
    public const int MaxTaps = 200, MaxChars = 2000;

    public static string Encode(IEnumerable<double> taps)
    {
        var sb = new StringBuilder();
        foreach (double t in taps.Take(MaxTaps))
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(((int)Math.Round(t * 1000)).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static double[]? Decode(string text)
    {
        if (text.Length > MaxChars) return null;
        if (text.Length == 0) return [];
        var list = new List<double>();
        foreach (string item in text.Split(','))
        {
            if (list.Count >= MaxTaps || !int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) || ms < 0 || ms > 600_000) return null;
            list.Add(ms / 1000.0);
        }
        return [.. list];
    }
}

/// <summary>A seat in a song: the chart's seed, the voice this player plays, and how many voices are in the song so far.</summary>
public readonly record struct GuggenSeat(int Seed, GuggenPart Part, int Voices);

/// <summary>What a finished song paid one voice: the score, how together the band was and the din multiplier (in hundredths), the Batzen granted and the board points.</summary>
public readonly record struct GuggenResult(GuggenPart Part, int Points, int Max, int Sync100, int Voices, int Din100, int Batzen, int BoardPoints);
