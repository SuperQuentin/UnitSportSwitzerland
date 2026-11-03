using System.Globalization;

namespace RideLink.Simulation;

/// <summary>One instant of a simulated or replayed ride.</summary>
/// <param name="PowerWatts">Rider power.</param>
/// <param name="CadenceRpm">Pedal cadence.</param>
/// <param name="HeartRateBpm">Heart rate.</param>
public readonly record struct RideSample(int PowerWatts, double CadenceRpm, int HeartRateBpm);

/// <summary>Supplies what the rider is doing at a point in the session.</summary>
public interface IRideProfile
{
    /// <summary>Total length, or null when the profile runs forever.</summary>
    TimeSpan? Duration { get; }

    RideSample Sample(TimeSpan elapsed);
}

/// <summary>Constant effort. The simplest thing that produces a believable stream.</summary>
public sealed class SteadyRideProfile(int watts = 180, double cadenceRpm = 88, int heartRateBpm = 140)
    : IRideProfile
{
    public TimeSpan? Duration => null;

    public RideSample Sample(TimeSpan elapsed)
    {
        // A little breathing so nothing downstream is accidentally tested against a constant.
        double wobble = Math.Sin(elapsed.TotalSeconds * 0.7);
        return new RideSample(
            (int)Math.Round(watts + wobble * 6),
            cadenceRpm + wobble * 2,
            (int)Math.Round(heartRateBpm + wobble * 2));
    }
}

/// <summary>Linear ramp between two efforts, for testing responsiveness and clamping.</summary>
public sealed class RampRideProfile(
    int startWatts, int endWatts, TimeSpan duration, double cadenceRpm = 90, int startHeartRate = 110)
    : IRideProfile
{
    public TimeSpan? Duration => duration;

    public RideSample Sample(TimeSpan elapsed)
    {
        double t = duration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0, 1);

        int watts = (int)Math.Round(startWatts + (endWatts - startWatts) * t);
        // Heart rate lags effort, as it does on a real body.
        int heartRate = (int)Math.Round(startHeartRate + (watts - startWatts) * 0.18);
        return new RideSample(watts, cadenceRpm, Math.Clamp(heartRate, 40, 210));
    }
}

/// <summary>Alternating hard and easy blocks.</summary>
public sealed class IntervalRideProfile(
    int hardWatts, int easyWatts, TimeSpan hardDuration, TimeSpan easyDuration)
    : IRideProfile
{
    public TimeSpan? Duration => null;

    public RideSample Sample(TimeSpan elapsed)
    {
        double cycle = (hardDuration + easyDuration).TotalSeconds;
        double phase = elapsed.TotalSeconds % cycle;
        bool hard = phase < hardDuration.TotalSeconds;

        return new RideSample(
            hard ? hardWatts : easyWatts,
            hard ? 95 : 80,
            hard ? 165 : 130);
    }
}

/// <summary>
/// Replays a recorded session from CSV.
/// <para>
/// Expected columns, header row required, order free:
/// <c>seconds</c> (or <c>time</c>), <c>power</c> (or <c>watts</c>), <c>cadence</c> (or <c>rpm</c>),
/// <c>heartrate</c> (or <c>hr</c>, <c>bpm</c>). Missing columns become zero.
/// </para>
/// <para>
/// Values between recorded rows are linearly interpolated, so a 1 Hz recording still drives a
/// 4 Hz notification rate smoothly. Parsing is invariant-culture throughout: this library is
/// developed on a fr-CH machine where <c>double.Parse("180.5")</c> would otherwise fail.
/// </para>
/// </summary>
public sealed class CsvRideProfile : IRideProfile
{
    private readonly (double Seconds, RideSample Sample)[] _rows;

    private CsvRideProfile((double, RideSample)[] rows)
    {
        _rows = rows;
        Duration = rows.Length == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(rows[^1].Item1);
    }

    public TimeSpan? Duration { get; }

    /// <summary>Number of recorded rows.</summary>
    public int RowCount => _rows.Length;

    public static CsvRideProfile Load(string path) =>
        Parse(File.ReadAllLines(path));

    public static CsvRideProfile Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2)
            throw new ArgumentException("CSV needs a header row and at least one data row", nameof(lines));

        string[] header = SplitRow(lines[0]);
        int timeColumn = FindColumn(header, "seconds", "second", "time", "elapsed", "t");
        int powerColumn = FindColumn(header, "power", "watts", "w");
        int cadenceColumn = FindColumn(header, "cadence", "rpm", "cad");
        int heartRateColumn = FindColumn(header, "heartrate", "heart_rate", "hr", "bpm");

        if (timeColumn < 0)
            throw new ArgumentException(
                $"CSV has no time column. Looked for seconds/time/elapsed in: {string.Join(", ", header)}");

        var rows = new List<(double, RideSample)>(lines.Count - 1);
        for (int i = 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] cells = SplitRow(line);
            if (cells.Length <= timeColumn) continue;

            double seconds = ParseDouble(cells, timeColumn);
            var sample = new RideSample(
                (int)Math.Round(ParseDouble(cells, powerColumn)),
                ParseDouble(cells, cadenceColumn),
                (int)Math.Round(ParseDouble(cells, heartRateColumn)));

            rows.Add((seconds, sample));
        }

        if (rows.Count == 0) throw new ArgumentException("CSV contained no usable data rows");

        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new CsvRideProfile(rows.ToArray());
    }

    public RideSample Sample(TimeSpan elapsed)
    {
        double t = elapsed.TotalSeconds;

        if (t <= _rows[0].Seconds) return _rows[0].Sample;
        if (t >= _rows[^1].Seconds) return _rows[^1].Sample;

        // Binary search for the bracketing pair, then interpolate.
        int low = 0, high = _rows.Length - 1;
        while (high - low > 1)
        {
            int mid = (low + high) / 2;
            if (_rows[mid].Seconds <= t) low = mid;
            else high = mid;
        }

        var (t0, a) = _rows[low];
        var (t1, b) = _rows[high];
        double span = t1 - t0;
        double f = span <= 0 ? 0 : (t - t0) / span;

        return new RideSample(
            (int)Math.Round(a.PowerWatts + (b.PowerWatts - a.PowerWatts) * f),
            a.CadenceRpm + (b.CadenceRpm - a.CadenceRpm) * f,
            (int)Math.Round(a.HeartRateBpm + (b.HeartRateBpm - a.HeartRateBpm) * f));
    }

    private static string[] SplitRow(string line) =>
        line.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);

    private static int FindColumn(string[] header, params string[] names)
    {
        for (int i = 0; i < header.Length; i++)
        {
            string cell = header[i].Trim().ToLowerInvariant().Replace(" ", "");
            if (names.Contains(cell)) return i;
        }
        return -1;
    }

    private static double ParseDouble(string[] cells, int column)
    {
        if (column < 0 || column >= cells.Length) return 0;
        return double.TryParse(cells[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : 0;
    }
}
