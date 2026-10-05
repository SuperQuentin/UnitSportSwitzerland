using System.Globalization;
using Godot;

namespace UnitSport.World;

/// <summary>
/// The world's time of day online (#452): one clock every peer computes, not one each.
///
/// <para>
/// The server always owns it: the hour <see cref="Hour0"/> at <see cref="Epoch"/> on the server's
/// clock (<see cref="Net.ClockSync.ServerNow"/>) and how fast it runs. Those three numbers cross the
/// wire, on join and on each <c>/time</c>, and every peer turns them into "the hour now" from its
/// own estimate of the server clock. Nothing is summed frame by frame, so nothing drifts, a late
/// joiner sees the same sky, and the trip over the wire does not move it.
/// </para>
///
/// <para>
/// Offline it is never <see cref="Active"/>: <see cref="DayNight"/> keeps this machine's own
/// clock from the settings. Static, because a process has one world.
/// </para>
/// </summary>
public static class WorldClock
{
    /// <summary>The hour at <see cref="Epoch"/>.</summary>
    public static double Hour0 { get; private set; }

    /// <summary>When <see cref="Hour0"/> held, in <see cref="Net.ClockSync.ServerNow"/> seconds.</summary>
    public static double Epoch { get; private set; }

    /// <summary>Real minutes per game day; 0 = stopped.</summary>
    public static float MinutesPerDay { get; private set; }

    /// <summary>The server runs it, or this client has been handed it.</summary>
    public static bool Active { get; private set; }

    /// <summary>The hour at this moment of the server's clock. Pure.</summary>
    public static double HourAt(double serverNow) => TimeCommand.HourAt(Hour0, Epoch, serverNow, MinutesPerDay);

    /// <summary>The hour now, by the server's clock as this peer knows it.</summary>
    public static double Hour => HourAt(Net.ClockSync.ServerNow);

    /// <summary>
    /// The hour for game logic on any peer: what the sky shows on a client (eased in after join),
    /// the world clock on the server, which has no sky. Noon if there is neither.
    /// </summary>
    public static double CurrentHour => DayNight.Instance?.Hour ?? (Active ? Hour : 12.0);

    /// <summary>Takes the clock as given, by the server or from its saved state.</summary>
    public static void Set(double hour0, double epoch, float minutesPerDay)
    {
        Hour0 = TimeCommand.Wrap(hour0);
        Epoch = epoch;
        MinutesPerDay = Math.Clamp(minutesPerDay, 0f, TimeCommand.MaxMinutesPerDay);
        Active = true;
    }

    /// <summary>Server: the clock from now on reads <paramref name="hour"/> and runs at this speed.</summary>
    public static void Rebase(double hour, float minutesPerDay) => Set(hour, Net.ClockSync.ServerNow, minutesPerDay);

    /// <summary>Back to offline: a client that left the server.</summary>
    public static void Reset() => Active = false;

    // ---- server start and persistence -------------------------------------------------------

    private const string File = "user://world_clock.cfg";

    /// <summary>
    /// Server: starts the clock. A dedicated server picks up at the hour it stopped at, so a restart
    /// does not jump the sky back to the morning. From the settings instead: a server hosted from
    /// the menu (<c>--parent-pid</c>, the player's own start time), a fixed <c>--time</c>, a test run
    /// (<c>--world</c>, <c>--systems</c>).
    /// </summary>
    public static void StartServer()
    {
        var settings = Core.GameSettings.Current;
        bool fixedStart = !Persists || Array.IndexOf(OS.GetCmdlineUserArgs(), "--time") >= 0;
        if (!fixedStart && Load() is { } saved)
        {
            Rebase(saved.Hour, saved.MinutesPerDay);
            GD.Print($"[time] the world's clock, as the server left it: {TimeCommand.Format(Hour0)}, {TimeCommand.DescribeSpeed(MinutesPerDay)}");
            return;
        }
        Rebase(settings.StartHour, settings.DayLengthMinutes);
        GD.Print($"[time] the world's clock starts at {TimeCommand.Format(Hour0)}, {TimeCommand.DescribeSpeed(MinutesPerDay)}");
    }

    /// <summary>Server: writes the hour now, so a restart picks up there. Not in test runs.</summary>
    public static void Save()
    {
        if (!Active || !Persists) return;
        var cfg = new ConfigFile();
        // strings, invariant: a float through ConfigFile is fine, but this is read by people too
        cfg.SetValue("clock", "hour", Hour.ToString("0.####", CultureInfo.InvariantCulture));
        cfg.SetValue("clock", "minutes_per_day", MinutesPerDay.ToString("0.####", CultureInfo.InvariantCulture));
        cfg.Save(File);
    }

    /// <summary>A dedicated server outside tests keeps its clock across restarts.</summary>
    private static bool Persists => !Core.Systems.Narrowed && Array.IndexOf(OS.GetCmdlineUserArgs(), "--parent-pid") < 0;

    private static (double Hour, float MinutesPerDay)? Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(File) != Error.Ok) return null;
        string h = cfg.GetValue("clock", "hour", "").AsString();
        string m = cfg.GetValue("clock", "minutes_per_day", "").AsString();
        return double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out double hour)
            && float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out float mpd)
            ? (hour, mpd) : null;
    }
}
