using Godot;
using UnitSport.Audio;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's sound while it plays a video (#1298), from its screen: the video's own sound, decoded by the hall's
/// <see cref="CinemaVideoStream"/>, pushed into an <see cref="AudioStreamGenerator"/> so that the sample heard at
/// local time t is the one due at <c>ServerNow(t) − T0</c>, as the car radio's speaker does
/// (<see cref="Audio.Live.WebRadioSpeaker"/>): the same sample in every ear in the hall. More than
/// <see cref="DriftTolerance"/> off, it jumps back onto the clock rather than chase. The hall's film music is off
/// meanwhile (<see cref="InteriorNode"/>).
/// </summary>
public partial class CinemaVideoSpeaker : AudioStreamPlayer3D, IMusicSpeaker
{
    private const float BaseDb = -4f;
    private const float DriftTolerance = 0.05f;

    /// <summary>The stream it plays, set by the hall each frame (null: silent).</summary>
    public ICinemaFeed? Video { get; set; }

    /// <summary>How far from it the sound keeps its full level, m: the hall's half-diagonal, its surround speakers.</summary>
    public float Spread { get; set; } = 8f;

    /// <summary>Times it jumped back onto the clock after the first; the sample heard now. For the probes.</summary>
    public int Resyncs { get; private set; }
    public long HeardIndex { get; private set; } = -1;

    private AudioStreamGeneratorPlayback? _playback;
    private long _next = -1;
    private int _capacity;
    private Vector2[] _frames = Array.Empty<Vector2>();
    private readonly Hearing _hearing = new(9000f);

    public float SourceDb { get; private set; }
    public bool Close => _hearing.Close;

    public override void _EnterTree() => MusicFocus.Add(this);
    public override void _ExitTree() => MusicFocus.Remove(this);

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Music;
        UnitSize = Spread;
        MaxDistance = Spread * 5;
        AttenuationModel = AttenuationModelEnum.InverseDistance;
        Stream = new AudioStreamGenerator { MixRate = CinemaVideo.AudioRate, BufferLength = 0.25f };
        _hearing.Attach(this);
    }

    public override void _Process(double delta)
    {
        _hearing.Step(this, (float)delta);
        SourceDb = BaseDb + _hearing.Db;
        VolumeDb = SourceDb;
        var stream = Video;
        double position = stream == null ? 0 : CinemaVideo.PositionAt(ClockSync.ServerNow, stream.T0);
        if (stream == null || !stream.SoundAt(position, out _))
        {
            if (Playing) Stop();
            _playback = null;
            HeardIndex = -1;
            return;
        }
        if (!Playing || _playback == null)
        {
            Play();
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
            _next = -1;
            _capacity = 0;
            if (_playback == null) return;
        }
        int available = _playback.GetFramesAvailable();
        _capacity = Math.Max(_capacity, available);
        int queued = _capacity - available;
        double latency = AudioServer.GetOutputLatency();
        // the sample that should be heard when the next frame pushed comes out of the speaker
        stream.SoundAt(position + queued / (double)CinemaVideo.AudioRate + latency, out long want);
        if (_next < 0 || Math.Abs(_next - want) > DriftTolerance * CinemaVideo.AudioRate)
        {
            if (_next >= 0) Resyncs++;
            _next = want;
        }
        HeardIndex = _next - queued - (long)(latency * CinemaVideo.AudioRate);
        stream.Heard(HeardIndex);
        if (available <= 0) return;
        if (_frames.Length < available) _frames = new Vector2[available];
        for (int i = 0; i < available; i++) _frames[i] = stream.Sample(_next++);
        _playback.PushBuffer(available == _frames.Length ? _frames : _frames[..available]);
    }
}
