using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>What a streamer can share (#1298 phase 3): a monitor, a window, a webcam; a test pattern for the checks.</summary>
public enum CaptureKind { Monitor, Window, Webcam, Test }

/// <summary>One thing to share: its kind, what ffmpeg names it (a window's title, a camera's name), the area of a monitor.</summary>
public sealed record CaptureSource(CaptureKind Kind, string Label, string Name, Rect2I Area = default);

/// <summary>
/// What this machine can share (#1298 phase 3), listed on a worker (ffmpeg's device listing takes a moment): its
/// monitors (Windows: their rectangles on the virtual desktop, as gdigrab takes them), its windows (Windows only, by
/// title), its cameras and microphones (ffmpeg's <c>dshow</c> / <c>avfoundation</c> listing; Linux <c>/dev/video*</c>
/// and PulseAudio's default source). Never a device name typed by a player: only what the listing gave.
/// </summary>
public static class CinemaDevices
{
    public sealed record Found(List<CaptureSource> Monitors, List<CaptureSource> Windows, List<CaptureSource> Cameras, List<string> Mics, bool PcSound);

    public static Found List()
    {
        var monitors = new List<CaptureSource>();
        var windows = new List<CaptureSource>();
        var cameras = new List<CaptureSource>();
        var mics = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int i = 0;
                foreach (var r in Win32.Monitors()) monitors.Add(new CaptureSource(CaptureKind.Monitor, $"Monitor {++i} ({r.Size.X}x{r.Size.Y})", "desktop", r));
                foreach (var (handle, title) in Win32.Windows()) windows.Add(new CaptureSource(CaptureKind.Window, title, handle.ToString()));
                foreach (var (name, video) in FfmpegDevices("dshow", "dummy"))
                    if (video) cameras.Add(new CaptureSource(CaptureKind.Webcam, name, name));
                    else mics.Add(name);
            }
            else if (OperatingSystem.IsMacOS())
            {
                int screen = 0;
                foreach (var (name, video) in FfmpegDevices("avfoundation", "\"\""))
                {
                    if (!video) { mics.Add(name); continue; }
                    if (name.StartsWith("Capture screen", StringComparison.Ordinal)) monitors.Add(new CaptureSource(CaptureKind.Monitor, $"Monitor {++screen}", name));
                    else cameras.Add(new CaptureSource(CaptureKind.Webcam, name, name));
                }
            }
            else
            {
                for (int s = 0; s < DisplayServer.GetScreenCount(); s++)
                {
                    var at = DisplayServer.ScreenGetPosition(s);
                    var size = DisplayServer.ScreenGetSize(s);
                    monitors.Add(new CaptureSource(CaptureKind.Monitor, $"Monitor {s + 1} ({size.X}x{size.Y})", "screen", new Rect2I(at, size)));
                }
                foreach (string dev in Directory.Exists("/dev") ? Directory.GetFiles("/dev", "video*").Order() : Enumerable.Empty<string>())
                    cameras.Add(new CaptureSource(CaptureKind.Webcam, Path.GetFileName(dev), dev));
                mics.Add("default");
            }
        }
        catch (Exception e)
        {
            GD.Print($"[cinema] listing what can be shared failed: {e.Message}");
        }
        bool pcSound = PcSoundLoopback.Supported || OperatingSystem.IsLinux();
        return new Found(monitors, windows, cameras, mics, pcSound);
    }

    /// <summary>ffmpeg's device list (<c>-list_devices true</c>), parsed from its log: each name and whether it is video.</summary>
    private static List<(string Name, bool Video)> FfmpegDevices(string format, string input)
    {
        var found = new List<(string, bool)>();
        var psi = new ProcessStartInfo(BundledTools.Resolve("ffmpeg")) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", format, "-i", input }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return found;
        string log = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        bool? macVideo = null;
        foreach (string raw in log.Split('\n'))
        {
            string line = raw.Trim();
            if (format == "avfoundation")
            {
                if (line.Contains("video devices:", StringComparison.Ordinal)) { macVideo = true; continue; }
                if (line.Contains("audio devices:", StringComparison.Ordinal)) { macVideo = false; continue; }
                // [AVFoundation indev @ 0x...] [0] FaceTime HD Camera
                int close = line.LastIndexOf("] ", StringComparison.Ordinal);
                if (macVideo is { } v && close > 0 && line[..close].Contains("] [", StringComparison.Ordinal)) found.Add((line[(close + 2)..], v));
                continue;
            }
            // [dshow @ 0x...] "Integrated Camera" (video)
            int q0 = line.IndexOf('"'), q1 = q0 >= 0 ? line.IndexOf('"', q0 + 1) : -1;
            if (q1 <= q0 || line.Contains("Alternative name", StringComparison.Ordinal)) continue;
            string name = line[(q0 + 1)..q1];
            if (line.EndsWith("(video)", StringComparison.Ordinal)) found.Add((name, true));
            else if (line.EndsWith("(audio)", StringComparison.Ordinal)) found.Add((name, false));
        }
        return found;
    }

    /// <summary>The monitors' rectangles and the visible windows' titles, through user32 (Windows only).</summary>
    private static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
        private delegate bool WindowProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowProc proc, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        public static List<Rect2I> Monitors()
        {
            var list = new List<Rect2I>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref Rect r, IntPtr _) =>
            {
                list.Add(new Rect2I(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>
        /// Visible top-level windows with a title (their handle, for PrintWindow): not tool windows, not cloaked ones (a
        /// suspended store app, another virtual desktop), not the desktop; this game's own left out.
        /// </summary>
        public static List<(long Handle, string Title)> Windows()
        {
            var list = new List<(long, string)>();
            var shell = GetShellWindow();
            string own = Process.GetCurrentProcess().MainWindowTitle;
            EnumWindows((w, _) =>
            {
                if (w == shell || !IsWindowVisible(w) || (GetWindowLong(w, -20) & 0x80) != 0) return true;   // WS_EX_TOOLWINDOW
                if (DwmGetWindowAttribute(w, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // DWMWA_CLOAKED
                var sb = new System.Text.StringBuilder(256);
                if (GetWindowText(w, sb, sb.Capacity) > 0 && sb.ToString() is var t && t != own && t != "Program Manager") list.Add((w.ToInt64(), t));
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}

/// <summary>
/// A streamer's live source (#1298 phase 3), on their own machine: ffmpeg grabs the monitor, window or webcam
/// (gdigrab, Windows.Graphics.Capture for a window, dshow on Windows, x11grab / v4l2 on Linux, avfoundation on macOS) and encodes JPEGs at the chosen
/// <see cref="CinemaVideo.LiveQuality"/>; the PC's sound (<see cref="PcSoundLoopback"/> on Windows, PulseAudio's
/// monitor on Linux) and a microphone (ffmpeg) are mixed into the relay's mono. Each frame is stamped with the hall's
/// moment it was captured plus <see cref="CinemaVideo.LiveDelay"/>; the sound runs on the shared clock (the radio
/// tap's way: sample n belongs to its moment, a source short of samples gives silence, one ahead is trimmed), so
/// every peer, the streamer included, shows and plays it at the same moment. <see cref="Pump"/> hands out what to send.
/// </summary>
public sealed class CinemaCapture : IDisposable
{
    public CaptureSource Source { get; }
    public CinemaVideo.LiveQuality Quality { get; }
    public string Status { get; private set; } = "Starting…";
    public bool Failed { get; private set; }

    private readonly (int Width, int Height, int Fps, int JpegQ) _size;
    private readonly ConcurrentQueue<byte[]> _frames = new();
    private readonly ConcurrentQueue<short[]> _pc = new(), _mic = new();
    private short[]? _pcHead, _micHead;
    private int _pcAt, _micAt;
    private Process? _video, _micProcess, _pcProcess;
    private PcSoundLoopback? _loopback;
    private volatile bool _disposed;
    private long _nextSample = -1;
    private int _sent;

    /// <summary>Most sound a source may have waiting, in samples: more is trimmed (a source running fast).</summary>
    private const int MaxBacklog = CinemaVideo.RelaySoundRate * 3 / 10;

    public CinemaCapture(CaptureSource source, CinemaVideo.LiveQuality quality, bool pcSound, string? mic)
    {
        Source = source;
        Quality = quality;
        _size = CinemaVideo.SizeOf(quality);
        StartVideo();
        if (source.Kind == CaptureKind.Test) mic = "test";
        if (pcSound && source.Kind != CaptureKind.Webcam) StartPcSound();
        if (!string.IsNullOrEmpty(mic)) StartMic(mic);
    }

    private void StartVideo()
    {
        string fps = _size.Fps.ToString();
        var input = new List<string> { "-nostdin", "-hide_banner", "-nostats" };
        var s = Source;
        if (s.Kind == CaptureKind.Test) input.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate={fps}" });
        else if (OperatingSystem.IsWindows())
        {
            if (s.Kind == CaptureKind.Monitor)
                input.AddRange(new[] { "-f", "gdigrab", "-framerate", fps, "-draw_mouse", "1", "-offset_x", s.Area.Position.X.ToString(), "-offset_y", s.Area.Position.Y.ToString(),
                    "-video_size", $"{s.Area.Size.X}x{s.Area.Size.Y}", "-i", "desktop" });
            else if (s.Kind == CaptureKind.Window)
            {
                // gdigrab reads GDI, and a browser draws on the GPU (blank); Windows.Graphics.Capture (ffmpeg's gfxcapture)
                // sends a frame only when the window changes, and ffmpeg held even the first back: PrintWindow here instead
                new Thread(GrabWindow) { IsBackground = true, Name = "cinema-capture-window" }.Start();
                return;
            }
            else input.AddRange(new[] { "-f", "dshow", "-rtbufsize", "64M", "-i", $"video={s.Name}" });
        }
        else if (OperatingSystem.IsMacOS())
            input.AddRange(new[] { "-f", "avfoundation", "-framerate", fps, "-capture_cursor", "1", "-i", $"{s.Name}:none" });
        else if (s.Kind == CaptureKind.Monitor)
        {
            string display = System.Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            input.AddRange(new[] { "-f", "x11grab", "-framerate", fps, "-video_size", $"{s.Area.Size.X}x{s.Area.Size.Y}", "-i", $"{display}+{s.Area.Position.X},{s.Area.Position.Y}" });
        }
        else input.AddRange(new[] { "-f", "v4l2", "-i", s.Name });
        string fit = $"scale={_size.Width}:{_size.Height}:force_original_aspect_ratio=decrease,pad={_size.Width}:{_size.Height}:(ow-iw)/2:(oh-ih)/2";
        _video = CinemaLookup.Ffmpeg(input.Concat(new[] { "-an", "-vf", $"{fit},fps={fps}", "-c:v", "mjpeg", "-q:v", _size.JpegQ.ToString(), "-f", "image2pipe", "-" }), false, Said);
        if (_video == null) { Fail("ffmpeg is missing: nothing can be shared from here"); return; }
        var stdout = _video.StandardOutput.BaseStream;
        new Thread(() =>
        {
            CinemaLookup.ReadJpegs(stdout, () => !_disposed, jpeg =>
            {
                // the hall shows them as they come: never more than a second waiting here
                if (_frames.Count < _size.Fps) _frames.Enqueue(jpeg);
            });
            if (!_disposed) Fail($"{Source.Label} stopped (closed, unplugged, or not allowed)");
        }) { IsBackground = true, Name = "cinema-capture" }.Start();
    }

    /// <summary>
    /// A window, <see cref="CaptureSource.Name"/> its handle, grabbed with PrintWindow (full content: what the GPU drew,
    /// even under other windows, not minimised) at the quality's rate, letterboxed and made a JPEG here.
    /// </summary>
    private void GrabWindow()
    {
        if (!long.TryParse(Source.Name, out long handle)) { Fail("no such window"); return; }
        var window = new IntPtr(handle);
        byte[] pixels = Array.Empty<byte>();
        double next = RealClock.Now;
        while (!_disposed)
        {
            double now = RealClock.Now;
            if (now < next) { Thread.Sleep((int)Math.Max(1, (next - now) * 1000)); continue; }
            next = Math.Max(next + 1.0 / _size.Fps, now);
            if (!WindowGrab.Grab(window, ref pixels, out int w, out int h)) { Fail($"{Source.Label} closed or minimised"); return; }
            if (w < 8 || h < 8 || _frames.Count >= _size.Fps) continue;
            var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, pixels.Length == w * h * 4 ? pixels : pixels[..(w * h * 4)]);
            float k = Math.Min(_size.Width / (float)w, _size.Height / (float)h);
            int fw = Math.Max(1, (int)(w * k)), fh = Math.Max(1, (int)(h * k));
            img.Resize(fw, fh, Image.Interpolation.Bilinear);
            var frame = Image.CreateEmpty(_size.Width, _size.Height, false, Image.Format.Rgba8);
            frame.BlitRect(img, new Rect2I(0, 0, fw, fh), new Vector2I((_size.Width - fw) / 2, (_size.Height - fh) / 2));
            _frames.Enqueue(frame.SaveJpgToBuffer(1f - _size.JpegQ / 31f));
            if (Status.Length > 0 && !Failed) Status = "";
        }
    }

    private void StartPcSound()
    {
        if (PcSoundLoopback.Supported) { _loopback = new PcSoundLoopback(block => Offer(_pc, block)); return; }
        if (OperatingSystem.IsLinux()) _pcProcess = StartPcm(new[] { "-f", "pulse", "-i", "@DEFAULT_MONITOR@" }, _pc);
    }

    private void StartMic(string mic)
    {
        string[] input = mic == "test" ? new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=440" }
            : OperatingSystem.IsWindows() ? new[] { "-f", "dshow", "-i", $"audio={mic}" }
            : OperatingSystem.IsMacOS() ? new[] { "-f", "avfoundation", "-i", $":{mic}" }
            : new[] { "-f", "pulse", "-i", mic };
        _micProcess = StartPcm(input, _mic);
    }

    private Process? StartPcm(string[] input, ConcurrentQueue<short[]> into)
    {
        var p = CinemaLookup.Ffmpeg(new[] { "-nostdin", "-hide_banner", "-nostats" }.Concat(input)
            .Concat(new[] { "-vn", "-ac", "1", "-ar", CinemaVideo.RelaySoundRate.ToString(), "-f", "s16le", "-" }), false, Said);
        if (p == null) return null;
        var stdout = p.StandardOutput.BaseStream;
        new Thread(() =>
        {
            var buf = new byte[2400 * 2];
            int carry = 0;
            try
            {
                while (!_disposed)
                {
                    int n = stdout.Read(buf, carry, buf.Length - carry);
                    if (n <= 0) break;
                    n += carry;
                    var block = new short[n / 2];
                    Buffer.BlockCopy(buf, 0, block, 0, block.Length * 2);
                    carry = n - block.Length * 2;
                    if (carry > 0) buf[0] = buf[n - 1];
                    Offer(into, block);
                }
            }
            catch (Exception) { /* killed */ }
        }) { IsBackground = true, Name = "cinema-capture-sound" }.Start();
        return p;
    }

    private static void Offer(ConcurrentQueue<short[]> into, short[] block)
    {
        into.Enqueue(block);
        // a source that ran ahead (or nobody pumping): keep the newest
        while (into.Count > 40) into.TryDequeue(out _);
    }

    private void Said(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line) && !_disposed) GD.Print($"[cinema] capture ffmpeg: {line}");
    }

    private void Fail(string why)
    {
        if (Failed) return;
        Failed = true;
        Status = why;
        GD.Print($"[cinema] sharing {Source.Label}: {why}");
    }

    /// <summary>
    /// Main thread: the hall is at <paramref name="position"/>; adds the frames (moment, JPEG) and sound chunks (relay
    /// index, µ-law) to send now.
    /// </summary>
    public void Pump(double position, List<(double Time, byte[] Jpeg)> frames, List<(long Index, byte[] MuLaw)> sound)
    {
        if (_disposed) return;
        double shown = position + CinemaVideo.LiveDelay;
        while (_frames.TryDequeue(out var jpeg))
        {
            frames.Add((shown, jpeg));
            if (Status.Length > 0 && !Failed) Status = "";
        }
        if (_loopback is { Problem.Length: > 0 } lb && Status.Length == 0) Status = lb.Problem;
        // the sound on the shared clock, in whole chunks on the relay's grid
        long due = CinemaVideo.RelaySampleAt(shown);
        if (_nextSample < 0 || due - _nextSample > CinemaVideo.RelaySoundRate)
            _nextSample = due / CinemaVideo.RelayChunk * CinemaVideo.RelayChunk;
        bool any = _mic.Count > 0 || _pc.Count > 0 || _micHead != null || _pcHead != null || _sent > 0;
        while (any && _nextSample + CinemaVideo.RelayChunk <= due)
        {
            Trim(_mic, ref _micHead, ref _micAt);
            Trim(_pc, ref _pcHead, ref _pcAt);
            var chunk = new byte[CinemaVideo.RelayChunk];
            for (int i = 0; i < chunk.Length; i++)
            {
                int v = Take(_mic, ref _micHead, ref _micAt) + Take(_pc, ref _pcHead, ref _pcAt);
                chunk[i] = Audio.Live.MuLaw.Encode((short)Math.Clamp(v, short.MinValue, short.MaxValue));
            }
            sound.Add((_nextSample, chunk));
            _nextSample += CinemaVideo.RelayChunk;
            _sent++;
        }
    }

    /// <summary>A source with more than <see cref="MaxBacklog"/> waiting drops its oldest: it stays on the clock.</summary>
    private static void Trim(ConcurrentQueue<short[]> q, ref short[]? head, ref int at)
    {
        int waiting = (head?.Length ?? 0) - at;
        foreach (var b in q) waiting += b.Length;
        while (waiting > MaxBacklog && (head != null || q.TryDequeue(out head)))
        {
            int drop = Math.Min(head!.Length - at, waiting - MaxBacklog);
            at += drop;
            waiting -= drop;
            if (at >= head.Length) { head = null; at = 0; }
        }
    }

    /// <summary>The next sample of a source, or silence when it has none.</summary>
    private static int Take(ConcurrentQueue<short[]> q, ref short[]? head, ref int at)
    {
        if (head == null || at >= head.Length)
        {
            if (!q.TryDequeue(out head)) { head = null; return 0; }
            at = 0;
        }
        return head[at++];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CinemaLookup.Stop(_video);
        CinemaLookup.Stop(_micProcess);
        CinemaLookup.Stop(_pcProcess);
        _loopback?.Dispose();
    }
}
