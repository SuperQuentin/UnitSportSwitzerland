using System.Diagnostics;
using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// What a hall's screen and speaker take from (#1298), however the video reaches this client: decoded here
/// (<see cref="CinemaVideoStream"/>) or through the server's relay (<see cref="CinemaRelayFeed"/>). Main thread only.
/// The hall is at <c>position</c> s of the video (<c>ServerNow − T0</c>); a feed shows the frame due then and the
/// sound sample due then, so peers on either way see and hear the same moment.
/// </summary>
public interface ICinemaFeed : IDisposable
{
    string Link { get; }
    double T0 { get; }

    /// <summary>2D, or a 3D film whose <see cref="Picture"/> holds both eyes side by side (phase 6).</summary>
    CinemaVideo.Stereo Stereo { get; }

    /// <summary>What it is doing, for the panel ("" once it plays), or why it cannot.</summary>
    string Status { get; }
    bool Failed { get; }
    bool Ended { get; }

    /// <summary>
    /// The frame shown now (<see cref="CinemaVideo.Width"/> x <see cref="CinemaVideo.Height"/>, RGBA8; twice as wide for a 3D
    /// film, <see cref="CinemaVideo.PackedSize"/>), null before the first.
    /// </summary>
    Image? Picture { get; }
    Color Tint { get; }

    /// <summary>The moment of the video the frame shown is of, s (-1 before the first); for the checks.</summary>
    double CurrentTime { get; }
    int Restarts { get; }

    /// <summary>The hall is at <paramref name="position"/>: true when <see cref="Picture"/> changed.</summary>
    bool Pump(double position);

    /// <summary>The 48 kHz sample index due at <paramref name="position"/>; false while there is no sound yet.</summary>
    bool SoundAt(double position, out long index);
    Vector2 Sample(long index);

    /// <summary>The sample being heard now: what a decoder may not overwrite yet.</summary>
    void Heard(long index);
}

/// <summary>
/// A link turned into what ffmpeg reads (#1298): as it is for a plain file, through yt-dlp for a site's page or a
/// stream's playlist (<see cref="CinemaVideo.NeedsLookup"/>): the video and sound URLs, live or not, the length.
/// Blocking: on a worker. The same on a client and on the server's relay.
/// </summary>
public static class CinemaLookup
{
    public readonly record struct Found(string Video, string Sound, bool Live, double Duration);

    /// <summary>The URLs of <paramref name="link"/>, or null with <paramref name="why"/> said.</summary>
    public static Found? Run(string link, out string why)
    {
        why = "";
        if (!CinemaVideo.NeedsLookup(link)) return new Found(link, link, false, 0);
        try
        {
            var psi = new ProcessStartInfo(BundledTools.Resolve("yt-dlp"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in BundledTools.YtDlpJsArgs().Concat(new[]
                     {
                         "--no-playlist", "--no-warnings", "-f", CinemaVideo.SiteFormat,
                         "-O", "%(is_live)s %(duration)s", "-O", "urls", link,
                     })) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) { why = "yt-dlp is missing: a site's link cannot play here"; return null; }
            var errors = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (p.ExitCode != 0 || lines.Length < 2)
            {
                GD.Print($"[cinema] yt-dlp could not open {link}: {errors.Result.Trim()}");
                why = "The site would not give the video";
                return null;
            }
            var head = lines[0].Split(' ');
            double.TryParse(head.Length > 1 ? head[1] : "", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double duration);
            return new Found(lines[1], lines.Length > 2 ? lines[2] : lines[1], head[0] == "True", duration);
        }
        catch (Exception e)
        {
            why = e is System.ComponentModel.Win32Exception ? "yt-dlp is missing: a site's link cannot play here" : $"yt-dlp failed: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// ffmpeg's input arguments: the web only (or a check's file), reconnecting, seeked to <paramref name="at"/>; a
    /// live playlist from near its edge.
    /// </summary>
    public static List<string> Input(string url, double at, bool live)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-nostats" };
        bool web = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        args.AddRange(new[] { "-protocol_whitelist", web ? "http,https,tls,tcp,crypto,hls" : "file,crypto" });
        if (web) args.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5" });
        // a live playlist from near its edge, not the start of its window: every client a segment or two behind live
        if (live && url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-live_start_index", "-2" });
        if (at > 0.01) args.AddRange(new[] { "-ss", at.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", url });
        return args;
    }

    /// <summary>Starts ffmpeg with <paramref name="args"/> (stdout piped, stderr to <paramref name="said"/>), or null.</summary>
    public static Process? Ffmpeg(IEnumerable<string> args, bool info, Action<string?> said)
    {
        try
        {
            var psi = new ProcessStartInfo(BundledTools.Resolve("ffmpeg"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add(info ? "info" : "error");
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p == null) return null;
            p.ErrorDataReceived += (_, e) => said(e.Data);
            p.BeginErrorReadLine();
            return p;
        }
        catch (Exception e)
        {
            GD.Print($"[cinema] cannot start ffmpeg: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// JPEGs out of an ffmpeg <c>image2pipe</c> (mjpeg), one after the other, each handed to <paramref name="frame"/>
    /// (which may block: ffmpeg waits): from its start marker (FF D8) to its end marker (FF D9), which no entropy-coded
    /// byte can form (a coded FF is always followed by 00). Returns when the pipe ends or <paramref name="go"/> says stop.
    /// </summary>
    public static void ReadJpegs(Stream stdout, Func<bool> go, Action<byte[]> frame)
    {
        var buf = new byte[64 * 1024];
        var current = new MemoryStream();
        bool inFrame = false;
        byte prev = 0;
        try
        {
            while (go())
            {
                int n = stdout.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8) { inFrame = true; current.SetLength(0); current.WriteByte(0xFF); current.WriteByte(0xD8); }
                    }
                    else
                    {
                        current.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9)
                        {
                            inFrame = false;
                            if (!go()) return;
                            frame(current.ToArray());
                            b = 0;
                        }
                    }
                    prev = b;
                }
            }
        }
        catch (Exception) { /* killed */ }
    }

    /// <summary>Kills a process it started, by its own handle (never by name), and lets it go.</summary>
    public static void Stop(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(); } catch (Exception) { }
        p?.Dispose();
    }
}
