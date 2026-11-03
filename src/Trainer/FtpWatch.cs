using Godot;
using UnitSport.Core;

namespace UnitSport.Trainer;

/// <summary>
/// Runs the hidden FTP test (#902, <see cref="FtpDetector"/>) on the local rider's bike rides:
/// <see cref="Sample"/> every physics step on the road bike, <see cref="RideEnded"/> when they
/// get off it or leave the game. Nothing shows during the ride; a better FTP is saved to
/// <c>user://rider.json</c> and announced through <see cref="Said"/> (a toast).
///
/// <para>
/// Only <b>measured</b> watts count, a trainer's or a power meter's. Keyboard watts are whatever the
/// key gives: a 30-minute ride holding W would "detect" 95% of <c>RiderWatts</c>. <c>--ftp-keys</c>
/// counts them anyway, to try the test without a trainer.
/// </para>
/// </summary>
public static class FtpWatch
{
    private const string File = "user://rider.json";

    private static readonly PowerLog Log = new();
    private static readonly bool CountKeys = CmdArgs.Has("--ftp-keys");
    private static RiderProfile? _profile;

    /// <summary>A detected FTP, as a sentence for the HUD.</summary>
    public static event Action<string>? Said;

    /// <summary>The rider's profile, loaded on first use.</summary>
    public static RiderProfile Profile => _profile ??= Load();

    /// <summary>One physics step on the bike: the watts the model used, and whether they were measured.</summary>
    public static void Sample(float watts, bool measured, double dt)
    {
        if (measured || CountKeys) Log.Add(watts, dt);
    }

    /// <summary>Off the bike or out of the game: the ride is over, test it and start a new one.</summary>
    public static void RideEnded()
    {
        if (Log.Seconds.Count == 0) return;
        var seconds = Log.Seconds;
        int? was = Profile.Ftp;
        var estimate = FtpDetector.Detect(seconds, was);
        static string W(double? w) => w is { } v ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " W" : "-";
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[ftp] ride {seconds.Count / 60.0:0.0} min, best 20 min {W(FtpDetector.BestAverage(seconds, FtpDetector.Window20))}, "
            + $"best 60 min {W(FtpDetector.BestAverage(seconds, FtpDetector.Window60))}, FTP {W(was)} -> {(estimate is { } d ? W(d.Watts) : "unchanged")}"));
        Log.Clear();
        if (estimate is not { } e) return;

        Profile.Apply(e, DateTime.UtcNow);
        try { JsonStore.Save(File, Profile, JsonStore.Indented); }
        catch (Exception ex) { GD.PushWarning($"[ftp] could not save {File}: {ex.Message}"); }
        Said?.Invoke(RiderProfile.Message(e, was));
    }

    private static RiderProfile Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(File);
            if (System.IO.File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<RiderProfile>(System.IO.File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { GD.PushWarning($"[ftp] could not read {File}: {ex.Message}"); }
        return new();
    }
}
