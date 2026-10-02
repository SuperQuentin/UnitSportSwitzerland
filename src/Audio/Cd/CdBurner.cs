using System.Diagnostics;
using System.Text;

using UnitSport.Core;

namespace UnitSport.Audio.Cd;

/// <summary>
/// Turns a YouTube link (or a local audio file) into a CD: <c>yt-dlp</c> fetches the audio,
/// <c>ffmpeg</c> decodes it once to mono PCM for <see cref="BeatAnalyzer"/> and once to a small
/// Ogg Vorbis for the radios. Both tools are looked for on PATH, like the GPX video export's
/// ffmpeg; without them a burn fails with a message that says so, and nothing else changes.
///
/// <para>
/// Runs on whoever owns the CD library — the dedicated server, or the local game offline —
/// and only there: a client sends a link, it does not run downloads for other people. Every
/// call is on a worker (<see cref="System.Threading.Tasks.Task.Run"/>): nothing here touches
/// the scene tree, and <see cref="CdLibrary"/> brings the result back to the main thread.
/// </para>
/// </summary>
public sealed class CdBurner
{
    /// <summary>Where <c>&lt;id&gt;.ogg</c> and <c>&lt;id&gt;.json</c> are written. Absolute.</summary>
    public required string CdDirectory { get; init; }

    /// <summary>A track longer than this is cut here: a 10-minute mix is 6 MB to stream to every listener.</summary>
    public const int MaxSeconds = 600;

    /// <summary>Sample rate the analyser is fed at.</summary>
    public const int AnalysisRate = 22050;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Whether both tools start; <paramref name="why"/> names the missing one.</summary>
    public static bool ToolsAvailable(out string why)
    {
        foreach (string tool in new[] { "yt-dlp", "ffmpeg" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(BundledTools.Resolve(tool), "-version")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p == null) { why = $"{tool} did not start"; return false; }
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
            }
            catch (Exception)
            {
                why = $"{tool} is not installed on the server (it must be on PATH)";
                return false;
            }
        }
        why = "";
        return true;
    }

    /// <summary>
    /// Burns CD <paramref name="id"/> from <paramref name="urlOrFile"/>. Progress lines go to
    /// <paramref name="status"/> (shown to the player who asked). Null when it failed, with the
    /// reason as the last status line.
    /// </summary>
    public async Task<CdInfo?> BurnAsync(int id, string urlOrFile, IProgress<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(CdDirectory);
        string temp = Path.Combine(CdDirectory, $"burn_{id}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string source, title;
            if (File.Exists(urlOrFile))
            {
                source = urlOrFile;
                title = Path.GetFileNameWithoutExtension(urlOrFile);
            }
            else
            {
                status.Report("Downloading…");
                var got = await DownloadAsync(urlOrFile, temp, ct);
                if (got == null) { status.Report("Download failed (is yt-dlp installed on the server, and is the link a video?)"); return null; }
                (source, title) = got.Value;
            }

            status.Report("Analysing the beat…");
            var pcm = await DecodeMonoAsync(source, ct);
            if (pcm == null || pcm.Length < AnalysisRate * 5)
            {
                status.Report("ffmpeg could not decode the audio (is ffmpeg installed on the server?)");
                return null;
            }
            var (bpm, offset, style, energy) = BeatAnalyzer.Analyse(pcm, AnalysisRate);
            float duration = pcm.Length / (float)AnalysisRate;

            status.Report("Encoding…");
            string ogg = Path.Combine(CdDirectory, $"{id}.ogg");
            if (!await EncodeOggAsync(source, ogg, ct))
            {
                status.Report("ffmpeg could not encode the CD");
                return null;
            }

            var info = new CdInfo(id, Clean(title), duration, bpm, offset, style, energy);
            Core.JsonStore.Save(Path.Combine(CdDirectory, $"{id}.json"), info, CdInfo.Json);
            status.Report($"Burnt: {info.Describe()}");
            return info;
        }
        catch (OperationCanceledException)
        {
            status.Report("Burn cancelled");
            return null;
        }
        catch (Exception e)
        {
            status.Report($"Burn failed: {e.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A title fit for a list and a JSON file: one line, no control characters, bounded.</summary>
    private static string Clean(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
            if (!char.IsControl(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        if (s.Length > 80) s = s[..80];
        return s.Length == 0 ? "Untitled" : s;
    }

    /// <summary>
    /// <c>yt-dlp</c> downloads the best audio-only stream as it is (no re-encode: ffmpeg reads
    /// the webm/m4a straight) and prints the title and the file it wrote.
    /// </summary>
    private static async Task<(string File, string Title)?> DownloadAsync(string url, string temp, CancellationToken ct)
    {
        var lines = new List<string>();
        int code = await RunAsync("yt-dlp", BundledTools.YtDlpJsArgs().Concat(new[]
        {
            "--no-playlist", "--no-simulate", "--quiet", "--no-warnings",
            "-f", "bestaudio/best",
            "--max-filesize", "100m",
            "--print", "title", "--print", "after_move:filepath",
            "-o", Path.Combine(temp, "dl.%(ext)s"),
            "--", url,
        }).ToArray(), line => lines.Add(line), null, ct);
        if (code != 0 || lines.Count < 2) return null;
        string file = lines[^1].Trim();
        if (!File.Exists(file))
        {
            // some versions print the pre-move path; take whatever landed in the folder
            file = Directory.EnumerateFiles(temp).FirstOrDefault() ?? "";
            if (!File.Exists(file)) return null;
        }
        return (file, lines[0].Trim());
    }

    /// <summary>Mono 22.05 kHz 16-bit PCM straight off ffmpeg's stdout, as floats.</summary>
    private static async Task<float[]?> DecodeMonoAsync(string source, CancellationToken ct)
    {
        using var raw = new MemoryStream();
        int code = await RunAsync("ffmpeg", new[]
        {
            "-nostdin", "-loglevel", "error",
            "-i", source, "-t", MaxSeconds.ToString(),
            "-vn", "-ac", "1", "-ar", AnalysisRate.ToString(), "-f", "s16le", "-",
        }, null, raw, ct);
        if (code != 0 || raw.Length < 2) return null;
        var bytes = raw.GetBuffer();
        int n = (int)(raw.Length / 2);
        var pcm = new float[n];
        for (int i = 0; i < n; i++)
            pcm[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return pcm;
    }

    /// <summary>Stereo 44.1 kHz Vorbis at quality 2: ~80 kbit/s, good enough for a portable radio.</summary>
    private static async Task<bool> EncodeOggAsync(string source, string ogg, CancellationToken ct)
    {
        string part = ogg + ".part.ogg";
        int code = await RunAsync("ffmpeg", new[]
        {
            "-nostdin", "-loglevel", "error", "-y",
            "-i", source, "-t", MaxSeconds.ToString(),
            "-vn", "-ac", "2", "-ar", "44100", "-c:a", "libvorbis", "-q:a", "2",
            part,
        }, null, null, ct);
        if (code != 0 || !File.Exists(part)) return false;
        File.Move(part, ogg, overwrite: true);
        return true;
    }

    /// <summary>
    /// Runs a tool with an argument list (never a shell string: a link is user input), draining
    /// stderr on its own thread so a chatty tool cannot deadlock on a full pipe, and killing it
    /// on cancellation or after <see cref="ToolTimeout"/>. Returns the exit code, or -1.
    /// </summary>
    private static async Task<int> RunAsync(string tool, string[] args, Action<string>? stdoutLine,
        Stream? stdoutBytes, CancellationToken ct)
    {
        var start = new ProcessStartInfo(BundledTools.Resolve(tool))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in args) start.ArgumentList.Add(a);

        using var p = Process.Start(start);
        if (p == null) return -1;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err)
            {
                if (err.Length < 4000) err.AppendLine(e.Data);
            }
        };
        p.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(ToolTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var reg = linked.Token.Register(() => { try { p.Kill(entireProcessTree: true); } catch (Exception) { } });

        if (stdoutBytes != null) await p.StandardOutput.BaseStream.CopyToAsync(stdoutBytes, linked.Token);
        else
        {
            while (await p.StandardOutput.ReadLineAsync(linked.Token) is { } line)
                stdoutLine?.Invoke(line);
        }
        await p.WaitForExitAsync(linked.Token);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (err) tail = err.ToString().Trim();
            Godot.GD.Print($"[cd] {tool} exited {p.ExitCode}{(tail.Length > 0 ? ": " + tail : "")}");
        }
        return p.ExitCode;
    }
}
