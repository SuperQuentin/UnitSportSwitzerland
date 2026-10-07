using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnitSport.Audio.Cd;

/// <summary>What an antivirus said of a file (#736).</summary>
public enum ScanVerdict { Clean, Infected, Failed, Unavailable }

/// <summary>
/// The server's antivirus, run on every audio file a player uploads before anything else touches
/// it (#736): ClamAV (<c>clamdscan</c>, the daemon, else <c>clamscan</c>) on Linux and macOS,
/// Windows Defender (<c>MpCmdRun.exe</c>) on Windows. No scanner that starts means
/// <see cref="ScanVerdict.Unavailable"/>, and the server refuses uploads: it fails closed. Argument
/// lists, never a shell string. How an exit code reads is pure (<see cref="Interpret"/>, tested).
/// </summary>
public static class CdScanner
{
    /// <summary>A scan longer than this is a failure, not a pass.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public enum Engine { ClamDaemon, ClamScan, Defender }

    /// <summary>
    /// What <paramref name="engine"/>'s exit code means: ClamAV 0 clean, 1 a virus found, anything
    /// else an error; Defender's MpCmdRun 0 no threat, 2 threats found, anything else an error.
    /// </summary>
    public static ScanVerdict Interpret(Engine engine, int exitCode) => engine switch
    {
        Engine.Defender => exitCode switch { 0 => ScanVerdict.Clean, 2 => ScanVerdict.Infected, _ => ScanVerdict.Failed },
        _ => exitCode switch { 0 => ScanVerdict.Clean, 1 => ScanVerdict.Infected, _ => ScanVerdict.Failed },
    };

    /// <summary>The engines to try on this OS, in order, with how each is started on <paramref name="file"/>.</summary>
    private static (Engine Engine, string Exe, string[] Args)[] Engines(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            var args = new[] { "-Scan", "-ScanType", "3", "-File", file, "-DisableRemediation" };
            return new[] { (Engine.Defender, DefenderExe(), args) };
        }
        return new[]
        {
            (Engine.ClamDaemon, "clamdscan", new[] { "--no-summary", "--fdpass", file }),
            (Engine.ClamScan, "clamscan", new[] { "--no-summary", file }),
        };
    }

    /// <summary>
    /// Defender's command-line scanner: the platform's own, kept up to date under ProgramData (the
    /// newest version folder), else the one under Program Files.
    /// </summary>
    private static string DefenderExe()
    {
        try
        {
            string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(platform))
            {
                string? newest = null;
                foreach (string dir in Directory.GetDirectories(platform))
                    if (File.Exists(Path.Combine(dir, "MpCmdRun.exe")) && (newest == null || string.CompareOrdinal(dir, newest) > 0)) newest = dir;
                if (newest != null) return Path.Combine(newest, "MpCmdRun.exe");
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
    }

    private static Task<bool>? _available;

    /// <summary>
    /// Whether a scanner works here: it must pass a harmless file. Checked once, on a worker (a
    /// scan takes a second or two), so a server can start it at boot and ask later.
    /// </summary>
    public static Task<bool> AvailableAsync() => _available ??= Task.Run(async () =>
    {
        string probe = Path.Combine(Path.GetTempPath(), $"unitsport_scan_probe_{Environment.ProcessId}.txt");
        try
        {
            await File.WriteAllTextAsync(probe, "unitsport");
            return (await ScanAsync(probe, CancellationToken.None)).Verdict == ScanVerdict.Clean;
        }
        catch { return false; }
        finally { try { File.Delete(probe); } catch { } }
    });

    /// <summary>Scans <paramref name="file"/>: the first engine that starts gives the verdict, with the scanner's name.</summary>
    public static async Task<(ScanVerdict Verdict, string Engine)> ScanAsync(string file, CancellationToken ct)
    {
        foreach (var (engine, exe, args) in Engines(file))
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string a in args) start.ArgumentList.Add(a);
            Process? p;
            try { p = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { continue; }   // not installed: the next one
            if (p == null) continue;
            using (p)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                var drainOut = p.StandardOutput.ReadToEndAsync(timeout.Token);
                var drainErr = p.StandardError.ReadToEndAsync(timeout.Token);
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (ScanVerdict.Failed, engine.ToString());
                }
                try { await Task.WhenAll(drainOut, drainErr); } catch { }
                var verdict = Interpret(engine, p.ExitCode);
                // the daemon not running is not a verdict on the file: try the stand-alone scanner
                if (engine == Engine.ClamDaemon && verdict == ScanVerdict.Failed) continue;
                return (verdict, engine.ToString());
            }
        }
        return (ScanVerdict.Unavailable, "");
    }
}
