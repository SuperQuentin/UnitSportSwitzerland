using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The server's copy of a hall's video (#1298 phase 2), for whoever cannot or may not fetch the link: ffmpeg (yt-dlp
/// first for a site or a playlist, <see cref="CinemaLookup"/>) encodes JPEG frames of
/// <see cref="CinemaVideo.RelayWidth"/> x <see cref="CinemaVideo.RelayHeight"/> at <see cref="CinemaVideo.RelayFps"/>
/// and a second one mono PCM at <see cref="CinemaVideo.RelaySoundRate"/>, as the car radio's tap does
/// (<see cref="Audio.Live.StationTap"/>). Every frame and chunk carries its moment of the video and goes out up to
/// <see cref="CinemaVideo.RelayAhead"/> s before the hall reaches it, so a watcher shows it on the shared clock with the
/// players who decode the link themselves. A live stream's moments are the server's own: a frame
/// <see cref="LiveDelay"/> s after it came.
///
/// <para>
/// Workers read the pipes into bounded queues (a full one blocks ffmpeg); the main thread hands out what is due
/// (<see cref="Pump"/>), drops what is already past, and starts again further ahead when it fell behind.
/// </para>
/// </summary>
public sealed class CinemaRelayTap : IDisposable
{
    /// <summary>Frames waiting at most: four seconds at the fastest quality.</summary>
    private const int FrameRing = 20 * 4;
    private const double LiveDelay = CinemaVideo.RelayAhead + 0.5;
    private const int SoundRing = CinemaVideo.RelaySoundRate * 12;

    public string Link { get; }
    public double T0 { get; }
    public string Status { get; private set; } = "Loading…";
    public bool Failed { get; private set; }
    public bool Ended { get; private set; }
    public int Restarts { get; private set; }

    /// <summary>Seconds since anyone watched; the owner closes it after a while.</summary>
    public double Unwanted { get; set; }

    private readonly ConcurrentQueue<(long Index, byte[] Jpeg)> _frames = new();
    private SemaphoreSlim _room = new(FrameRing);
    private readonly short[] _sound = new short[SoundRing];
    private long _written, _sent;

    private volatile bool _disposed, _resolved, _resolving, _videoDone;
    private int _generation;
    private Process? _video, _audio;
    private CinemaLookup.Found? _found;
    private bool _live;
    private double _start = double.NaN, _soundStart = double.NaN, _restartAfter;
    private long _lastIndex = -1, _nextIndex;

    private readonly (int Width, int Height, int Fps, int JpegQ) _size;

    /// <summary>
    /// A tap on <paramref name="link"/> from <paramref name="t0"/>: the server's relay at <see cref="CinemaVideo.LiveQuality.Low"/>,
    /// a streamer's own file (phase 4) at the quality they picked.
    /// </summary>
    public CinemaRelayTap(string link, double t0, CinemaVideo.LiveQuality quality = CinemaVideo.LiveQuality.Low)
    {
        Link = link;
        T0 = t0;
        _size = CinemaVideo.SizeOf(quality);
    }

    /// <summary>
    /// Main thread, each frame while anyone watches: the hall is at <paramref name="position"/>; adds the frames
    /// (moment, JPEG) and sound chunks (the relay's sample index, µ-law) to send now.
    /// </summary>
    public void Pump(double position, List<(double Time, byte[] Jpeg)> frames, List<(long Index, byte[] MuLaw)> sound)
    {
        if (_disposed || Failed || Ended) return;
        if (!_resolved)
        {
            if (!_resolving) { _resolving = true; new Thread(Resolve) { IsBackground = true, Name = "cinema-relay-lookup" }.Start(); }
            return;
        }
        if (_found is not { } found) return;
        if (_video == null) Begin(position, CinemaVideo.StartLead, found);
        if (_live && double.IsNaN(_start) && _frames.TryPeek(out _)) _start = position + LiveDelay;
        if (!double.IsNaN(_start))
        {
            while (_frames.TryPeek(out var f))
            {
                double at = _start + f.Index / (double)_size.Fps;
                if (at > position + CinemaVideo.RelayAhead) break;
                _frames.TryDequeue(out f);
                _room.Release();
                _nextIndex = f.Index + 1;
                // already past for everyone: not worth the bandwidth
                if (at >= position - 0.2) frames.Add((at, f.Jpeg));
                if (Status.Length > 0) Status = "";
            }
            double next = _start + (_frames.TryPeek(out var n) ? n.Index : _lastIndex + 1) / (double)_size.Fps;
            bool late = next < position - CinemaVideo.FallBehind;
            if (!_live && late && position > _restartAfter && !_videoDone) Begin(position, CinemaVideo.RestartLead(++Restarts), found);
        }
        if (_live && double.IsNaN(_soundStart) && Volatile.Read(ref _written) > 0) _soundStart = position + LiveDelay;
        if (!double.IsNaN(_soundStart))
        {
            long written = Volatile.Read(ref _written);
            long first = CinemaVideo.RelaySampleAt(_soundStart);
            // whole chunks on the relay's own grid (multiples of RelayChunk), as the radio's are
            while (true)
            {
                long index = first + _sent;
                long chunkStart = index / CinemaVideo.RelayChunk * CinemaVideo.RelayChunk;
                long chunkEnd = chunkStart + CinemaVideo.RelayChunk;
                if (chunkEnd - first > written) break;
                if (chunkStart / (double)CinemaVideo.RelaySoundRate > position + CinemaVideo.RelayAhead) break;
                var chunk = new byte[CinemaVideo.RelayChunk];
                for (long i = chunkStart; i < chunkEnd; i++)
                {
                    long local = i - first;
                    chunk[i - chunkStart] = Audio.Live.MuLaw.Encode(local >= 0 && local >= written - SoundRing ? _sound[local % SoundRing] : (short)0);
                }
                if (chunkEnd / (double)CinemaVideo.RelaySoundRate >= position - 0.2) sound.Add((chunkStart, chunk));
                _sent = chunkEnd - first;
            }
        }
        if (_videoDone && _frames.IsEmpty && position > _restartAfter)
        {
            if (!_live && found.Duration > 0 && position < found.Duration - 1) Begin(position, CinemaVideo.RestartLead(++Restarts), found);
            else if (_nextIndex > 0 || found.Duration > 0) { Ended = true; Status = "Ended"; }
            else Fail("The server could not read the video (is the link a video?)");
        }
    }

    private void Resolve()
    {
        try
        {
            _found = CinemaLookup.Run(Link, out string why);
            if (_found == null) Fail(why);
            else _live = _found.Value.Live;
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
        GD.Print($"[cinema] relay {Link}: {why}");
    }

    private void Begin(double position, double lead, CinemaLookup.Found found)
    {
        if (_video != null) GD.Print($"[cinema] relay {CinemaVideo.Title(Link)} fell behind at {position:F1} s: again from {position + lead:F1} s");
        StopProcesses();
        int gen = ++_generation;
        while (_frames.TryDequeue(out _)) { }
        _room = new SemaphoreSlim(FrameRing);
        Volatile.Write(ref _written, 0);
        _sent = 0;
        _lastIndex = -1;
        _nextIndex = 0;
        _videoDone = false;
        if (CinemaVideo.NeedsLookup(Link)) lead += CinemaVideo.SiteLead;
        double at = _live ? 0 : position + lead;
        _start = _live ? double.NaN : at;
        _soundStart = _live ? double.NaN : at;
        _restartAfter = position + lead + CinemaVideo.FallBehind;
        string size = $"scale={_size.Width}:{_size.Height}:force_original_aspect_ratio=decrease,"
            + $"pad={_size.Width}:{_size.Height}:(ow-iw)/2:(oh-ih)/2,fps={_size.Fps}";
        _video = CinemaLookup.Ffmpeg(CinemaLookup.Input(found.Video, at, _live).Concat(new[]
            { "-an", "-sn", "-vf", size, "-c:v", "mjpeg", "-q:v", _size.JpegQ.ToString(), "-f", "image2pipe", "-" }), false, Said);
        _audio = CinemaLookup.Ffmpeg(CinemaLookup.Input(found.Sound, at, _live).Concat(new[]
            { "-vn", "-sn", "-ac", "1", "-ar", CinemaVideo.RelaySoundRate.ToString(), "-f", "s16le", "-" }), false, Said);
        if (_video == null) { Fail("ffmpeg is missing on the server"); return; }
        var room = _room;
        var vout = _video.StandardOutput.BaseStream;
        new Thread(() => ReadFrames(vout, gen, room)) { IsBackground = true, Name = "cinema-relay-video" }.Start();
        if (_audio != null)
        {
            var aout = _audio.StandardOutput.BaseStream;
            new Thread(() => ReadSound(aout, gen)) { IsBackground = true, Name = "cinema-relay-sound" }.Start();
        }
    }

    private void Said(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line) && !_disposed) GD.Print($"[cinema] relay ffmpeg: {line}");
    }

    private void ReadFrames(Stream stdout, int gen, SemaphoreSlim room)
    {
        long index = 0;
        CinemaLookup.ReadJpegs(stdout, () => !_disposed && gen == _generation, jpeg =>
        {
            room.Wait();
            if (_disposed || gen != _generation) return;
            _frames.Enqueue((index, jpeg));
            _lastIndex = index++;
        });
        if (gen == _generation) _videoDone = true;
    }

    private void ReadSound(Stream stdout, int gen)
    {
        var buf = new byte[4800 * 2];
        int carry = 0;
        try
        {
            while (!_disposed && gen == _generation)
            {
                // never further ahead of what was sent than half the ring
                while (Volatile.Read(ref _written) - _sent > SoundRing / 2)
                {
                    if (_disposed || gen != _generation) return;
                    Thread.Sleep(20);
                }
                int n = stdout.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                n += carry;
                int samples = n / 2;
                long w = Volatile.Read(ref _written);
                for (int i = 0; i < samples; i++) _sound[(w + i) % SoundRing] = (short)(buf[i * 2] | buf[i * 2 + 1] << 8);
                if (gen != _generation) return;
                Volatile.Write(ref _written, w + samples);
                carry = n - samples * 2;
                if (carry > 0) buf[0] = buf[n - 1];
            }
        }
        catch (Exception) { /* killed */ }
    }

    private void StopProcesses()
    {
        _generation++;
        CinemaLookup.Stop(_video);
        CinemaLookup.Stop(_audio);
        _video = _audio = null;
        _room.Release(FrameRing);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcesses();
    }
}
