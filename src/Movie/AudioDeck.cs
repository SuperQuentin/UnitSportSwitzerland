using Godot;

namespace UnitSport.Movie;

/// <summary>
/// The movie's sound clips played in step with the stage's clock (#656): one player per clip,
/// started where the clock is, nudged back when it drifts more than a tenth of a second, its pitch
/// following the playback speed. Silent backwards, while paused and while scrubbing. Straight to
/// the Master bus, so muting the world's buses (the studio's "World sound") never mutes the movie.
/// </summary>
public partial class AudioDeck : Node
{
    private const double Drift = 0.1;

    private readonly Dictionary<int, AudioStreamPlayer> _players = new();
    private readonly Dictionary<string, AudioStream?> _streams = new();
    private readonly List<int> _gone = new();
    private int _clipsSeen = -1;

    public AudioDeck() => Name = "AudioDeck";

    /// <summary>Whether clip <paramref name="id"/> is sounding now (for the checks).</summary>
    public bool Sounding(int id) => _players.TryGetValue(id, out var p) && p.Playing;

    /// <summary>Every clip's player in line with <paramref name="time"/>; <paramref name="audible"/> false silences them all.</summary>
    public void Follow(MovieProject project, double time, double speed, bool audible)
    {
        if (project.Clips.Count != _clipsSeen) Prune(project);
        foreach (var c in project.Clips)
        {
            if (!c.Audio) continue;
            bool on = audible && speed > 0 && c.Covers(time) && time < c.End - 0.02;
            _players.TryGetValue(c.Id, out var player);
            if (!on)
            {
                if (player is { Playing: true }) player.Stop();
                continue;
            }
            player ??= Add(project, c);
            if (player == null) continue;
            double want = c.Local(time);
            player.PitchScale = (float)Math.Clamp(speed, 0.1, 4);
            if (!player.Playing) player.Play((float)want);
            else if (Math.Abs(player.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - want) > Drift * speed) player.Seek((float)want);
        }
    }

    public void StopAll()
    {
        foreach (var p in _players.Values) p.Stop();
    }

    private AudioStreamPlayer? Add(MovieProject project, Clip c)
    {
        string file = project.Audio[c.Track].File;
        if (!_streams.TryGetValue(file, out var stream))
            _streams[file] = stream = AudioDecode.Load(SoundImport.PathOf(file));
        if (stream == null) return null;
        var player = new AudioStreamPlayer { Stream = stream, Bus = "Master" };
        AddChild(player);
        _players[c.Id] = player;
        return player;
    }

    /// <summary>Players of clips that were deleted, split away or loaded over go.</summary>
    private void Prune(MovieProject project)
    {
        _clipsSeen = project.Clips.Count;
        _gone.Clear();
        foreach (var id in _players.Keys)
            if (project.Find(id) is not { Audio: true }) _gone.Add(id);
        foreach (var id in _gone)
        {
            _players[id].QueueFree();
            _players.Remove(id);
        }
    }

    /// <summary>Another project: every player goes.</summary>
    public void Reset()
    {
        foreach (var p in _players.Values) p.QueueFree();
        _players.Clear();
        _clipsSeen = -1;
    }
}
