using Godot;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Audio.Live;

/// <summary>
/// A car's loudspeaker for a live station (<see cref="WebRadio"/>). It feeds an
/// <see cref="AudioStreamGenerator"/> from the station's buffer so that the frame heard at local
/// time t is sample <c>(ServerNow(t) − T0 − Delay) · Rate</c>: the same sample on every machine.
/// When what it would play drifts from that by more than <see cref="DriftTolerance"/> it jumps
/// back into line rather than chase.
/// </summary>
public partial class WebRadioSpeaker : AudioStreamPlayer3D
{
    /// <summary>Under the engine, like the boombox (<see cref="RadioSpeaker"/>).</summary>
    private const float BaseDb = -6f;
    private const float DriftTolerance = 0.05f;

    public int Station { get; set; }

    /// <summary>The sample index coming out of the speaker right now, or -1 when silent. For the probes.</summary>
    public long HeardIndex { get; private set; } = -1;

    /// <summary>Times the speaker jumped back onto the clock after the first. For the probes.</summary>
    public int Resyncs { get; private set; }

    private AudioStreamGeneratorPlayback? _playback;
    private long _next = -1;
    private int _capacity;
    private Vector2[] _frames = Array.Empty<Vector2>();

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Music;
        // a car stereo: loud in the cabin, heard across a car park, gone down the road
        UnitSize = 3f;
        MaxDistance = 45f;
        AttenuationModel = AttenuationModelEnum.InverseDistance;
        AttenuationFilterCutoffHz = 6000f;
        Stream = new AudioStreamGenerator { MixRate = WebRadio.Rate, BufferLength = 0.25f };
        _hearing.Attach(this);
    }

    private readonly Hearing _hearing = new(6000f);

    /// <summary>The stereo's own volume, 0..1, everyone's (#734): gain and reach (<see cref="Items.RadioLoudness"/>).</summary>
    public float Volume { get; set; } = Items.RadioLoudness.Default;
    private float _rangeFor = float.NaN;

    public override void _Process(double delta)
    {
        _hearing.Step(this, (float)delta);
        VolumeDb = BaseDb + Items.RadioLoudness.Db(Volume) + _hearing.Db;
        if (Volume != _rangeFor) { _rangeFor = Volume; MaxDistance = Items.RadioLoudness.Radius(Volume); }
        var buffer = WebRadio.Instance?.Buffer(Station);
        if (buffer == null || buffer.End == 0)
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
        long want = (long)((ClockSync.ServerNow + queued / (double)WebRadio.Rate + latency - buffer.T0 - WebRadio.Delay) * WebRadio.Rate);
        if (_next < 0 || Math.Abs(_next - want) > DriftTolerance * WebRadio.Rate)
        {
            if (_next >= 0) Resyncs++;
            _next = want;
        }
        HeardIndex = _next - queued - (long)(latency * WebRadio.Rate);

        if (available <= 0) return;
        if (_frames.Length < available) _frames = new Vector2[available];
        for (int i = 0; i < available; i++)
        {
            float v = buffer.Sample(_next++);
            _frames[i] = new Vector2(v, v);
        }
        _playback.PushBuffer(available == _frames.Length ? _frames : _frames[..available]);
    }
}
