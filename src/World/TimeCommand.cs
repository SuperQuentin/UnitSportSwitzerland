using System.Globalization;

namespace UnitSport.World;

/// <summary>What a <c>/time</c> line asks for.</summary>
public enum TimeOp
{
    /// <summary>Show the clock.</summary>
    Query,

    /// <summary>Move the clock to an hour.</summary>
    Set,

    /// <summary>Move the clock on by some hours (back, if negative).</summary>
    Add,

    /// <summary>Real minutes per game day; 0 stops the clock.</summary>
    Speed,
}

/// <summary>
/// <c>/time</c>, Minecraft style: <c>/time set noon</c>, <c>/time set 21:30</c>, <c>/time add 2</c>,
/// <c>/time speed 0</c>. Pure text in, an operation out — offline the chat applies it to
/// <see cref="DayNight"/>, online the server applies it to the world's clock and sends that round.
/// </summary>
public static class TimeCommand
{
    /// <summary>
    /// Names for <c>/time set</c>. Sunrise and sunset are the default summer day's; an occasion
    /// may move the real ones (a short winter day).
    /// </summary>
    public static readonly (string Name, double Hour)[] Named =
    [
        ("sunrise", 6),
        ("day", 8),
        ("noon", 12),
        ("sunset", 18),
        ("night", 21),
        ("midnight", 0),
    ];

    public static readonly string[] Verbs = ["query", "set", "add", "speed"];

    public const string Usage = "Usage: /time [query] | set <hh:mm | sunrise | day | noon | sunset | night | midnight> | add <hours> | speed <minutes per day, 0 stops>";

    /// <summary>Longest day <c>/time speed</c> takes, the same bound as the settings.</summary>
    public const float MaxMinutesPerDay = 240f;

    /// <summary>Parses the words after <c>/time</c>. A bare hour (<c>/time 14:00</c>) is a set.</summary>
    public static bool TryParse(string[] args, out TimeOp op, out double value, out string error)
    {
        op = TimeOp.Query;
        value = 0;
        error = "";

        if (args.Length == 0) return true;

        string verb = args[0].ToLowerInvariant();
        string arg = args.Length > 1 ? args[1] : "";
        switch (verb)
        {
            case "query" when args.Length == 1:
                return true;

            case "set" when args.Length == 2:
                op = TimeOp.Set;
                if (TryParseHour(arg, out value)) return true;
                error = $"'{arg}' is not a time of day. {Usage}";
                return false;

            case "add" when args.Length == 2:
                op = TimeOp.Add;
                if (TryParseNumber(arg, out value)) return true;
                error = $"'{arg}' is not a number of hours.";
                return false;

            case "speed" when args.Length == 2:
                op = TimeOp.Speed;
                if (TryParseNumber(arg, out value) && value >= 0 && value <= MaxMinutesPerDay) return true;
                error = $"Speed is real minutes per game day, 0 to {MaxMinutesPerDay:0} (0 stops the clock).";
                return false;
        }

        if (args.Length == 1 && TryParseHour(args[0], out value))
        {
            op = TimeOp.Set;
            return true;
        }

        error = Usage;
        return false;
    }

    /// <summary>"14", "14:30", "14.5" or a name, to an hour in 0..24.</summary>
    public static bool TryParseHour(string text, out double hour)
    {
        hour = 0;
        text = text.Trim().ToLowerInvariant();
        foreach (var (name, h) in Named)
            if (name == text)
            {
                hour = h;
                return true;
            }

        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int hh)
                || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int mm)
                || hh > 24 || mm > 59 || (hh == 24 && mm > 0))
                return false;
            hour = (hh + mm / 60.0) % 24.0;
            return true;
        }

        if (!TryParseNumber(text, out hour) || hour < 0 || hour > 24) return false;
        hour %= 24.0;
        return true;
    }

    /// <summary>Hours after <paramref name="seconds"/> of real time at this day length.</summary>
    public static double Advance(double hour, double seconds, float minutesPerDay) =>
        minutesPerDay > 0 ? Wrap(hour + seconds * 24.0 / (minutesPerDay * 60.0)) : Wrap(hour);

    public static double Wrap(double hour) => ((hour % 24.0) + 24.0) % 24.0;

    /// <summary>
    /// The world clock's hour (#452, <see cref="WorldClock"/>): <paramref name="hour0"/> at
    /// <paramref name="epoch"/>, read at <paramref name="now"/>, both on the server's clock. Any two
    /// peers that agree on the server's clock agree on the hour.
    /// </summary>
    public static double HourAt(double hour0, double epoch, double now, float minutesPerDay) =>
        Advance(hour0, now - epoch, minutesPerDay);

    /// <summary>Hours from <paramref name="to"/> to <paramref name="from"/> the short way round the dial, -12..12.</summary>
    public static double ShortWay(double from, double to) => Wrap(from - to + 12.0) - 12.0;

    /// <summary>"14:05".</summary>
    public static string Format(double hour)
    {
        int minutes = (int)Math.Floor(Wrap(hour) * 60.0 + 1e-6) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>"24 min a day" / "stopped", for replies.</summary>
    public static string DescribeSpeed(float minutesPerDay) =>
        minutesPerDay > 0 ? $"{minutesPerDay.ToString("0.#", CultureInfo.InvariantCulture)} min a day" : "stopped";

    /// <summary>Invariant culture: the French locale would read "1.5" as 15.</summary>
    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
