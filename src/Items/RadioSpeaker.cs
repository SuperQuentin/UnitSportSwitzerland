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
public partial class RadioSpeaker : AudioStreamPlayer3D, IMusicSpeaker
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

    private readonly Dictionary<(int Id, int Part), string> _have = new();
    private (int Id, int Part) _fetching = (0, -1);
    private int _pathFor, _failed, _stemsFailed;
    private double _sinceSeek, _failedAt;

    /// <summary>
    /// The voice/music mix (#1153), 0..1 each, everyone's like <see cref="Volume"/>: at 1 and 1 the
    /// CD plays as burnt; otherwise its two stems play in step (<see cref="AudioStreamSynchronized"/>),
    /// each at its own level. A CD without stems (or a server that has none) plays as burnt.
    /// </summary>
    public float Voice { get; set; } = 1f;
    public float Music { get; set; } = 1f;

    /// <summary>The stems are in the player (rather than the CD as burnt). For the probes.</summary>
    public bool LoadedStems { get; private set; }

    private float _sentVoice = float.NaN, _sentMusic = float.NaN;

    // the listener's own level is the Music bus (Settings -> Audio -> Music, #261), applied by the
    // bus; this radio's own volume (#734) is its gain and reach below, the same for everyone

    private readonly Hearing _hearing = new(8000f);

    /// <summary>The radio's own volume, 0..1, everyone's (#734): its gain and how far it reaches (<see cref="RadioLoudness"/>).</summary>
    public float Volume { get; set; } = RadioLoudness.Default;

    /// <summary>A stadium's PA (#885): how far it carries and the size of its full-volume zone, instead of a boombox's.</summary>
    public float? Reach { get; init; }
    public float? Unit { get; init; }
    private float _rangeFor = float.NaN;

    /// <summary>The path the sound takes to this machine's ear: "open", "wall", "door", "walls". For the probes.</summary>
    public string HeardThrough => _hearing.Path;

    /// <summary>The share of the occlusion rays blocked, 0..1. For the probes.</summary>
    public float HeardBlocked => _hearing.Blocked;

    /// <summary>Where the sound comes from on the parent (its offset at _Ready). For the probes.</summary>
    public Vector3 SourceOffset => _hearing.Offset;

    /// <summary>Its level before the duck, dB (#813).</summary>
    public float SourceDb { get; private set; }

    public bool Close => _hearing.Close;

    /// <summary>How far it is ducked under the radio the ears follow, dB (0 or down to <see cref="RadioFocus.DuckDb"/>). For the probes.</summary>
    public float DuckDb { get; private set; }

    public override void _EnterTree() => MusicFocus.Add(this);
    public override void _ExitTree() => MusicFocus.Remove(this);

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Music;
        // full volume within ~3 m, a quarter at 12 m, gone past 45 m: a boombox, not a stadium
        UnitSize = Unit ?? 3f;
        MaxDistance = Reach ?? 45f;
        AttenuationModel = AttenuationModelEnum.InverseDistance;
        AttenuationFilterCutoffHz = 8000f;
        _hearing.Attach(this);
    }

    public override void _Process(double delta)
    {
        // where it is heard from and how muffled: only worth the rays while it has something to play
        if (On && CdId != 0) _hearing.Step(this, (float)delta);
        SourceDb = BaseDb + RadioLoudness.Db(Volume) + _hearing.Db;
        // another radio louder at the ears: this one plays under it (#813)
        DuckDb = RadioFocus.Step(DuckDb, MusicFocus.Full(this), (float)delta);
        VolumeDb = SourceDb + DuckDb;
        if (Volume != _rangeFor) { _rangeFor = Volume; MaxDistance = Reach ?? RadioLoudness.Radius(Volume); }
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
        // the mix left 1/1 (or came back): the stems take over once here, the CD plays on until then
        else if (LoadedStems != WantsStems(CdId) && TryLoad()) Silence();
        ApplyMix();

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

    /// <summary>Whether CD <paramref name="id"/> should play from its stems: a mix other than 1/1 and stems to play.</summary>
    private bool WantsStems(int id) => (MathF.Abs(Voice - 1f) > 0.01f || MathF.Abs(Music - 1f) > 0.01f)
        && _stemsFailed != id && CdLibrary.Instance?.Find(id) is { Analysis.Stems: true };

    /// <summary>Each stem's level, only when it changed; the synchronized stream reads it as it mixes.</summary>
    private void ApplyMix()
    {
        if (!LoadedStems || Stream is not AudioStreamSynchronized sync || (Voice == _sentVoice && Music == _sentMusic)) return;
        _sentVoice = Voice;
        _sentMusic = Music;
        sync.SetSyncStreamVolume(0, Db(Music));
        sync.SetSyncStreamVolume(1, Db(Voice));
    }

    private static float Db(float gain) => gain <= 0.001f ? -80f : 20f * MathF.Log10(gain);

    /// <summary>
    /// Puts the CD's Ogg in the player, or its two stems (#1153) when the mix asks for them,
    /// fetching from the server first if need be. False while something is still on its way.
    /// </summary>
    private bool TryLoad()
    {
        int id = CdId;
        if (_pathFor != id)
        {
            _have.Clear();
            _pathFor = id;
        }
        bool stems = WantsStems(id);
        string? mix = null, music = null, voice = null;
        if (stems)
        {
            music = Need(id, CdStem.Music);
            voice = Need(id, CdStem.Voice);
            if (music == null || voice == null) return false;
        }
        else if ((mix = Need(id, null)) == null) return false;

        AudioStream stream;
        if (stems)
        {
            var m = AudioStreamOggVorbis.LoadFromFile(music);
            var v = AudioStreamOggVorbis.LoadFromFile(voice);
            if (m == null || v == null) { _stemsFailed = id; _have.Clear(); return false; }
            m.Loop = v.Loop = false;
            var sync = new AudioStreamSynchronized { StreamCount = 2 };
            sync.SetSyncStream(0, m);
            sync.SetSyncStream(1, v);
            stream = sync;
            LoadedLength = m.GetLength();
        }
        else
        {
            var ogg = AudioStreamOggVorbis.LoadFromFile(mix);
            if (ogg == null)
            {
                GD.PushWarning($"[cd] could not load {mix}");
                _have.Clear();
                Fail(id);
                return false;
            }
            ogg.Loop = false;
            stream = ogg;
            LoadedLength = ogg.GetLength();
        }
        Stream = stream;
        LoadedCd = id;
        LoadedStems = stems;
        _sentVoice = _sentMusic = float.NaN;
        GD.Print($"[radio] speaker {GetParent()?.Name} loaded CD {id}{(stems ? FormattableString.Invariant($" as stems (voice {Voice:F2}, music {Music:F2})") : "")} ({LoadedLength:F1} s)");
        return true;
    }

    /// <summary>The file of CD <paramref name="id"/> (or a stem of it) if here, else starts fetching it and answers null.</summary>
    private string? Need(int id, CdStem? stem)
    {
        var key = (id, CdCache.Part(stem));
        if (_have.TryGetValue(key, out var path)) return path;
        if (_fetching == key) return null;
        if (stem == null && _failed == id && Time.GetTicksMsec() / 1000.0 - _failedAt < RetryAfter) return null;
        if (CdCache.LocalPath(id, stem) is { } here) return _have[key] = here;
        // someone else's personal CD: no file anywhere we could ask for
        if (id < 0 || Streamer() is not { } streamer)
        {
            if (stem == null) Fail(id);
            else _stemsFailed = id;
            return null;
        }
        _fetching = key;
        _ = CdCache.FetchAsync(streamer, id, stem).ContinueWith(t => Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            if (_fetching == key) _fetching = (0, -1);
            if (t.Result is { } got) { if (_pathFor == id) _have[key] = got; }
            // a server without this CD's stems: the CD plays as burnt
            else if (stem == null) Fail(id);
            else _stemsFailed = id;
        }).CallDeferred());
        return null;
    }

    private void Fail(int id)
    {
        _failed = id;
        _failedAt = Time.GetTicksMsec() / 1000.0;
    }

    private static ChunkStreamer? Streamer() =>
        RadioManager.Instance?.GetNodeOrNull<ChunkStreamer>("../" + ChunkStreamer.NodeName);
}
