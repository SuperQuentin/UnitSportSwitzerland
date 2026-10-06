using System.Globalization;
using Godot;

namespace UnitSport.World;

/// <summary>
/// The world's environment time online (#452, re-keyed onto the simulation clock in #579): one
/// clock every peer computes, not one each.
///
/// <para>
/// The server always owns it. Two layers stack (<c>docs/notes/core/three-clocks.md</c>):
/// </para>
/// <code>
/// EnvNow(simNow) = Env0 + (simNow - EnvEpoch) * DayFactor(MinutesPerDay)
/// Hour           = HourOf(EnvNow, HourShift)
/// </code>
/// <para>
/// <see cref="Env0"/>, <see cref="EnvEpoch"/>, <see cref="HourShift"/> and
/// <see cref="MinutesPerDay"/> cross the wire, on join and on each <c>/time</c>, and every peer
/// turns them into "the environment time now" from its own estimate of the server clock. Nothing is
/// summed frame by frame, so nothing drifts, a late joiner sees the same sky, and the trip over the
/// wire does not move it.
/// </para>
///
/// <para>
/// <b>Keyed to <c>Core.SimClock</c>, not to <c>Net.ClockSync.ServerNow</c></b>: environment time
/// rides simulation speed, so at 0.25x the sun and the traffic lights crawl along with the cars and
/// the world stays internally consistent. Because the env layer is keyed to simulated time, a
/// simulation-speed change needs no environment rebase at all.
/// </para>
///
/// <para>
/// <b><see cref="EnvNow"/> only ever counts up</b>, which is what makes it safe for things that
/// grow and burn. <c>/time set</c> and <c>/time add</c> move <see cref="HourShift"/> instead, so an
/// admin can turn the sky to any hour without a campfire lit ten env-minutes ago becoming one lit
/// in the future.
/// </para>
///
/// <para>
/// Offline it is never <see cref="Active"/>: <see cref="DayNight"/> keeps this machine's own clock
/// from the settings, integrating a <c>delta</c> the engine has already scaled, so offline the sun
/// rides simulation speed too. Static, because a process has one world.
/// </para>
/// </summary>
public static class WorldClock
{
    /// <summary>Environment seconds at <see cref="EnvEpoch"/>.</summary>
    public static double Env0 { get; private set; }

    /// <summary>When <see cref="Env0"/> held, in <c>Core.SimClock</c> simulated seconds.</summary>
    public static double EnvEpoch { get; private set; }

    /// <summary>What <c>/time set</c> has turned the sky by, in seconds. Never moves <see cref="EnvNow"/>.</summary>
    public static double HourShift { get; private set; }

    /// <summary>Real minutes per game day at 1x simulation speed; 0 = stopped.</summary>
    public static float MinutesPerDay { get; private set; }

    /// <summary>The server runs it, or this client has been handed it.</summary>
    public static bool Active { get; private set; }

    /// <summary>
    /// The day length in force on this screen: the server's when it owns the clock, this machine's
    /// own otherwise. Offline <see cref="MinutesPerDay"/> is 0 because no server ever set it, and
    /// reading that as "stopped" would freeze <see cref="EnvNow"/> at zero — nothing would ever
    /// regrow or burn down in a single-player game.
    /// </summary>
    private static float Pace => Active ? MinutesPerDay
        : DayNight.Instance?.MinutesPerDay ?? Core.GameSettings.Current.DayLengthMinutes;

    /// <summary>Environment seconds per simulated second, 0 when the clock is stopped.</summary>
    public static double DayFactor => TimeCommand.DayFactor(Pace);

    /// <summary>Environment seconds at a moment of the simulation clock. Pure.</summary>
    public static double EnvAt(double simNow) => TimeCommand.EnvAt(Env0, EnvEpoch, simNow, Pace);

    /// <summary>
    /// Environment seconds now, by the server's clock as this peer knows it. Monotonic: what
    /// anything that grows, burns or ripens should be measured against.
    /// </summary>
    public static double EnvNow => EnvAt(Core.SimClock.SimAt(Net.ClockSync.ServerNow));

    /// <summary>The hour at this moment of the server's clock. Pure.</summary>
    public static double HourAt(double serverNow) =>
        TimeCommand.HourOf(EnvAt(Core.SimClock.SimAt(serverNow)), HourShift);

    /// <summary>The hour now, by the server's clock as this peer knows it.</summary>
    public static double Hour => HourAt(Net.ClockSync.ServerNow);

    /// <summary>
    /// The hour for game logic on any peer: what the sky shows on a client (eased in after join),
    /// the world clock on the server, which has no sky. Noon if there is neither.
    /// </summary>
    public static double CurrentHour => DayNight.Instance?.Hour ?? (Active ? Hour : 12.0);

    /// <summary>Takes the clock as given, by the server or from its saved state.</summary>
    public static void Set(double env0, double envEpoch, double hourShift, float minutesPerDay)
    {
        Env0 = env0;
        EnvEpoch = envEpoch;
        HourShift = TimeCommand.Wrap(hourShift / 3600.0) * 3600.0;
        MinutesPerDay = Math.Clamp(minutesPerDay, 0f, TimeCommand.MaxMinutesPerDay);
        Active = true;
    }

    /// <summary>
    /// Server: carries the environment seconds already elapsed into <see cref="Env0"/> and runs at
    /// <paramref name="minutesPerDay"/> from now on. <see cref="EnvNow"/> is continuous across the
    /// change; the hour does not move.
    /// </summary>
    public static void Rebase(float minutesPerDay)
    {
        double sim = Core.SimClock.SimAt(Net.ClockSync.ServerNow);
        Set(EnvAt(sim), sim, HourShift, minutesPerDay);
    }

    /// <summary>Server: the sky now reads <paramref name="hour"/>, without winding <see cref="EnvNow"/> back.</summary>
    public static void SetHour(double hour) => Set(Env0, EnvEpoch, TimeCommand.ShiftFor(EnvNow, hour), MinutesPerDay);

    /// <summary>Server: moves the sky on by <paramref name="hours"/> (back, if negative). <see cref="EnvNow"/> is untouched.</summary>
    public static void AddHours(double hours) => SetHour(TimeCommand.Wrap(Hour + hours));

    /// <summary>Server: starts the clock at <paramref name="hour"/>, running at this day length.</summary>
    public static void Start(double hour, float minutesPerDay)
    {
        double sim = Core.SimClock.SimAt(Net.ClockSync.ServerNow);
        Set(0, sim, 0, minutesPerDay);
        SetHour(hour);
    }

    /// <summary>Back to offline: a client that left the server.</summary>
    public static void Reset() => Active = false;

    // ---- server start and persistence -------------------------------------------------------

    private const string File = "user://world_clock.cfg";

    /// <summary>
    /// Server: starts the clock. A dedicated server picks up at the environment time it stopped at,
    /// so a restart does not jump the sky back to the morning and nothing mid-growth resets. From
    /// the settings instead: a server hosted from the menu (<c>--parent-pid</c>, the player's own
    /// start time), a fixed <c>--time</c>, a test run (<c>--world</c>, <c>--systems</c>).
    /// </summary>
    public static void StartServer()
    {
        var settings = Core.GameSettings.Current;
        bool fixedStart = !Persists || Array.IndexOf(OS.GetCmdlineUserArgs(), "--time") >= 0;
        if (!fixedStart && Load() is { } saved)
        {
            double sim = Core.SimClock.SimAt(Net.ClockSync.ServerNow);
            Set(saved.EnvNow, sim, saved.HourShift, saved.MinutesPerDay);
            GD.Print($"[time] the world's clock, as the server left it: {TimeCommand.Format(Hour)}, {TimeCommand.DescribeSpeed(MinutesPerDay)}");
            return;
        }
        Start(settings.StartHour, settings.DayLengthMinutes);
        GD.Print($"[time] the world's clock starts at {TimeCommand.Format(Hour)}, {TimeCommand.DescribeSpeed(MinutesPerDay)}");
    }

    /// <summary>
    /// Server: writes the environment seconds now, so a restart picks up there. Not in test runs.
    /// The monotonic counter, not the hour: the hour alone loses the day count, and anything
    /// growing in environment time would reset with it.
    /// </summary>
    public static void Save()
    {
        if (!Active || !Persists) return;
        var cfg = new ConfigFile();
        // strings, invariant: a float through ConfigFile is fine, but this is read by people too
        cfg.SetValue("clock", "env_now", EnvNow.ToString("0.####", CultureInfo.InvariantCulture));
        cfg.SetValue("clock", "hour_shift", HourShift.ToString("0.####", CultureInfo.InvariantCulture));
        cfg.SetValue("clock", "minutes_per_day", MinutesPerDay.ToString("0.####", CultureInfo.InvariantCulture));
        cfg.Save(File);
    }

    /// <summary>A dedicated server outside tests keeps its clock across restarts.</summary>
    private static bool Persists => !Core.Systems.Narrowed && Array.IndexOf(OS.GetCmdlineUserArgs(), "--parent-pid") < 0;

    private static (double EnvNow, double HourShift, float MinutesPerDay)? Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(File) != Error.Ok) return null;
        string m = cfg.GetValue("clock", "minutes_per_day", "").AsString();
        if (!float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out float mpd)) return null;

        // a file written before #579 has the wrapped hour and no counter: keep the sky, start the
        // counter at zero rather than inventing a day count nothing recorded
        string e = cfg.GetValue("clock", "env_now", "").AsString();
        string s = cfg.GetValue("clock", "hour_shift", "").AsString();
        if (double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out double envNow)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double shift))
            return (envNow, shift, mpd);

        string h = cfg.GetValue("clock", "hour", "").AsString();
        return double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out double hour)
            ? (0, TimeCommand.Wrap(hour) * 3600.0, mpd) : null;
    }
}
