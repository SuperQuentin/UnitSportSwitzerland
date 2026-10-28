using System.Collections.Concurrent;
using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's video through the server (#1298 phase 2), on a client: the JPEG frames and µ-law sound the server's
/// <see cref="CinemaRelayTap"/> sends, each with its moment of the video, shown and played when the hall reaches that
/// moment (<c>ServerNow − T0</c>), so a phone, a headset or a player whose own fetch failed is on the same clock as
/// the rest. Needs no ffmpeg: Godot decodes the JPEGs, on a worker, into a few pooled images scaled up to the
/// screen's <see cref="CinemaVideo.Width"/> x <see cref="CinemaVideo.Height"/>. The server sends up to
/// <see cref="CinemaVideo.RelayAhead"/> s ahead; the small JPEGs wait as they came and each is decoded only
/// <see cref="DecodeAhead"/> s before it is due (a frame already past is skipped undecoded), so a handful of images
/// covers it.
/// </summary>
public sealed class CinemaRelayFeed : ICinemaFeed
{
    /// <summary>Images decoded and waiting, at most (10 MB with the one shown).</summary>
    private const int Pool = 8;

    /// <summary>How long before its moment a frame is decoded, s; JPEGs waiting at most (the server's lead and some).</summary>
    private const double DecodeAhead = 0.3;
    private const int MaxWaiting = (int)(CinemaVideo.RelayAhead * 2 * CinemaVideo.RelayFps);
    private const int SoundRing = CinemaVideo.RelaySoundRate * 16;
    private const int Slots = SoundRing / CinemaVideo.RelayChunk;

    public string Link { get; }
    public double T0 { get; }
    public CinemaVideo.Stereo Stereo { get; }

    private readonly CinemaVideo.Shape _shape = new(CinemaVideo.RelayFps / 3);
    private int _measured;
    public float ContentWidth => _shape.Width;
    private readonly int _width, _height;
    public string Status { get; private set; } = "Through the server…";
    public bool Failed { get; private set; }
    public bool Ended { get; private set; }
    public Image? Picture { get; private set; }
    public Color Tint { get; private set; } = Colors.White;
    public double CurrentTime { get; private set; } = -1;
    public int Restarts => 0;

    private readonly ConcurrentQueue<(double Time, byte[] Jpeg)> _incoming = new();
    private volatile bool _disposed;
    /// <summary>The hall's position as the main thread last saw it, for the worker (a double's bits: atomic).</summary>
    private long _position = BitConverter.DoubleToInt64Bits(double.MinValue);
    private readonly ConcurrentQueue<(double Time, Image Image, Color Tint)> _decoded = new();
    private readonly ConcurrentBag<Image> _free = new();
    private int _images;
    private double _lastQueued = double.MinValue;

    private readonly float[] _sound = new float[SoundRing];
    private readonly long[] _chunkOf = new long[Slots];
    private bool _anySound;

    public CinemaRelayFeed(string link, double t0, CinemaVideo.Stereo stereo = CinemaVideo.Stereo.Mono)
    {
        Link = link;
        T0 = t0;
        Stereo = stereo;
        (_width, _height) = CinemaVideo.PackedSize(stereo, CinemaVideo.Width, CinemaVideo.Height);
        Array.Fill(_chunkOf, -1);
        new Thread(Decode) { IsBackground = true, Name = "cinema-relay-jpeg" }.Start();
    }

    // ---- what the server sends -------------------------------------------------------------------

    /// <summary>A frame for moment <paramref name="time"/> of the video. Out of order (the frames go unreliable) or too many waiting: dropped.</summary>
    public void PutFrame(double time, byte[] jpeg)
    {
        if (time <= _lastQueued || _disposed || _incoming.Count >= MaxWaiting) return;
        _incoming.Enqueue((time, jpeg));
        _lastQueued = time;
    }

    /// <summary>A chunk of sound starting at the relay's sample <paramref name="index"/> (video time × rate).</summary>
    public void PutSound(long index, byte[] mulaw)
    {
        if (index < 0 || mulaw.Length != CinemaVideo.RelayChunk || index % CinemaVideo.RelayChunk != 0) return;
        long chunk = index / CinemaVideo.RelayChunk;
        int slot = (int)(chunk % Slots), at = slot * CinemaVideo.RelayChunk;
        for (int i = 0; i < mulaw.Length; i++) _sound[at + i] = Audio.Live.MuLaw.Decode(mulaw[i]);
        _chunkOf[slot] = chunk;
        _anySound = true;
    }

    /// <summary>The server's word on its copy: why it cannot, or that it ended.</summary>
    public void SetStatus(string status, bool failed, bool ended)
    {
        Status = status;
        Failed |= failed;
        Ended |= ended;
    }

    private void Decode()
    {
        try
        {
            while (!_disposed)
            {
                double position = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _position));
                if (!_incoming.TryPeek(out var next) || next.Time > position + DecodeAhead) { Thread.Sleep(5); continue; }
                _incoming.TryDequeue(out var item);
                var (time, jpeg) = item;
                // already past, and a later one is here: not worth decoding
                if (_incoming.TryPeek(out var after) && after.Time <= position) continue;
                if (!_free.TryTake(out var img))
                {
                    if (Interlocked.Increment(ref _images) > Pool) { Interlocked.Decrement(ref _images); Thread.Sleep(5); continue; }
                    img = new Image();
                }
                if (img.LoadJpgFromBuffer(jpeg) != Error.Ok) { _free.Add(img); continue; }
                var tint = MeanColour(img);
                // the picture's width before it is scaled (the JPEG's own pixels), every third frame, the left eye of a 3D one
                if (_measured++ % 3 == 0)
                {
                    var im = img;
                    int eye = Stereo == CinemaVideo.Stereo.Mono ? im.GetWidth() : im.GetWidth() / 2;
                    _shape.Add(CinemaVideo.ContentWidth(eye, im.GetHeight(), (x, y) =>
                    {
                        var c = im.GetPixel(x, y);
                        return c.R + c.G + c.B > 3 * 0.1f;
                    }));
                }
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                if (img.GetWidth() != _width || img.GetHeight() != _height)
                    img.Resize(_width, _height, Image.Interpolation.Bilinear);
                _decoded.Enqueue((time, img, tint));
            }
        }
        catch (Exception) { /* disposed */ }
    }

    /// <summary>A frame's mean colour from a sparse grid of the decoded JPEG, before it is scaled up.</summary>
    private static Color MeanColour(Image img)
    {
        float r = 0, g = 0, b = 0;
        int w = img.GetWidth(), h = img.GetHeight(), count = 0;
        for (int y = h / 16; y < h; y += h / 8)
            for (int x = w / 32; x < w; x += w / 16)
            {
                var c = img.GetPixel(x, y);
                r += c.R; g += c.G; b += c.B;
                count++;
            }
        return count == 0 ? Colors.Black : new Color(r / count, g / count, b / count);
    }

    // ---- the hall ---------------------------------------------------------------------------------

    public bool Pump(double position)
    {
        Interlocked.Exchange(ref _position, BitConverter.DoubleToInt64Bits(position));
        bool changed = false;
        while (_decoded.TryPeek(out var f) && f.Time <= position)
        {
            _decoded.TryDequeue(out f);
            if (Picture != null) _free.Add(Picture);
            Picture = f.Image;
            Tint = f.Tint;
            CurrentTime = f.Time;
            changed = true;
        }
        if (changed && !Failed && !Ended) Status = "";
        return changed;
    }

    public bool SoundAt(double position, out long index)
    {
        index = (long)Math.Floor(position * CinemaVideo.AudioRate);
        return _anySound;
    }

    /// <summary>A 48 kHz sample: the relay's 24 kHz mono, each sample twice, both ears; silence where no chunk came.</summary>
    public Vector2 Sample(long index)
    {
        if (index < 0) return Vector2.Zero;
        long n = index * CinemaVideo.RelaySoundRate / CinemaVideo.AudioRate;
        long chunk = n / CinemaVideo.RelayChunk;
        int slot = (int)(chunk % Slots);
        if (_chunkOf[slot] != chunk) return Vector2.Zero;
        float v = _sound[slot * CinemaVideo.RelayChunk + (int)(n % CinemaVideo.RelayChunk)];
        return new Vector2(v, v);
    }

    public void Heard(long index) { }

    public void Dispose() => _disposed = true;
}
