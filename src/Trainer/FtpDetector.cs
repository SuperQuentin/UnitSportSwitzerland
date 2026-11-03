using System.Globalization;

namespace UnitSport.Trainer;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A ride's power, one average per whole second (#902). Physics steps arrive at any rate (60 Hz,
/// a hitch of 0.2 s); each one is split across the second boundaries it straddles, so a second is
/// the true time-weighted average of what the rider put out in it, whatever the frame rate was.
/// </summary>
public sealed class PowerLog
{
    /// <summary>Above this a reading is a sensor spike, not a rider: a track sprinter peaks near 2500 W.</summary>
    public const float MaxWatts = 2500f;

    private readonly List<float> _seconds = new();
    private double _joules;      // work in the second being filled
    private double _filled;      // how much of it, s

    /// <summary>The finished seconds, oldest first.</summary>
    public IReadOnlyList<float> Seconds => _seconds;

    public void Add(float watts, double dt)
    {
        if (!(dt > 0)) return;
        double w = float.IsFinite(watts) ? Math.Clamp(watts, 0f, MaxWatts) : 0f;
        while (dt > 0)
        {
            double take = Math.Min(dt, 1.0 - _filled);
            _joules += w * take;
            _filled += take;
            dt -= take;
            if (_filled >= 1.0 - 1e-9)
            {
                _seconds.Add((float)_joules);
                _joules = 0;
                _filled = 0;
            }
        }
    }

    public void Clear()
    {
        _seconds.Clear();
        _joules = 0;
        _filled = 0;
    }
}

public enum FtpMethod
{
    /// <summary>95% of the best 20 minutes, the classic 20-minute test (a ride of 30 minutes or more).</summary>
    Best20,
    /// <summary>The best 60 minutes: FTP's own definition, the power held for an hour.</summary>
    Best60,
}

/// <summary>What one ride says about FTP, and from what.</summary>
public readonly record struct FtpEstimate(int Watts, FtpMethod Method, int Best20, int? Best60, int RideSeconds)
{
    /// <summary>"95% of your best 20 min (258 W)" / "your best 60 min".</summary>
    public string Why => Method == FtpMethod.Best60
        ? "your best 60 min"
        : string.Create(CultureInfo.InvariantCulture, $"95% of your best 20 min ({Best20} W)");
}

/// <summary>
/// The hidden FTP test (#902): no protocol, no screen. A ride on measured power of 30 minutes or
/// more is a 20-minute test, an hour or more is also a 60-minute one, and the higher reading wins.
///
/// <para>
/// It only ever raises the FTP (<see cref="Detect"/>). An easy ride is not a test: its best 20
/// minutes are what the rider chose to do, not what they could, so it says nothing about the
/// ceiling. Lowering an FTP is the rider's own call.
/// </para>
/// </summary>
public static class FtpDetector
{
    public const int Window20 = 20 * 60, Window60 = 60 * 60;

    /// <summary>The shortest ride that counts as a test. Asked for by the owner: a 30-minute ride.</summary>
    public const int MinRideSeconds = 30 * 60;

    /// <summary>Below this, the reading is a dead sensor or a coast, not a threshold.</summary>
    public const int MinFtp = 50;

    /// <summary>The best average over any <paramref name="window"/> consecutive seconds; null if the ride is shorter.</summary>
    public static double? BestAverage(IReadOnlyList<float> seconds, int window)
    {
        if (window <= 0 || seconds.Count < window) return null;
        double sum = 0;
        for (int i = 0; i < window; i++) sum += seconds[i];
        double best = sum;
        for (int i = window; i < seconds.Count; i++)
        {
            sum += seconds[i] - seconds[i - window];
            if (sum > best) best = sum;
        }
        return best / window;
    }

    /// <summary>What the ride reads as an FTP, whatever the rider's current one; null if it is no test.</summary>
    public static FtpEstimate? Estimate(IReadOnlyList<float> seconds)
    {
        if (seconds.Count < MinRideSeconds) return null;
        int best20 = (int)Math.Round(BestAverage(seconds, Window20)!.Value);
        int? best60 = BestAverage(seconds, Window60) is { } b60 ? (int)Math.Round(b60) : null;

        int from20 = (int)Math.Round(best20 * 0.95);
        var estimate = best60 is { } b && b >= from20
            ? new FtpEstimate(b, FtpMethod.Best60, best20, best60, seconds.Count)
            : new FtpEstimate(from20, FtpMethod.Best20, best20, best60, seconds.Count);
        return estimate.Watts >= MinFtp ? estimate : null;
    }

    /// <summary>The estimate when it beats <paramref name="currentFtp"/> (null: none set yet), else null.</summary>
    public static FtpEstimate? Detect(IReadOnlyList<float> seconds, int? currentFtp) =>
        Estimate(seconds) is { } e && (currentFtp is not { } now || e.Watts > now) ? e : null;
}

/// <summary>One detected or set FTP, for the history.</summary>
public sealed class FtpEntry
{
    public DateTime At { get; set; }
    public int Watts { get; set; }
    /// <summary>"Best20", "Best60", or "Manual".</summary>
    public string Source { get; set; } = "";
}

/// <summary>The rider's own numbers (<c>user://rider.json</c>). Serialised as it is: names are the JSON's.</summary>
public sealed class RiderProfile
{
    public const int HistoryLength = 20;

    /// <summary>Functional threshold power, W; null until a test or the rider sets one.</summary>
    public int? Ftp { get; set; }

    /// <summary>Newest last, at most <see cref="HistoryLength"/>.</summary>
    public List<FtpEntry> FtpHistory { get; set; } = new();

    /// <summary>Takes a detected FTP; returns the one it replaces (null if there was none).</summary>
    public int? Apply(FtpEstimate estimate, DateTime at)
    {
        int? was = Ftp;
        Ftp = estimate.Watts;
        FtpHistory.Add(new FtpEntry { At = at, Watts = estimate.Watts, Source = estimate.Method.ToString() });
        if (FtpHistory.Count > HistoryLength) FtpHistory.RemoveRange(0, FtpHistory.Count - HistoryLength);
        return was;
    }

    /// <summary>"Hidden FTP test: 245 W, 95% of your best 20 min (258 W). Was 232 W."</summary>
    public static string Message(FtpEstimate e, int? was) => string.Create(CultureInfo.InvariantCulture,
        $"Hidden FTP test: {e.Watts} W, {e.Why}.{(was is { } w ? $" Was {w} W." : " Your first FTP.")}");
}
