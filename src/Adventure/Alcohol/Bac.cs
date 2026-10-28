using System.Globalization;

namespace UnitSport.Adventure.Alcohol;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/alcohol.md).

/// <summary>One drink in the log: when it was drunk (seconds on <see cref="AlcoholClock"/>), its grams of ethanol, and whether the stomach was fed.</summary>
public sealed class Shot
{
    public long T { get; set; }
    public float G { get; set; }
    public bool F { get; set; }
}

/// <summary>The <c>alcohol</c> progress section (#1271): the drink log of the last 24 h. Readings are never saved.</summary>
public sealed class AlcoholProgress
{
    public static readonly SectionKey<AlcoholProgress> Section = new("alcohol", Version: 1);

    public List<Shot> Drinks { get; set; } = new();
}

/// <summary>
/// The body's blood alcohol (#1271, <c>docs/plans/adventure-alcohol.md</c> part 1): grams of ethanol of a drink, absorption (the
/// unabsorbed grams halve every 15 min, 30 min when fed), Widmark distribution (grams / (kg x r)) and a linear elimination of
/// 0.15 per mille an hour, never below 0. The level is computed when asked from the drink log, never integrated per frame.
/// </summary>
public static class Bac
{
    /// <summary>Ethanol's density, g/ml.</summary>
    public const double Density = 0.789;
    /// <summary>Widmark's r for every figure (the body model has no sex): between the textbook 0.68 and 0.55.</summary>
    public const double R = 0.6;
    public const double DefaultKg = 75;
    /// <summary>Elimination, per mille an hour.</summary>
    public const double EliminationPerHour = 0.15;
    /// <summary>Absorption half-life, s: empty stomach and fed.</summary>
    public const double HalfLifeEmpty = 15 * 60, HalfLifeFed = 30 * 60;
    /// <summary>Within this long of the last drink a breath reading is mouth alcohol, s.</summary>
    public const double MouthSeconds = 15 * 60;
    /// <summary>The log keeps this long, s.</summary>
    public const double KeepSeconds = 24 * 3600;
    public const int MaxDrinks = 200;

    /// <summary>Grams of ethanol in <paramref name="ml"/> of a drink of strength <paramref name="abv"/> (0.05 = 5 %).</summary>
    public static double Grams(double ml, double abv) => ml * abv * Density;

    /// <summary>Appends a drink and forgets what is over a day old (or past <see cref="MaxDrinks"/>).</summary>
    public static void Add(List<Shot> log, double now, double grams, bool fed)
    {
        if (grams <= 0) return;
        log.Add(new Shot { T = (long)Math.Floor(now), G = (float)grams, F = fed });
        Prune(log, now);
    }

    public static void Prune(List<Shot> log, double now)
    {
        log.RemoveAll(s => now - s.T > KeepSeconds);
        if (log.Count > MaxDrinks) log.RemoveRange(0, log.Count - MaxDrinks);
    }

    /// <summary>Grams of one drink absorbed into the blood <paramref name="age"/> s after it was drunk.</summary>
    public static double Absorbed(Shot s, double age) =>
        age <= 0 ? 0 : s.G * (1 - Math.Pow(0.5, age / (s.F ? HalfLifeFed : HalfLifeEmpty)));

    /// <summary>
    /// The level at <paramref name="now"/>, per mille, for a body of <paramref name="kg"/>: what the log absorbed, spread over
    /// the body's water, less 0.15 an hour while there is any in the blood. Stepped over the log's span (30 s steps at most
    /// 2 880 of them): the elimination stops at 0, so it has no closed form.
    /// </summary>
    public static double Level(IReadOnlyList<Shot> log, double now, double kg = DefaultKg)
    {
        if (log.Count == 0) return 0;
        double start = double.MaxValue;
        foreach (var s in log) if (s.T < start) start = s.T;
        if (now <= start) return 0;
        double span = now - start;
        double step = Math.Max(30, span / 2880);
        double perGram = 1.0 / (Math.Max(20, kg) * R);
        double level = 0, t = start;
        Span<double> before = log.Count <= 64 ? stackalloc double[log.Count] : new double[log.Count];
        while (t < now)
        {
            double dt = Math.Min(step, now - t);
            double inflow = 0;
            for (int i = 0; i < log.Count; i++)
            {
                double a = Absorbed(log[i], t + dt - log[i].T);
                inflow += a - before[i];
                before[i] = a;
            }
            level = Math.Max(0, level + inflow * perGram - EliminationPerHour / 3600.0 * dt);
            t += dt;
        }
        return level;
    }

    /// <summary>Seconds of mouth alcohol left (the last drink was less than 15 min ago), else 0.</summary>
    public static double MouthLeft(IReadOnlyList<Shot> log, double now)
    {
        double last = double.MinValue;
        foreach (var s in log) if (s.T > last) last = s.T;
        return log.Count == 0 ? 0 : Math.Max(0, MouthSeconds - (now - last));
    }

    /// <summary>Seconds until the level reaches 0 if nothing more is drunk (a coarse search, for the notes and the probe).</summary>
    public static double SoberIn(IReadOnlyList<Shot> log, double now, double kg = DefaultKg)
    {
        bool risen = false;
        for (double t = 0; t <= KeepSeconds; t += 300)
        {
            bool any = Level(log, now + t, kg) > 0.001;
            if (any) risen = true;
            else if (risen || t > 0 && log.Count == 0) return t;
        }
        return KeepSeconds;
    }
}

/// <summary>The alcohol clock: real time (so a player who logs off drunk comes back sober), with a check-only speed-up.</summary>
public static class AlcoholClock
{
    private static double _scale = 1, _skip, _anchorWall, _anchorSim;

    /// <summary>Seconds. Real wall time unless <see cref="Speed"/> or <see cref="Skip"/> moved it.</summary>
    public static double Now => _scale == 1 ? Wall() + _skip : _anchorSim + (Wall() - _anchorWall) * _scale + _skip;

    private static double Wall() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary><c>--alcohol-clock n</c> (checks): the clock runs n times faster from now on.</summary>
    public static void Speed(double scale)
    {
        _anchorSim = Now - _skip;
        _anchorWall = Wall();
        _scale = scale <= 0 ? 1 : scale;
    }

    /// <summary>Checks: jumps the clock forward.</summary>
    public static void Skip(double seconds) => _skip += seconds;

    public static void Reset() { _scale = 1; _skip = 0; }
}
