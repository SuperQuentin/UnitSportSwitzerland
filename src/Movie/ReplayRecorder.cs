using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// The replay buffer (#638): always records the last <see cref="GameSettings.ReplayMinutes"/> of
/// every player in view, local and remote, at the network's own 30 Hz, from the same replicated
/// state another peer draws them from. <see cref="Grab"/> hands what was recorded since the last
/// grab to a movie project. One fixed ring per player: nothing is allocated while it records.
///
/// <para>
/// The game's own sound goes in too (#656): an <see cref="AudioEffectCapture"/> at the end of the
/// Master bus, drained five times a second into a 16-bit mono <see cref="SoundRing"/> at half the
/// mix rate (about 2.8 MB a minute). The capture hands over a new array on every drain; at 5 a
/// second that is the one allocation this records with.
/// </para>
/// </summary>
public partial class ReplayRecorder : Node
{
    public static ReplayRecorder? Instance { get; private set; }

    /// <summary>The studio is open: its puppets are not players, and the world it shows is not being played.</summary>
    public bool Paused { get; set; }

    private sealed class Actor
    {
        public required ReplayRing Ring;
        public required string Key, Label;
        public long Peer;
        public double LastSeen, Grabbed = double.NegativeInfinity;
    }

    // by instance: a node's name is a StringName, and turning it into a string every frame allocates
    private readonly Dictionary<ulong, Actor> _actors = new();
    private readonly ActorState _scratch = new(ActorIo.Names.Length);
    private double _next, _nextPrune;
    private int _minutes;

    // the game's sound (#656)
    private AudioEffectCapture? _capture;
    private SoundRing? _sound;
    private float[] _halved = Array.Empty<float>();
    private double _nextDrain, _soundGrabbed = double.NegativeInfinity;
    private bool _wasPaused;
    public const string GameSoundLane = "Game sound";

    public ReplayRecorder() => Name = "ReplayRecorder";

    public override void _EnterTree()
    {
        Instance = this;
        _minutes = GameSettings.Current.ReplayMinutes;
        GameSettings.Changed += OnSettings;
        _capture = new AudioEffectCapture { BufferLength = 0.5f };
        AudioServer.AddBusEffect(0, _capture);
        MakeSoundRing();
    }

    private void MakeSoundRing() =>
        _sound = _minutes > 0 ? new SoundRing(_minutes * 60, (int)AudioServer.GetMixRate() / 2) : null;

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
        _actors.Clear();   // the rings are sized by it
        MakeSoundRing();
    }

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
        if (Paused || _sound == null) { _wasPaused = true; return; }
        if (_wasPaused) { _sound.Clear(); _wasPaused = false; }
        int half = frames.Length / 2;
        if (_halved.Length < half) _halved = new float[half];
        for (int i = 0; i < half; i++)
        {
            var a = frames[2 * i];
            var b = frames[2 * i + 1];
            _halved[i] = (a.X + a.Y + b.X + b.Y) * 0.25f;
        }
        _sound.Append(_halved.AsSpan(0, half), now);
    }

    private int Capacity => (int)(_minutes * 60 * Channels.Rate) + 1;

    /// <summary>Seconds of the oldest frame still held, for the pause menu's "last 4:32".</summary>
    public double Held
    {
        get
        {
            double held = 0;
            foreach (var a in _actors.Values)
                if (a.Ring.Count > 1) held = Math.Max(held, a.Ring.Newest - Math.Max(a.Ring.Oldest, a.Grabbed));
            return held;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Paused || _minutes <= 0) return;
        double now = GameClock.Now;
        if (now + 1e-6 < _next) return;
        _next = now + 1.0 / Channels.Rate;

        foreach (var sample in PlayerSnapshot.Of(GetTree()))
        {
            var p = sample.Player;
            if (p.Puppet || p.NetProxy) continue;
            ulong id = p.GetInstanceId();
            if (!_actors.TryGetValue(id, out var actor))
            {
                string key = p.Name;
                long peer = FootPlayer.NetId(key) ?? 0;
                actor = new Actor
                {
                    Ring = new ReplayRing(Capacity, ActorIo.Names.Length),
                    Key = key,
                    Label = p.IsMultiplayerAuthority() && !p.Npc ? "You" : p.Npc ? $"Racer {key}" : $"Player {key}",
                    Peer = peer,
                };
                _actors[id] = actor;
            }
            ActorIo.Read(p, _scratch);
            actor.Ring.Append(now, _scratch);
            actor.LastSeen = now;
        }

        if (now >= _nextPrune)
        {
            // a player gone from view stays grabbable until its last frame is older than the buffer
            _nextPrune = now + 10;
            double keep = _minutes * 60;
            foreach (var (id, a) in _actors.ToArray())
                if (now - a.LastSeen > keep) _actors.Remove(id);
        }
    }

    /// <summary>
    /// Everything recorded since the last grab, as one clip per player on that player's lane, at the
    /// timeline time it happened. The number of clips added.
    /// </summary>
    public int Grab(MovieProject project)
    {
        var slices = new List<(Actor Actor, ActorTrack Track, double Start)>();
        foreach (var a in _actors.Values)
            if (a.Ring.Slice(a.Grabbed, out double start) is { } track) slices.Add((a, track, start));
        // the earliest first: it anchors this session's clock on the timeline, the rest keep their spacing from it
        slices.Sort((x, y) => x.Start.CompareTo(y.Start));
        foreach (var (a, track, start) in slices)
        {
            int lane = project.LaneFor(a.Key, a.Label, a.Peer);
            project.AddTrack(lane, track, project.PlaceGrab(start));
            a.Grabbed = a.Ring.Newest;
        }
        return slices.Count + (GrabSound(project, slices.Count > 0 ? slices[0].Start : double.NegativeInfinity) ? 1 : 0);
    }

    /// <summary>The game's sound since the last grab, from <paramref name="notBefore"/> on, on its own lane (#656).</summary>
    private bool GrabSound(MovieProject project, double notBefore)
    {
        if (_sound == null || _sound.Count == 0) return false;
        var samples = _sound.Slice(_soundGrabbed, out double start);
        if (start < notBefore)
        {
            int skip = Math.Min(samples.Length, (int)Math.Round((notBefore - start) * _sound.Rate));
            samples = samples[skip..];
            start = notBefore;
        }
        if (SoundImport.FromGame(samples, _sound.Rate) is not { } asset) return false;
        project.AddAudio(project.AudioLaneFor(GameSoundLane), asset, project.PlaceGrab(start));
        _soundGrabbed = _sound.Newest;
        return true;
    }

    /// <summary>A new or loaded project: the next grab hands it the whole buffer again.</summary>
    public void ForgetGrabs()
    {
        foreach (var a in _actors.Values) a.Grabbed = double.NegativeInfinity;
        _soundGrabbed = double.NegativeInfinity;
    }
}
