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
    /// <summary>
    /// A clip of the recorded world (#775): on the one World row, <see cref="Track"/> a
    /// <see cref="WorldTrack"/>; <see cref="Lane"/> is unused.
    /// </summary>
    public bool World { get; init; }
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
    /// <summary>Recorded from the game, not imported: drawn in its own colour, its beat found after the grab (#669).</summary>
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
    /// <summary>The recorded world's tracks (#775), played by the clips flagged <see cref="Clip.World"/>.</summary>
    public List<WorldTrack> Worlds { get; } = new();
    /// <summary>Whether the timeline has a World row.</summary>
    public bool HasWorld => Clips.Exists(c => c.World);
    public List<Clip> Clips { get; } = new();
    /// <summary>The sound lanes' names (#656), drawn under the actors' lanes.</summary>
    public List<string> AudioLanes { get; } = new();
    public List<AudioAsset> Audio { get; } = new();
    /// <summary>Markers kept on the timeline (#656), sorted, in timeline seconds.</summary>
    public List<double> Markers { get; } = new();
    /// <summary>The movie's cameras (#675), each its own keyframes (#669); never fewer than one.</summary>
    public List<CameraTrack> Cameras { get; } = new() { new CameraTrack { Name = "Cam 1" } };
    /// <summary>The first camera: the only one a version 3 file had.</summary>
    public CameraTrack Camera => Cameras[0];
    /// <summary>The named sequences (#776), each with its own shot track; never fewer than one.</summary>
    public List<Sequence> Sequences { get; } = new() { new Sequence() };
    /// <summary>The sequence being edited: the one <see cref="Cuts"/> and the Program row show.</summary>
    public int ActiveSequence { get => Math.Clamp(_activeSequence, 0, Sequences.Count - 1); set => _activeSequence = value; }
    private int _activeSequence;
    /// <summary>The program (#675) of the sequence being edited: which camera shows from when, sorted by time.</summary>
    public List<CameraCut> Cuts => Sequences[ActiveSequence].Cuts;
    private int _nextId = 1;

    // this run's recording clock against the timeline: grabs made in one session keep their real
    // spacing; the first grab of a session (or after loading) goes after everything else
    private double? _anchorAbs, _anchorTimeline;

    public MovieProject(string[] discrete) => Discrete = discrete;

    public double Duration
    {
        get
        {
            double d = 0;
            foreach (var cam in Cameras)
                if (cam.Keys.Count > 0) d = Math.Max(d, cam.Keys[^1].T);
            foreach (var seq in Sequences)
                foreach (var cut in seq.Cuts) d = Math.Max(d, cut.T);
            foreach (var c in Clips) d = Math.Max(d, c.End);
            return d;
        }
    }

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
    public double SourceLength(Clip c) =>
        c.Audio ? Audio[c.Track].Duration : c.World ? Worlds[c.Track].Duration : Tracks[c.Track].Duration;

    /// <summary>A whole recording of the world on the World row, from timeline time <paramref name="start"/> (#775).</summary>
    public Clip AddWorld(WorldTrack track, double start)
    {
        Worlds.Add(track);
        var clip = new Clip { Id = _nextId++, World = true, Track = Worlds.Count - 1, In = 0, Out = track.Duration, Start = Math.Max(0, start) };
        Clips.Add(clip);
        return clip;
    }

    /// <summary>The world clip playing at <paramref name="t"/>: the latest-starting one, as on an actor's lane.</summary>
    public Clip? ActiveWorld(double t)
    {
        Clip? best = null;
        foreach (var c in Clips)
            if (c.World && c.Covers(t) && (best == null || c.Start >= best.Start)) best = c;
        return best;
    }

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
        var clip = new Clip { Id = _nextId++, Audio = c.Audio, World = c.World, Lane = c.Lane, Track = c.Track, In = inT, Out = outT, Start = Math.Max(0, start) };
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
            if (!c.Audio && !c.World && c.Lane == lane && c.Covers(t) && (best == null || c.Start >= best.Start)) best = c;
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

    /// <summary>
    /// Cuts every clip under timeline time <paramref name="t"/>, on every lane, actors and sound
    /// alike (#669): a cut through the whole movie. The right halves made.
    /// </summary>
    public List<Clip> CutAll(double t)
    {
        var made = new List<Clip>();
        foreach (var c in Clips.Where(c => c.Covers(t)).ToList())
            if (Split(c.Id, t) is { } right) made.Add(right);
        return made;
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
        List<int> Used(Func<Clip, bool> kind, Func<Clip, int> of) =>
            Clips.Where(kind).Select(of).Distinct().OrderBy(i => i).ToList();
        static bool Actor(Clip c) => !c.Audio && !c.World;
        var usedTracks = Used(Actor, c => c.Track);
        var usedLanes = Used(Actor, c => c.Lane);
        var usedAudio = Used(c => c.Audio, c => c.Track);
        var usedAudioLanes = Used(c => c.Audio, c => c.Lane);
        var usedWorlds = Used(c => c.World, c => c.Track);
        var worlds = usedWorlds.Select(i => Worlds[i]).ToList();
        var tracks = usedTracks.Select(i => Tracks[i]).ToList();
        var lanes = usedLanes.Select(i => Lanes[i]).ToList();
        var audio = usedAudio.Select(i => Audio[i]).ToList();
        var audioLanes = usedAudioLanes.Select(i => AudioLanes[i]).ToList();
        var clips = Clips.Select(c => new Clip
        {
            Id = c.Id, Audio = c.Audio, World = c.World,
            Lane = c.Audio ? usedAudioLanes.IndexOf(c.Lane) : c.World ? 0 : usedLanes.IndexOf(c.Lane),
            Track = c.Audio ? usedAudio.IndexOf(c.Track) : c.World ? usedWorlds.IndexOf(c.Track) : usedTracks.IndexOf(c.Track),
            In = c.In, Out = c.Out, Start = c.Start,
        }).ToList();
        Tracks.Clear(); Tracks.AddRange(tracks);
        Worlds.Clear(); Worlds.AddRange(worlds);
        Lanes.Clear(); Lanes.AddRange(lanes);
        Audio.Clear(); Audio.AddRange(audio);
        AudioLanes.Clear(); AudioLanes.AddRange(audioLanes);
        Clips.Clear(); Clips.AddRange(clips);
        // a camera aimed at (or riding with) an actor whose lane went no longer aims at anyone
        foreach (var cam in Cameras)
        {
            foreach (var k in cam.Keys) k.LookAt = k.LookAt >= 0 ? usedLanes.IndexOf(k.LookAt) : -1;
            cam.Target = cam.Target >= 0 ? usedLanes.IndexOf(cam.Target) : -1;
        }
    }

    internal void SetNextId(int next) => _nextId = Math.Max(_nextId, next);

    // ---- cameras and the program (#675) --------------------------------------------------------------

    /// <summary>A new camera, "Cam N" with the first free N, after the others.</summary>
    public CameraTrack AddCamera()
    {
        int n = 1;
        while (Cameras.Exists(c => c.Name == $"Cam {n}")) n++;
        var cam = new CameraTrack { Name = $"Cam {n}" };
        Cameras.Add(cam);
        return cam;
    }

    /// <summary>Drops camera <paramref name="index"/> and its cuts; the last camera stays. Whether it went.</summary>
    public bool RemoveCamera(int index)
    {
        if (Cameras.Count <= 1 || index < 0 || index >= Cameras.Count) return false;
        Cameras.RemoveAt(index);
        foreach (var seq in Sequences)
        {
            seq.Cuts.RemoveAll(c => c.Camera == index);
            foreach (var c in seq.Cuts)
                if (c.Camera > index) c.Camera--;
        }
        return true;
    }

    /// <summary>
    /// The movie cuts to camera <paramref name="camera"/> at <paramref name="t"/>; a cut within
    /// <paramref name="within"/> seconds is changed instead of a second one made. The cut now there.
    /// </summary>
    public CameraCut CutTo(double t, int camera, double within)
    {
        t = Math.Max(0, t);
        var near = Cuts.Find(c => Math.Abs(c.T - t) <= within);
        if (near != null) { near.Camera = camera; return near; }
        var cut = new CameraCut { T = t, Camera = camera };
        int i = Cuts.FindIndex(c => c.T > t);
        Cuts.Insert(i < 0 ? Cuts.Count : i, cut);
        return cut;
    }

    public void RetimeCut(CameraCut cut, double t)
    {
        Cuts.Remove(cut);
        cut.T = Math.Max(0, t);
        int i = Cuts.FindIndex(c => c.T > cut.T);
        Cuts.Insert(i < 0 ? Cuts.Count : i, cut);
    }

    public bool RemoveCut(CameraCut cut) => Cuts.Remove(cut);

    /// <summary>The cut nearest <paramref name="t"/> within <paramref name="tolerance"/> s, or null.</summary>
    public CameraCut? CutNear(double t, double tolerance)
    {
        CameraCut? best = null;
        foreach (var c in Cuts)
            if (Math.Abs(c.T - t) <= tolerance && (best == null || Math.Abs(c.T - t) < Math.Abs(best.T - t))) best = c;
        return best;
    }

    /// <summary>The camera the movie shows at <paramref name="t"/>: the last cut's, the first camera before any. A loop: it runs every frame.</summary>
    public int ProgramCamera(double t)
    {
        int cam = 0;
        foreach (var c in Cuts)
        {
            if (c.T > t) break;
            cam = c.Camera;
        }
        return Math.Clamp(cam, 0, Cameras.Count - 1);
    }

    // ---- shots and the program with transitions (#776) ----------------------------------------------

    /// <summary>A new sequence, "Sequence N", over the whole movie. Its index.</summary>
    public int AddSequence()
    {
        int n = Sequences.Count + 1;
        while (Sequences.Exists(s => s.Name == $"Sequence {n}")) n++;
        Sequences.Add(new Sequence { Name = $"Sequence {n}" });
        return Sequences.Count - 1;
    }

    /// <summary>
    /// Actor lane <paramref name="lane"/>'s frame at timeline time <paramref name="t"/>: its feet in LV95 and
    /// the way it faces, from the clip playing there; false where none does. Pure, no engine.
    /// </summary>
    public bool ActorFrame(int lane, double t, out double e, out double n, out double alt, out float yaw)
    {
        if (ActiveClip(lane, t) is not { } clip) { e = n = alt = 0; yaw = 0; return false; }
        Tracks[clip.Track].Frame(clip.Local(t), out e, out n, out alt, out yaw);
        return true;
    }

    /// <summary>
    /// Shot <paramref name="index"/> at timeline time <paramref name="t"/>, everything applied: its keys (or its
    /// rig's default), the rig's frame on its target with the follow smoothing, the beat links (on the beats
    /// and markers of the movie) and the shake. <paramref name="mount"/> gives a mounted rig the full rotation
    /// of the target's vehicle (the engine knows it; null keeps the heading only). The fade a Fade link adds
    /// comes out in <paramref name="fade"/>. False when the shot has no keys and no target.
    /// </summary>
    public bool ShotPose(int index, double t, Func<int, Godot.Quaternion?>? mount, out CameraPose pose, out float fade)
    {
        fade = 0;
        var cam = Cameras[Math.Clamp(index, 0, Cameras.Count - 1)];
        bool keyed = cam.Sample(t, out pose);
        if (!keyed)
        {
            if (!cam.Relative) return false;
            pose = CameraTrack.DefaultPose(cam.Rig);
        }

        // the beat links: everything they add, summed by what they move
        float lens = 0, shake = 0, distance = 0, height = 0, roll = 0, orbit = 0;
        if (cam.Links.Count > 0)
        {
            var beats = CachedBeats();
            foreach (var link in cam.Links)
            {
                float v = ShotMath.Beat(link, t, beats, Markers);
                switch (link.Param)
                {
                    case BeatParam.Lens: lens += v; break;
                    case BeatParam.Shake: shake += v; break;
                    case BeatParam.Distance: distance += v; break;
                    case BeatParam.Height: height += v; break;
                    case BeatParam.Roll: roll += v; break;
                    case BeatParam.Orbit: orbit += v; break;
                    case BeatParam.Fade: fade += v; break;
                }
            }
        }

        if (cam.Relative)
        {
            if (!RigFrame(index, t, mount, out var fe, out var fn, out var fa, out var fy, out var frame)) return false;
            // orbiting turns the offset round the target; height lifts it
            pose.Alt += height;
            ShotMath.InFrame(ref pose, fe, fn, fa, fy + Godot.Mathf.DegToRad(orbit), frame);
        }
        else pose.Alt += height;

        var q = new Godot.Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw).Normalized();
        var (move, turn) = ShotMath.Shake(cam.Shake, cam.Seed, t, shake);
        // the shake and a dolly along the view in the camera's own frame (z back)
        var offset = q * (move + new Godot.Vector3(0, 0, distance));
        pose.E += offset.X; pose.N -= offset.Z; pose.Alt += offset.Y;
        q = (q * Godot.Quaternion.FromEuler(new Godot.Vector3(turn.X, turn.Y, turn.Z + Godot.Mathf.DegToRad(roll)))).Normalized();
        pose.Qx = q.X; pose.Qy = q.Y; pose.Qz = q.Z; pose.Qw = q.W;
        pose.Lens = Math.Clamp(pose.Lens + lens, CameraTrack.MinLens, CameraTrack.MaxLens);
        fade = Math.Clamp(fade, 0, 1);
        return true;
    }

    /// <summary>
    /// The frame a following shot <paramref name="index"/> hangs in at <paramref name="t"/>: the target's part,
    /// smoothed by the shot's follow, facing its yaw, or the vehicle's full rotation in <paramref name="frame"/>
    /// for a mount. False for a World shot, or where the target is absent.
    /// </summary>
    public bool RigFrame(int index, double t, Func<int, Godot.Quaternion?>? mount,
        out double e, out double n, out double alt, out float yaw, out Godot.Quaternion? frame)
    {
        e = n = alt = 0; yaw = 0; frame = null;
        var cam = Cameras[Math.Clamp(index, 0, Cameras.Count - 1)];
        if (!cam.Relative) return false;
        double smoothing = cam.Rig == ShotRig.Mount ? 0 : cam.Smoothing;
        // one sampler made once, pointed at the lane: no closure a frame
        _samplerLane = cam.Target;
        _sampler ??= (double s, out double se, out double sn, out double sa, out float sy) => ActorFrame(_samplerLane, s, out se, out sn, out sa, out sy);
        if (!ShotMath.Follow(t, smoothing, _sampler, out e, out n, out alt, out yaw)) return false;
        alt += ShotMath.PartHeight(cam.Part);
        if (cam.Rig == ShotRig.Mount) frame = mount?.Invoke(cam.Target);
        return true;
    }

    /// <summary>
    /// "Frame from view" (#777): shot <paramref name="index"/> keyed at <paramref name="t"/> to film from where
    /// the view is now (LV95 <paramref name="e"/>, <paramref name="n"/>, <paramref name="alt"/>, world rotation
    /// <paramref name="rotation"/>) with <paramref name="lens"/>. A World shot keeps the world pose; a following
    /// one keeps it in its target's frame, so the camera then moves with the actor. A shot with a single key
    /// (a placement) has that key replaced; one with a move gets a key at <paramref name="t"/>. The key, or null
    /// when a following shot's target is absent there.
    /// </summary>
    public CameraKey? FrameFromView(int index, double t, Func<int, Godot.Quaternion?>? mount,
        double e, double n, double alt, Godot.Quaternion rotation, float lens)
    {
        var cam = Cameras[Math.Clamp(index, 0, Cameras.Count - 1)];
        var key = new CameraKey { T = t, E = e, N = n, Alt = alt, Qx = rotation.X, Qy = rotation.Y, Qz = rotation.Z, Qw = rotation.W, Lens = lens };
        if (cam.Relative)
        {
            if (!RigFrame(index, t, mount, out var fe, out var fn, out var fa, out var fy, out var frame)) return null;
            var pose = new CameraPose { E = e, N = n, Alt = alt, Qx = rotation.X, Qy = rotation.Y, Qz = rotation.Z, Qw = rotation.W, Lens = lens };
            ShotMath.ToFrame(ref pose, fe, fn, fa, fy, frame);
            key.E = pose.E; key.N = pose.N; key.Alt = pose.Alt;
            key.Qx = pose.Qx; key.Qy = pose.Qy; key.Qz = pose.Qz; key.Qw = pose.Qw;
        }
        if (cam.Keys.Count <= 1)
        {
            if (cam.Keys.Count == 1) { key.T = cam.Keys[0].T; key.Ease = cam.Keys[0].Ease; key.LookAt = cam.Keys[0].LookAt; }
            cam.Keys.Clear();
            cam.Keys.Add(key);
            return key;
        }
        return cam.Set(key, 1.0 / Channels.Rate);
    }

    private ShotMath.TargetSampler? _sampler;
    private int _samplerLane;
    private List<(double T, bool Downbeat)>? _beats;
    private double _beatsShape = double.NaN;

    /// <summary>
    /// <see cref="Beats"/>, made again only when the sound clips change: a beat link reads it every frame.
    /// </summary>
    public List<(double T, bool Downbeat)> CachedBeats()
    {
        double shape = 0;
        foreach (var c in Clips)
            if (c.Audio) shape = shape * 1.000173 + c.Start * 3.1 + c.In * 1.7 + c.Out * 0.9 + c.Track * 0.37 + Audio[c.Track].Beat.Beats.Length;
        if (_beats == null || shape != _beatsShape) { _beats = Beats(); _beatsShape = shape; }
        return _beats;
    }

    /// <summary>What the movie shows at a time (#776): the camera, how far through a transition, the fade.</summary>
    public struct ProgramView
    {
        public CameraPose Pose;
        /// <summary>0 clear .. 1 black: a fade to black, or a Fade beat link.</summary>
        public float Fade;
        /// <summary>The shot being left and the shot coming, and how far between them (0..1), during a transition.</summary>
        public int From, To;
        public float Mix;
        public TransitionKind Kind;
        public bool Ok;
    }

    /// <summary>
    /// The active sequence's program at timeline time <paramref name="t"/>, transitions applied: a blend
    /// eases the camera from one shot into the next, a fade goes through black at the cut, a zoom punch
    /// pushes the lens in before it, a whip pan swings across fast. Crossfades and wipes need two pictures:
    /// the engine mixes them (<see cref="ProgramView.Mix"/>); here they cut at the middle.
    /// </summary>
    public ProgramView Program(double t, Func<int, Godot.Quaternion?>? mount)
    {
        var view = new ProgramView { From = ProgramCamera(t), To = -1, Kind = TransitionKind.Cut };
        var cuts = Cuts;
        // the transition whose span holds t: centred on its cut
        CameraCut? at = null;
        int before = 0;
        for (int i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            if (c.Transition != TransitionKind.Cut && Math.Abs(t - c.T) < c.Duration / 2) { at = c; before = i > 0 ? cuts[i - 1].Camera : 0; break; }
        }
        if (at == null)
        {
            view.Ok = ShotPose(view.From, t, mount, out view.Pose, out view.Fade);
            return view;
        }
        float u = (float)Math.Clamp((t - (at.T - at.Duration / 2)) / at.Duration, 0, 1);
        view.From = Math.Clamp(before, 0, Cameras.Count - 1);
        view.To = Math.Clamp(at.Camera, 0, Cameras.Count - 1);
        view.Mix = u;
        view.Kind = at.Transition;
        bool a = ShotPose(view.From, t, mount, out var pa, out float fa);
        bool b = ShotPose(view.To, t, mount, out var pb, out float fb);
        view.Ok = a || b;
        if (!a) pa = pb;
        if (!b) pb = pa;
        switch (at.Transition)
        {
            case TransitionKind.Blend:
            case TransitionKind.WhipPan:
            {
                // a whip pan is a blend that waits, then swings across in a blur of a moment
                float s = at.Transition == TransitionKind.WhipPan
                    ? (float)(0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp((u - 0.35) / 0.3, 0, 1)))
                    : u * u * (3 - 2 * u);
                view.Pose = CameraTrack.Lerp(pa, pb, s);
                view.Fade = fa + (fb - fa) * s;
                break;
            }
            case TransitionKind.FadeBlack:
                view.Pose = u < 0.5f ? pa : pb;
                view.Fade = Math.Max(u < 0.5f ? fa : fb, 1 - Math.Abs(2 * u - 1));
                break;
            case TransitionKind.ZoomPunch:
                view.Pose = u < 0.5f ? pa : pb;
                // the lens pushes in towards the cut and springs back out after it
                float punch = 1 - Math.Abs(2 * u - 1);
                view.Pose.Lens = Math.Clamp(view.Pose.Lens * (1 + 1.2f * punch * punch), CameraTrack.MinLens, CameraTrack.MaxLens);
                view.Fade = u < 0.5f ? fa : fb;
                break;
            default:
                view.Pose = u < 0.5f ? pa : pb;
                view.Fade = u < 0.5f ? fa : fb;
                break;
        }
        return view;
    }

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
