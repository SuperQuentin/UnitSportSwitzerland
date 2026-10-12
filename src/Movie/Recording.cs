using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// One recording of the game (#638, #775): a ring per player, the world as it is drawn and the game's
/// sound, all holding the last <see cref="Seconds"/>. The replay buffer is one, sized by the setting
/// and always running; a Record / Stop session is another, a minute long, emptied into a
/// <see cref="SessionSpool"/> piece by piece (<see cref="Piece"/>, #782). <see cref="ReplayRecorder"/>
/// reads the game once a tick and feeds both. Everything is allocated up front: nothing more while it records.
/// </summary>
public sealed class Recording
{
    private sealed class Tracked
    {
        public required ReplayRing Ring;
        public required string Key, Label;
        public long Peer;
        public int Serial;
        public double LastSeen, Grabbed = double.NegativeInfinity;
    }

    public double Seconds { get; }
    // by instance: a node's name is a StringName, and turning it into a string every frame allocates
    private readonly Dictionary<ulong, Tracked> _actors = new();
    private readonly WorldRecorder _world;   // shared by every recording: the world is read once
    private readonly WorldRing _ring;
    private readonly SoundRing? _sound;
    private double _worldGrabbed = double.NegativeInfinity, _soundGrabbed = double.NegativeInfinity, _nextPrune;
    private int _serials;

    public Recording(double seconds, int soundRate, WorldRecorder world, double now)
    {
        Seconds = seconds;
        _world = world;
        _ring = new WorldRing(seconds);
        world.Attach(_ring, now);
        _sound = soundRate > 0 ? new SoundRing((int)Math.Ceiling(seconds), soundRate) : null;
    }

    /// <summary>When it began (game clock), NaN before the first tick.</summary>
    public double Started { get; private set; } = double.NaN;
    public int WorldObjects => _world.Live;

    /// <summary>Its world ring no longer written to: the recording is over.</summary>
    public void Close() => _world.Detach(_ring);
    public int Players => _actors.Count;
    private int Capacity => (int)(Seconds * Channels.Rate) + 1;

    /// <summary>Seconds still held since the last grab, for the pause menu's "last 4:32".</summary>
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

    /// <summary>Player <paramref name="p"/>, already read into <paramref name="state"/>, at <paramref name="now"/>.</summary>
    public void Actor(FootPlayer p, ulong id, ActorState state, double now)
    {
        if (double.IsNaN(Started)) Started = now;
        if (!_actors.TryGetValue(id, out var actor))
        {
            string key = p.Name;
            actor = new Tracked
            {
                Ring = new ReplayRing(Capacity, ActorIo.Names.Length),
                Key = key,
                Label = p.IsMultiplayerAuthority() && !p.Npc ? "You" : p.Npc ? $"Racer {key}" : $"Player {key}",
                Peer = FootPlayer.NetId(key) ?? 0,
                Serial = ++_serials,
            };
            _actors[id] = actor;
        }
        actor.Ring.Append(now, state);
        actor.LastSeen = now;
    }

    /// <summary>Players long gone from view forgotten (the world itself is read by the shared recorder).</summary>
    public void Prune(double now)
    {
        if (now < _nextPrune) return;
        // a player gone from view stays grabbable until its last frame is older than what is held
        _nextPrune = now + 10;
        foreach (var (id, a) in _actors.ToArray())
            if (now - a.LastSeen > Seconds) _actors.Remove(id);
    }

    public void Sound(ReadOnlySpan<float> mono, double now) => _sound?.Append(mono, now);

    /// <summary>The sound before a pause does not run on into what comes after it.</summary>
    public void BreakSound() => _sound?.Clear();

    /// <summary>
    /// Everything recorded since the last grab, as one clip per player on that player's lane, the world
    /// on its row and the game's sound, at the timeline time it happened. The number of clips added.
    /// </summary>
    public int Grab(MovieProject project, double now)
    {
        var slices = new List<(Tracked Actor, ActorTrack Track, double Start)>();
        foreach (var a in _actors.Values)
            if (a.Ring.Slice(a.Grabbed, out double start) is { } track) slices.Add((a, track, start));
        // the earliest first: it anchors this session's clock on the timeline, the rest keep their spacing from it
        slices.Sort((x, y) => x.Start.CompareTo(y.Start));
        var world = _ring.Slice(_worldGrabbed, now, _world.Looks, out double worldStart);
        if (world != null && (slices.Count == 0 || worldStart < slices[0].Start)) project.PlaceGrab(worldStart);
        foreach (var (a, track, start) in slices)
        {
            int lane = project.LaneFor(a.Key, a.Label, a.Peer);
            project.AddTrack(lane, track, project.PlaceGrab(start));
            a.Grabbed = a.Ring.Newest;
        }
        if (world != null)
        {
            project.AddWorld(world, project.PlaceGrab(worldStart));
            _worldGrabbed = now;
            GD.Print($"[movie] grabbed the world: {world.Entities.Count(e => e.IsGroup)} objects, {_world.Live} live, {_world.TickMicros:F0} µs a tick, "
                + $"{world.Entities.Sum(e => e.Times.Length)} poses, {world.Looks.Length} looks, {world.Duration:F1} s");
        }
        // the sound from where the pictures begin: a drain may hand over samples heard just before
        double first = slices.Count > 0 ? slices[0].Start : double.PositiveInfinity;
        if (world != null) first = Math.Min(first, worldStart);
        return slices.Count + (world != null ? 1 : 0)
            + (GrabSound(project, double.IsPositiveInfinity(first) ? double.NegativeInfinity : first) ? 1 : 0);
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
        project.AddAudio(project.AudioLaneFor(MovieProject.GameSoundLane), asset, project.PlaceGrab(start));
        _soundGrabbed = _sound.Newest;
        return true;
    }

    /// <summary>
    /// A Record / Stop session's piece (#782): everything recorded since the last piece, the world
    /// sliced raw (the recorder's ids and looks, for the join) and every sample of sound since.
    /// </summary>
    public SessionSpool.Piece Piece(double now)
    {
        var piece = new SessionSpool.Piece();
        foreach (var a in _actors.Values)
        {
            if (a.Ring.Slice(a.Grabbed, out double start) is not { } track) continue;
            piece.Actors.Add(new SessionSpool.ActorPiece(a.Serial, a.Key, a.Label, a.Peer, start, track));
            a.Grabbed = a.Ring.Newest;
        }
        piece.World = _ring.Slice(_worldGrabbed, now, _world.Looks, out piece.WorldStart, raw: true);
        if (piece.World != null) _worldGrabbed = now;
        if (_sound is { Count: > 0 }) piece.Sound = _sound.Drain(out piece.SoundStart);
        return piece;
    }

    /// <summary>A new or loaded project: the next grab hands it everything held again.</summary>
    public void ForgetGrabs()
    {
        foreach (var a in _actors.Values) a.Grabbed = double.NegativeInfinity;
        _worldGrabbed = double.NegativeInfinity;
        _soundGrabbed = double.NegativeInfinity;
    }
}
