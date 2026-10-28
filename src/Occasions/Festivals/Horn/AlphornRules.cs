// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-alphorn.md).

using UnitSport.Concert;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>How a note's start or end landed on its beat.</summary>
public enum HornGrade : byte { Miss, Good, Perfect }

/// <summary>One blow as the player played it: the partial chosen, and when it started and stopped, in seconds from the song's start.</summary>
public readonly record struct HornPlay(int Partial, double On, double Off);

/// <summary>What a part scored: <see cref="Points"/> of <see cref="Max"/>, the notes hit, and the plays as they sounded (breath-cut).</summary>
public sealed record HornScore(int Points, int Max, int Hit, int Notes, int Perfects, int Winded, HornPlay[] Sounded)
{
    public double Accuracy => Max == 0 ? 0 : (double)Points / Max;
}

/// <summary>
/// The rules of the alphorn game (#1269), pure: the horn's natural tones, how two sounding partials fit together, the timing windows
/// (the concert pogo's, <see cref="PogoBeat"/>), the breath bar, a part's grade and the harmony between voices. The server grades from
/// these; the client's screen uses the same code, so what it shows is what is paid.
/// </summary>
public static class AlphornRules
{
    /// <summary>The horn's fundamental, F-sharp 1, Hz. Only its overtones play: partials 2-12.</summary>
    public const double Fundamental = 46.2;
    public const int LowestPartial = 2, HighestPartial = 12;
    /// <summary>The breath bar: this many beats of blowing, refilled <see cref="RefillPerBeat"/> beats per beat of rest.</summary>
    public const double BreathBeats = 8, RefillPerBeat = 2;
    public const int MaxVoices = 4;
    /// <summary>The game's id in a <see cref="FestivalDef"/>.</summary>
    public const string GameId = "alphorn";
    /// <summary>The best harmony multiplier on every voice's score.</summary>
    public const double MaxHarmony = 1.6;

    public static double Frequency(int partial) => Fundamental * partial;

    public static bool IsPartial(int partial) => partial is >= LowestPartial and <= HighestPartial;

    /// <summary>
    /// How two partials sound together, from their ratio folded into one octave: unison and octave 3, the fifth (3:2), the fourth
    /// (4:3) and the major third (5:4) 2, the minor third and the sixths and the horn's own eleventh (11:8, the "Alphorn-Fa") 1, a
    /// second or a seventh -1, anything else 0.
    /// </summary>
    public static int Interval(int a, int b)
    {
        if (a <= 0 || b <= 0) return 0;
        int hi = Math.Max(a, b), lo = Math.Min(a, b);
        int g = Gcd(hi, lo);
        hi /= g;
        lo /= g;
        while (hi >= 2 * lo) lo *= 2;
        g = Gcd(hi, lo);
        hi /= g;
        lo /= g;
        return (hi, lo) switch
        {
            (1, 1) => 3,
            (3, 2) or (4, 3) or (5, 4) => 2,
            (6, 5) or (5, 3) or (8, 5) or (11, 8) => 1,
            _ => (double)hi / lo is < 1.2 or > 1.85 ? -1 : 0,
        };
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    // ---- timing -----------------------------------------------------------------------------------

    /// <summary>A press <paramref name="offSeconds"/> off its beat: the pogo's windows (Perfect 70 ms, Good 140 ms, capped at 0.4 beat).</summary>
    public static HornGrade GradeOff(double offSeconds, double secondsPerBeat)
    {
        double w = PogoBeat.Window(secondsPerBeat), off = Math.Abs(offSeconds);
        return off > w ? HornGrade.Miss : off <= Math.Min(PogoBeat.PerfectSeconds, w * 0.5) ? HornGrade.Perfect : HornGrade.Good;
    }

    public static int StartPoints(HornGrade g) => g switch { HornGrade.Perfect => 10, HornGrade.Good => 6, _ => 0 };
    public static int EndPoints(HornGrade g) => g switch { HornGrade.Perfect => 5, HornGrade.Good => 3, _ => 0 };
    /// <summary>The most one note is worth: a perfect start and a perfect end.</summary>
    public const int NoteMax = 15;
    /// <summary>A yodel tap is worth this much when perfect.</summary>
    public const int TapMax = 10;

    // ---- a part's grade ---------------------------------------------------------------------------

    /// <summary>
    /// Grades <paramref name="plays"/> against a part's notes. A blow counts for the note whose start is nearest that no earlier blow
    /// took, if it lands within the Good window; the right partial is required; a blow that runs out of breath ends early; the yodel is a
    /// tap (the partial is the chart's, the end is free). The returned <see cref="HornScore.Sounded"/> is what could be heard, breath-cut.
    /// </summary>
    public static HornScore Grade(AlphornChart chart, HornPart part, IEnumerable<HornPlay> plays)
    {
        double spb = chart.SecondsPerBeat;
        var notes = chart.Of(part);
        bool tap = part == HornPart.Yodel;
        var taken = new bool[notes.Length];
        var sounded = new List<HornPlay>();
        double breath = BreathBeats, lastOff = 0;
        int points = 0, hit = 0, perfects = 0, winded = 0;
        foreach (var play in plays.Where(p => p.Off >= p.On && IsPartial(p.Partial) && p.On >= 0).OrderBy(p => p.On).Take(notes.Length * 3 + 8))
        {
            breath = Math.Min(BreathBeats, breath + Math.Max(0, play.On - lastOff) / spb * RefillPerBeat);
            double hold = tap ? 1 : (play.Off - play.On) / spb;
            double off = play.Off;
            bool cut = false;
            if (!tap && hold > breath)
            {
                off = play.On + breath * spb;
                hold = breath;
                cut = true;
            }
            breath = Math.Max(0, breath - hold);
            lastOff = off;
            sounded.Add(play with { Off = off });

            int best = -1;
            double bestOff = double.MaxValue;
            for (int i = 0; i < notes.Length; i++)
            {
                double d = Math.Abs(play.On - notes[i].Start * spb);
                if (!taken[i] && d < bestOff) { best = i; bestOff = d; }
            }
            if (best < 0) continue;
            var start = GradeOff(play.On - notes[best].Start * spb, spb);
            if (start == HornGrade.Miss) continue;
            if (!tap && play.Partial != notes[best].Partial) continue;
            taken[best] = true;
            hit++;
            if (tap)
            {
                points += start == HornGrade.Perfect ? TapMax : 6;
                if (start == HornGrade.Perfect) perfects++;
                continue;
            }
            var end = cut ? HornGrade.Miss : GradeOff(off - notes[best].End * spb, spb);
            if (cut) winded++;
            points += StartPoints(start) + EndPoints(end);
            if (start == HornGrade.Perfect && end == HornGrade.Perfect) perfects++;
        }
        return new HornScore(points, notes.Length * (tap ? TapMax : NoteMax), hit, notes.Length, perfects, winded, [.. sounded]);
    }

    // ---- harmony ----------------------------------------------------------------------------------

    /// <summary>The partial sounding at <paramref name="seconds"/> from the song's start in <paramref name="sounded"/>, or 0.</summary>
    public static int SoundingAt(IReadOnlyList<HornPlay> sounded, double seconds)
    {
        foreach (var p in sounded)
            if (seconds >= p.On && seconds < p.Off) return p.Partial;
        return 0;
    }

    /// <summary>
    /// The harmony of voices playing the same chart, each as <see cref="HornScore.Sounded"/>: every half beat the song runs, each pair of
    /// sounding voices is scored by <see cref="Interval"/>; a mean of 2 or more is the full <see cref="MaxHarmony"/>, none or a clash is
    /// 1. Under eight half beats of two voices sounding together there is no harmony (1). A yodel tap sounds for a beat.
    /// </summary>
    public static double Harmony(AlphornChart chart, IReadOnlyList<IReadOnlyList<HornPlay>> voices)
    {
        if (voices.Count < 2) return 1;
        double step = chart.SecondsPerBeat / 2;
        int pairs = 0, sum = 0, together = 0;
        var sounding = new int[MaxVoices];
        for (double t = 0; t < chart.Seconds; t += step)
        {
            int n = 0;
            foreach (var v in voices)
            {
                int p = SoundingAt(v, t);
                if (p > 0 && n < MaxVoices) sounding[n++] = p;
            }
            if (n < 2) continue;
            together++;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++) { sum += Interval(sounding[i], sounding[j]); pairs++; }
        }
        if (together < 8 || pairs == 0) return 1;
        double mean = (double)sum / pairs;
        return 1 + (MaxHarmony - 1) * Math.Clamp(mean / 2, 0, 1);
    }

    // ---- pay ----------------------------------------------------------------------------------------

    /// <summary>The most Batzen one song pays at no harmony; harmony multiplies it.</summary>
    public const int BatzenPerSong = 20;
    /// <summary>The festival game's daily cap in Batzen (about four songs at full harmony).</summary>
    public const int DayCap = 80;

    /// <summary>A song's Batzen: its accuracy times <see cref="BatzenPerSong"/> times the harmony; nothing under a quarter of the points.</summary>
    public static int Batzen(HornScore score, double harmony) =>
        score.Accuracy < 0.25 ? 0 : (int)Math.Round(score.Accuracy * BatzenPerSong * harmony);

    /// <summary>The party-board points of a song: the score times the harmony.</summary>
    public static int BoardPoints(HornScore score, double harmony) => (int)Math.Round(score.Points * harmony);

    // ---- a bot's voice ------------------------------------------------------------------------------

    /// <summary>
    /// What a bot plays for <paramref name="part"/>: the chart's notes with a timing error by <paramref name="skill"/> (0..1; 1 is within
    /// 25 ms), and a missed note now and then at low skill. Through the same grading as a player's.
    /// </summary>
    public static HornPlay[] BotPlays(AlphornChart chart, HornPart part, double skill, Random rng)
    {
        double spb = chart.SecondsPerBeat, jitter = 0.025 + (1 - Math.Clamp(skill, 0, 1)) * 0.12;
        var list = new List<HornPlay>();
        foreach (var n in chart.Of(part))
        {
            if (rng.NextDouble() < (1 - skill) * 0.2) continue;
            double on = n.Start * spb + (rng.NextDouble() * 2 - 1) * jitter;
            double off = part == HornPart.Yodel ? on + spb : n.End * spb + (rng.NextDouble() * 2 - 1) * jitter;
            list.Add(new HornPlay(n.Partial, Math.Max(0, on), Math.Max(on, off) - 0.01));
        }
        return [.. list];
    }
}
