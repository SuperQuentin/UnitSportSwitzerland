using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// The replay buffer (#638): always records the last <see cref="GameSettings.ReplayMinutes"/> of
/// every player in view, local and remote, at the network's own 30 Hz, from the same replicated
/// state another peer draws them from, and the world as it is drawn (#775). <see cref="Grab"/> hands
/// what was recorded since the last grab to a movie project.
///
/// <para>
/// Record / Stop (#775), like ReplayMod: <see cref="StartSession"/> opens a second
/// <see cref="Recording"/>, as long as <see cref="SessionMinutes"/>, fed from the same reads;
/// <see cref="StopSession"/> turns it into a project of its own. The buffer keeps running under it.
/// </para>
///
/// <para>
/// The game's own sound goes in too (#656): an <see cref="AudioEffectCapture"/> at the end of the
/// Master bus, drained five times a second into 16-bit mono at half the mix rate (about 2.8 MB a
/// minute). The capture hands over a new array on every drain; at 5 a second that is the one
/// allocation this records with.
/// </para>
/// </summary>
public partial class ReplayRecorder : Node
{
    public static ReplayRecorder? Instance { get; private set; }

    /// <summary>The longest Record / Stop session, after which it stops by itself.</summary>
    public const int SessionMinutes = 30;
    public const string GameSoundLane = "Game sound";

    /// <summary>The studio is open: its puppets are not players, and the world it shows is not being played.</summary>
    public bool Paused { get; set; }

    private Recording? _buffer, _session;
    // the world as it is drawn (#775), read once a tick for the buffer and a session alike
    private readonly WorldRecorder _world = new();
    private RecordingBadge? _badge;
    private readonly ActorState _scratch = new(ActorIo.Names.Length);
    private double _next;
    private int _minutes;

    // the game's sound (#656)
    private AudioEffectCapture? _capture;
    private float[] _halved = Array.Empty<float>();
    private double _nextDrain;
    private bool _wasPaused;

    /// <summary>A Record / Stop session is running.</summary>
    public bool Recording => _session != null;
    /// <summary>Seconds the session has run.</summary>
    public double SessionSeconds => _session is { Started: var s } && !double.IsNaN(s) ? GameClock.Now - s : 0;
    /// <summary>The session reached <see cref="SessionMinutes"/> and stopped: the project it left, for the world to save.</summary>
    public event Action<MovieProject>? SessionFull;
    /// <summary>Objects of the world being recorded now (for the checks and the pause menu).</summary>
    public int WorldObjects => _world.Live;

    public ReplayRecorder() => Name = "ReplayRecorder";

    private int SoundRate => (int)AudioServer.GetMixRate() / 2;

    public override void _EnterTree()
    {
        Instance = this;
        _minutes = GameSettings.Current.ReplayMinutes;
        MakeBuffer();
        GameSettings.Changed += OnSettings;
        _capture = new AudioEffectCapture { BufferLength = 0.5f };
        AudioServer.AddBusEffect(0, _capture);
    }

    private void MakeBuffer()
    {
        _buffer?.Close();
        _buffer = _minutes > 0 ? new Recording(_minutes * 60, SoundRate, _world, GameClock.Now) : null;
    }

    public override void _ExitTree()
    {
        if (_capture != null)
            for (int i = AudioServer.GetBusEffectCount(0) - 1; i >= 0; i--)
                if (AudioServer.GetBusEffect(0, i) == _capture) AudioServer.RemoveBusEffect(0, i);
        GameSettings.Changed -= OnSettings;
        if (Instance == this) Instance = null;
    }

    private void OnSettings()
    {
        if (GameSettings.Current.ReplayMinutes == _minutes) return;
        _minutes = GameSettings.Current.ReplayMinutes;
        MakeBuffer();   // the rings are sized by it
    }

    // ---- Record / Stop -------------------------------------------------------------------------

    /// <summary>Starts a session: everything from now on, up to <see cref="SessionMinutes"/>. False if one runs.</summary>
    public bool StartSession()
    {
        if (_session != null) return false;
        _session = new Recording(SessionMinutes * 60, SoundRate, _world, GameClock.Now);
        AddChild(_badge = new RecordingBadge(this));
        GD.Print("[movie] recording started");
        return true;
    }

    /// <summary>Stops the session: what it recorded as a project of its own, or null when nothing was.</summary>
    public MovieProject? StopSession()
    {
        if (_session is not { } s) return null;
        _session = null;
        s.Close();
        _badge?.QueueFree();
        _badge = null;
        var project = new MovieProject(ActorIo.Names) { Name = $"Recording {DateTime.Now:yyyy-MM-dd HH-mm}" };
        int clips = s.Grab(project, GameClock.Now);
        GD.Print($"[movie] recording stopped: {clips} clips from {s.Players} players, {project.Duration:F1} s");
        return clips > 0 ? project : null;
    }

    // ---- recording -----------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_capture == null) return;
        double now = GameClock.Now;
        if (now < _nextDrain) return;
        _nextDrain = now + 0.2;
        int n = _capture.GetFramesAvailable();
        if (n == 0) return;
        var frames = _capture.GetBuffer(n);
        // while the studio is open the sound is its own playback: dropped, and what was before no
        // longer runs on into what comes after
        if (Paused) { _wasPaused = true; return; }
        if (_wasPaused) { _buffer?.BreakSound(); _session?.BreakSound(); _wasPaused = false; }
        int half = frames.Length / 2;
        if (_halved.Length < half) _halved = new float[half];
        for (int i = 0; i < half; i++)
        {
            var a = frames[2 * i];
            var b = frames[2 * i + 1];
            _halved[i] = (a.X + a.Y + b.X + b.Y) * 0.25f;
        }
        _buffer?.Sound(_halved.AsSpan(0, half), now);
        _session?.Sound(_halved.AsSpan(0, half), now);
    }

    /// <summary>Seconds still held by the buffer since the last grab, for the pause menu's "last 4:32".</summary>
    public double Held => _buffer?.Held ?? 0;

    public override void _PhysicsProcess(double delta)
    {
        if (Paused || (_buffer == null && _session == null)) return;
        double now = GameClock.Now;
        if (now + 1e-6 < _next) return;
        _next = now + 1.0 / Channels.Rate;

        foreach (var sample in PlayerSnapshot.Of(GetTree()))
        {
            var p = sample.Player;
            if (p.Puppet || p.NetProxy) continue;
            ulong id = p.GetInstanceId();
            ActorIo.Read(p, _scratch);
            _buffer?.Actor(p, id, _scratch, now);
            _session?.Actor(p, id, _scratch, now);
        }
        if (OriginNow() is { } origin) _world.Tick(now, origin);
        _buffer?.Prune(now);
        _session?.Prune(now);

        if (_session != null && SessionSeconds >= SessionMinutes * 60 && StopSession() is { } full) SessionFull?.Invoke(full);
    }

    private WorldOrigin? OriginNow() =>
        GetParent() is ClientWorld w ? w.Chunks?.Origin ?? w.StudioPlayer?.Origin : null;

    /// <summary>
    /// Everything the buffer recorded since the last grab, as one clip per player on that player's
    /// lane, the world and the sound, at the timeline time it happened. The number of clips added.
    /// </summary>
    public int Grab(MovieProject project) => _buffer?.Grab(project, GameClock.Now) ?? 0;

    /// <summary>A new or loaded project: the next grab hands it the whole buffer again.</summary>
    public void ForgetGrabs() => _buffer?.ForgetGrabs();
}
