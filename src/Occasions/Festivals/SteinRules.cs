using UnitSport.Concert;

// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-steinstossen.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>A stone of the put: the Unspunnen stone and two lighter ones for a first go.</summary>
/// <param name="Par">How far a perfect throw puts it, m (the real Unspunnen stone lands around 3.5-4 m).</param>
public sealed record SteinClass(string Title, double Kg, double Par, int MaxBatzen, int MaxPoints);

/// <summary>How near a press came to its beat (<see cref="PogoBeat"/>'s windows).</summary>
public enum StepGrade { Miss, Good, Perfect }

/// <summary>
/// What a put came to. <see cref="Foul"/> says why nothing was thrown (the stone slipped from the hands, the stamps were
/// nonsense); a foul lands 0.3 m off the line and pays nothing.
/// </summary>
public sealed record SteinResult(string? Foul, double Lift, StepGrade[] Steps, StepGrade Push, double Angle, double Power,
    double Speed, double Metres, int Batzen, int Points);

/// <summary>A throw as the client sends it: six server-time stamps and the camera's pitch at the push, in degrees.</summary>
public sealed record SteinThrow(double[] Stamps, double Pitch);

/// <summary>
/// The rules of Steinstossen (#1269 part 3, <c>docs/plans/adventure-festivals.md</c> section 3), pure. An attempt is: hold the
/// lift (<see cref="Stamp.Down"/>), let go on the peak of a swinging meter (<see cref="Stamp.Up"/>), take three steps on the beat
/// of a 90 BPM drum that starts at the release, then push on the fourth beat with the camera set to the angle. The server holds
/// the attempt's seed and start; the client sends only the stamps, and everything below (meter, grades, power, distance, pay) is
/// worked out on the server from them.
/// </summary>
public static class SteinRules
{
    public const string GameId = "stein";

    /// <summary>Attempts a day, per player; the best counts for the Journal and every one pays.</summary>
    public const int AttemptsPerDay = 3;

    public const double Bpm = 90, SecondsPerBeat = 60.0 / Bpm;

    /// <summary>The release point above the ground, m, and gravity.</summary>
    public const double ReleaseHeight = 1.9, Gravity = 9.81;

    /// <summary>The camera pitch (degrees above the horizon) that throws furthest is 40-45; 38-42 is the plan's best band.</summary>
    public const double BestAngle = 40, MinAngle = 5, MaxAngle = 75;

    /// <summary>The stone's reach on the ground: the server refuses an attempt from further, m.</summary>
    public const double ReachM = 6;

    /// <summary>The indices of the six stamps of a throw: lift down, lift up, the three steps, the push.</summary>
    public static class Stamp
    {
        public const int Down = 0, Up = 1, Step1 = 2, Step2 = 3, Step3 = 4, Push = 5, Count = 6;
    }

    public static readonly SteinClass[] Classes =
    [
        new("Unspunnen stone, 83.5 kg", 83.5, 4.2, 30, 120),
        new("Hall stone, 40 kg", 40, 8.5, 14, 60),
        new("Practice stone, 18 kg", 18, 15, 6, 24),
    ];

    public static SteinClass Class(int cls) => Classes[Math.Clamp(cls, 0, Classes.Length - 1)];

    /// <summary>The release speed at full power that puts a class's stone exactly to its par at the best angle.</summary>
    public static double FullSpeed(int cls) => FullSpeeds[Math.Clamp(cls, 0, Classes.Length - 1)];

    private static readonly double[] FullSpeeds = Classes.Select(c => SpeedFor(c.Par, BestAngle)).ToArray();

    // ---- the flight ----------------------------------------------------------------------------

    /// <summary>The ground range of a stone let go at <paramref name="speed"/> and <paramref name="angleDeg"/> from the release height, m.</summary>
    public static double Range(double speed, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180, vx = speed * Math.Cos(a), vy = speed * Math.Sin(a);
        return vx * FlightTime(speed, angleDeg);
    }

    /// <summary>Seconds from the release until the stone is down.</summary>
    public static double FlightTime(double speed, double angleDeg)
    {
        double vy = speed * Math.Sin(angleDeg * Math.PI / 180);
        return (vy + Math.Sqrt(vy * vy + 2 * Gravity * ReleaseHeight)) / Gravity;
    }

    /// <summary>The stone <paramref name="t"/> seconds after the release: metres along the throw and metres above the ground.</summary>
    public static (double Along, double Up) Position(double speed, double angleDeg, double t)
    {
        double a = angleDeg * Math.PI / 180;
        return (speed * Math.Cos(a) * t, Math.Max(0, ReleaseHeight + speed * Math.Sin(a) * t - 0.5 * Gravity * t * t));
    }

    private static double SpeedFor(double range, double angleDeg)
    {
        double lo = 1, hi = 60;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Range(mid, angleDeg) < range) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    // ---- the lift ------------------------------------------------------------------------------

    /// <summary>Seconds one swing of the meter takes: a heavier stone swings slower, and is harder to catch at the top.</summary>
    public static double MeterPeriod(int cls) => 1.1 + 0.019 * Class(cls).Kg;

    /// <summary>
    /// The meter after <paramref name="held"/> seconds of holding, 0 to 1 (1 is the peak): a triangle wave whose phase at the
    /// press is the attempt's seed, so it is never the same twice and the client cannot learn it.
    /// </summary>
    public static double Meter(int seed, int cls, double held)
    {
        double x = (held / MeterPeriod(cls) + Phase(seed)) % 1.0;
        if (x < 0) x += 1;
        return 1 - Math.Abs(2 * x - 1);
    }

    /// <summary>The meter's starting phase for a seed, 0 to 1 (FNV-1a over the seed: <c>GetHashCode</c> differs between runs).</summary>
    public static double Phase(int seed)
    {
        uint h = 2166136261;
        for (int i = 0; i < 4; i++) h = (h ^ (byte)(seed >> (8 * i))) * 16777619;
        return h / (double)uint.MaxValue;
    }

    /// <summary>How well a meter reading lifted the stone, 0 (it slipped) to 1 (on the peak); 0.95 and up is a full lift.</summary>
    public static double LiftQuality(double meter) => Math.Clamp((meter - 0.55) / 0.4, 0, 1);

    /// <summary>The seconds of holding at which the meter peaks next at or after <paramref name="from"/> (for the bots and the checks).</summary>
    public static double NextPeak(int seed, int cls, double from)
    {
        double period = MeterPeriod(cls), x0 = Phase(seed);
        // the peak is at x = 0.5: held = (0.5 - x0 + k) * period
        double k = Math.Ceiling(from / period - 0.5 + x0);
        return (0.5 - x0 + k) * period;
    }

    // ---- the run-up and the push ---------------------------------------------------------------

    /// <summary>The server-time moment of beat <paramref name="n"/> (1-4) of the drum that starts at the release.</summary>
    public static double BeatAt(double release, int n) => release + n * SecondsPerBeat;

    public static StepGrade Grade(double pressAt, double beatAt)
    {
        double off = Math.Abs(pressAt - beatAt), window = PogoBeat.Window(SecondsPerBeat);
        return off > window ? StepGrade.Miss : off <= Math.Min(PogoBeat.PerfectSeconds, window * 0.5) ? StepGrade.Perfect : StepGrade.Good;
    }

    public static double Factor(StepGrade g) => g switch { StepGrade.Perfect => 1.0, StepGrade.Good => 0.72, _ => 0.3 };

    /// <summary>
    /// How cleanly the push is made at <paramref name="angleDeg"/>: 1 inside the best band (38-42 degrees), 1.2 % of the speed lost for every
    /// degree outside it. A stone let go from shoulder height at this speed would carry furthest nearer 33 degrees; the push is a technique, not a
    /// cannon, so the band is what the plan says and a flat or a lofted one is worse.
    /// </summary>
    public static double Technique(double angleDeg) => Math.Max(0.4, 1 - 0.012 * Math.Max(0, Math.Abs(angleDeg - BestAngle) - 2));

    // ---- the whole put -------------------------------------------------------------------------

    /// <summary>
    /// Why a throw sent at <paramref name="now"/> (server time) for an attempt begun at <paramref name="begun"/> cannot be true, or null
    /// when it can: six finite stamps in order, never from before the attempt or from the future (the latency allowed for), the lift
    /// not held longer than a minute, the push not stale. The grades themselves are a skill, not a rule: late is just a Miss.
    /// </summary>
    public static string? Implausible(double[]? stamps, double begun, double now, double slack = 0.35)
    {
        if (stamps == null || stamps.Length != Stamp.Count) return "A put has six presses.";
        foreach (double s in stamps)
            if (!double.IsFinite(s)) return "Those presses make no sense.";
        if (stamps[Stamp.Down] < begun - slack) return "That press came before the attempt.";
        for (int i = 1; i < Stamp.Count; i++)
            if (stamps[i] <= stamps[i - 1]) return "The presses are out of order.";
        if (stamps[Stamp.Up] - stamps[Stamp.Down] > 60) return "The lift was held for too long.";
        for (int i = Stamp.Step1; i <= Stamp.Push; i++)
            if (stamps[i] - stamps[i - 1] < 0.1) return "Those steps came too fast.";
        if (stamps[Stamp.Push] > now + slack) return "That press has not happened yet.";
        if (stamps[Stamp.Push] < now - 2.0) return "That put is stale.";
        return null;
    }

    /// <summary>Works a put out: everything the server decides from a client's stamps.</summary>
    public static SteinResult Resolve(int seed, int cls, SteinThrow t)
    {
        var s = t.Stamps;
        double held = s[Stamp.Up] - s[Stamp.Down];
        double lift = LiftQuality(Meter(seed, cls, held));
        var steps = new StepGrade[3];
        for (int i = 0; i < 3; i++) steps[i] = Grade(s[Stamp.Step1 + i], BeatAt(s[Stamp.Up], i + 1));
        var push = Grade(s[Stamp.Push], BeatAt(s[Stamp.Up], 4));
        double angle = Math.Clamp(double.IsFinite(t.Pitch) ? t.Pitch : BestAngle, MinAngle, MaxAngle);
        if (lift <= 0)
            return new SteinResult("The stone slipped from your hands.", 0, steps, push, angle, 0, 0, 0.3, 0, 0);

        double run = steps.Average(Factor);
        double power = 0.2 + 0.8 * (0.4 * lift + 0.45 * run + 0.15 * Factor(push));
        double speed = FullSpeed(cls) * power * Technique(angle);
        double metres = Range(speed, angle);
        var c = Class(cls);
        double rating = Math.Min(1.0, metres / c.Par);
        return new SteinResult(null, lift, steps, push, angle, power, speed, metres,
            (int)Math.Round(rating * c.MaxBatzen), (int)Math.Round(Math.Min(1.05, metres / c.Par) * c.MaxPoints));
    }

    // ---- the bots ------------------------------------------------------------------------------

    /// <summary>
    /// A bot's put (<paramref name="skill"/> 0 to 1): its presses as a person of that skill would make them, from the attempt's start:
    /// the lift let go on a peak, the drum followed, the angle near the best. The more skilled, the smaller every error. Bots
    /// earn nothing at festivals; they make up the field, so the same server path takes this as takes a client's.
    /// </summary>
    public static SteinThrow BotAttempt(double skill, Random rng, int seed, int cls, double begun)
    {
        skill = Math.Clamp(skill, 0, 1);
        double sloppy = 1 - skill;
        double down = begun + 0.25;
        double up = down + NextPeak(seed, cls, 0.4) + Gauss(rng) * sloppy * 0.22;
        var st = new double[Stamp.Count];
        st[Stamp.Down] = down;
        st[Stamp.Up] = Math.Max(up, down + 0.3);
        for (int i = 0; i < 4; i++) st[Stamp.Step1 + i] = BeatAt(st[Stamp.Up], i + 1) + Gauss(rng) * sloppy * 0.12;
        for (int i = Stamp.Step1; i < Stamp.Count; i++) st[i] = Math.Max(st[i], st[i - 1] + 0.3);
        return new SteinThrow(st, BestAngle + Gauss(rng) * sloppy * 14);
    }

    private static double Gauss(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}

/// <summary>Where a festival's stone lies and which way it is thrown: a pure function of the site, so the server and every client agree with nothing sent.</summary>
public readonly record struct SteinGround(double E, double N, double DirE, double DirN)
{
    /// <summary>The stone's distance from the site's middle, m.</summary>
    public const double OffsetM = 12;

    /// <summary>Beside the middle (clear of the mortar rack 14 m west), thrown along one of eight compass directions picked by the site.</summary>
    public static SteinGround Of(ResolvedSite site)
    {
        int k = (int)(((long)Math.Abs(site.E) + (long)Math.Abs(site.N)) % 8);
        double a = k * Math.PI / 4;
        double dirE = Math.Cos(a), dirN = Math.Sin(a);
        // 12 m off the middle, to the left of the throw
        return new SteinGround(site.E - dirN * OffsetM, site.N + dirE * OffsetM, dirE, dirN);
    }

    public double DistanceTo(double e, double n) => Math.Sqrt((e - E) * (e - E) + (n - N) * (n - N));

    /// <summary>The point <paramref name="metres"/> along the throw, LV95.</summary>
    public (double E, double N) Along(double metres) => (E + DirE * metres, N + DirN * metres);

    /// <summary>The throw's heading as a yaw about +Y for a Godot node whose +X is the throw (LV95 north is -Z in the world).</summary>
    public float Yaw => (float)Math.Atan2(DirN, DirE);
}
