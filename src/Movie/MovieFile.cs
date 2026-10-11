using System.Text;

namespace UnitSport.Movie;

/// <summary>
/// A <see cref="MovieProject"/> on disk (#638), <c>user://movies/*.usmovie</c>: little-endian
/// binary, doubles and floats as they are in memory, so nothing depends on the locale. The
/// change-only properties are stored by name: a file from a build with more or fewer of them
/// loads, the unknown ones dropped and the new ones at their default.
/// </summary>
public static class MovieFile
{
    private const uint Magic = 0x564D5355;   // "USMV"
    private const int Version = 1;

    public static void Write(MovieProject p, Stream to)
    {
        using var w = new BinaryWriter(to, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(p.Name);
        w.Write(Channels.Stride);
        w.Write(p.Discrete.Length);
        foreach (var name in p.Discrete) w.Write(name);

        w.Write(p.Lanes.Count);
        foreach (var l in p.Lanes) { w.Write(l.Key); w.Write(l.Label); w.Write(l.PeerId); }

        w.Write(p.Tracks.Count);
        foreach (var t in p.Tracks)
        {
            w.Write(t.Frames);
            foreach (double v in t.Times) w.Write(v);
            foreach (double v in t.Pos) w.Write(v);
            foreach (float v in t.F) w.Write(v);
            for (int i = 0; i < t.Discrete; i++) { w.Write(t.BaseNum[i]); w.Write(t.BaseStr[i]); }
            w.Write(t.Events.Length);
            foreach (var e in t.Events) { w.Write(e.T); w.Write(e.Prop); w.Write(e.Num); w.Write(e.Str); }
        }

        w.Write(p.Clips.Count);
        foreach (var c in p.Clips)
        {
            w.Write(c.Id); w.Write(c.Lane); w.Write(c.Track);
            w.Write(c.In); w.Write(c.Out); w.Write(c.Start);
        }
    }

    /// <summary>Reads a project saved by <see cref="Write"/>, its properties mapped onto <paramref name="discrete"/>.</summary>
    public static MovieProject Read(Stream from, string[] discrete)
    {
        using var r = new BinaryReader(from, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a movie file");
        int version = r.ReadInt32();
        if (version > Version) throw new InvalidDataException($"movie file version {version} is newer than this game ({Version})");
        string name = r.ReadString();
        int stride = r.ReadInt32();
        if (stride != Channels.Stride) throw new InvalidDataException($"frame layout {stride} floats, expected {Channels.Stride}");
        var fileNames = new string[r.ReadInt32()];
        for (int i = 0; i < fileNames.Length; i++) fileNames[i] = r.ReadString();
        // the file's property i is this build's map[i], or -1 when this build has no such property
        var map = fileNames.Select(n => Array.IndexOf(discrete, n)).ToArray();

        var p = new MovieProject(discrete) { Name = name };
        int lanes = r.ReadInt32();
        for (int i = 0; i < lanes; i++)
            p.Lanes.Add(new Lane { Key = r.ReadString(), Label = r.ReadString(), PeerId = r.ReadInt64() });

        int tracks = r.ReadInt32();
        for (int i = 0; i < tracks; i++)
        {
            int n = r.ReadInt32();
            var times = new double[n];
            for (int k = 0; k < n; k++) times[k] = r.ReadDouble();
            var pos = new double[n * 3];
            for (int k = 0; k < pos.Length; k++) pos[k] = r.ReadDouble();
            var f = new float[n * stride];
            for (int k = 0; k < f.Length; k++) f[k] = r.ReadSingle();
            var num = new long[discrete.Length];
            var str = new string[discrete.Length];
            Array.Fill(str, "");
            for (int k = 0; k < fileNames.Length; k++)
            {
                long v = r.ReadInt64();
                string s = r.ReadString();
                if (map[k] >= 0) { num[map[k]] = v; str[map[k]] = s; }
            }
            int events = r.ReadInt32();
            var list = new List<DiscreteEvent>(events);
            for (int k = 0; k < events; k++)
            {
                double t = r.ReadDouble();
                int prop = r.ReadInt32();
                long v = r.ReadInt64();
                string s = r.ReadString();
                if (prop >= 0 && prop < map.Length && map[prop] >= 0) list.Add(new DiscreteEvent(t, map[prop], v, s));
            }
            p.Tracks.Add(new ActorTrack(times, pos, f, num, str, list.ToArray()));
        }

        int clips = r.ReadInt32();
        int maxId = 0;
        for (int i = 0; i < clips; i++)
        {
            int id = r.ReadInt32(), lane = r.ReadInt32(), track = r.ReadInt32();
            double inT = r.ReadDouble(), outT = r.ReadDouble(), start = r.ReadDouble();
            if (lane < 0 || lane >= p.Lanes.Count || track < 0 || track >= p.Tracks.Count) continue;
            p.Clips.Add(new Clip { Id = id, Lane = lane, Track = track, In = inT, Out = outT, Start = start });
            maxId = Math.Max(maxId, id);
        }
        p.SetNextId(maxId + 1);
        return p;
    }
}
