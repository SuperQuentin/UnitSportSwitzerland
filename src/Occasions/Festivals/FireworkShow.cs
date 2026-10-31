// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fireworks.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>The shape of a shell's burst (#1269 part 2). The Swiss cross is a red peony with a white cross of stars over it.</summary>
public enum ShellKind { Peony, Chrysanthemum, Willow, Palm, Ring, Crossette, SwissCross }

/// <summary>
/// One shell of a show: it bursts <see cref="Height"/> m up at <see cref="T"/> seconds into the show, <see cref="East"/> m east and
/// <see cref="North"/> m north of the launch point; it left the ground <see cref="FireworkShow.RiseSeconds"/> before. The colour
/// is an index into the palette the drawing node holds (<see cref="FireworkShow.Colours"/> of them).
/// </summary>
public readonly record struct Shell(double T, float East, float North, float Height, ShellKind Kind, int Colour)
{
    public double Launch => T - FireworkShow.RiseSeconds;
}

/// <summary>
/// A fireworks show as data (#1269 part 2, <c>docs/plans/adventure-festivals.md</c> section 2): from a seed alone, a script of shells
/// in three acts and a finale of <see cref="FinaleShells"/> shells in the last <see cref="FinaleSeconds"/> seconds, 6 to 12 minutes long.
/// Pure and deterministic, so every peer that is given the seed and the start (server time) sees the same shell at the same moment,
/// and no show is ever sent over the wire. Positions are metres from the launch point, east and north (LV95 axes).
/// </summary>
public sealed class FireworkShow
{
    /// <summary>Seconds a shell takes from the mortar to its burst.</summary>
    public const double RiseSeconds = 2.2;

    /// <summary>Quiet seconds before the first shell.</summary>
    public const double LeadIn = 6;

    public const double FinaleSeconds = 20;
    public const int FinaleShells = 40;
    public const int Colours = 8;

    /// <summary>The 6 to 12 minutes a show lasts, from its seed alone (the server needs it without the script).</summary>
    public static double DurationOf(int seed) => 360 + new Random(seed).Next(0, 7) * 60;

    public int Seed { get; }
    public double Duration { get; }

    /// <summary>The direction the shells fan out in, rad from north towards east: the audience looks the other way.</summary>
    public float Heading { get; }

    /// <summary>Every shell, by burst time.</summary>
    public Shell[] Shells { get; }

    private FireworkShow(int seed, double duration, float heading, Shell[] shells) =>
        (Seed, Duration, Heading, Shells) = (seed, duration, heading, shells);

    /// <summary>Index of the first shell that bursts at or after <paramref name="t"/> (Shells.Length when none does).</summary>
    public int First(double t)
    {
        int lo = 0, hi = Shells.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Shells[mid].T < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public static FireworkShow Plan(int seed)
    {
        var rng = new Random(seed);
        double duration = 360 + rng.Next(0, 7) * 60;   // the same first draw as DurationOf
        float heading = (float)(rng.NextDouble() * Math.Tau);
        var list = new List<Shell>(256);

        double finaleFrom = duration - FinaleSeconds;
        double actEnd1 = LeadIn + (finaleFrom - LeadIn) * 0.35;
        double actEnd2 = LeadIn + (finaleFrom - LeadIn) * 0.75;

        // act 1: single shells, plain and slow
        double t = LeadIn;
        ShellKind[] plain = [ShellKind.Peony, ShellKind.Chrysanthemum, ShellKind.Ring];
        while (t < actEnd1)
        {
            list.Add(Make(rng, heading, t, plain[rng.Next(plain.Length)]));
            t += 4 + rng.NextDouble() * 3;
        }

        // act 2: pairs and triples, the heavy willows and palms, ending on the Swiss cross
        ShellKind[] heavy = [ShellKind.Willow, ShellKind.Palm, ShellKind.Chrysanthemum, ShellKind.Ring, ShellKind.Peony];
        t = actEnd1;
        while (t < actEnd2 - 8)
        {
            Salvo(list, rng, heading, t, 1 + rng.Next(3), heavy);
            t += 3 + rng.NextDouble() * 2.5;
        }
        list.Add(Cross(heading, actEnd2 - 1));

        // act 3: faster salvos, the crossettes join
        ShellKind[] all = [ShellKind.Peony, ShellKind.Chrysanthemum, ShellKind.Willow, ShellKind.Palm, ShellKind.Ring, ShellKind.Crossette, ShellKind.Crossette];
        t = actEnd2 + 2;
        while (t < finaleFrom - 3)
        {
            Salvo(list, rng, heading, t, 2 + rng.Next(3), all);
            t += 2 + rng.NextDouble() * 2;
        }

        // the finale: 40 shells in 20 s, a Swiss cross in the middle, the biggest willows on the last beats
        for (int i = 0; i < FinaleShells; i++)
        {
            double at = finaleFrom + (i + 0.5) * FinaleSeconds / FinaleShells + (rng.NextDouble() - 0.5) * 0.3;
            var kind = i == FinaleShells / 2 ? ShellKind.SwissCross : i >= FinaleShells - 2 ? ShellKind.Willow : all[rng.Next(all.Length)];
            list.Add(kind == ShellKind.SwissCross ? Cross(heading, at) : Make(rng, heading, at, kind));
        }

        // a stable sort by time only, so the same seed always lists the same shells in the same order
        return new FireworkShow(seed, duration, heading, list.OrderBy(s => s.T).ToArray());
    }

    /// <summary>Seconds a player's own volley (the 1 August firework box) lasts.</summary>
    public const double VolleySeconds = 40;

    /// <summary>
    /// A small show for one box of fireworks (#1355): 14 shells and a little Swiss cross over 40 s, 20-60 m out, 55-95 m high; from a seed alone like
    /// <see cref="Plan"/>, so every peer that is told the seed and the start plays the same volley.
    /// </summary>
    public static FireworkShow Volley(int seed)
    {
        var rng = new Random(seed);
        float heading = (float)(rng.NextDouble() * Math.Tau);
        var list = new List<Shell>(16);
        ShellKind[] kinds = [ShellKind.Peony, ShellKind.Chrysanthemum, ShellKind.Willow, ShellKind.Ring, ShellKind.Crossette];
        for (int i = 0; i < 14; i++)
        {
            double side = (rng.NextDouble() * 2 - 1) * 30, depth = 20 + rng.NextDouble() * 40;
            float east = (float)(Math.Sin(heading) * depth + Math.Cos(heading) * side);
            float north = (float)(Math.Cos(heading) * depth - Math.Sin(heading) * side);
            list.Add(new Shell(2.5 + i * 2.4 + rng.NextDouble() * 0.6, east, north, 55 + (float)rng.NextDouble() * 40, kinds[rng.Next(kinds.Length)], rng.Next(Colours)));
        }
        list.Add(new Shell(VolleySeconds - 4, (float)(Math.Sin(heading) * 40), (float)(Math.Cos(heading) * 40), 80, ShellKind.SwissCross, 0));
        return new FireworkShow(seed, VolleySeconds, heading, list.OrderBy(s => s.T).ToArray());
    }

    private static void Salvo(List<Shell> list, Random rng, float heading, double t, int count, ShellKind[] kinds)
    {
        for (int i = 0; i < count; i++) list.Add(Make(rng, heading, t + i * 0.4, kinds[rng.Next(kinds.Length)]));
    }

    private static Shell Make(Random rng, float heading, double t, ShellKind kind)
    {
        // a fan 280 m wide, 60 to 180 m out in the heading's direction
        double side = (rng.NextDouble() * 2 - 1) * 140, depth = 60 + rng.NextDouble() * 120;
        float east = (float)(Math.Sin(heading) * depth + Math.Cos(heading) * side);
        float north = (float)(Math.Cos(heading) * depth - Math.Sin(heading) * side);
        var (lo, hi) = HeightRange(kind);
        float height = lo + (float)rng.NextDouble() * (hi - lo);
        return new Shell(t, east, north, height, kind, rng.Next(Colours));
    }

    /// <summary>The Swiss cross: over the middle of the fan, at the middle of its heights.</summary>
    private static Shell Cross(float heading, double t)
    {
        var (lo, hi) = HeightRange(ShellKind.SwissCross);
        return new Shell(t, (float)(Math.Sin(heading) * 110), (float)(Math.Cos(heading) * 110), (lo + hi) / 2, ShellKind.SwissCross, 0);
    }

    /// <summary>The burst heights of a kind, m (willows and palms go highest, rings stay low).</summary>
    public static (float Lo, float Hi) HeightRange(ShellKind kind) => kind switch
    {
        ShellKind.Willow => (120, 170),
        ShellKind.Palm => (100, 140),
        ShellKind.Chrysanthemum or ShellKind.Crossette => (90, 130),
        ShellKind.SwissCross => (110, 140),
        _ => (80, 115),
    };
}

/// <summary>The rules about when a festival's show plays and who counts as watching it (pure, tier 0).</summary>
public static class FestivalShows
{
    /// <summary>Game hour the closing show starts at, on every game evening of the festival's last real day.</summary>
    public const double StartHour = 21.5;

    /// <summary>A show does not start in the small hours: a player who logs in at 02:00 waits for the next evening.</summary>
    public const double EndHour = 24;

    /// <summary>Seconds between the server deciding and the first shell: time for the news to reach every client.</summary>
    public const double StartLead = 15;

    /// <summary>Seconds a finished show stays on the books (a client that hears of it late does not replay it).</summary>
    public const double Tail = 15;

    /// <summary>Within this many metres of the site, a player counts as watching the show.</summary>
    public const double WatchRadiusM = 1500;

    /// <summary>Seconds of the show a player must be within <see cref="WatchRadiusM"/> for it to count as watched.</summary>
    public const double WatchSeconds = 20;

    /// <summary>Shells are drawn and heard within this many metres of the camera.</summary>
    public const double DrawRangeM = 15000;

    /// <summary>Speed of sound, m/s: a shell 2 km off is heard 6 s late.</summary>
    public const double SoundSpeed = 343;

    public static bool HasFireworks(ClosingShow show) => show is ClosingShow.Fireworks or ClosingShow.Both;

    /// <summary>
    /// Whether an evening show starts now: the game hour crossed <see cref="StartHour"/> since the last look, or this is the first look
    /// (<paramref name="lastHour"/> null) and the evening is under way. The festival's last real day only: the caller checks it.
    /// </summary>
    public static bool Starts(double? lastHour, double hour) =>
        hour >= StartHour && hour < EndHour && (lastHour is null || lastHour < StartHour);

    /// <summary>A stable seed per festival instance and show of the day (FNV-1a: <c>string.GetHashCode</c> differs from run to run).</summary>
    public static int SeedOf(string instance, int number)
    {
        uint h = 2166136261;
        foreach (char c in instance) h = (h ^ c) * 16777619;
        h = (h ^ (uint)number) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>Whether a show that began at <paramref name="start"/> (server time) is still on the books at <paramref name="now"/>.</summary>
    public static bool Active(double start, int seed, double now) => start > 0 && now < start + FireworkShow.DurationOf(seed) + Tail;

    /// <summary>The seconds a shell bursting at <paramref name="distance"/> m is heard after it is seen.</summary>
    public static double SoundDelay(double distance) => distance / SoundSpeed;
}
