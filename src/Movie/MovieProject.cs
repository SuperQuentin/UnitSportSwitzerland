namespace UnitSport.Movie;

/// <summary>One actor's row on the timeline: every clip of the same player lands on it.</summary>
public sealed class Lane
{
    public required string Key { get; init; }    // the player's node name: one lane per player per project
    public required string Label { get; set; }
    /// <summary>The peer id it had when recorded: what other players' <c>RidingWith</c> named it by.</summary>
    public long PeerId { get; init; }
}

/// <summary>
/// A piece of a recorded track placed on the timeline: the track's local times
/// <see cref="In"/>..<see cref="Out"/> play from timeline time <see cref="Start"/>.
/// </summary>
public sealed class Clip
{
    public int Id { get; init; }
    /// <summary>A sound clip (#656): <see cref="Lane"/> is then an audio lane and <see cref="Track"/> an <see cref="AudioAsset"/>.</summary>
    public bool Audio { get; init; }
    public int Lane { get; init; }
    public int Track { get; init; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Start { get; set; }
    public double Length => Out - In;
    public double End => Start + Length;
    public bool Covers(double t) => t >= Start && t <= End;
    /// <summary>The track's local time at timeline time <paramref name="t"/>.</summary>
    public double Local(double t) => In + Math.Clamp(t - Start, 0, Length);
}

/// <summary>
/// A sound a movie uses (#656): music imported from disk or the game's own sound from the replay
/// buffer, as a file in <c>user://movies/audio/</c>, with what was worked out from it once.
/// </summary>
public sealed class AudioAsset
{
    public required string Name { get; set; }
    /// <summary>The file's name inside the movies' audio folder.</summary>
    public required string File { get; init; }
    public double Duration { get; init; }
    public BeatGrid Beat { get; set; } = BeatGrid.None;
    /// <summary>The waveform: the loudest sample every 1/<see cref="PeaksPerSecond"/> s, 0..255.</summary>
    public byte[] Peaks { get => _peaks; set { _peaks = value; _peakMax = null; } }
    private byte[] _peaks = Array.Empty<byte>();
    public const int PeaksPerSecond = 50;
    /// <summary>The loudest peak (at least 1): a waveform is drawn against it, so a quiet recording still shows its shape.</summary>
    public int PeakMax => _peakMax ??= Peaks.Length == 0 ? 1 : Math.Max(1, (int)Peaks.Max());
    private int? _peakMax;
    /// <summary>Recorded from the game, not music: no beat is looked for in it.</summary>
    public bool Game { get; init; }
}

/// <summary>
/// A movie in the making (#638): recorded tracks, the lanes they play on and the clips cut from
/// them. Pure data and edits, no engine: the studio draws and plays it, <see cref="MovieFile"/> saves it.
/// </summary>
public sealed class MovieProject
{
    /// <summary>Shortest clip a trim or split leaves: below a few frames it cannot be grabbed again.</summary>
    public const double MinLength = 0.2;

    public string Name { get; set; } = "Untitled";
    /// <summary>The change-only properties' names, in the order every track stores them.</summary>
    public string[] Discrete { get; }
    public List<Lane> Lanes { get; } = new();
    public List<ActorTrack> Tracks { get; } = new();
    public List<Clip> Clips { get; } = new();
    /// <summary>The sound lanes' names (#656), drawn under the actors' lanes.</summary>
    public List<string> AudioLanes { get; } = new();
    public List<AudioAsset> Audio { get; } = new();
    /// <summary>Markers kept on the timeline (#656), sorted, in timeline seconds.</summary>
    public List<double> Markers { get; } = new();
    private int _nextId = 1;

    // this run's recording clock against the timeline: grabs made in one session keep their real
    // spacing; the first grab of a session (or after loading) goes after everything else
    private double? _anchorAbs, _anchorTimeline;

    public MovieProject(string[] discrete) => Discrete = discrete;

    public double Duration => Clips.Count == 0 ? 0 : Clips.Max(c => c.End);

    public Clip? Find(int id) => Clips.Find(c => c.Id == id);

    /// <summary>The timeline time a grab recorded at absolute time <paramref name="abs"/> goes to.</summary>
    public double PlaceGrab(double abs)
    {
        if (_anchorAbs is not { } a || abs < a)
        {
            _anchorAbs = abs;
            _anchorTimeline = Clips.Count == 0 ? 0 : Duration + 1;
            return _anchorTimeline.Value;
        }
        return _anchorTimeline!.Value + (abs - a);
    }

    /// <summary>Forgets this session's recording clock: after loading, or when the clock restarts.</summary>
    public void NewSession() { _anchorAbs = null; _anchorTimeline = null; }

    /// <summary>The lane of player <paramref name="key"/>, made on first use.</summary>
    /// <summary>The audio lane named <paramref name="name"/>, made on first use.</summary>
    public int AudioLaneFor(string name)
    {
        int i = AudioLanes.IndexOf(name);
        if (i >= 0) return i;
        AudioLanes.Add(name);
        return AudioLanes.Count - 1;
    }

    /// <summary>A whole sound on audio lane <paramref name="lane"/>, from timeline time <paramref name="start"/>.</summary>
    public Clip AddAudio(int lane, AudioAsset asset, double start)
    {
        Audio.Add(asset);
        var clip = new Clip { Id = _nextId++, Audio = true, Lane = lane, Track = Audio.Count - 1, In = 0, Out = asset.Duration, Start = Math.Max(0, start) };
        Clips.Add(clip);
        return clip;
    }

    /// <summary>How long the recording or sound under clip <paramref name="c"/> is.</summary>
    public double SourceLength(Clip c) => c.Audio ? Audio[c.Track].Duration : Tracks[c.Track].Duration;

    public int LaneFor(string key, string label, long peerId)
    {
        int i = Lanes.FindIndex(l => l.Key == key);
        if (i >= 0) return i;
        Lanes.Add(new Lane { Key = key, Label = label, PeerId = peerId });
        return Lanes.Count - 1;
    }

    /// <summary>A whole track on lane <paramref name="lane"/>, starting at timeline time <paramref name="start"/>.</summary>
    public Clip AddTrack(int lane, ActorTrack track, double start)
    {
        Tracks.Add(track);
        return AddClip(lane, Tracks.Count - 1, 0, track.Duration, start);
    }

    public Clip AddClip(int lane, int track, double inT, double outT, double start)
    {
        var clip = new Clip { Id = _nextId++, Lane = lane, Track = track, In = inT, Out = outT, Start = Math.Max(0, start) };
        Clips.Add(clip);
        return clip;
    }

    private Clip CopyOf(Clip c, double inT, double outT, double start)
    {
        var clip = new Clip { Id = _nextId++, Audio = c.Audio, Lane = c.Lane, Track = c.Track, In = inT, Out = outT, Start = Math.Max(0, start) };
        Clips.Add(clip);
        return clip;
    }

    /// <summary>
    /// The clip lane <paramref name="lane"/> plays at <paramref name="t"/>: of overlapping clips the
    /// one that starts last, so a clip dropped onto another takes over from where it begins.
    /// </summary>
    public Clip? ActiveClip(int lane, double t)
    {
        Clip? best = null;
        foreach (var c in Clips)
            if (!c.Audio && c.Lane == lane && c.Covers(t) && (best == null || c.Start >= best.Start)) best = c;
        return best;
    }

    /// <summary>Cuts clip <paramref name="id"/> in two at timeline time <paramref name="t"/>; the new right half, or null.</summary>
    public Clip? Split(int id, double t)
    {
        if (Find(id) is not { } c || t - c.Start < MinLength || c.End - t < MinLength) return null;
        double cut = c.Local(t);
        var right = CopyOf(c, cut, c.Out, t);
        c.Out = cut;
        return right;
    }

    /// <summary>Moves clip <paramref name="id"/>'s left edge to timeline time <paramref name="t"/>, within what was recorded.</summary>
    public void TrimStart(int id, double t)
    {
        if (Find(id) is not { } c) return;
        double shift = Math.Clamp(t - c.Start, -c.In, c.Length - MinLength);
        if (c.Start + shift < 0) shift = -c.Start;
        c.In += shift;
        c.Start += shift;
    }

    /// <summary>Moves clip <paramref name="id"/>'s right edge to timeline time <paramref name="t"/>, within what was recorded.</summary>
    public void TrimEnd(int id, double t)
    {
        if (Find(id) is not { } c) return;
        double max = SourceLength(c);
        c.Out = Math.Clamp(c.In + (t - c.Start), c.In + MinLength, max);
    }

    public void Move(int id, double start)
    {
        if (Find(id) is { } c) c.Start = Math.Max(0, start);
    }

    public bool Delete(int id) => Clips.RemoveAll(c => c.Id == id) > 0;

    /// <summary>A copy of clip <paramref name="id"/> right after it, on the same lane.</summary>
    public Clip? Duplicate(int id) =>
        Find(id) is { } c ? CopyOf(c, c.In, c.Out, c.End) : null;

    /// <summary>
    /// Drops the tracks no clip plays any more and the lanes with no clip, renumbering the rest.
    /// Run before saving, so a file only holds what the movie uses.
    /// </summary>
    public void Compact()
    {
        List<int> Used(bool audio, Func<Clip, int> of) =>
            Clips.Where(c => c.Audio == audio).Select(of).Distinct().OrderBy(i => i).ToList();
        var usedTracks = Used(false, c => c.Track);
        var usedLanes = Used(false, c => c.Lane);
        var usedAudio = Used(true, c => c.Track);
        var usedAudioLanes = Used(true, c => c.Lane);
        var tracks = usedTracks.Select(i => Tracks[i]).ToList();
        var lanes = usedLanes.Select(i => Lanes[i]).ToList();
        var audio = usedAudio.Select(i => Audio[i]).ToList();
        var audioLanes = usedAudioLanes.Select(i => AudioLanes[i]).ToList();
        var clips = Clips.Select(c => new Clip
        {
            Id = c.Id, Audio = c.Audio,
            Lane = c.Audio ? usedAudioLanes.IndexOf(c.Lane) : usedLanes.IndexOf(c.Lane),
            Track = c.Audio ? usedAudio.IndexOf(c.Track) : usedTracks.IndexOf(c.Track),
            In = c.In, Out = c.Out, Start = c.Start,
        }).ToList();
        Tracks.Clear(); Tracks.AddRange(tracks);
        Lanes.Clear(); Lanes.AddRange(lanes);
        Audio.Clear(); Audio.AddRange(audio);
        AudioLanes.Clear(); AudioLanes.AddRange(audioLanes);
        Clips.Clear(); Clips.AddRange(clips);
    }

    internal void SetNextId(int next) => _nextId = Math.Max(_nextId, next);

    // ---- beats and markers (#656) ------------------------------------------------------------------

    /// <summary>
    /// Every beat the sound clips put on the timeline (the ghost markers), sorted: a clip's beats
    /// inside its in..out, where it plays them; every fourth is a downbeat.
    /// </summary>
    public List<(double T, bool Downbeat)> Beats()
    {
        var beats = new List<(double T, bool Downbeat)>();
        foreach (var c in Clips)
        {
            if (!c.Audio) continue;
            var grid = Audio[c.Track].Beat.Beats;
            for (int i = 0; i < grid.Length; i++)
                if (grid[i] >= c.In && grid[i] <= c.Out) beats.Add((c.Start + grid[i] - c.In, i % 4 == 0));
        }
        beats.Sort((a, b) => a.T.CompareTo(b.T));
        return beats;
    }

    /// <summary>Keeps a marker at <paramref name="t"/>; false if one is already within a millisecond.</summary>
    public bool AddMarker(double t)
    {
        t = Math.Max(0, t);
        int i = Markers.BinarySearch(t);
        if (i < 0) i = ~i;
        if ((i < Markers.Count && Markers[i] - t < 1e-3) || (i > 0 && t - Markers[i - 1] < 1e-3)) return false;
        Markers.Insert(i, t);
        return true;
    }

    /// <summary>Drops the marker nearest <paramref name="t"/> within <paramref name="tolerance"/> s; whether one went.</summary>
    public bool RemoveMarker(double t, double tolerance)
    {
        if (Nearest(Markers, t, tolerance) is not { } m) return false;
        Markers.Remove(m);
        return true;
    }

    /// <summary>The ghost beat nearest <paramref name="t"/> within <paramref name="tolerance"/> s, or null.</summary>
    public double? NearestBeat(double t, double tolerance) => Nearest(Beats().Select(b => b.T), t, tolerance);

    /// <summary>
    /// Where a dragged edge at <paramref name="t"/> lands: the nearest marker, beat or other clip's
    /// edge within <paramref name="tolerance"/> s, else <paramref name="t"/> itself. Clip
    /// <paramref name="exclude"/>'s own edges do not count.
    /// </summary>
    public double Snap(double t, double tolerance, int exclude = 0)
    {
        var edges = Clips.Where(c => c.Id != exclude).SelectMany(c => new[] { c.Start, c.End });
        return Nearest(Markers.Concat(Beats().Select(b => b.T)).Concat(edges), t, tolerance) ?? t;
    }

    private static double? Nearest(IEnumerable<double> candidates, double t, double tolerance)
    {
        double? best = null;
        foreach (double c in candidates)
            if (Math.Abs(c - t) <= tolerance && (best == null || Math.Abs(c - t) < Math.Abs(best.Value - t))) best = c;
        return best;
    }
}
