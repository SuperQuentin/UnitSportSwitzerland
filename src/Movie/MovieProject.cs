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

    /// <summary>
    /// The clip lane <paramref name="lane"/> plays at <paramref name="t"/>: of overlapping clips the
    /// one that starts last, so a clip dropped onto another takes over from where it begins.
    /// </summary>
    public Clip? ActiveClip(int lane, double t)
    {
        Clip? best = null;
        foreach (var c in Clips)
            if (c.Lane == lane && c.Covers(t) && (best == null || c.Start >= best.Start)) best = c;
        return best;
    }

    /// <summary>Cuts clip <paramref name="id"/> in two at timeline time <paramref name="t"/>; the new right half, or null.</summary>
    public Clip? Split(int id, double t)
    {
        if (Find(id) is not { } c || t - c.Start < MinLength || c.End - t < MinLength) return null;
        double cut = c.Local(t);
        var right = AddClip(c.Lane, c.Track, cut, c.Out, t);
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
        double max = Tracks[c.Track].Duration;
        c.Out = Math.Clamp(c.In + (t - c.Start), c.In + MinLength, max);
    }

    public void Move(int id, double start)
    {
        if (Find(id) is { } c) c.Start = Math.Max(0, start);
    }

    public bool Delete(int id) => Clips.RemoveAll(c => c.Id == id) > 0;

    /// <summary>A copy of clip <paramref name="id"/> right after it, on the same lane.</summary>
    public Clip? Duplicate(int id) =>
        Find(id) is { } c ? AddClip(c.Lane, c.Track, c.In, c.Out, c.End) : null;

    /// <summary>
    /// Drops the tracks no clip plays any more and the lanes with no clip, renumbering the rest.
    /// Run before saving, so a file only holds what the movie uses.
    /// </summary>
    public void Compact()
    {
        var usedTracks = Clips.Select(c => c.Track).Distinct().OrderBy(i => i).ToList();
        var usedLanes = Clips.Select(c => c.Lane).Distinct().OrderBy(i => i).ToList();
        var tracks = usedTracks.Select(i => Tracks[i]).ToList();
        var lanes = usedLanes.Select(i => Lanes[i]).ToList();
        var clips = Clips.Select(c => new Clip
        {
            Id = c.Id, Lane = usedLanes.IndexOf(c.Lane), Track = usedTracks.IndexOf(c.Track),
            In = c.In, Out = c.Out, Start = c.Start,
        }).ToList();
        Tracks.Clear(); Tracks.AddRange(tracks);
        Lanes.Clear(); Lanes.AddRange(lanes);
        Clips.Clear(); Clips.AddRange(clips);
    }

    internal void SetNextId(int next) => _nextId = Math.Max(_nextId, next);
}
