// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fahnenschwingen.md).

namespace UnitSport.Occasions.Festivals.Fahnen;

public enum FahnenFigure : byte { Ufzug, Muehlirad, Achter, Lufthieb, Unterschwung, Hochwurf }

/// <summary>How a figure landed: Perfect and Good are on the beat, Off is near it, Miss is a wrong gesture or far from it.</summary>
public enum FahnenGrade : byte { Miss, Off, Good, Perfect }

/// <summary>A figure of a routine: it starts on <see cref="StartBeat"/> and must be done by <see cref="StartBeat"/> + <see cref="Beats"/>.</summary>
public readonly record struct FigureSlot(FahnenFigure Kind, int StartBeat, int Beats);

public sealed record FahnenRoutine(int Seed, FigureSlot[] Figures)
{
    /// <summary>The beat the last figure ends on (the count-in and the figures).</summary>
    public int TotalBeats => Figures.Length == 0 ? 0 : Figures[^1].StartBeat + Figures[^1].Beats;
}

/// <summary>What the player did in one figure: a gesture's finish and its sectors, or a throw's three stamps. Milliseconds from the routine's start.</summary>
public readonly record struct FigureInput(int A, int B, int C, byte[] Path);

/// <summary>The result of one throw; <see cref="Flight"/> is its beats in the air (0 when it was missed).</summary>
public readonly record struct FigureResult(FahnenGrade Grade, int Flight);

/// <summary>A graded routine: a grade per figure (those after a dropped flag are Misses), the score out of 1000.</summary>
public sealed record FahnenOutcome(FahnenGrade[] Grades, int Score, bool Dropped)
{
    public int Hits => Grades.Count(g => g >= FahnenGrade.Good);
}

/// <summary>
/// Fahnenschwingen, the pure half (#1269 part 5). A routine is 8 to 12 figures on a 96 BPM march planned from a seed alone
/// (so everyone who starts in the same slot swings the same routine and can be scored for being in step). Each gesture figure must be
/// drawn (<see cref="GestureRing"/>) and finished on its last beat; the Hochwurf is held and released on a beat, flies two to four beats
/// and is caught on the beat it lands. Stamps are seconds from the routine's start, graded on the server.
/// </summary>
public static class FahnenRules
{
    public const double Bpm = 96;
    public const double SecondsPerBeat = 60.0 / Bpm;
    /// <summary>Routines start on a multiple of this many beats of the festival clock, so players who ask together swing together.</summary>
    public const int SlotBeats = 8;
    public const double LeadSeconds = 3.0;
    /// <summary>The count-in, before the first figure.</summary>
    public const int CountInBeats = 4;
    public const int GestureBeats = 4, ThrowBeats = 8;
    /// <summary>Half windows round the beat, s.</summary>
    public const double PerfectWindow = 0.15, GoodWindow = 0.30, OffWindow = 0.50, ThrowPerfect = 0.12, ThrowGood = 0.25, ThrowOff = 0.40;
    public const double SyncBonus = 1.3;
    /// <summary>Share of the figures two players both land Good or better that counts as swinging in step.</summary>
    public const double SyncShare = 0.6;
    public const int MaxScore = 1000, BatzenDivisor = 25;
    public const int MaxFigures = 12;

    public static FahnenRoutine Plan(int seed)
    {
        var rng = new Mix((uint)seed);
        int count = 8 + rng.Next(5);
        int throwAt = count / 2 + rng.Next(2);
        var figs = new List<FigureSlot>();
        int beat = CountInBeats;
        var last = FahnenFigure.Hochwurf;
        for (int i = 0; i < count; i++)
        {
            FahnenFigure kind;
            if (i == 0) kind = FahnenFigure.Ufzug;
            else if (i == throwAt || (count >= 11 && i == count - 1)) kind = FahnenFigure.Hochwurf;
            else
            {
                do kind = (FahnenFigure)rng.Next(5); while (kind == last);
            }
            int beats = kind == FahnenFigure.Hochwurf ? ThrowBeats : GestureBeats;
            figs.Add(new FigureSlot(kind, beat, beats));
            beat += beats;
            last = kind;
        }
        return new FahnenRoutine(seed, [.. figs]);
    }

    /// <summary>The slot a request at <paramref name="now"/> (the festival clock, s) joins: the next boundary at least the lead-in away.</summary>
    public static long SlotOf(double now) => (long)Math.Ceiling((now + LeadSeconds) / (SlotBeats * SecondsPerBeat));

    public static double SlotStart(long slot) => slot * SlotBeats * SecondsPerBeat;

    public static int SeedOf(string instance, long slot)
    {
        uint h = 2166136261;
        foreach (char c in instance) h = (h ^ c) * 16777619;
        h = (h ^ (uint)slot) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    public static double Seconds(int beats) => beats * SecondsPerBeat;

    /// <summary>Whether a drawn stroke is what the figure asks for (a Lufthieb sweeps either way).</summary>
    public static bool Matches(FahnenFigure kind, Stroke s) => kind switch
    {
        FahnenFigure.Ufzug => s == Stroke.Up,
        FahnenFigure.Muehlirad => s == Stroke.CircleCw || s == Stroke.CircleCcw,
        FahnenFigure.Achter => s == Stroke.Eight,
        FahnenFigure.Lufthieb => s is Stroke.Left or Stroke.Right,
        FahnenFigure.Unterschwung => s == Stroke.Down,
        _ => false,
    };

    /// <summary>The stroke a bot (or a tutorial) draws for a figure.</summary>
    public static Stroke StrokeFor(FahnenFigure kind, bool flip = false) => kind switch
    {
        FahnenFigure.Ufzug => Stroke.Up,
        FahnenFigure.Muehlirad => flip ? Stroke.CircleCcw : Stroke.CircleCw,
        FahnenFigure.Achter => Stroke.Eight,
        FahnenFigure.Lufthieb => flip ? Stroke.Left : Stroke.Right,
        FahnenFigure.Unterschwung => Stroke.Down,
        _ => Stroke.None,
    };

    private static FahnenGrade ByWindow(double off, double perfect, double good, double near) =>
        off <= perfect ? FahnenGrade.Perfect : off <= good ? FahnenGrade.Good : off <= near ? FahnenGrade.Off : FahnenGrade.Miss;

    /// <summary>A gesture figure: right stroke, finished near its last beat.</summary>
    public static FahnenGrade GradeGesture(FigureSlot slot, FigureInput input)
    {
        if (!Matches(slot.Kind, GestureRing.Recognise(input.Path))) return FahnenGrade.Miss;
        double off = Math.Abs(input.A / 1000.0 - Seconds(slot.StartBeat + slot.Beats));
        return ByWindow(off, PerfectWindow, GoodWindow, OffWindow);
    }

    /// <summary>
    /// The Hochwurf: held from <c>A</c>, let go on <c>B</c> (beat 1, 2 or 3 after its start), caught on <c>C</c> as it lands (the release
    /// beat plus the flight, one beat more than the beats held). The worse of the release and the catch counts; a catch that is a Miss
    /// drops the flag.
    /// </summary>
    public static FigureResult GradeThrow(FigureSlot slot, FigureInput input)
    {
        double start = Seconds(slot.StartBeat);
        if (Math.Abs(input.A / 1000.0 - start) > 0.75) return new(FahnenGrade.Miss, 0);
        double rel = input.B / 1000.0;
        int held = (int)Math.Round((rel - start) / SecondsPerBeat);
        if (held is < 1 or > 3) return new(FahnenGrade.Miss, 0);
        int flight = held + 1;
        var release = ByWindow(Math.Abs(rel - Seconds(slot.StartBeat + held)), ThrowPerfect, ThrowGood, ThrowOff);
        if (release == FahnenGrade.Miss) return new(FahnenGrade.Miss, 0);
        var catches = ByWindow(Math.Abs(input.C / 1000.0 - Seconds(slot.StartBeat + held + flight)), ThrowPerfect, ThrowGood, ThrowOff);
        return new(catches == FahnenGrade.Miss ? FahnenGrade.Miss : (FahnenGrade)Math.Min((int)release, (int)catches), flight);
    }

    private static int Points(FahnenGrade g) => g switch { FahnenGrade.Perfect => 100, FahnenGrade.Good => 70, FahnenGrade.Off => 40, _ => 0 };

    /// <summary>A throw is worth more the higher it goes: 1.0 for two beats in the air, 1.8 for four.</summary>
    public static double FlightFactor(int flight) => 1 + 0.4 * Math.Clamp(flight - 2, 0, 2);

    public static FahnenOutcome Grade(FahnenRoutine routine, IReadOnlyList<FigureInput> inputs)
    {
        var grades = new List<FahnenGrade>();
        double got = 0, best = 0;
        foreach (var f in routine.Figures) best += 100 * (f.Kind == FahnenFigure.Hochwurf ? FlightFactor(4) : 1);
        bool dropped = false;
        for (int i = 0; i < routine.Figures.Length && !dropped; i++)
        {
            var slot = routine.Figures[i];
            if (i >= inputs.Count) { grades.Add(FahnenGrade.Miss); continue; }
            if (slot.Kind == FahnenFigure.Hochwurf)
            {
                var r = GradeThrow(slot, inputs[i]);
                grades.Add(r.Grade);
                got += Points(r.Grade) * FlightFactor(r.Flight);
                dropped = r.Grade == FahnenGrade.Miss;
            }
            else
            {
                var g = GradeGesture(slot, inputs[i]);
                grades.Add(g);
                got += Points(g);
            }
        }
        while (grades.Count < routine.Figures.Length) grades.Add(FahnenGrade.Miss);
        return new FahnenOutcome([.. grades], (int)Math.Round(MaxScore * got / best), dropped);
    }

    public static int BatzenFor(int score) => Math.Max(0, score) / BatzenDivisor;

    /// <summary>Whether two routines of one slot were in step: both landed at least <see cref="SyncShare"/> of the figures Good or better.</summary>
    public static bool InStep(FahnenGrade[] a, FahnenGrade[] b)
    {
        int n = Math.Min(a.Length, b.Length), both = 0;
        for (int i = 0; i < n; i++)
            if (a[i] >= FahnenGrade.Good && b[i] >= FahnenGrade.Good) both++;
        return n > 0 && both >= Math.Ceiling(n * SyncShare);
    }

    /// <summary>For each routine of one slot, whether another one was in step with it (they all earn the bonus together).</summary>
    public static bool[] SyncedOf(IReadOnlyList<FahnenGrade[]> slot)
    {
        var r = new bool[slot.Count];
        for (int i = 0; i < slot.Count; i++)
            for (int j = i + 1; j < slot.Count; j++)
                if (InStep(slot[i], slot[j])) r[i] = r[j] = true;
        return r;
    }

    public static int WithSync(int score, bool synced) => synced ? (int)Math.Round(score * SyncBonus) : score;

    // ---- the wire -------------------------------------------------------------------------------

    /// <summary>A figure is four ints on the wire: A, B, C and its path's length.</summary>
    public const int IntsPerFigure = 4;

    public static (int[] Stamps, byte[] Path) ToWire(IReadOnlyList<FigureInput> inputs)
    {
        var ints = new List<int>();
        var path = new List<byte>();
        foreach (var f in inputs)
        {
            ints.Add(f.A); ints.Add(f.B); ints.Add(f.C); ints.Add(f.Path.Length);
            path.AddRange(f.Path);
        }
        return ([.. ints], [.. path]);
    }

    /// <summary>Reads a submission back; null when it is not one a client could have made.</summary>
    public static FigureInput[]? FromWire(int[] stamps, byte[] path)
    {
        if (stamps.Length % IntsPerFigure != 0 || stamps.Length / IntsPerFigure > MaxFigures) return null;
        var list = new FigureInput[stamps.Length / IntsPerFigure];
        int at = 0;
        for (int i = 0; i < list.Length; i++)
        {
            int len = stamps[i * IntsPerFigure + 3];
            if (len < 0 || len > GestureRing.MaxSamples || at + len > path.Length) return null;
            list[i] = new FigureInput(stamps[i * IntsPerFigure], stamps[i * IntsPerFigure + 1], stamps[i * IntsPerFigure + 2], path[at..(at + len)]);
            at += len;
        }
        return at == path.Length ? list : null;
    }

    /// <summary>The latest stamp in a submission, ms: it cannot be later than the routine has run.</summary>
    public static int LastStamp(IReadOnlyList<FigureInput> inputs)
    {
        int m = 0;
        foreach (var f in inputs) m = Math.Max(m, Math.Max(f.A, Math.Max(f.B, f.C)));
        return m;
    }

    // ---- bots -----------------------------------------------------------------------------------

    /// <summary>What a bot of <paramref name="skill"/> (0..1) swings: the right strokes, a little late or early, now and then wrong, rarely a dropped flag.</summary>
    public static FigureInput[] BotRoutine(FahnenRoutine routine, double skill, int seed)
    {
        var rng = new Mix((uint)seed);
        double sigma = 0.04 + (1 - skill) * 0.22;
        double Jitter() => (rng.Unit() + rng.Unit() + rng.Unit() - 1.5) * 2 * sigma;
        var list = new List<FigureInput>();
        foreach (var f in routine.Figures)
        {
            if (f.Kind == FahnenFigure.Hochwurf)
            {
                int held = 1 + rng.Next(3);
                double land = Seconds(f.StartBeat + held + held + 1);
                bool drop = rng.Unit() < (1 - skill) * 0.12;
                list.Add(new FigureInput(Ms(Seconds(f.StartBeat)), Ms(Seconds(f.StartBeat + held) + Jitter()), Ms(land + (drop ? 1.2 : Jitter())), []));
                if (drop) break;
            }
            else
            {
                bool wrong = rng.Unit() < (1 - skill) * 0.15;
                var right = StrokeFor(f.Kind, rng.Unit() < 0.5);
                var stroke = wrong ? (right == Stroke.Up ? Stroke.Down : Stroke.Up) : right;
                list.Add(new FigureInput(Ms(Seconds(f.StartBeat + f.Beats) + Jitter()), 0, 0, GestureRing.Ideal(stroke)));
            }
        }
        return [.. list];
    }

    private static int Ms(double s) => (int)Math.Round(s * 1000);

    /// <summary>A small deterministic generator (<c>System.Random</c> is not promised stable between runtimes).</summary>
    private struct Mix
    {
        private uint _s;

        public Mix(uint seed) => _s = seed * 2654435761u + 0x9E3779B9;

        public uint Next32()
        {
            _s += 0x9E3779B9;
            uint z = _s;
            z = (z ^ (z >> 16)) * 0x85EBCA6B;
            z = (z ^ (z >> 13)) * 0xC2B2AE35;
            return z ^ (z >> 16);
        }

        public int Next(int n) => (int)(Next32() % (uint)n);
        public double Unit() => Next32() / 4294967296.0;
    }
}
