using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// What one traced thing is (#844), written once to <c>entities.jsonl</c> when it is first seen:
/// a player or a watched world object. <see cref="Name"/> is the node's name, which networked nodes
/// share on every peer (deterministic names), so a server trace and a client trace match on it.
/// </summary>
public sealed record TraceEntity
{
    [JsonPropertyName("id")] public int Id { get; init; }
    /// <summary><c>player</c>, or the object's kind: a vehicle's ride kind, else its class (Traffic car, DroppedItem...).</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("cls")] public string Class { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    /// <summary>What the top view writes beside it.</summary>
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    /// <summary>Footprint in metres: across (X), up (Y), along (Z), from its meshes or shapes; 0 when it has none.</summary>
    [JsonPropertyName("size")] public float[] Size { get; init; } = [0, 0, 0];
    /// <summary>The peer whose node it is (players), 0 otherwise.</summary>
    [JsonPropertyName("peer")] public long Peer { get; init; }
    [JsonPropertyName("local")] public bool Local { get; init; }
    [JsonPropertyName("first")] public double First { get; init; }

    public bool IsPlayer => Kind == "player";
}

/// <summary>One thing at one instant, in LV95 metres. Heading is a compass bearing (0 north, π/2 east).</summary>
public struct TraceState
{
    public int Id;
    public double E, N;
    public float Alt, Heading, VE, VN, VUp, Spin;
    /// <summary>Players only (<see cref="HasPlayer"/>): ride kind, seat, held item, whose vehicle it rides in.</summary>
    public short Ride;
    public sbyte Seat;
    public int Held, RidingWith;
    public bool HasPlayer;

    public readonly float Speed => MathF.Sqrt(VE * VE + VN * VN + VUp * VUp);
}

/// <summary>Every traced thing at one instant: <see cref="T"/> seconds since the session started.</summary>
public sealed class TraceFrame
{
    public double T;
    public int Count;
    public TraceState[] States = new TraceState[16];

    public ref TraceState Add()
    {
        if (Count == States.Length) Array.Resize(ref States, States.Length * 2);
        States[Count] = default;
        return ref States[Count++];
    }

    public void Clear(double t)
    {
        T = t;
        Count = 0;
    }

    public void CopyFrom(TraceFrame f)
    {
        T = f.T;
        Count = 0;
        for (int i = 0; i < f.Count; i++) Add() = f.States[i];
    }

    /// <summary>The state of <paramref name="id"/> in this frame, if it is in it.</summary>
    public bool TryGet(int id, out TraceState s)
    {
        for (int i = 0; i < Count; i++)
            if (States[i].Id == id) { s = States[i]; return true; }
        s = default;
        return false;
    }
}

/// <summary>
/// The binary trace (#844, docs/notes/general/playtest-recorder.md). <c>trace.bin</c>: a header, then
/// frames. <c>trace.idx</c>: one little-endian Int64 per whole second, the offset of the first frame
/// at or after it, so a window is read without the 30 minutes before it. The header's
/// <see cref="TraceHeader.StartUnixMs"/> is the wall clock the session started at, the zero of every
/// frame's time: a server's trace and a client's on one machine line up on it.
/// </summary>
public sealed record TraceHeader(long StartUnixMs, string Role);

public static class TraceFormat
{
    public const string TraceFile = "trace.bin", IndexFile = "trace.idx", EntitiesFile = "entities.jsonl";
    private const uint Magic = 0x54505355;   // "USPT"
    private const int Version = 1;
    private const byte FrameTag = 1;
    private const byte PlayerBit = 1;

    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static void WriteHeader(BinaryWriter w, TraceHeader h)
    {
        w.Write(Magic);
        w.Write(Version);
        w.Write(h.StartUnixMs);
        w.Write(h.Role);
    }

    public static TraceHeader ReadHeader(BinaryReader r)
    {
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a playtest trace");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"trace version {version}, this reads {Version}");
        return new TraceHeader(r.ReadInt64(), r.ReadString());
    }

    public static void WriteFrame(BinaryWriter w, TraceFrame f)
    {
        w.Write(FrameTag);
        w.Write(f.T);
        w.Write(f.Count);
        for (int i = 0; i < f.Count; i++)
        {
            ref readonly var s = ref f.States[i];
            w.Write(s.Id);
            w.Write(s.HasPlayer ? PlayerBit : (byte)0);
            w.Write(s.E); w.Write(s.N); w.Write(s.Alt);
            w.Write(s.Heading); w.Write(s.VE); w.Write(s.VN); w.Write(s.VUp); w.Write(s.Spin);
            if (!s.HasPlayer) continue;
            w.Write(s.Ride); w.Write(s.Seat); w.Write(s.Held); w.Write(s.RidingWith);
        }
    }

    /// <summary>The next frame into <paramref name="f"/>; false at the end of the file (or a frame cut short by a crash).</summary>
    public static bool ReadFrame(BinaryReader r, TraceFrame f)
    {
        try
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return false;
            if (r.ReadByte() != FrameTag) return false;
            f.Clear(r.ReadDouble());
            int count = r.ReadInt32();
            if (count < 0 || count > 1_000_000) return false;
            for (int i = 0; i < count; i++)
            {
                ref var s = ref f.Add();
                s.Id = r.ReadInt32();
                s.HasPlayer = (r.ReadByte() & PlayerBit) != 0;
                s.E = r.ReadDouble(); s.N = r.ReadDouble(); s.Alt = r.ReadSingle();
                s.Heading = r.ReadSingle(); s.VE = r.ReadSingle(); s.VN = r.ReadSingle(); s.VUp = r.ReadSingle(); s.Spin = r.ReadSingle();
                if (!s.HasPlayer) continue;
                s.Ride = r.ReadInt16(); s.Seat = r.ReadSByte(); s.Held = r.ReadInt32(); s.RidingWith = r.ReadInt32();
            }
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }
}

/// <summary>
/// The trace files of one session, written on the recorder's worker thread (never the main one).
/// Opening a folder that already has a trace appends to it: a playtest that relaunches (another
/// fixture course, a rebuild) goes on in the same session.
/// </summary>
public sealed class TraceWriter : IDisposable
{
    private readonly FileStream _trace, _index;
    private readonly BinaryWriter _w, _iw;
    private readonly StreamWriter _entities;
    private long _indexed;   // seconds indexed so far

    public TraceHeader Header { get; }
    public long Bytes => _trace.Length;

    public TraceWriter(string dir, TraceHeader fresh)
    {
        Directory.CreateDirectory(dir);
        string tracePath = Path.Combine(dir, TraceFormat.TraceFile);
        bool append = File.Exists(tracePath) && new FileInfo(tracePath).Length > 0;
        if (append)
        {
            using (var r = new BinaryReader(File.OpenRead(tracePath))) Header = TraceFormat.ReadHeader(r);
        }
        else Header = fresh;
        _trace = new FileStream(tracePath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        _w = new BinaryWriter(_trace);
        if (!append) TraceFormat.WriteHeader(_w, Header);
        _index = new FileStream(Path.Combine(dir, TraceFormat.IndexFile), append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        _iw = new BinaryWriter(_index);
        _indexed = _index.Length / 8;
        _entities = new StreamWriter(new FileStream(Path.Combine(dir, TraceFormat.EntitiesFile), append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read));
    }

    public void Entity(TraceEntity e) => _entities.WriteLine(JsonSerializer.Serialize(e, TraceFormat.Json));

    public void Frame(TraceFrame f)
    {
        long second = (long)Math.Floor(Math.Max(0, f.T));
        _w.Flush();
        long at = _trace.Position;
        // every second up to this frame's starts here (a gap, a relaunch: the seconds without frames point at the next one)
        while (_indexed <= second) { _iw.Write(at); _indexed++; }
        TraceFormat.WriteFrame(_w, f);
    }

    public void Flush()
    {
        _w.Flush();
        _iw.Flush();
        _entities.Flush();
    }

    public void Dispose()
    {
        Flush();
        _w.Dispose();
        _iw.Dispose();
        _entities.Dispose();
    }
}

/// <summary>A session's trace read back: its header, its things, and any window of its frames.</summary>
public sealed class TraceReader
{
    public TraceHeader Header { get; }
    public Dictionary<int, TraceEntity> Entities { get; } = new();
    private readonly string _path;
    private readonly long[] _index;
    private readonly long _dataStart;

    public TraceReader(string dir)
    {
        _path = Path.Combine(dir, TraceFormat.TraceFile);
        using (var r = new BinaryReader(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            Header = TraceFormat.ReadHeader(r);
            _dataStart = r.BaseStream.Position;
        }
        string idx = Path.Combine(dir, TraceFormat.IndexFile);
        if (File.Exists(idx))
        {
            var bytes = ReadShared(idx);
            _index = new long[bytes.Length / 8];
            Buffer.BlockCopy(bytes, 0, _index, 0, _index.Length * 8);
        }
        else _index = [];
        string ents = Path.Combine(dir, TraceFormat.EntitiesFile);
        if (File.Exists(ents))
            foreach (string line in System.Text.Encoding.UTF8.GetString(ReadShared(ents)).Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<TraceEntity>(line, TraceFormat.Json) is { } e) Entities[e.Id] = e;
                }
                catch (JsonException) { }   // a line cut short by a crash
            }
    }

    /// <summary>Seconds the index covers: the session's length, give or take a second.</summary>
    public double Duration => _index.Length;

    private static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ms = new MemoryStream();
        f.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The frames from <paramref name="t0"/> to <paramref name="t1"/> (seconds), every <paramref name="every"/>-th one.</summary>
    public List<TraceFrame> Window(double t0, double t1, int every = 1)
    {
        var frames = new List<TraceFrame>();
        long second = (long)Math.Floor(Math.Max(0, t0));
        long start = second < _index.Length ? _index[second] : _index.Length > 0 ? _index[^1] : _dataStart;
        using var r = new BinaryReader(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        r.BaseStream.Position = Math.Max(start, _dataStart);
        var f = new TraceFrame();
        int k = 0;
        while (TraceFormat.ReadFrame(r, f))
        {
            if (f.T > t1) break;
            if (f.T < t0) continue;
            if (k++ % Math.Max(1, every) != 0) continue;
            var copy = new TraceFrame();
            copy.CopyFrom(f);
            frames.Add(copy);
        }
        return frames;
    }
}

/// <summary>The last minute of frames, in memory, for the live top view and the notes' pictures. Frames are reused, nothing allocated once full.</summary>
public sealed class TraceRing
{
    private readonly TraceFrame[] _frames;
    private int _start, _count;

    public TraceRing(int capacity)
    {
        _frames = new TraceFrame[capacity];
        for (int i = 0; i < capacity; i++) _frames[i] = new TraceFrame();
    }

    public int Count => _count;
    public TraceFrame this[int i] => _frames[(_start + i) % _frames.Length];
    public TraceFrame? Last => _count == 0 ? null : this[_count - 1];

    /// <summary>The slot for the next frame, cleared to time <paramref name="t"/>: the oldest one once full.</summary>
    public TraceFrame Next(double t)
    {
        TraceFrame f;
        if (_count < _frames.Length) f = _frames[(_start + _count++) % _frames.Length];
        else
        {
            f = _frames[_start];
            _start = (_start + 1) % _frames.Length;
        }
        f.Clear(t);
        return f;
    }

    /// <summary>Copies of the frames between <paramref name="t0"/> and <paramref name="t1"/>.</summary>
    public List<TraceFrame> Window(double t0, double t1)
    {
        var list = new List<TraceFrame>();
        for (int i = 0; i < _count; i++)
        {
            var f = this[i];
            if (f.T < t0 || f.T > t1) continue;
            var copy = new TraceFrame();
            copy.CopyFrom(f);
            list.Add(copy);
        }
        return list;
    }
}
