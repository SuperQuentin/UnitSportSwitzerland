// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-alphorn.md).

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>The four voices of a song: the melody, a consonant voice below it, the drone, and the yodel.</summary>
public enum HornPart { Melody, Third, Drone, Yodel }

/// <summary>One note of a part: a natural-tone <paramref name="Partial"/> (2-12 of the horn's fundamental), the beat it starts on and its length in beats.</summary>
public readonly record struct HornNote(int Partial, double Start, double Beats)
{
    public double End => Start + Beats;
}

/// <summary>
/// A song of the alphorn game (#1269): a seeded chart of 16-32 notes at 60-76 BPM, and the other three voices derived from the melody.
/// Every number is a pure function of the seed, so the server, every client and a bot see the same song with nothing sent but the seed.
/// A bar of <see cref="LeadBeats"/> counts the player in before the first note.
/// </summary>
public sealed class AlphornChart
{
    /// <summary>Beats of count-in before the first note.</summary>
    public const int LeadBeats = 4;
    /// <summary>The partials a melody walks on: the notes an alphorn plays, with the eleventh, the "Alphorn-Fa".</summary>
    public static readonly int[] Ladder = [4, 5, 6, 8, 9, 10, 11, 12];
    /// <summary>The tonic: the partial every melody ends on.</summary>
    public const int Home = 8;

    public int Seed { get; }
    public double Bpm { get; }
    public double SecondsPerBeat => 60.0 / Bpm;
    public HornNote[] Melody { get; }
    /// <summary>The beat the last note ends on.</summary>
    public double Beats { get; }
    /// <summary>The song's length in seconds, count-in included.</summary>
    public double Seconds => Beats * SecondsPerBeat;

    private HornNote[]? _third, _drone, _yodel;

    private AlphornChart(int seed, double bpm, HornNote[] melody)
    {
        Seed = seed;
        Bpm = bpm;
        Melody = melody;
        Beats = melody[^1].End + 1;
    }

    public static AlphornChart Make(int seed)
    {
        var rng = new Random(seed);
        double bpm = 60 + rng.Next(0, 17);
        int count = 16 + rng.Next(0, 17);
        var notes = new HornNote[count];
        int at = Array.IndexOf(Ladder, Home);
        double beat = LeadBeats;
        int[] lengths = [1, 2, 2, 3, 4];
        for (int i = 0; i < count; i++)
        {
            at = Math.Clamp(at + rng.Next(-2, 3), 0, Ladder.Length - 1);
            int partial = Ladder[at];
            double len = lengths[rng.Next(lengths.Length)];
            if (i == count - 1) { partial = Home; len = 4; }
            notes[i] = new HornNote(partial, beat, len);
            // a rest now and then: the breath refills in rests
            beat += len + (len >= 3 ? 2 : 1);   // a rest long enough that the breath always refills
        }
        return new AlphornChart(seed, bpm, notes);
    }

    public HornNote[] Of(HornPart part) => part switch
    {
        HornPart.Melody => Melody,
        HornPart.Third => _third ??= MakeThird(),
        HornPart.Drone => _drone ??= MakeDrone(),
        _ => _yodel ??= MakeYodel(),
    };

    /// <summary>The voice under the melody: for each note the lower partial that sounds best with it (a third, a fifth, a sixth).</summary>
    private HornNote[] MakeThird()
    {
        var r = new HornNote[Melody.Length];
        for (int i = 0; i < r.Length; i++)
        {
            var m = Melody[i];
            int best = 2, bestScore = int.MinValue;
            for (int q = 2; q < m.Partial; q++)
            {
                int s = AlphornRules.Interval(q, m.Partial) * 10 + q;   // the nearest below among the equally good
                if (s > bestScore) { bestScore = s; best = q; }
            }
            r[i] = new HornNote(best, m.Start, m.Beats);
        }
        return r;
    }

    /// <summary>The drone: two beats and a breath of two, a bar at a time, on the partial 2-4 that sits best under the melody of that bar.</summary>
    private HornNote[] MakeDrone()
    {
        var list = new List<HornNote>();
        for (double bar = LeadBeats; bar + 2 <= Beats; bar += 4)
        {
            int best = 2, bestScore = int.MinValue;
            for (int q = 2; q <= 4; q++)
            {
                int s = 0;
                foreach (var m in Melody)
                    if (m.Start < bar + 4 && m.End > bar) s += AlphornRules.Interval(q, m.Partial);
                if (s > bestScore) { bestScore = s; best = q; }
            }
            list.Add(new HornNote(best, bar, 2));
        }
        return [.. list];
    }

    /// <summary>The yodel: a tap on every other melody note's first beat, the chest voice (3-5) and the head voice (6-10) taking turns.</summary>
    private HornNote[] MakeYodel()
    {
        var list = new List<HornNote>();
        for (int i = 0; i < Melody.Length; i += 2)
        {
            var m = Melody[i];
            bool head = (i / 2) % 2 == 1;
            int best = head ? 6 : 3, bestScore = int.MinValue;
            for (int q = head ? 6 : 3; q <= (head ? 10 : 5); q++)
            {
                int s = AlphornRules.Interval(q, m.Partial);
                if (s > bestScore) { bestScore = s; best = q; }
            }
            list.Add(new HornNote(best, m.Start, 1));
        }
        return [.. list];
    }

    /// <summary>The distinct partials a part uses, lowest first: the lanes the player chooses between.</summary>
    public int[] Lanes(HornPart part) => Of(part).Select(n => n.Partial).Distinct().Order().ToArray();
}
