using System.Text;

namespace UnitSport.Movie;

/// <summary>
/// A <see cref="MovieProject"/> on disk (#638), <c>user://movies/*.usmovie</c>: little-endian
/// binary, doubles and floats as they are in memory, so nothing depends on the locale. The
/// change-only properties are stored by name: a file from a build with more or fewer of them
/// loads, the unknown ones dropped and the new ones at their default. Version 2 (#656) adds the
/// sound: audio lanes, the sounds (file name, beats, waveform), a flag per clip and the markers.
/// Version 1 files still load, silent. Version 3 (#669) adds the camera's keyframes.
/// </summary>
public static class MovieFile
{
    private const uint Magic = 0x564D5355;   // "USMV"
    private const int Version = 3;

    public static void Write(MovieProject p, Stream to) => Write(p, to, Version);

    /// <summary>Writes the given format version: older ones are for the checks that older files still load.</summary>
    internal static void Write(MovieProject p, Stream to, int version)
    {
        using var w = new BinaryWriter(to, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(version);
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

        if (version >= 2)
        {
            w.Write(p.AudioLanes.Count);
            foreach (var name in p.AudioLanes) w.Write(name);
            w.Write(p.Audio.Count);
            foreach (var a in p.Audio)
            {
                w.Write(a.Name); w.Write(a.File); w.Write(a.Duration); w.Write(a.Game);
                w.Write(a.Beat.Bpm);
                w.Write(a.Beat.Beats.Length);
                foreach (double b in a.Beat.Beats) w.Write(b);
                w.Write(a.Peaks.Length);
                w.Write(a.Peaks);
            }
        }

        var clips = version >= 2 ? p.Clips : p.Clips.Where(c => !c.Audio).ToList();
        w.Write(clips.Count);
        foreach (var c in clips)
        {
            w.Write(c.Id); w.Write(c.Lane); w.Write(c.Track);
            w.Write(c.In); w.Write(c.Out); w.Write(c.Start);
            if (version >= 2) w.Write(c.Audio);
        }

        if (version >= 2)
        {
            w.Write(p.Markers.Count);
            foreach (double m in p.Markers) w.Write(m);
        }

        if (version >= 3)
        {
            w.Write(p.Camera.Keys.Count);
            foreach (var k in p.Camera.Keys)
            {
                w.Write(k.T); w.Write(k.E); w.Write(k.N); w.Write(k.Alt);
                w.Write(k.Qx); w.Write(k.Qy); w.Write(k.Qz); w.Write(k.Qw);
                w.Write(k.Lens); w.Write((byte)k.Ease); w.Write(k.LookAt);
            }
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

        if (version >= 2)
        {
            int audioLanes = r.ReadInt32();
            for (int i = 0; i < audioLanes; i++) p.AudioLanes.Add(r.ReadString());
            int sounds = r.ReadInt32();
            for (int i = 0; i < sounds; i++)
            {
                string sound = r.ReadString(), file = r.ReadString();
                double duration = r.ReadDouble();
                bool game = r.ReadBoolean();
                double bpm = r.ReadDouble();
                var beats = new double[r.ReadInt32()];
                for (int k = 0; k < beats.Length; k++) beats[k] = r.ReadDouble();
                var peaks = r.ReadBytes(r.ReadInt32());
                p.Audio.Add(new AudioAsset { Name = sound, File = file, Duration = duration, Game = game, Beat = new BeatGrid(bpm, beats), Peaks = peaks });
            }
        }

        int clips = r.ReadInt32();
        int maxId = 0;
        for (int i = 0; i < clips; i++)
        {
            int id = r.ReadInt32(), lane = r.ReadInt32(), track = r.ReadInt32();
            double inT = r.ReadDouble(), outT = r.ReadDouble(), start = r.ReadDouble();
            bool audio = version >= 2 && r.ReadBoolean();
            bool valid = audio
                ? lane >= 0 && lane < p.AudioLanes.Count && track >= 0 && track < p.Audio.Count
                : lane >= 0 && lane < p.Lanes.Count && track >= 0 && track < p.Tracks.Count;
            if (!valid) continue;
            p.Clips.Add(new Clip { Id = id, Audio = audio, Lane = lane, Track = track, In = inT, Out = outT, Start = start });
            maxId = Math.Max(maxId, id);
        }
        p.SetNextId(maxId + 1);

        if (version >= 2)
        {
            int markers = r.ReadInt32();
            for (int i = 0; i < markers; i++) p.AddMarker(r.ReadDouble());
        }

        if (version >= 3)
        {
            int keys = r.ReadInt32();
            for (int i = 0; i < keys; i++)
            {
                var k = new CameraKey
                {
                    T = r.ReadDouble(), E = r.ReadDouble(), N = r.ReadDouble(), Alt = r.ReadDouble(),
                    Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(),
                    Lens = r.ReadSingle(), Ease = (KeyEase)Math.Min(r.ReadByte(), (byte)KeyEase.Cut), LookAt = r.ReadInt32(),
                };
                if (k.LookAt >= p.Lanes.Count) k.LookAt = -1;
                p.Camera.Set(k, -1, replaceOptions: true);
            }
        }
        return p;
    }
}
