using System.Globalization;
using UnitSport.Core;

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
    /// Names for <c>/time set</c>, with their hours in Switzerland. Sunrise and sunset are the fixed
    /// day's here; under the real sun (#1430) they are today's at Bern (<see cref="SunPath.RiseOrSet(bool)"/>).
    /// Far from Switzerland they are read off the sun where the player stands (<see cref="LocalHours"/>, #1801).
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

    public const string Usage = "Usage: /time [query] [zone] | set <hh:mm | sunrise | day | noon | sunset | night | midnight> [zone] | add <hours> | speed <minutes per day, 0 stops | normal | real>."
        + " Hours read in the zone you are in unless you name one (here, swiss, utc, utc+8); the named hours are the sun's where you stand.";

    /// <summary>
    /// Longest day <c>/time speed</c> takes: a real day, so <c>/time speed real</c> runs the sky at
    /// one day a day. The settings' slider stops at 240.
    /// </summary>
    public const float MaxMinutesPerDay = RealMinutesPerDay;

    /// <summary>The game's usual day, 24 real minutes (<c>/time speed normal</c>), and a real day (<c>/time speed real</c>).</summary>
    public const float NormalMinutesPerDay = 24f, RealMinutesPerDay = 24f * 60f;

    /// <summary>Words for <c>/time speed</c>.</summary>
    public static readonly (string Name, float MinutesPerDay)[] SpeedNames =
    [
        ("normal", NormalMinutesPerDay),
        ("real", RealMinutesPerDay),
    ];

    /// <summary>
    /// Parses the words after <c>/time</c>. A bare hour (<c>/time 14:00</c>) is a set. Named hours
    /// come back as their hour at Bern; <see cref="TryParse(string[], out TimeRequest, out string)"/>
    /// keeps the name and the zone for <see cref="Resolve"/>.
    /// </summary>
    public static bool TryParse(string[] args, out TimeOp op, out double value, out string error)
    {
        bool ok = TryParse(args, out var request, out error);
        op = request.Op;
        value = request.Value;
        return ok;
    }

    /// <summary>
    /// Parses the words after <c>/time</c> (#1801): <c>[query] [zone]</c>, <c>set &lt;hour&gt; [zone]</c>,
    /// <c>&lt;hour&gt; [zone]</c>, <c>add &lt;hours&gt;</c>, <c>speed &lt;minutes&gt;</c>.
    /// </summary>
    public static bool TryParse(string[] args, out TimeRequest request, out string error)
    {
        request = new TimeRequest(TimeOp.Query, 0, null, null);
        error = "";

        if (args.Length == 0) return true;

        string verb = args[0].ToLowerInvariant();
        string arg = args.Length > 1 ? args[1] : "";
        switch (verb)
        {
            case "query" when args.Length <= 2:
                if (args.Length == 1) return true;
                if (ClockZone.TryParse(arg, out var queryZone))
                {
                    request = request with { Zone = queryZone, ZoneNamed = true };
                    return true;
                }
                error = $"'{arg}' is not a time zone. {Usage}";
                return false;

            case "set" when args.Length is 2 or 3:
                return TrySet(arg, args.Length == 3 ? args[2] : null, out request, out error);

            case "add" when args.Length == 2:
                if (TryParseNumber(arg, out double hours))
                {
                    request = request with { Op = TimeOp.Add, Value = hours };
                    return true;
                }
                request = request with { Op = TimeOp.Add };
                error = $"'{arg}' is not a number of hours.";
                return false;

            case "speed" when args.Length == 2:
                request = request with { Op = TimeOp.Speed };
                string speedWord = arg.Trim().ToLowerInvariant();
                int named = Array.FindIndex(SpeedNames, n => n.Name == speedWord);
                if (named >= 0)
                {
                    request = request with { Value = SpeedNames[named].MinutesPerDay };
                    return true;
                }
                if (TryParseNumber(arg, out double speed) && speed >= 0 && speed <= MaxMinutesPerDay)
                {
                    request = request with { Value = speed };
                    return true;
                }
                error = $"Speed is real minutes per game day, 0 to {MaxMinutesPerDay:0} (0 stops the clock), or normal ({NormalMinutesPerDay:0} min) or real (a day a day).";
                return false;
        }

        if (args.Length <= 2 && TryParseHour(args[0], out _))
            return TrySet(args[0], args.Length == 2 ? args[1] : null, out request, out error);

        if (args.Length == 1 && ClockZone.TryParse(args[0], out var zone))
        {
            request = request with { Zone = zone, ZoneNamed = true };
            return true;
        }

        error = Usage;
        return false;
    }

    private static bool TrySet(string hourWord, string? zoneWord, out TimeRequest request, out string error)
    {
        request = new TimeRequest(TimeOp.Set, 0, null, null);
        error = "";
        if (!TryParseHour(hourWord, out double hour))
        {
            error = $"'{hourWord}' is not a time of day. {Usage}";
            return false;
        }
        string word = hourWord.Trim().ToLowerInvariant();
        string? named = Array.Exists(Named, n => n.Name == word) ? word : null;
        ClockZone? zone = null;
        if (zoneWord != null && !ClockZone.TryParse(zoneWord, out zone))
        {
            error = $"'{zoneWord}' is not a time zone (here, swiss, utc, utc+8, utc-3:30).";
            return false;
        }
        request = new TimeRequest(TimeOp.Set, hour, named, zone) { ZoneNamed = zoneWord != null };
        return true;
    }

    /// <summary>
    /// The world clock's (Swiss) hour a set asks for, from a player at <paramref name="at"/> (an Earth place;
    /// null: nobody's place, the console, read as home), and the zone its hours read in: the one
    /// named, or the player's own (<see cref="LocalHours.ZoneAt"/>). A named hour is the sun's at the
    /// place whatever the zone. False, with a reason, when the sun does not rise or set there today.
    /// </summary>
    public static bool Resolve(TimeRequest request, GlobalPos? at, DateOnly date, bool realSun,
        out double swissHour, out ClockZone zone, out string error)
    {
        error = "";
        var place = at ?? LocalHours.Bern;
        zone = request.Zone ?? LocalHours.ZoneAt(place, realSun);
        if (request.Named is { } name)
            return LocalHours.TryNamed(name, place, date, realSun, out swissHour, out error);
        swissHour = zone.ToSwiss(request.Value, date);
        return true;
    }

    /// <summary>
    /// A world clock (Swiss) hour as a player reads it: "14:05" on Swiss time, "22:05 UTC+8
    /// (14:05 Swiss time)" in another zone.
    /// </summary>
    public static string Describe(double swissHour, ClockZone zone, DateOnly date) =>
        zone.IsSwiss ? Format(swissHour) : $"{Format(zone.FromSwiss(swissHour, date))} {zone.Label} ({Format(swissHour)} Swiss time)";

    /// <summary>"14", "14:30", "14.5" or a name, to an hour in 0..24.</summary>
    public static bool TryParseHour(string text, out double hour)
    {
        hour = 0;
        text = text.Trim().ToLowerInvariant();
        foreach (var (name, h) in Named)
            if (name == text)
            {
                hour = name switch
                {
                    "sunrise" => SunPath.RiseOrSet(rise: true),
                    "sunset" => SunPath.RiseOrSet(rise: false),
                    _ => h,
                };
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

    /// <summary>Seconds in a day of environment time.</summary>
    public const double DaySeconds = 24 * 3600;

    /// <summary>
    /// Environment seconds per simulated second at this day length; 0 when the clock is stopped.
    /// At the default 24 min a day this is 60: environment time runs 60x simulated time, which is
    /// why a duration moved into env units must be re-expressed and never copied (#579).
    /// </summary>
    public static double DayFactor(float minutesPerDay) =>
        minutesPerDay > 0 ? DaySeconds / (minutesPerDay * 60.0) : 0;

    /// <summary>
    /// Environment seconds at <paramref name="simNow"/>, given <paramref name="env0"/> at
    /// <paramref name="envEpoch"/> — both on the simulation clock (<c>Core.SimClock</c>), so
    /// environment time rides simulation speed. Monotonic: it only ever counts up.
    /// </summary>
    public static double EnvAt(double env0, double envEpoch, double simNow, float minutesPerDay) =>
        env0 + (simNow - envEpoch) * DayFactor(minutesPerDay);

    /// <summary>
    /// The hour of the day a count of environment seconds shows, once
    /// <paramref name="hourShift"/> is allowed for. The shift is what <c>/time set</c> moves, so an
    /// admin can turn the sky to any hour without ever winding the monotonic counter back.
    /// </summary>
    public static double HourOf(double envSeconds, double hourShift = 0) =>
        Wrap((envSeconds + hourShift) / 3600.0);

    /// <summary>The shift that makes <paramref name="envSeconds"/> read <paramref name="hour"/>.</summary>
    public static double ShiftFor(double envSeconds, double hour) =>
        Wrap(hour - envSeconds / 3600.0) * 3600.0;

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
        minutesPerDay == RealMinutesPerDay ? "real time, a day a day"
        : minutesPerDay > 0 ? $"{minutesPerDay.ToString("0.#", CultureInfo.InvariantCulture)} min a day" : "stopped";

    /// <summary>Invariant culture: the French locale would read "1.5" as 15.</summary>
    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

/// <summary>
/// A parsed <c>/time</c> line (#1801): the operation and its number, the named hour when one was
/// given (resolved at the player's place, <see cref="TimeCommand.Resolve"/>), and the zone named, if
/// any (null: the player's own). <see cref="ZoneNamed"/> is true when a zone word was typed, even
/// <c>here</c>.
/// </summary>
public readonly record struct TimeRequest(TimeOp Op, double Value, string? Named, ClockZone? Zone)
{
    public bool ZoneNamed { get; init; }
}
