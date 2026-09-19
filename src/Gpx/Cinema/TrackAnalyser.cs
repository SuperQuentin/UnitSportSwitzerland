using Godot;
using UnitSport.Core;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Reads a whole run once and returns the moments worth covering.
///
/// <para>
/// This is what makes the mode a director rather than a camera. Because a replay is on rails the
/// entire future is knowable before the first frame, so instead of reacting to what just happened
/// the edit can be planned around what is about to.
/// </para>
///
/// <para>
/// Two measurement rules run through all of it, both learned the hard way elsewhere in this
/// project. Speed comes from <see cref="GpxTrack.Sample"/>'s windowed figure, never from
/// differencing adjacent fixes, which reports a walk as a sprint. Gradient comes from the
/// <b>draped terrain</b>, never from the recorded GPX elevation, which is kept only as a drift
/// statistic and would put a cliff at every fix.
/// </para>
/// </summary>
public static class TrackAnalyser
{
    /// <summary>Sampling interval along the track, seconds.</summary>
    private const double Step = 1.0;

    /// <summary>Horizontal span the gradient is measured over. Shorter reads as noise.</summary>
    private const double GradientSpanM = 40.0;

    /// <summary>A summit or valley must stand this far clear of its surroundings.</summary>
    private const double ProminenceM = 12.0;

    /// <summary>Closest two events of one kind may be, seconds. The strongest survives.</summary>
    private const double MinSpacing = 25.0;

    /// <summary>
    /// Spacing for effort events specifically.
    ///
    /// <para>
    /// A run/walk interval session genuinely alternates surge and fade every half minute, and
    /// every one of those is a real event — but fifty of them are not fifty moments, they are one
    /// pattern. Spacing them wider keeps the strongest of each cluster and leaves the director
    /// room to cut on something else in between.
    /// </para>
    /// </summary>
    private const double EffortSpacing = 75.0;

    public sealed record Sample(double Time, Vector3 World, double E, double N,
        double Height, double Speed, double Distance, Vector3 Heading);

    /// <summary>Everything the director needs about the run, computed once.</summary>
    public sealed class Profile
    {
        public required IReadOnlyList<Sample> Samples { get; init; }
        public required IReadOnlyList<CinemaEvent> Events { get; init; }
        public required double[] Gradient { get; init; }

        /// <summary>
        /// Per sample: is this ground the run already covered at some other time?
        ///
        /// <para>
        /// Out-and-backs and lap sessions are extremely common, and they are the single biggest
        /// reason a faithful replay makes a dull film — measured 30.7% of one real run passing
        /// within 20 m of a part of itself more than three minutes apart, against 3.1% for a
        /// point-to-point ride. Played linearly the viewer sees the same corner three times and
        /// assumes the video is broken. This is the flag that lets the edit skip the repeats.
        /// </para>
        /// </summary>
        public required bool[] Revisited { get; init; }

        public double Duration { get; init; }

        /// <summary>Share of the run that retreads its own ground, 0-1.</summary>
        public double RevisitShare => Revisited.Length == 0
            ? 0 : Revisited.Count(r => r) / (double)Revisited.Length;
    }

    public static Profile Analyse(GpxTrack track, WorldOrigin origin, Corridor corridor)
    {
        var samples = Walk(track, origin, corridor);
        if (samples.Count < 4)
            return new Profile
            {
                Samples = samples, Events = Array.Empty<CinemaEvent>(),
                Gradient = Array.Empty<double>(), Revisited = Array.Empty<bool>(),
                Duration = track.Duration,
            };

        var gradient = Gradients(samples);
        var events = new List<CinemaEvent>
        {
            new(0, CinemaEventKind.Start, 1f, samples[0].World),
            new(track.Duration, CinemaEventKind.Finish, 1f, samples[^1].World),
        };

        Kinematics(samples, events);
        Elevation(samples, gradient, events);
        Geometry(samples, events);

        return new Profile
        {
            Samples = samples,
            Events = Thin(events),
            Gradient = gradient,
            Revisited = Revisits(samples),
            Duration = track.Duration,
        };
    }

    // ---- sampling -----------------------------------------------------------------------

    private static List<Sample> Walk(GpxTrack track, WorldOrigin origin, Corridor corridor)
    {
        var samples = new List<Sample>((int)(track.Duration / Step) + 2);

        for (double t = 0; t <= track.Duration; t += Step)
        {
            var (e, n, ele, speed, distance) = track.Sample(t);
            double height = corridor.HeightAt(e, n) ?? ele;
            samples.Add(new Sample(t, origin.ToWorld(e, n, height), e, n,
                height, speed, distance, Vector3.Forward));
        }

        // Heading from a look-ahead, for the same reason the runner uses one: over a single step
        // the residual GPS jitter dominates the direction.
        for (int i = 0; i < samples.Count; i++)
        {
            int a = Math.Max(0, i - 3);
            int b = Math.Min(samples.Count - 1, i + 3);
            var dir = samples[b].World - samples[a].World;
            dir.Y = 0;

            if (dir.LengthSquared() > 1e-4f) samples[i] = samples[i] with { Heading = dir.Normalized() };
            else if (i > 0) samples[i] = samples[i] with { Heading = samples[i - 1].Heading };
        }

        return samples;
    }

    /// <summary>Rise over run across a fixed horizontal span rather than a fixed time.</summary>
    private static double[] Gradients(IReadOnlyList<Sample> s)
    {
        var gradient = new double[s.Count];

        for (int i = 0; i < s.Count; i++)
        {
            int a = i, b = i;
            while (a > 0 && s[i].Distance - s[a].Distance < GradientSpanM / 2) a--;
            while (b < s.Count - 1 && s[b].Distance - s[i].Distance < GradientSpanM / 2) b++;

            double run = s[b].Distance - s[a].Distance;
            gradient[i] = run > 1.0 ? (s[b].Height - s[a].Height) / run : 0;
        }

        return gradient;
    }

    /// <summary>
    /// Marks ground the run covers more than once, well separated in time.
    ///
    /// <para>
    /// Bucketed into a grid so this stays linear: a lap session is tens of thousands of samples
    /// and comparing every pair would be minutes of work for a fact the director needs before the
    /// first frame.
    /// </para>
    /// </summary>
    private static bool[] Revisits(IReadOnlyList<Sample> s)
    {
        const double NearM = 20.0;
        const double ApartSeconds = 180.0;
        const double Cell = NearM;

        var buckets = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < s.Count; i++)
        {
            var key = ((long)Math.Floor(s[i].E / Cell), (long)Math.Floor(s[i].N / Cell));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
            list.Add(i);
        }

        var revisited = new bool[s.Count];

        for (int i = 0; i < s.Count; i++)
        {
            long cx = (long)Math.Floor(s[i].E / Cell), cy = (long)Math.Floor(s[i].N / Cell);

            for (long dx = -1; dx <= 1 && !revisited[i]; dx++)
                for (long dy = -1; dy <= 1 && !revisited[i]; dy++)
                {
                    if (!buckets.TryGetValue((cx + dx, cy + dy), out var list)) continue;

                    foreach (int j in list)
                    {
                        if (Math.Abs(s[i].Time - s[j].Time) < ApartSeconds) continue;

                        double de = s[i].E - s[j].E, dn = s[i].N - s[j].N;
                        if (de * de + dn * dn < NearM * NearM) { revisited[i] = true; break; }
                    }
                }
        }

        return revisited;
    }

    // ---- detectors ----------------------------------------------------------------------

    private static void Kinematics(IReadOnlyList<Sample> s, List<CinemaEvent> into)
    {
        double peak = 0;
        int peakAt = 0;
        for (int i = 0; i < s.Count; i++)
            if (s[i].Speed > peak) { peak = s[i].Speed; peakAt = i; }

        if (peak > 1.0)
            into.Add(new CinemaEvent(s[peakAt].Time, CinemaEventKind.PeakSpeed, 1f, s[peakAt].World));

        int surge = (int)(5 / Step);
        int fade = (int)(20 / Step);

        for (int i = surge; i < s.Count; i++)
        {
            double before = s[i - surge].Speed;
            double now = s[i].Speed;

            // An attack is proportionally AND absolutely faster, so a crawl speeding up slightly
            // does not register as a sprint.
            if (before > 0.8 && now > before * 1.15 && now - before > 0.5)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.SpeedSurge,
                    (float)Math.Clamp((now - before) / 2.5, 0.25, 1.0), s[i].World));

            if (i >= fade)
            {
                double earlier = s[i - fade].Speed;
                if (earlier > 1.5 && now < earlier * 0.75)
                    into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Fade,
                        (float)Math.Clamp((earlier - now) / 2.0, 0.2, 0.8), s[i].World));
            }
        }

        // A stop is the START of a quiet stretch, not every second of it.
        bool stopped = false;
        for (int i = 0; i < s.Count; i++)
        {
            bool slow = s[i].Speed < 0.3;
            if (slow && !stopped)
            {
                int j = i;
                while (j < s.Count && s[j].Speed < 0.3) j++;
                double held = s[Math.Min(j, s.Count - 1)].Time - s[i].Time;
                if (held > 3)
                    into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Stop,
                        (float)Math.Clamp(held / 30.0, 0.2, 0.7), s[i].World));
                stopped = true;
            }
            else if (!slow) stopped = false;
        }

        // The walk/run changeover. Hysteresis, and a dwell, because a runner holding station
        // either side of a single threshold crosses it constantly: on this track a bare
        // comparison fired 47 times in 54 minutes, which is a cut every minute on a
        // non-event. Enter the run at 2.2 m/s, drop back at 1.8, and only once it sticks.
        bool running = s[0].Speed >= 2.2;
        double changedAt = double.NegativeInfinity;

        for (int i = 1; i < s.Count; i++)
        {
            bool next = running ? s[i].Speed >= 1.8 : s[i].Speed >= 2.2;
            if (next == running) continue;

            // must hold the new state for a while to count as a change of gait
            int held = 0;
            for (int k = i; k < s.Count && held < 8; k++, held++)
                if ((running ? s[k].Speed >= 1.8 : s[k].Speed >= 2.2) == running) break;
            if (held < 8) continue;

            running = next;
            if (s[i].Time - changedAt < 45) continue;

            changedAt = s[i].Time;
            into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.GaitChange, 0.35f, s[i].World));
        }
    }

    private static void Elevation(IReadOnlyList<Sample> s, double[] gradient, List<CinemaEvent> into)
    {
        for (int i = 1; i < s.Count; i++)
        {
            if (gradient[i - 1] < 0.06 && gradient[i] >= 0.06)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.ClimbOnset,
                    (float)Math.Clamp(gradient[i] / 0.15, 0.3, 1.0), s[i].World));

            if (gradient[i - 1] > -0.06 && gradient[i] <= -0.06)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.DescentOnset,
                    (float)Math.Clamp(-gradient[i] / 0.15, 0.3, 1.0), s[i].World));
        }

        // A wall is the gradient's own rate of change: the road kicking up under the rider.
        for (int i = 2; i < s.Count - 2; i++)
        {
            double kick = gradient[i + 2] - gradient[i - 2];
            if (kick > 0.10)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Wall,
                    (float)Math.Clamp(kick / 0.25, 0.3, 1.0), s[i].World));
        }

        Extrema(s, into);

        double climbed = 0, nextMark = 100;
        for (int i = 1; i < s.Count; i++)
        {
            double rise = s[i].Height - s[i - 1].Height;
            if (rise > 0) climbed += rise;
            if (climbed >= nextMark)
            {
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.AscentMilestone, 0.4f, s[i].World));
                nextMark += 100;
            }
        }
    }

    /// <summary>Local maxima and minima of the draped profile, filtered by prominence.</summary>
    private static void Extrema(IReadOnlyList<Sample> s, List<CinemaEvent> into)
    {
        int span = (int)(45 / Step);

        for (int i = span; i < s.Count - span; i++)
        {
            double here = s[i].Height;
            double lowBefore = double.MaxValue, lowAfter = double.MaxValue;
            double highBefore = double.MinValue, highAfter = double.MinValue;
            bool top = true, bottom = true;

            for (int k = i - span; k <= i + span; k++)
            {
                if (s[k].Height > here + 0.01) top = false;
                if (s[k].Height < here - 0.01) bottom = false;

                if (k < i) { lowBefore = Math.Min(lowBefore, s[k].Height); highBefore = Math.Max(highBefore, s[k].Height); }
                if (k > i) { lowAfter = Math.Min(lowAfter, s[k].Height); highAfter = Math.Max(highAfter, s[k].Height); }
            }

            if (top)
            {
                double prominence = here - Math.Max(lowBefore, lowAfter);
                if (prominence >= ProminenceM)
                    into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Summit,
                        (float)Math.Clamp(prominence / 120.0, 0.4, 1.0), s[i].World));
            }
            else if (bottom)
            {
                double depth = Math.Min(highBefore, highAfter) - here;
                if (depth >= ProminenceM)
                    into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Valley,
                        (float)Math.Clamp(depth / 120.0, 0.3, 0.8), s[i].World));
            }
        }
    }

    private static void Geometry(IReadOnlyList<Sample> s, List<CinemaEvent> into)
    {
        // signed turn over a short window, in degrees
        var turns = new double[s.Count];
        int window = (int)(8 / Step);

        for (int i = window; i < s.Count; i++)
        {
            var a = s[i - window].Heading;
            var b = s[i].Heading;
            double cross = a.X * b.Z - a.Z * b.X;
            double dot = Math.Clamp(a.X * b.X + a.Z * b.Z, -1, 1);
            turns[i] = Mathf.RadToDeg((float)Math.Atan2(cross, dot));
        }

        for (int i = window; i < s.Count; i++)
        {
            double turn = Math.Abs(turns[i]);
            if (turn >= 120)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Hairpin,
                    (float)Math.Clamp(turn / 180.0, 0.5, 1.0), s[i].World));
            else if (turn >= 25)
                into.Add(new CinemaEvent(s[i].Time, CinemaEventKind.Bend,
                    (float)Math.Clamp(turn / 90.0, 0.25, 0.7), s[i].World));
        }

        // A run of hairpins alternating in direction is a switchback climb, which is the one
        // thing the top-down shot exists for.
        var hairpins = new List<(double Time, int Sign, Vector3 Where)>();
        for (int i = window; i < s.Count; i++)
            if (Math.Abs(turns[i]) >= 100
                && (hairpins.Count == 0 || s[i].Time - hairpins[^1].Time > 10))
                hairpins.Add((s[i].Time, Math.Sign(turns[i]), s[i].World));

        for (int i = 2; i < hairpins.Count; i++)
            if (hairpins[i].Sign != hairpins[i - 1].Sign
                && hairpins[i - 1].Sign != hairpins[i - 2].Sign
                && hairpins[i].Time - hairpins[i - 2].Time < 240)
                into.Add(new CinemaEvent(hairpins[i - 1].Time, CinemaEventKind.Switchbacks, 0.9f,
                    hairpins[i - 1].Where));

        // A long straight, for the compressed long lens. One event per straight, not one per
        // sample along it, so the cursor jumps past what it just consumed.
        for (int i = window; i < s.Count; i++)
        {
            int j = i;
            double total = 0;
            while (j < s.Count && s[j].Distance - s[i].Distance < 300)
            {
                total += Math.Abs(turns[j]);
                j++;
            }

            if (j < s.Count && total < 12)
            {
                into.Add(new CinemaEvent(s[(i + j) / 2].Time, CinemaEventKind.LongStraight, 0.3f,
                    s[(i + j) / 2].World));
                i = j;
            }
        }
    }

    // ---- thinning -----------------------------------------------------------------------

    /// <summary>
    /// Collapses each kind's clusters down to their strongest member.
    ///
    /// <para>
    /// Detectors fire on every sample that satisfies them, so a two-minute climb yields a hundred
    /// ClimbOnset events. Without this the director has far more candidates than screen time and
    /// ends up cutting on noise rather than on moments.
    /// </para>
    /// </summary>
    private static List<CinemaEvent> Thin(List<CinemaEvent> events)
    {
        var kept = new List<CinemaEvent>();

        foreach (var group in events.GroupBy(e => e.Kind))
        {
            CinemaEvent? best = null;

            foreach (var e in group.OrderBy(e => e.Time))
            {
                double spacing = group.Key is CinemaEventKind.SpeedSurge or CinemaEventKind.Fade
                    ? EffortSpacing : MinSpacing;

                if (best is { } b && e.Time - b.Time < spacing)
                {
                    if (e.Strength > b.Strength) best = e;
                    continue;
                }

                if (best is { } flush) kept.Add(flush);
                best = e;
            }

            if (best is { } last) kept.Add(last);
        }

        kept.Sort((a, b) => a.Time.CompareTo(b.Time));
        return kept;
    }
}
