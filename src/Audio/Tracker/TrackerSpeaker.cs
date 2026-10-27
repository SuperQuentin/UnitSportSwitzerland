using Godot;
using UnitSport.Audio.Live;
using UnitSport.Net;

namespace UnitSport.Audio.Tracker;

/// <summary>
/// The fallback beside a venue's radio (#1244): when the relay has given its <see cref="VenueMusicSource"/>
/// nothing for <see cref="SilentFor"/> (offline without ffmpeg, a server without internet), this speaker
/// plays the venue's mood from <see cref="TrackerLibrary"/>, on the shared clock, so every client hears the
/// same bar. The radio back, it stops. Only the <see cref="MaxPlaying"/> nearest sources play at once
/// (<see cref="Rank"/>, from <see cref="WebRadio"/>'s scan): each one mixes its own song. Heard as the radio
/// is: <see cref="Hearing"/> through walls, ducked under a louder radio (<see cref="MusicFocus"/>).
/// </summary>
public partial class TrackerSpeaker : AudioStreamPlayer3D, IMusicSpeaker
{
    public const string NodeName = "Tracker";
    /// <summary>How long the radio stays silent before the module takes over, s.</summary>
    public const double SilentFor = 5;
    public const int MaxPlaying = 2;
    private const float BaseDb = -8f;
    private const double Resync = 1.0;

    private static readonly NodePath SelfPath = NodeName, RadioPath = WebRadio.SpeakerName;
    private static readonly HashSet<TrackerSpeaker> Allowed = new();
    private static readonly List<(float Dist, TrackerSpeaker S)> Ranking = new();

    public VenueMood Mood { get; set; }
    /// <summary>Shifts the venue's playlist on the clock, so neighbours of one kind do not play in unison.</summary>
    public uint Salt { get; set; }

    private readonly Hearing _hearing = new(6000f);
    private AudioStreamGeneratorPlayback? _playback;
    private TrackerPlayer? _player;
    private VenueMusicSource? _source;
    private WebRadioSpeaker? _radio;
    private string _file = "";
    private double _songEnds, _silent, _checkAt;
    private float[] _mix = Array.Empty<float>();
    private Vector2[] _frames = Array.Empty<Vector2>();
    private float _rangeFor = float.NaN;

    public float SourceDb { get; private set; }
    public bool Close => _hearing.Close;
    public float DuckDb { get; private set; }
    /// <summary>The module it plays now, or "" when silent. For the probes.</summary>
    public string Playing_ => _player != null ? _file : "";

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Music;
        UnitSize = 3f;
        MaxDistance = 45f;
        AttenuationModel = AttenuationModelEnum.InverseDistance;
        AttenuationFilterCutoffHz = 6000f;
        Stream = new AudioStreamGenerator { MixRate = Dsp.Rate, BufferLength = 0.25f };
        _hearing.Attach(this);
    }

    public override void _EnterTree() => MusicFocus.Add(this);

    public override void _ExitTree()
    {
        MusicFocus.Remove(this);
        Allowed.Remove(this);
    }

    /// <summary>
    /// The <see cref="MaxPlaying"/> nearest sources the radio leaves silent may play; called by
    /// <see cref="WebRadio"/>'s scan, 4 times a second, with the ears' place.
    /// </summary>
    public static void Rank(Vector3? ear)
    {
        Allowed.Clear();
        if (ear is not { } at) return;
        Ranking.Clear();
        foreach (var source in VenueMusicSource.All)
        {
            if (!IsInstanceValid(source) || source.GetNodeOrNull<TrackerSpeaker>(SelfPath) is not { } s) continue;
            if (s._silent < SilentFor) continue;
            float d = at.DistanceTo(source.GlobalPosition);
            if (d < Items.RadioLoudness.Radius(source.Volume)) Ranking.Add((d, s));
        }
        Ranking.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        for (int i = 0; i < Ranking.Count && i < MaxPlaying; i++) Allowed.Add(Ranking[i].S);
    }

    public override void _Process(double delta)
    {
        var source = _source ??= GetParent() as VenueMusicSource;
        // the radio beside it comes once the scan wakes the venue; held, not looked up every frame
        if (_radio == null || !IsInstanceValid(_radio)) _radio = source?.GetNodeOrNull<WebRadioSpeaker>(RadioPath);
        var radio = _radio;
        // the relay first: while it plays, this one waits
        _silent = radio != null && radio.HeardIndex >= 0 ? 0 : _silent + delta;
        if (_silent < SilentFor || !Allowed.Contains(this) || Mood == VenueMood.None)
        {
            if (Playing) Stop();
            _playback = null;
            _player = null;
            return;
        }
        float volume = source?.Volume ?? Items.RadioLoudness.Default;
        _hearing.Step(this, (float)delta);
        SourceDb = BaseDb + Items.RadioLoudness.Db(volume) + _hearing.Db;
        DuckDb = Items.RadioFocus.Step(DuckDb, MusicFocus.Full(this), (float)delta);
        VolumeDb = SourceDb + DuckDb;
        if (volume != _rangeFor) { _rangeFor = volume; MaxDistance = Items.RadioLoudness.Radius(volume); }

        double now = ClockSync.ServerNow;
        // a new song when the last one is over, and back onto the clock when it drifted
        if (_player == null || now >= _songEnds || now >= _checkAt && Math.Abs(OnClock(now) - _player.Time) > Resync)
            if (!Cue(now)) return;
        _checkAt = now + 1;
        if (!Playing || _playback == null)
        {
            Play();
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
        }
        if (_playback == null || _player == null) return;
        int frames = _playback.GetFramesAvailable();
        if (frames <= 0) return;
        if (_mix.Length < frames * 2) { _mix = new float[frames * 2]; _frames = new Vector2[frames]; }
        _player.Render(_mix, frames);
        for (int k = 0; k < frames; k++) _frames[k] = new Vector2(_mix[k * 2], _mix[k * 2 + 1]);
        _playback.PushBuffer(frames == _frames.Length ? _frames : _frames[..frames]);
    }

    /// <summary>How far into the current song the clock says it is, s.</summary>
    private double OnClock(double now) => TrackerLibrary.At(Mood, now, Salt) is { } at && at.File == _file ? at.Into : double.MaxValue;

    private bool Cue(double now)
    {
        if (TrackerLibrary.At(Mood, now, Salt) is not { } at) return false;
        var (module, _) = TrackerLibrary.Get(at.File);
        if (module == null) return false;
        _file = at.File;
        _player = new TrackerPlayer(module, Dsp.Rate);
        _player.Skip(at.Into);
        _songEnds = now + at.Left;
        GD.Print($"[tracker] {Mood} plays {at.File} from {at.Into:F0} s");
        return true;
    }
}
