using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UnitSport.Movie;

/// <summary>
/// A Record / Stop session on disk (#782), so it can run for hours in bounded memory. Every
/// <c>ReplayRecorder.SpoolSeconds</c> the session hands over a <see cref="Piece"/>, what it recorded
/// since the last one, and a worker thread writes it: the players and the world to a temp file, the
/// game's sound straight into a .wav in the movies' audio folder. At Stop, <see cref="Finish"/>
/// reads the temp file back and joins the pieces (a player's run into one track, the world into
/// one, by the recorder's ids) into the project a single grab would have made, then deletes it.
/// <para>
/// No engine: the looks the world's pieces name are indices into the recorder's list, resolved at
/// <see cref="Finish"/> from a copy taken on the main thread.
/// </para>
/// </summary>
public sealed class SessionSpool
{
    public readonly record struct ActorPiece(int Serial, string Key, string Label, long Peer, double Start, ActorTrack Track);

    /// <summary>What a session recorded since its last piece: sliced on the main thread, written on a worker.</summary>
    public sealed class Piece
    {
        public readonly List<ActorPiece> Actors = new();
        /// <summary>Sliced raw (<see cref="WorldTrack.Ids"/>), or null when nothing was recorded since.</summary>
        public WorldTrack? World;
        public double WorldStart;
        public short[]? Sound;
        public double SoundStart;
    }

    /// <summary>A player's piece starting this long after its previous one ended starts a clip of its own (it was out of view).</summary>
    public const double ActorGap = 1.0;
    /// <summary>
    /// The sound's next piece more than this off where its .wav has got to starts a .wav of its own:
    /// the studio was open, or a hitch overflowed the capture and samples were lost.
    /// </summary>
    public const double SoundSlack = 0.1;
    /// <summary>A .wav is closed and the next one begun after this long, so no file outgrows what loads comfortably.</summary>
    public const double MaxWavSeconds = 3600;
    /// <summary>The beat of the game's sound (a radio, a car stereo) is looked for in a .wav up to this long (#669).</summary>
    public const double BeatMaxSeconds = 600;

    private const byte ActorTag = 1, WorldTag = 2;

    private readonly string _temp, _audio;
    private readonly int _rate;
    private readonly FileStream _file;
    private readonly BinaryWriter _w;
    private Task _work = Task.CompletedTask;
    private string? _error;

    // the .wav being written
    private FileStream? _wav;
    private string _wavFile = "";
    private double _wavStart;
    private long _wavSamples;
    private readonly List<byte> _peaks = new();
    private float _peak;
    private int _peakFill;
    private readonly List<AudioAsset> _sounds = new();
    private readonly List<double> _soundStarts = new();

    /// <summary>A spool writing to <paramref name="temp"/> (a file path) and its .wav files into <paramref name="audioFolder"/>.</summary>
    public SessionSpool(string temp, string audioFolder, int rate)
    {
        _temp = temp;
        _audio = audioFolder;
        _rate = rate;
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        _file = File.Create(temp);
        _w = new BinaryWriter(_file);
    }

    /// <summary>Pieces handed over so far, and bytes in the temp file once they are written (for the logs).</summary>
    public int Pieces { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>Queues <paramref name="p"/> to be written; returns at once.</summary>
    public void Add(Piece p)
    {
        Pieces++;
        _work = _work.ContinueWith(_ => Write(p), TaskScheduler.Default);
    }

    /// <summary>
    /// Once everything queued is written: the session as a project named <paramref name="name"/>, or
    /// null when nothing was recorded. Faults with the first write error, if there was one.
    /// <paramref name="looks"/> is a copy of the recorder's looks the world's pieces index.
    /// </summary>
    public Task<MovieProject?> Finish(string name, string[] discrete, IReadOnlyList<WorldLook> looks)
    {
        var done = _work.ContinueWith(_ => Join(name, discrete, looks), TaskScheduler.Default);
        _work = done;
        return done;
    }

    // ---- the worker ------------------------------------------------------------------------------

    private void Write(Piece p)
    {
        if (_error != null) return;
        try
        {
            foreach (var a in p.Actors)
            {
                _w.Write(ActorTag);
                _w.Write(a.Serial); _w.Write(a.Key); _w.Write(a.Label); _w.Write(a.Peer); _w.Write(a.Start);
                MovieFile.WriteTrack(_w, a.Track);
            }
            if (p.World is { Ids: { } ids } world)
            {
                _w.Write(WorldTag);
                _w.Write(p.WorldStart); _w.Write(world.Duration);
                _w.Write(ids.Length);
                foreach (int id in ids) _w.Write(id);
                MovieFile.WriteEntities(_w, world.Entities);
            }
            _w.Flush();
            Bytes = _file.Length;
            if (p.Sound is { Length: > 0 } sound) Sound(sound, p.SoundStart);
        }
        catch (Exception e)
        {
            _error = e.Message;
        }
    }

    private void Sound(short[] samples, double start)
    {
        double at = _wavStart + (double)_wavSamples / _rate;
        if (_wav != null && (Math.Abs(start - at) > SoundSlack || _wavSamples >= MaxWavSeconds * _rate)) CloseWav();
        if (_wav == null)
        {
            Directory.CreateDirectory(_audio);
            _wavFile = $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.wav";
            _wav = File.Create(Path.Combine(_audio, _wavFile));
            Pcm.WriteWavHeader(_wav, 0, _rate);   // its sizes written again when it closes
            _wavStart = start;
            _wavSamples = 0;
            _peaks.Clear();
            _peak = 0;
            _peakFill = 0;
        }
        // a .wav is little-endian, like every platform the game runs on
        _wav.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        _wavSamples += samples.Length;
        // the waveform the way Pcm.Peaks draws it, carried across pieces
        int step = Math.Max(1, _rate / AudioAsset.PeaksPerSecond);
        foreach (short v in samples)
        {
            _peak = Math.Max(_peak, Math.Abs(v / 32768f));
            if (++_peakFill < step) continue;
            _peaks.Add((byte)Math.Clamp(_peak * 255, 0, 255));
            _peak = 0;
            _peakFill = 0;
        }
    }

    private void CloseWav()
    {
        if (_wav == null) return;
        if (_peakFill > 0) _peaks.Add((byte)Math.Clamp(_peak * 255, 0, 255));
        _wav.Position = 0;
        Pcm.WriteWavHeader(_wav, _wavSamples, _rate);
        _wav.Dispose();
        _wav = null;
        string path = Path.Combine(_audio, _wavFile);
        // under half a second is no clip, as on a grab
        if (_wavSamples < _rate / 2) { File.Delete(path); return; }
        double duration = (double)_wavSamples / _rate;
        var asset = new AudioAsset { Name = "Game sound", File = _wavFile, Duration = duration, Game = true, Peaks = _peaks.ToArray() };
        if (duration <= BeatMaxSeconds) asset.Beat = BeatOf(path);
        _sounds.Add(asset);
        _soundStarts.Add(_wavStart);
    }

    /// <summary>The beat of a .wav this wrote, when it is rhythmic (an engine's drone or wind has a "tempo" too).</summary>
    private BeatGrid BeatOf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(44));
        var mono = new float[samples.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = samples[i] / 32768f;
        var low = Pcm.Decimate(mono, _rate, 11025, out int lowRate);
        var beat = BeatDetector.Detect(low, lowRate);
        return beat.Confidence >= BeatDetector.Rhythmic ? beat : BeatGrid.None;
    }

    // ---- the join --------------------------------------------------------------------------------

    private MovieProject? Join(string name, string[] discrete, IReadOnlyList<WorldLook> looks)
    {
        try
        {
            if (_error == null)
                try { CloseWav(); }
                catch (Exception e) { _error = e.Message; }
            _w.Dispose();
            if (_error != null) throw new IOException(_error);
            using var file = File.OpenRead(_temp);
            using var r = new BinaryReader(file);
            return Joined(r, file.Length, name, discrete, looks);
        }
        finally
        {
            _w.Dispose();
            _wav?.Dispose();
            File.Delete(_temp);
        }
    }

    private MovieProject? Joined(BinaryReader r, long length, string name, string[] discrete, IReadOnlyList<WorldLook> looks)
    {
        var identity = Enumerable.Range(0, discrete.Length).ToArray();
        var open = new Dictionary<int, ActorRun>();
        var runs = new List<ActorRun>();
        WorldJoin? world = null;
        while (r.BaseStream.Position < length)
        {
            byte tag = r.ReadByte();
            if (tag == ActorTag)
            {
                int serial = r.ReadInt32();
                string key = r.ReadString(), label = r.ReadString();
                long peer = r.ReadInt64();
                double start = r.ReadDouble();
                var track = MovieFile.ReadTrack(r, identity, discrete.Length);
                if (open.TryGetValue(serial, out var run) && start - run.End <= ActorGap) run.Add(track, start);
                else
                {
                    open[serial] = run = new ActorRun(key, label, peer, start, track);
                    runs.Add(run);
                }
            }
            else if (tag == WorldTag)
            {
                double start = r.ReadDouble(), duration = r.ReadDouble();
                var ids = new int[r.ReadInt32()];
                for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadInt32();
                var entities = MovieFile.ReadEntities(r, int.MaxValue);
                (world ??= new WorldJoin(start)).Add(start, duration, ids, entities);
            }
            else throw new InvalidDataException($"session spool: unknown piece {tag}");
        }

        if (runs.Count == 0 && world == null && _sounds.Count == 0) return null;
        var p = new MovieProject(discrete) { Name = name };
        // the earliest of everything anchors the session on the timeline, the rest keep their spacing from it
        double anchor = double.PositiveInfinity;
        foreach (var run in runs) anchor = Math.Min(anchor, run.Start);
        if (world != null) anchor = Math.Min(anchor, world.First);
        foreach (double s in _soundStarts) anchor = Math.Min(anchor, s);
        p.PlaceGrab(anchor);
        foreach (var run in runs.OrderBy(x => x.Start))
            p.AddTrack(p.LaneFor(run.Key, run.Label, run.Peer), run.Build(), p.PlaceGrab(run.Start));
        if (world != null) p.AddWorld(world.Build(looks), p.PlaceGrab(world.First));
        for (int i = 0; i < _sounds.Count; i++)
            p.AddAudio(p.AudioLaneFor(MovieProject.GameSoundLane), _sounds[i], p.PlaceGrab(_soundStarts[i]));
        return p;
    }

    /// <summary>One player's pieces that follow on from each other, joined into one track.</summary>
    private sealed class ActorRun
    {
        public readonly string Key, Label;
        public readonly long Peer;
        public readonly double Start;
        private readonly List<(ActorTrack Track, double Offset)> _pieces = new();

        public ActorRun(string key, string label, long peer, double start, ActorTrack first)
        {
            Key = key; Label = label; Peer = peer; Start = start;
            _pieces.Add((first, 0));
        }

        /// <summary>The absolute time of its last frame.</summary>
        public double End => Start + _pieces[^1].Offset + _pieces[^1].Track.Duration;

        public void Add(ActorTrack track, double start) => _pieces.Add((track, start - Start));

        public ActorTrack Build()
        {
            if (_pieces.Count == 1) return _pieces[0].Track;
            int frames = _pieces.Sum(x => x.Track.Frames);
            var times = new double[frames];
            var pos = new double[frames * 3];
            var f = new float[frames * Channels.Stride];
            var first = _pieces[0].Track;
            var num = (long[])first.BaseNum.Clone();
            var str = (string[])first.BaseStr.Clone();
            // the values as the join goes, so a piece's starting values that differ become events
            var curNum = (long[])num.Clone();
            var curStr = (string[])str.Clone();
            var events = new List<DiscreteEvent>();
            int at = 0;
            foreach (var (t, o) in _pieces)
            {
                for (int k = 0; k < t.Frames; k++) times[at + k] = t.Times[k] + o;
                Array.Copy(t.Pos, 0, pos, at * 3, t.Frames * 3);
                Array.Copy(t.F, 0, f, at * Channels.Stride, t.Frames * Channels.Stride);
                at += t.Frames;
                for (int p = 0; p < curNum.Length; p++)
                {
                    if (t.BaseNum[p] == curNum[p] && t.BaseStr[p] == curStr[p]) continue;
                    curNum[p] = t.BaseNum[p];
                    curStr[p] = t.BaseStr[p];
                    events.Add(new DiscreteEvent(o, p, curNum[p], curStr[p]));
                }
                foreach (var e in t.Events)
                {
                    curNum[e.Prop] = e.Num;
                    curStr[e.Prop] = e.Str;
                    events.Add(e with { T = e.T + o });
                }
            }
            return new ActorTrack(times, pos, f, num, str, events.ToArray());
        }
    }

    /// <summary>
    /// The world's pieces joined by the recorder's ids: an object alive across a piece's start goes
    /// on (its starting state there is what it already was, so it is skipped), anything else is new.
    /// </summary>
    private sealed class WorldJoin
    {
        private sealed class Ent
        {
            public int Parent;   // a recorder id, or negative as in WorldEntity.Parent
            public double Born, Died = double.PositiveInfinity;
            public readonly List<double> Times = new(), ShowTimes = new(), LookTimes = new();
            public readonly List<WorldPose> Poses = new();
            public readonly List<bool> Shown = new();
            public readonly List<int> Looks = new();
        }

        public readonly double First;
        private double _end;
        private readonly Dictionary<int, Ent> _byId = new();

        public WorldJoin(double first) => First = first;

        public void Add(double start, double duration, int[] ids, WorldEntity[] entities)
        {
            double o = start - First;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                // alive at the piece's start, and already known alive: it goes on
                int skip = 1;
                if (e.Born != 0 || !_byId.TryGetValue(ids[i], out var b) || b.Died < double.PositiveInfinity)
                {
                    skip = 0;
                    b = new Ent { Parent = e.Parent >= 0 ? ids[e.Parent] : e.Parent, Born = o + e.Born };
                    _byId[ids[i]] = b;
                }
                for (int k = skip; k < e.Times.Length; k++) { b.Times.Add(o + e.Times[k]); b.Poses.Add(e.Poses[k]); }
                for (int k = skip; k < e.ShowTimes.Length; k++) { b.ShowTimes.Add(o + e.ShowTimes[k]); b.Shown.Add(e.Shown[k]); }
                for (int k = skip; k < e.LookTimes.Length; k++) { b.LookTimes.Add(o + e.LookTimes[k]); b.Looks.Add(e.Looks[k]); }
                if (e.Died < double.PositiveInfinity) b.Died = o + e.Died;
            }
            _end = Math.Max(_end, o + duration);
        }

        public WorldTrack Build(IReadOnlyList<WorldLook> looks)
        {
            var ids = _byId.Keys.OrderBy(id => id).ToList();
            var index = new Dictionary<int, int>(ids.Count);
            for (int i = 0; i < ids.Count; i++) index[ids[i]] = i;
            // looks renumbered to the ones used, as a grab's slice does
            var lookIndex = new Dictionary<int, int>();
            var used = new List<WorldLook>();
            int Remap(int look)
            {
                if (look < 0 || look >= looks.Count) return -1;
                if (!lookIndex.TryGetValue(look, out int i)) { i = used.Count; lookIndex[look] = i; used.Add(looks[look]); }
                return i;
            }
            var entities = new WorldEntity[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                var b = _byId[ids[i]];
                entities[i] = new WorldEntity
                {
                    Parent = b.Parent < 0 ? b.Parent : index.TryGetValue(b.Parent, out int p) ? p : -1,
                    Born = b.Born,
                    Died = b.Died,
                    Times = b.Times.ToArray(),
                    Poses = b.Poses.ToArray(),
                    ShowTimes = b.ShowTimes.ToArray(),
                    Shown = b.Shown.ToArray(),
                    LookTimes = b.LookTimes.ToArray(),
                    Looks = b.Looks.Select(Remap).ToArray(),
                };
            }
            return new WorldTrack(entities, used.ToArray(), _end);
        }
    }
}
