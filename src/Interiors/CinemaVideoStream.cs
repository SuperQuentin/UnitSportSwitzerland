using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// One client's decoding of a hall's video (#1298): its bundled ffmpeg (<see cref="BundledTools"/>; this Godot
/// build plays only Theora) reads the link and pipes raw RGBA frames, letterboxed to
/// <see cref="CinemaVideo.Width"/> x <see cref="CinemaVideo.Height"/> at <see cref="CinemaVideo.Fps"/>, and a second
/// one its sound as 48 kHz PCM, both started at the same moment of the video, a little ahead of the clock.
/// A site's link (YouTube, Twitch) goes through yt-dlp first, here, so each client fetches its own stream of the
/// same video.
///
/// <para>
/// Workers read the pipes into a small ring (frames) and a sample ring (sound); a full ring blocks the worker,
/// which blocks ffmpeg: nothing runs further ahead than it holds. The main thread asks for the frame due at the
/// hall's position (<see cref="Pump"/>): earlier ones are dropped, a later one is held. Fallen
/// <see cref="CinemaVideo.FallBehind"/> behind (a stall, a slow start), it starts again further ahead. A live
/// stream (no duration) plays as it comes, a second behind its first frame.
/// </para>
/// <para>
/// Every process is killed by its own <see cref="Process"/> on <see cref="Dispose"/>, never by name.
/// </para>
/// </summary>
public sealed class CinemaVideoStream : ICinemaFeed
{
    public const int FrameBytes = CinemaVideo.Width * CinemaVideo.Height * 4;

    /// <summary>Frames decoded ahead at most (12 MB); the sound ahead at most, s, of a ring twice that.</summary>
    private const int Ring = 10;
    private const double SoundAhead = 8;
    private const int SoundRing = (int)(SoundAhead * 2 * CinemaVideo.AudioRate);

    /// <summary>A live stream's frames are shown this long after the first one came.</summary>
    private const double LiveDelay = 1.0;

    public string Link { get; }
    public double T0 { get; }

    /// <summary>What it is doing, for the panel: "Loading", "", or why it cannot play.</summary>
    public string Status { get; private set; } = "Loading…";
    public bool Failed { get; private set; }
    public bool Live { get; private set; }

    /// <summary>The video's length, s, once ffmpeg or yt-dlp said it; 0 unknown.</summary>
    public double Duration { get; private set; }

    /// <summary>Past its end: the hall goes back to its slides.</summary>
    public bool Ended { get; private set; }

    /// <summary>The frame shown now (RGBA, <see cref="FrameBytes"/>), null before the first; its mean colour; its index.</summary>
    public byte[]? Current { get; private set; }

    /// <summary>The frame shown now as an image (one, refilled on change): what the screen uploads.</summary>
    public Image? Picture { get; private set; }
    public Color Tint { get; private set; } = Colors.White;
    public long CurrentIndex { get; private set; } = -1;

    /// <summary>Where in the video the frame shown is, s; for the sync check.</summary>
    public double CurrentTime => CurrentIndex < 0 ? -1 : _start + CurrentIndex / (double)CinemaVideo.Fps;

    /// <summary>Restarts after falling behind; for the probes.</summary>
    public int Restarts { get; private set; }

    private readonly record struct Frame(long Index, byte[] Pixels, Color Tint);

    private readonly ConcurrentQueue<Frame> _frames = new();
    private readonly ConcurrentBag<byte[]> _free = new();
    private SemaphoreSlim _room = new(Ring);
    private readonly short[] _sound = new short[SoundRing * 2];
    private long _written;          // sound frames written (worker)
    private long _heard;            // sound frame being played (main), to keep the worker from overwriting it

    private volatile bool _disposed;
    private int _generation;
    private Process? _video, _audio;
    private string? _videoUrl, _audioUrl;
    private volatile bool _resolved, _resolving;
    private double _start = double.NaN, _soundStart = double.NaN;
    private double _restartAfter;
    private long _lastIndex = -1;
    private volatile bool _videoDone;

    public CinemaVideoStream(string link, double t0)
    {
        Link = link;
        T0 = t0;
    }

    /// <summary>
    /// Whether this machine can decode at all: a desktop that may start processes. <c>--cinemanodecode</c> plays one
    /// that cannot (a phone's way: the server's relay), for the checks.
    /// </summary>
    public static bool CanDecode => Platform.CanSpawnProcesses && !NoDecode;

    private static readonly bool NoDecode = CmdArgs.Has("--cinemanodecode");

    // ---- main thread -----------------------------------------------------------------------------

    /// <summary>
    /// Main thread, every frame the hall is watched: the hall is at <paramref name="position"/> s of the video.
    /// True when <see cref="Current"/> changed. Starts, and restarts, the decoders as needed.
    /// </summary>
    public bool Pump(double position)
    {
        if (_disposed || Failed || Ended) return false;
        if (!_resolved)
        {
            if (!_resolving) { _resolving = true; new Thread(Resolve) { IsBackground = true, Name = "cinema-lookup" }.Start(); }
            return false;
        }
        if (_video == null) Begin(position, CinemaVideo.StartLead);
        bool changed = false;
        if (Live && double.IsNaN(_start) && _frames.TryPeek(out _)) _start = position + LiveDelay;
        if (!double.IsNaN(_start))
        {
            long due = CinemaVideo.FrameAt(position, _start);
            while (due >= 0 && _frames.TryPeek(out var f) && f.Index <= due)
            {
                _frames.TryDequeue(out f);
                _room.Release();
                if (Current != null) _free.Add(Current);
                Current = f.Pixels;
                Tint = f.Tint;
                CurrentIndex = f.Index;
                changed = true;
            }
            if (changed)
            {
                Status = "";
                Picture ??= Image.CreateEmpty(CinemaVideo.Width, CinemaVideo.Height, false, Image.Format.Rgba8);
                Picture.SetData(CinemaVideo.Width, CinemaVideo.Height, false, Image.Format.Rgba8, Current!);
            }
            // fallen behind: the next frame it has (or would have) is long past due
            long next = _frames.TryPeek(out var n) ? n.Index : _lastIndex + 1;
            bool late = due - next > CinemaVideo.FallBehind * CinemaVideo.Fps;
            bool slowStart = CurrentIndex < 0 && position > _start + CinemaVideo.FallBehind;
            if (!Live && (late || slowStart) && position > _restartAfter && !(_videoDone && _frames.IsEmpty))
                Begin(position, CinemaVideo.RestartLead(++Restarts));
        }
        if (_videoDone && _frames.IsEmpty)
        {
            // the pipe ran dry: past the end, or the source went away
            if (!Live && Duration > 0 && position < Duration - 1 && position > _restartAfter) Begin(position, CinemaVideo.RestartLead(++Restarts));
            else if (CurrentIndex >= 0 || Duration > 0) { Ended = true; Status = "Ended"; }
            else Fail("The video could not be read (is the link a video?)");
        }
        return changed;
    }

    /// <summary>
    /// The sound sample frame (left, right in -1..1) due at <paramref name="position"/> plus <paramref name="ahead"/>
    /// frames: what the screen's speaker pushes. Silence before its start, past what is decoded, or overwritten.
    /// </summary>
    public bool SoundAt(double position, out long index)
    {
        if (Live && double.IsNaN(_soundStart) && Volatile.Read(ref _written) > 0) _soundStart = position + LiveDelay;
        index = double.IsNaN(_soundStart) ? -1 : CinemaVideo.SampleAt(position, _soundStart);
        return index >= 0;
    }

    public Vector2 Sample(long index)
    {
        long written = Volatile.Read(ref _written);
        if (index < 0 || index >= written || index < written - SoundRing) return Vector2.Zero;
        int at = (int)(index % SoundRing) * 2;
        return new Vector2(_sound[at] / 32768f, _sound[at + 1] / 32768f);
    }

    /// <summary>The main thread is playing sound frame <paramref name="index"/>: the worker may write up to the ring's size past it.</summary>
    public void Heard(long index) => Volatile.Write(ref _heard, Math.Max(0, index));

    // ---- the decoders ----------------------------------------------------------------------------

    /// <summary>yt-dlp for a site's link or a playlist: the video and sound URLs, live or not, the length. On a worker.</summary>
    private void Resolve()
    {
        try
        {
            if (CinemaLookup.Run(Link, out string why) is not { } found) { Fail(why); return; }
            Live = found.Live;
            Duration = found.Duration;
            _videoUrl = found.Video;
            _audioUrl = found.Sound;
        }
        finally
        {
            _resolved = true;
        }
    }

    private void Fail(string why)
    {
        if (Failed) return;
        Failed = true;
        Status = why;
        GD.Print($"[cinema] video {Link}: {why}");
    }

    /// <summary>(Re)starts both decoders at <paramref name="position"/> + <paramref name="lead"/> (a live stream at its edge).</summary>
    private void Begin(double position, double lead)
    {
        if (_videoUrl == null || _audioUrl == null) return;
        if (_video != null) GD.Print($"[cinema] video {CinemaVideo.Title(Link)} fell behind at {position:F1} s: again from {position + lead:F1} s");
        StopProcesses();
        int gen = ++_generation;
        while (_frames.TryDequeue(out var f)) _free.Add(f.Pixels);
        _room = new SemaphoreSlim(Ring);
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _heard, 0);
        _lastIndex = -1;
        _videoDone = false;
        if (CinemaVideo.NeedsLookup(Link)) lead += CinemaVideo.SiteLead;
        double at = Live ? 0 : position + lead;
        // a live stream's frames are timed as they come (Pump); a video's from where it was started
        _start = Live ? double.NaN : at;
        _soundStart = Live ? double.NaN : at;
        _restartAfter = position + lead + CinemaVideo.FallBehind;
        string size = $"scale={CinemaVideo.Width}:{CinemaVideo.Height}:force_original_aspect_ratio=decrease,"
            + $"pad={CinemaVideo.Width}:{CinemaVideo.Height}:(ow-iw)/2:(oh-ih)/2,fps={CinemaVideo.Fps}";
        _video = CinemaLookup.Ffmpeg(CinemaLookup.Input(_videoUrl, at, Live).Concat(new[] { "-an", "-sn", "-vf", size, "-f", "rawvideo", "-pix_fmt", "rgba", "-" }),
            info: true, line => Said(line, true));
        _audio = CinemaLookup.Ffmpeg(CinemaLookup.Input(_audioUrl, at, Live).Concat(new[] { "-vn", "-sn", "-ac", "2", "-ar", CinemaVideo.AudioRate.ToString(), "-f", "s16le", "-" }),
            info: false, line => Said(line, false));
        if (_video == null) { Fail("ffmpeg is missing: the video cannot play here"); return; }
        var room = _room;
        var vout = _video.StandardOutput.BaseStream;
        new Thread(() => ReadVideo(vout, gen, room)) { IsBackground = true, Name = "cinema-video" }.Start();
        if (_audio != null)
        {
            var aout = _audio.StandardOutput.BaseStream;
            new Thread(() => ReadAudio(aout, gen)) { IsBackground = true, Name = "cinema-sound" }.Start();
        }
    }

    /// <summary>ffmpeg's log: the length from its input's header ("Duration: N/A" a live stream), errors printed.</summary>
    private void Said(string? line, bool info)
    {
        if (string.IsNullOrWhiteSpace(line) || _disposed) return;
        int d = line.IndexOf("Duration: ", StringComparison.Ordinal);
        if (info && d >= 0)
        {
            string value = line[(d + 10)..].Split(',')[0].Trim();
            if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var span)) Duration = Math.Max(Duration, span.TotalSeconds);
            else if (value == "N/A" && Duration <= 0 && !Live && !CinemaVideo.NeedsLookup(Link)) { Live = true; _start = double.NaN; _soundStart = double.NaN; }
            return;
        }
        if (!info || line.Contains("rror", StringComparison.Ordinal) || line.Contains("failed", StringComparison.Ordinal))
            GD.Print($"[cinema] ffmpeg: {line}");
    }

    private void ReadVideo(Stream stdout, int gen, SemaphoreSlim room)
    {
        long index = 0;
        try
        {
            while (!_disposed && gen == _generation)
            {
                room.Wait();
                if (_disposed || gen != _generation) break;
                if (!_free.TryTake(out var pixels)) pixels = new byte[FrameBytes];
                int got = 0;
                while (got < FrameBytes)
                {
                    int n = stdout.Read(pixels, got, FrameBytes - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < FrameBytes) { _free.Add(pixels); break; }
                if (gen != _generation) { _free.Add(pixels); break; }
                _frames.Enqueue(new Frame(index, pixels, MeanColour(pixels)));
                _lastIndex = index++;
            }
        }
        catch (Exception) { /* killed: a restart or the end */ }
        if (gen == _generation) _videoDone = true;
    }

    private void ReadAudio(Stream stdout, int gen)
    {
        var buf = new byte[4800 * 4];
        int carry = 0;
        try
        {
            while (!_disposed && gen == _generation)
            {
                // never more than the ring's half ahead of what is heard: the other half is the past
                while (Volatile.Read(ref _written) - Volatile.Read(ref _heard) > SoundAhead * CinemaVideo.AudioRate)
                {
                    if (_disposed || gen != _generation) return;
                    Thread.Sleep(20);
                }
                int n = stdout.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                n += carry;
                int frames = n / 4;
                long w = Volatile.Read(ref _written);
                for (int i = 0; i < frames; i++)
                {
                    int at = (int)((w + i) % SoundRing) * 2;
                    _sound[at] = (short)(buf[i * 4] | buf[i * 4 + 1] << 8);
                    _sound[at + 1] = (short)(buf[i * 4 + 2] | buf[i * 4 + 3] << 8);
                }
                if (gen != _generation) return;
                Volatile.Write(ref _written, w + frames);
                carry = n - frames * 4;
                if (carry > 0) Buffer.BlockCopy(buf, frames * 4, buf, 0, carry);
            }
        }
        catch (Exception) { /* killed */ }
    }

    /// <summary>A frame's mean colour, from a sparse grid of its pixels: what the beam, the glow and the hall's wash take.</summary>
    private static Color MeanColour(byte[] p)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (int y = 8; y < CinemaVideo.Height; y += 24)
            for (int x = 8; x < CinemaVideo.Width; x += 24)
            {
                int at = (y * CinemaVideo.Width + x) * 4;
                r += p[at]; g += p[at + 1]; b += p[at + 2];
                count++;
            }
        return count == 0 ? Colors.Black : new Color(r / (255f * count), g / (255f * count), b / (255f * count));
    }

    private void StopProcesses()
    {
        _generation++;
        CinemaLookup.Stop(_video);
        CinemaLookup.Stop(_audio);
        _video = _audio = null;
        // a worker blocked on a full ring wakes to see its generation is gone
        _room.Release(Ring);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcesses();
    }
}
