using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Records a short clip of the screen as a GIF (and an MP4 when ffmpeg has libx264), to show a new
/// feature in a PR or a release (#487, <c>docs/notes/general/feature-clips.md</c>).
///
///   chat:          /clip start name [seconds] [delay]   /clip stop   /clip
///   command line:  --clip name[,delay[,seconds]] [--clip-quit]
///
/// Real time, unlike <see cref="Gpx.VideoExporter"/>: it films what the game draws while someone
/// plays it, or while a probe or a <c>--shot-queue</c> drives it. Every 1/<see cref="Fps"/> s the
/// last drawn frame is read back, scaled to <see cref="Width"/> and piped raw to ffmpeg, which
/// encodes on its own core; nothing goes to disk until ffmpeg writes the files. Client-only: the
/// chat answers <c>/clip</c> here and never sends it to a server. A dev tool: no key, so no pad or
/// VR way either (<c>general/new-action-three-devices</c>).
/// </summary>
public partial class ClipRecorder : Node
{
    public const string Usage = "Usage: /clip start <name> [seconds] [delay] | /clip stop";

    /// <summary>What Tab offers after <c>/clip</c>.</summary>
    public static readonly string[] Words = ["start", "stop"];

    /// <summary>Frames read back per second; the GIF keeps every other one.</summary>
    private const int Fps = 30, GifFps = 15;

    /// <summary>Width of the captured frames (the MP4); the GIF is scaled down to <see cref="GifWidth"/>.</summary>
    private const int Width = 960, GifWidth = 640;

    private const double DefaultSeconds = 10, MaxSeconds = 60, ChatDelay = 2;

    private static readonly StringName ClipName = "ClipRecorder";

    private static ClipRecorder? _instance;
    private static bool? _hasX264;

    private string _name = "";
    private string _gifPath = "", _mp4Path = "";
    private double _delay, _seconds, _elapsed, _sinceFrame;
    private bool _recording, _encoding, _quitAfter;
    private int _frames;
    private Vector2I _size;
    private System.Action<string>? _reply;

    private Process? _ffmpeg;
    private Channel<byte[]>? _queue;
    private System.Threading.Tasks.Task? _writer;
    private volatile string? _pipeError;
    private readonly System.Text.StringBuilder _log = new();

    /// <summary>Where clips go: <c>test_output/clips/</c> of the checkout when run from source, else <c>user://clips</c>.</summary>
    public static string Directory => ProjectSettings.GlobalizePath(OS.HasFeature("editor") ? "res://test_output/clips" : "user://clips");

    /// <summary>Starts the <c>--clip name[,delay[,seconds]]</c> recording asked on the command line, if any.</summary>
    public static void StartFromArgs(SceneTree tree)
    {
        if (CmdArgs.Value("--clip") is not { } arg) return;
        var p = arg.Split(',');
        var inv = CultureInfo.InvariantCulture;
        double delay = p.Length > 1 && double.TryParse(p[1], NumberStyles.Float, inv, out double d) ? d : 0;
        double seconds = p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, inv, out double s) ? s : DefaultSeconds;
        string? error = Ensure(tree).Start(p[0], seconds, delay, _ => { });   // Say logs it
        if (error != null)
        {
            GD.PrintErr($"[clip] FAILED {error}");
            if (CmdArgs.Has("--clip-quit")) tree.Quit(1);
            return;
        }
        _instance!._quitAfter = CmdArgs.Has("--clip-quit");
    }

    /// <summary>The reply to a <c>/clip</c> line, or null when the line is another command.</summary>
    public static string? Run(string text, System.Action<string> reply)
    {
        if (!text.StartsWith('/')) return null;
        string[] parts = text[1..].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("clip", System.StringComparison.OrdinalIgnoreCase)) return null;
        if (Engine.GetMainLoop() is not SceneTree tree) return "No game to record.";

        if (parts.Length == 1)
            return _instance is { _recording: true } r ? $"Recording '{r._name}': {r._elapsed:F0} of {r._seconds:F0} s. {Usage}"
                : _instance is { _encoding: true } e ? $"Encoding '{e._name}'."
                : $"Not recording. Clips go to {Directory}. {Usage}";

        var inv = CultureInfo.InvariantCulture;
        switch (parts[1].ToLowerInvariant())
        {
            case "start" when parts.Length >= 3:
                double seconds = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, inv, out double s) ? s : DefaultSeconds;
                double delay = parts.Length > 4 && double.TryParse(parts[4], NumberStyles.Float, inv, out double d) ? d : ChatDelay;
                return Ensure(tree).Start(parts[2], seconds, delay, reply)
                    ?? $"Recording '{parts[2]}' for {System.Math.Min(seconds, MaxSeconds):F0} s in {delay:F0} s (close the chat).";
            case "stop":
                if (_instance is not { _recording: true } rec) return "Not recording.";
                rec.Stop();
                return $"Stopped '{rec._name}', encoding.";   // and again once the files are written
            default:
                return Usage;
        }
    }

    private static ClipRecorder Ensure(SceneTree tree)
    {
        if (_instance != null && IsInstanceValid(_instance)) return _instance;
        // on the root, so it outlives a change of scene (title screen -> world)
        _instance = new ClipRecorder { Name = ClipName, ProcessMode = ProcessModeEnum.Always };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        return _instance;
    }

    /// <summary>Arms a recording; null when it starts, else why not.</summary>
    private string? Start(string name, double seconds, double delay, System.Action<string> reply)
    {
        if (_recording || _encoding) return $"Already busy with '{_name}'.";
        if (DisplayServer.GetName() == "headless") return "Nothing is drawn in a headless run.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('.')) return $"'{name}' is not a file name.";

        System.IO.Directory.CreateDirectory(Directory);
        _name = name;
        _gifPath = $"{Directory}/{name}.gif";
        _mp4Path = $"{Directory}/{name}.mp4";
        _seconds = System.Math.Clamp(seconds, 1, MaxSeconds);
        _delay = System.Math.Max(0, delay);
        _elapsed = 0;
        _sinceFrame = 0;
        _frames = 0;
        _size = default;
        _pipeError = null;
        _reply = reply;
        _recording = true;
        return null;
    }

    public override void _Process(double delta)
    {
        if (!_recording) return;
        if (_delay > 0)
        {
            _delay -= delta;
            return;
        }
        _elapsed += delta;
        _sinceFrame += delta;
        if (_frames > 0 && _sinceFrame < 1.0 / Fps) return;
        // a slow frame takes one picture, not a burst of the same one
        _sinceFrame = _frames > 0 ? System.Math.Min(_sinceFrame - 1.0 / Fps, 1.0 / Fps) : 0;

        var image = GetViewport().GetTexture().GetImage();
        if (_size == default)
        {
            // even sides: yuv420p (the MP4) needs them
            int h = (int)System.Math.Round(Width * image.GetHeight() / (double)image.GetWidth() / 2) * 2;
            _size = new Vector2I(Width, h);
            if (!StartEncoder())
            {
                _recording = false;
                Say($"could not start ffmpeg ({BundledTools.Resolve("ffmpeg")}): is it installed?", failed: true);
                return;
            }
        }
        image.Convert(Image.Format.Rgb8);
        if (image.GetSize() != _size) image.Resize(_size.X, _size.Y, Image.Interpolation.Bilinear);
        // full: ffmpeg is behind, drop this frame rather than stall the game
        if (_queue!.Writer.TryWrite(image.GetData())) _frames++;

        if (_pipeError != null || _elapsed >= _seconds) Stop();
    }

    private bool StartEncoder()
    {
        _hasX264 ??= Encoders().Contains("libx264");
        string gifChain = $"fps={GifFps},scale={GifWidth}:-2:flags=lanczos,split[g1][g2];[g1]palettegen=stats_mode=diff[p];[g2][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";
        var start = new ProcessStartInfo(BundledTools.Resolve("ffmpeg"))
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", $"{_size.X}x{_size.Y}", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-i", "-" })
            start.ArgumentList.Add(a);
        if (_hasX264 == true)
        {
            foreach (string a in new[] { "-filter_complex", $"[0:v]split[v][g];[g]{gifChain}[gif]",
                         "-map", "[v]", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", _mp4Path,
                         "-map", "[gif]", "-loop", "0", _gifPath })
                start.ArgumentList.Add(a);
        }
        else
        {
            foreach (string a in new[] { "-filter_complex", $"[0:v]{gifChain}[gif]", "-map", "[gif]", "-loop", "0", _gifPath })
                start.ArgumentList.Add(a);
        }
        try
        {
            _ffmpeg = Process.Start(start);
            if (_ffmpeg == null) return false;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[clip] {e.Message}");
            return false;
        }
        lock (_log) _log.Clear();
        // drained on its own thread: a full stderr pipe would deadlock ffmpeg against the frames
        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_log) _log.AppendLine(e.Data);
        };
        _ffmpeg.BeginErrorReadLine();

        // about two seconds of frames: a hiccup in the encoder is absorbed, a dead one drops frames
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Fps * 2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var stdin = _ffmpeg.StandardInput.BaseStream;
        var reader = _queue.Reader;
        _writer = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in reader.ReadAllAsync().ConfigureAwait(false))
                    await stdin.WriteAsync(bytes).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (System.Exception e) { _pipeError = e.Message; }
            finally { try { stdin.Close(); } catch (System.Exception) { } }
        });
        GD.Print($"[clip] recording {_name}: {_size.X}x{_size.Y} at {Fps} fps for {_seconds:F0} s -> {_gifPath}"
            + (_hasX264 == true ? " and .mp4" : " (no libx264 in this ffmpeg: GIF only)"));
        return true;
    }

    /// <summary>Ends the capture; ffmpeg finishes the files off the main thread, then the result is told.</summary>
    private void Stop()
    {
        _recording = false;
        if (_queue == null || _ffmpeg == null) return;
        _encoding = true;
        _queue.Writer.TryComplete();
        var ffmpeg = _ffmpeg;
        var writer = _writer!;
        int frames = _frames;
        double seconds = _elapsed;
        System.Threading.Tasks.Task.Run(async () =>
        {
            await writer.ConfigureAwait(false);
            bool exited = ffmpeg.WaitForExit(120_000);
            if (!exited) ffmpeg.Kill(entireProcessTree: true);
            bool ok = exited && ffmpeg.ExitCode == 0 && _pipeError == null && File.Exists(_gifPath);
            string log;
            lock (_log) log = _log.ToString().Trim();
            ffmpeg.Dispose();
            Callable.From(() => Finished(ok, frames, seconds, log)).CallDeferred();
        });
    }

    private void Finished(bool ok, int frames, double seconds, string log)
    {
        _encoding = false;
        _ffmpeg = null;
        _queue = null;
        _writer = null;
        if (ok)
        {
            long kb = new FileInfo(_gifPath).Length / 1024;
            string also = _hasX264 == true ? $" and {Path.GetFileName(_mp4Path)}" : "";
            Say(string.Create(CultureInfo.InvariantCulture, $"wrote {_gifPath} ({kb} KB, {frames} frames over {seconds:F1} s){also}"), failed: false);
        }
        else Say($"{_name}: ffmpeg failed. {_pipeError} {log}", failed: true);
    }

    private void Say(string message, bool failed)
    {
        if (failed) GD.PrintErr($"[clip] FAILED {message}");
        else GD.Print($"[clip] {message}");
        _reply?.Invoke(failed ? $"Clip failed: {message}" : $"Clip {message}");
        if (_quitAfter) GetTree().Quit(failed ? 1 : 0);
    }

    /// <summary>The encoders this ffmpeg was built with; empty when it does not run.</summary>
    private static string Encoders()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(BundledTools.Resolve("ffmpeg"), "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return "";
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (System.Exception)
        {
            return "";
        }
    }

    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
        _queue?.Writer.TryComplete();
    }
}
