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

    /// <summary>
    /// The beat grid the <see cref="CdAnalysis"/> block is laid on, when the CD's grid is set by
    /// hand (the chess type beat, <see cref="CdLibrary.RatBeatBpm"/>); null = the analyser's own.
    /// </summary>
    public (float Bpm, float Offset)? Grid { get; init; }

    /// <summary>Whether both tools start; <paramref name="why"/> names the missing one.</summary>
    public static bool ToolsAvailable(out string why)
    {
        foreach (string tool in new[] { "yt-dlp", "ffmpeg" })
        {
            if (!ToolStarts(tool, out why)) return false;
        }
        why = "";
        return true;
    }

    /// <summary>Whether ffmpeg starts: all a re-analysis needs.</summary>
    public static bool FfmpegAvailable() => ToolStarts("ffmpeg", out _);

    private static bool ToolStarts(string tool, out string why)
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
        why = "";
        return true;
    }

    /// <summary>
    /// The <see cref="CdAnalysis"/> of a CD already burnt (#725 backfill): its Ogg decoded again to
    /// mono, analysed on its stored grid. Null when ffmpeg fails. Runs on a worker.
    /// </summary>
    public static async Task<CdAnalysis?> ReanalyseAsync(string ogg, float bpm, float beatOffset, CancellationToken ct)
    {
        var pcm = await DecodeMonoAsync(ogg, ct);
        if (pcm == null || pcm.Length < AnalysisRate) return null;
        var analysis = BeatAnalyzer.AnalyseFull(pcm, AnalysisRate, bpm, beatOffset).analysis;
        return analysis == null ? null : await WithVocalAsync(analysis, ogg, bpm, ogg, ct);
    }

    /// <summary>
    /// <c>--vocalstem &lt;audio file&gt;</c> (#1153): separates the file both ways (the model when it
    /// is here, and the centre fallback), writes each vocal as a WAV and its 20 Hz lanes as CSV to
    /// <c>test_output/vocal/</c> to listen to and plot, and prints what the analyser found and how
    /// long it took. True when every pass gave a vocal with some singing in it.
    /// </summary>
    public static bool VocalStemProbe(string? file)
    {
        if (file == null || !File.Exists(file)) { Godot.GD.PrintErr($"[vocalstem] no file '{file}'"); return false; }
        return Task.Run(async () =>
        {
            var stereo = await DecodeStereoAsync(file, CancellationToken.None);
            var mono = await DecodeMonoAsync(file, CancellationToken.None);
            if (stereo == null || mono == null) { Godot.GD.PrintErr("[vocalstem] ffmpeg could not decode it"); return false; }
            float bpm = BeatAnalyzer.AnalyseFull(mono, AnalysisRate, 0f, 0f).bpm;
            string dir = Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), "test_output", "vocal");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(file);
            bool ok = true;
            foreach (bool model in VocalSeparator.ModelName != null ? new[] { true, false } : new[] { false })
            {
                var watch = Stopwatch.StartNew();
                var (vocal, how, _) = VocalSeparator.Separate(stereo, CancellationToken.None, model);
                double sep = watch.Elapsed.TotalSeconds;
                var r = VocalAnalyzer.Analyse(vocal, AnalysisRate, bpm);
                double all = watch.Elapsed.TotalSeconds;
                WriteWav(Path.Combine(dir, $"{name}_{how}.wav"), vocal, AnalysisRate);
                var csv = new StringBuilder().AppendLine("t,level,syllable,vowel,pitch");
                for (int o = 0; o < r.Frames.Length / 4; o++)
                    csv.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{o / (float)CdAnalysis.EnvRate:F2},{r.Frames[4 * o]},{r.Frames[4 * o + 1]},{r.Frames[4 * o + 2]},{r.Frames[4 * o + 3]}"));
                File.WriteAllText(Path.Combine(dir, $"{name}_{how}.csv"), csv.ToString());
                int sung = 0, voiced = 0;
                for (int o = 0; o < r.Frames.Length / 4; o++) { if (r.Frames[4 * o] > 0) sung++; if (r.Frames[4 * o + 3] > 0) voiced++; }
                int frames = Math.Max(1, r.Frames.Length / 4);
                Godot.GD.Print($"[vocalstem] {how}: separated in {sep:F1} s, analysed in {all - sep:F1} s; sung {100f * sung / frames:F0} %, "
                    + $"pitched {100f * voiced / frames:F0} %; {r.Rises.Length / 2} rise(s) {r.Falls.Length / 2} fall(s) "
                    + $"{r.Holds.Length / 2} long note(s) {r.VocalIns.Length} entr(ies) at bpm {bpm:F0}");
                ok &= sung > 0;
            }
            return ok;
        }).GetAwaiter().GetResult();
    }

    private static void WriteWav(string path, float[] x, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + x.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(x.Length * 2);
        foreach (float v in x) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }

    /// <summary>The voice/music mix's stems beside a CD's Ogg (#1153): <c>&lt;id&gt;.voc.ogg</c> and <c>&lt;id&gt;.inst.ogg</c>.</summary>
    public static string StemPath(string ogg, CdStem stem) =>
        Path.Combine(Path.GetDirectoryName(ogg) ?? "", Path.GetFileNameWithoutExtension(ogg) + (stem == CdStem.Voice ? ".voc.ogg" : ".inst.ogg"));

    /// <summary>
    /// The block with the singer (#1153): the source decoded again to stereo 44.1 kHz, the vocal
    /// separated (<see cref="VocalSeparator"/>) and analysed, and the two stems of the voice/music mix
    /// written beside <paramref name="ogg"/> (the vocal, and the mix without it). The block as it was
    /// when ffmpeg fails; without stems when their encoding does.
    /// </summary>
    private static async Task<CdAnalysis> WithVocalAsync(CdAnalysis analysis, string source, float bpm, string ogg, CancellationToken ct)
    {
        var stereo = await DecodeStereoAsync(source, ct);
        if (stereo == null) return analysis;
        var watch = Stopwatch.StartNew();
        var (vocal, how, stem) = await Task.Run(() => VocalSeparator.Separate(stereo, ct), ct);
        var result = VocalAnalyzer.Analyse(vocal, AnalysisRate, bpm);
        bool stems = await EncodeStemAsync(stem, StemPath(ogg, CdStem.Voice), ct)
            && await EncodeStemAsync(VocalSeparator.Instrumental(stereo, stem), StemPath(ogg, CdStem.Music), ct);
        Godot.GD.Print($"[cd] vocal ({how}) in {watch.Elapsed.TotalSeconds:F1} s: {result.Rises.Length / 2} rise(s), "
            + $"{result.Falls.Length / 2} fall(s), {result.Holds.Length / 2} long note(s), {result.VocalIns.Length} entr(ies){(stems ? ", stems written" : ", no stems")}");
        return analysis.WithVocal(result, how, stems);
    }

    /// <summary>Interleaved stereo 44.1 kHz PCM to an Ogg like the CD's own, through a WAV beside it.</summary>
    private static async Task<bool> EncodeStemAsync(short[] stereo, string ogg, CancellationToken ct)
    {
        string wav = ogg + ".part.wav";
        try
        {
            using (var w = new BinaryWriter(File.Create(wav)))
            {
                int bytes = stereo.Length * 2;
                w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8);
                w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(VocalSeparator.Rate); w.Write(VocalSeparator.Rate * 4);
                w.Write((short)4); w.Write((short)16);
                w.Write("data"u8); w.Write(bytes);
                var raw = new byte[bytes];
                Buffer.BlockCopy(stereo, 0, raw, 0, bytes);
                w.Write(raw);
            }
            return await EncodeOggAsync(wav, ogg, ct);
        }
        catch (IOException e)
        {
            Godot.GD.PushWarning($"[cd] could not write the stem {ogg}: {e.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { }
        }
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
            var (bpm, offset, style, energy, analysis) =
                BeatAnalyzer.AnalyseFull(pcm, AnalysisRate, Grid?.Bpm ?? 0f, Grid?.Offset ?? 0f);
            float duration = pcm.Length / (float)AnalysisRate;
            string ogg = Path.Combine(CdDirectory, $"{id}.ogg");
            // the singer comes after (#1405): the vocal pass took 30-36 s and the CD waited for it; the library
            // queues it on the analysis backfill once the CD is listed, which adds the block and the stems

            status.Report("Encoding…");
            if (!await EncodeOggAsync(source, ogg, ct))
            {
                status.Report("ffmpeg could not encode the CD");
                return null;
            }

            var info = new CdInfo(id, Clean(title), duration, bpm, offset, style, energy, Analysis: analysis);
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
            "--encoding", "utf-8",   // else Windows prints the title in the console code page: "V�ronique"
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

    /// <summary>Interleaved stereo 44.1 kHz 16-bit PCM off ffmpeg's stdout, for <see cref="VocalSeparator"/> (a mono file comes out as two equal channels).</summary>
    private static async Task<short[]?> DecodeStereoAsync(string source, CancellationToken ct)
    {
        using var raw = new MemoryStream();
        int code = await RunAsync("ffmpeg", new[]
        {
            "-nostdin", "-loglevel", "error",
            "-i", source, "-t", MaxSeconds.ToString(),
            "-vn", "-ac", "2", "-ar", VocalSeparator.Rate.ToString(), "-f", "s16le", "-",
        }, null, raw, ct);
        if (code != 0 || raw.Length < 4) return null;
        var pcm = new short[raw.Length / 2];
        Buffer.BlockCopy(raw.GetBuffer(), 0, pcm, 0, pcm.Length * 2);
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
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
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
