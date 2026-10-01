using Godot;
using UnitSport.Audio;
using UnitSport.Audio.Cd;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// The loudspeaker of a radio, in the world (<see cref="RadioBody"/>) or in a player's hand
/// (<see cref="RadioManager"/> hangs one on the holder). Whoever owns it says which CD plays since
/// when; the speaker fetches the Ogg once (<see cref="CdCache"/>) and plays it from
/// <c>ServerNow − StartedAt</c> on the shared clock, nudging the playback position back in line
/// when it drifts. Nobody streams audio to anyone.
///
/// <para>
/// Everything it has loaded or is fetching is keyed by the CD id, so changing CD loads the new
/// file: the radio used to keep one Ogg path for its whole life and played the first CD's audio
/// on the second CD's clock (#168).
/// </para>
/// </summary>
public partial class RadioSpeaker : AudioStreamPlayer3D
{
    /// <summary>Under the engines and footsteps: a radio is background, not a PA system.</summary>
    private const float BaseDb = -8f;
    private const float DriftTolerance = 0.08f;
    private const double RetryAfter = 10;
    private const string SettingsFile = "user://radio.cfg";

    public int CdId { get; set; }
    public double StartedAt { get; set; }
    public bool On { get; set; }

    /// <summary>Seconds the CD lasts; silent after.</summary>
    public float Length { get; set; }

    /// <summary>Where the clock says the CD is, seconds, or NaN when nothing plays.</summary>
    public double WantedPosition => On ? ClockSync.ServerNow - StartedAt : double.NaN;

    /// <summary>What the speaker is at right now, seconds into the CD, or NaN while silent. For the probes.</summary>
    public double HeardPosition { get; private set; } = double.NaN;

    /// <summary>The CD whose audio is in the player (0 = none) and that audio's length. For the probes.</summary>
    public int LoadedCd { get; private set; }
    public double LoadedLength { get; private set; }

    private string? _path;
    private int _pathFor, _fetching, _failed;
    private double _sinceSeek, _failedAt;

    // ---- the listener's volume, one for every radio, kept on this machine --------------------

    private static float _volume = -1;

    /// <summary>0..1, the panel's slider; scales every radio this player hears.</summary>
    public static float UserVolume
    {
        get
        {
            if (_volume < 0)
            {
                var cfg = new ConfigFile();
                _volume = cfg.Load(SettingsFile) == Error.Ok ? Mathf.Clamp(cfg.GetValue("radio", "volume", 0.6f).AsSingle(), 0f, 1f) : 0.6f;
            }
            return _volume;
        }
        set => _volume = Mathf.Clamp(value, 0f, 1f);
    }

    public static void SaveVolume()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("radio", "volume", UserVolume);
        cfg.Save(SettingsFile);
    }

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Name;
        // full volume within ~3 m, a quarter at 12 m, gone past 45 m: a boombox, not a stadium
        UnitSize = 3f;
        MaxDistance = 45f;
        AttenuationModel = AttenuationModelEnum.InverseDistance;
        AttenuationFilterCutoffHz = 8000f;
    }

    public override void _Process(double delta)
    {
        VolumeDb = UserVolume <= 0.001f ? -80f : BaseDb + Mathf.LinearToDb(UserVolume);
        _sinceSeek += delta;
        double want = WantedPosition;
        if (double.IsNaN(want) || want < 0 || want >= Length || CdId == 0)
        {
            Silence();
            return;
        }

        if (LoadedCd != CdId)
        {
            Silence();
            if (!TryLoad()) return;
        }

        if (!Playing)
        {
            Play((float)want);
            _sinceSeek = 0;
            return;
        }
        double heard = GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - AudioServer.GetOutputLatency();
        HeardPosition = heard;
        // a nudge, not a chase: a seek every frame would stutter, and the clock itself moves
        if (Math.Abs(heard - want) > DriftTolerance && _sinceSeek > 1.0)
        {
            Seek((float)want);
            _sinceSeek = 0;
        }
    }

    private void Silence()
    {
        if (Playing) Stop();
        HeardPosition = double.NaN;
    }

    /// <summary>Puts the CD's Ogg in the player, fetching it from the server first if need be.</summary>
    private bool TryLoad()
    {
        int id = CdId;
        if (_pathFor != id)
        {
            _path = null;
            _pathFor = id;
        }
        if (_path == null)
        {
            if (_fetching == id) return false;
            if (_failed == id && Time.GetTicksMsec() / 1000.0 - _failedAt < RetryAfter) return false;
            if (CdCache.LocalPath(id) is { } here) _path = here;
            else
            {
                // someone else's personal CD: no file anywhere we could ask for
                if (id < 0 || Streamer() is not { } streamer) { Fail(id); return false; }
                _fetching = id;
                _ = CdCache.FetchAsync(streamer, id).ContinueWith(t => Callable.From(() =>
                {
                    if (!IsInstanceValid(this)) return;
                    if (_fetching == id) _fetching = 0;
                    if (t.Result is { } path) { if (_pathFor == id) _path = path; }
                    else Fail(id);
                }).CallDeferred());
                return false;
            }
        }
        var stream = AudioStreamOggVorbis.LoadFromFile(_path);
        if (stream == null)
        {
            GD.PushWarning($"[cd] could not load {_path}");
            _path = null;
            Fail(id);
            return false;
        }
        stream.Loop = false;
        Stream = stream;
        LoadedCd = id;
        LoadedLength = stream.GetLength();
        GD.Print($"[radio] speaker {GetParent()?.Name} loaded CD {id} ({LoadedLength:F1} s)");
        return true;
    }

    private void Fail(int id)
    {
        _failed = id;
        _failedAt = Time.GetTicksMsec() / 1000.0;
    }

    private static ChunkStreamer? Streamer() =>
        RadioManager.Instance?.GetNodeOrNull<ChunkStreamer>("../" + ChunkStreamer.NodeName);
}
