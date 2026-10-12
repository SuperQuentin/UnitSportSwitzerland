using System.Text;

namespace UnitSport.Movie;

/// <summary>
/// A <see cref="MovieProject"/> on disk (#638), <c>user://movies/*.usmovie</c>: little-endian
/// binary, doubles and floats as they are in memory, so nothing depends on the locale. The
/// change-only properties are stored by name: a file from a build with more or fewer of them
/// loads, the unknown ones dropped and the new ones at their default. Version 2 (#656) adds the
/// sound: audio lanes, the sounds (file name, beats, waveform), a flag per clip and the markers.
/// Version 1 files still load, silent. Version 3 (#669) adds the camera's keyframes, version 4
/// (#675) several named cameras and the cut list; a version 3 camera becomes Cam 1. Version 5
/// (#775) adds the recorded world: its tracks, before the clips, and a flag per clip. A look is
/// stored by the name of its asset file (<see cref="WorldLook.Asset"/>), which the engine side
/// writes and reads (<c>WorldLookStore</c>). Version 6 (#776) adds each camera's shot settings (rig,
/// target, smoothing, shake, beat links) and named sequences in place of the one cut list; an older
/// file's cuts become the sequence "Main".
/// </summary>
public static class MovieFile
{
    private const uint Magic = 0x564D5355;   // "USMV"
    private const int Version = 6;

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
        foreach (var t in p.Tracks) WriteTrack(w, t);

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

        if (version >= 5)
        {
            w.Write(p.Worlds.Count);
            foreach (var t in p.Worlds) WriteWorld(w, t);
        }

        var clips = p.Clips.Where(c => (version >= 2 || !c.Audio) && (version >= 5 || !c.World)).ToList();
        w.Write(clips.Count);
        foreach (var c in clips)
        {
            w.Write(c.Id); w.Write(c.Lane); w.Write(c.Track);
            w.Write(c.In); w.Write(c.Out); w.Write(c.Start);
            if (version >= 2) w.Write(c.Audio);
            if (version >= 5) w.Write(c.World);
        }

        if (version >= 2)
        {
            w.Write(p.Markers.Count);
            foreach (double m in p.Markers) w.Write(m);
        }

        if (version == 3) WriteKeys(w, p.Camera);
        if (version >= 4)
        {
            w.Write(p.Cameras.Count);
            foreach (var cam in p.Cameras)
            {
                w.Write(cam.Name);
                WriteKeys(w, cam);
                if (version >= 6) WriteShot(w, cam);
            }
            if (version >= 6)
            {
                w.Write(p.Sequences.Count);
                foreach (var seq in p.Sequences)
                {
                    w.Write(seq.Name); w.Write(seq.In); w.Write(seq.Out);
                    w.Write(seq.Cuts.Count);
                    foreach (var c in seq.Cuts) { w.Write(c.T); w.Write(c.Camera); w.Write((byte)c.Transition); w.Write(c.Duration); }
                }
                w.Write(p.ActiveSequence);
            }
            else
            {
                var cuts = p.Sequences[0].Cuts;
                w.Write(cuts.Count);
                foreach (var c in cuts) { w.Write(c.T); w.Write(c.Camera); }
            }
        }
    }

    /// <summary>One player's track (also a Record / Stop session's pieces on disk, #782).</summary>
    internal static void WriteTrack(BinaryWriter w, ActorTrack t)
    {
        w.Write(t.Frames);
        foreach (double v in t.Times) w.Write(v);
        foreach (double v in t.Pos) w.Write(v);
        foreach (float v in t.F) w.Write(v);
        for (int i = 0; i < t.Discrete; i++) { w.Write(t.BaseNum[i]); w.Write(t.BaseStr[i]); }
        w.Write(t.Events.Length);
        foreach (var e in t.Events) { w.Write(e.T); w.Write(e.Prop); w.Write(e.Num); w.Write(e.Str); }
    }

    /// <summary>A track written by <see cref="WriteTrack"/>, the file's property k mapped to <paramref name="map"/>[k] (-1: dropped).</summary>
    internal static ActorTrack ReadTrack(BinaryReader r, int[] map, int discrete)
    {
        int n = r.ReadInt32();
        var times = new double[n];
        for (int k = 0; k < n; k++) times[k] = r.ReadDouble();
        var pos = new double[n * 3];
        for (int k = 0; k < pos.Length; k++) pos[k] = r.ReadDouble();
        var f = new float[n * Channels.Stride];
        for (int k = 0; k < f.Length; k++) f[k] = r.ReadSingle();
        var num = new long[discrete];
        var str = new string[discrete];
        Array.Fill(str, "");
        for (int k = 0; k < map.Length; k++)
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
        return new ActorTrack(times, pos, f, num, str, list.ToArray());
    }

    private static void WriteShot(BinaryWriter w, CameraTrack cam)
    {
        w.Write(cam.Group); w.Write((byte)cam.Rig); w.Write(cam.Target); w.Write((byte)cam.Part);
        w.Write(cam.Smoothing); w.Write(cam.Seed);
        var s = cam.Shake;
        w.Write((byte)s.Style); w.Write(s.Strength); w.Write(s.Speed); w.Write(s.Turn); w.Write(s.Roll);
        w.Write(cam.Links.Count);
        foreach (var l in cam.Links)
        {
            w.Write((byte)l.Param); w.Write((byte)l.Shape); w.Write(l.Amount); w.Write(l.Every); w.Write(l.Phase);
            w.Write(l.Steps.Length);
            foreach (float v in l.Steps) w.Write(v);
        }
    }

    private static void ReadShot(BinaryReader r, CameraTrack cam, int lanes)
    {
        cam.Group = r.ReadString();
        cam.Rig = (ShotRig)Math.Min(r.ReadByte(), (byte)ShotRig.Mount);
        cam.Target = r.ReadInt32();
        if (cam.Target >= lanes) cam.Target = -1;
        cam.Part = (TargetPart)Math.Min(r.ReadByte(), (byte)TargetPart.Ride);
        cam.Smoothing = r.ReadSingle();
        cam.Seed = r.ReadInt32();
        cam.Shake = new ShakeSettings
        {
            Style = (ShakeStyle)Math.Min(r.ReadByte(), (byte)ShakeStyle.Impact),
            Strength = r.ReadSingle(), Speed = r.ReadSingle(), Turn = r.ReadSingle(), Roll = r.ReadSingle(),
        };
        int links = r.ReadInt32();
        for (int i = 0; i < links; i++)
        {
            var l = new BeatLink
            {
                Param = (BeatParam)Math.Min(r.ReadByte(), (byte)BeatParam.Fade),
                Shape = (BeatShape)Math.Min(r.ReadByte(), (byte)BeatShape.Ramp),
                Amount = r.ReadSingle(), Every = r.ReadInt32(), Phase = r.ReadSingle(),
            };
            var steps = new float[r.ReadInt32()];
            for (int k = 0; k < steps.Length; k++) steps[k] = r.ReadSingle();
            l.Steps = steps;
            cam.Links.Add(l);
        }
    }

    private static void WriteWorld(BinaryWriter w, WorldTrack t)
    {
        w.Write(t.Duration);
        w.Write(t.Looks.Length);
        foreach (var l in t.Looks) w.Write(l.Asset ?? "");
        WriteEntities(w, t.Entities);
    }

    /// <summary>A world track's objects (also a Record / Stop session's pieces on disk, #782).</summary>
    internal static void WriteEntities(BinaryWriter w, WorldEntity[] entities)
    {
        w.Write(entities.Length);
        foreach (var e in entities)
        {
            w.Write(e.Parent); w.Write(e.Born); w.Write(e.Died);
            w.Write(e.Times.Length);
            for (int k = 0; k < e.Times.Length; k++)
            {
                var q = e.Poses[k];
                w.Write(e.Times[k]); w.Write(q.X); w.Write(q.Y); w.Write(q.Z);
                w.Write(q.Rot.X); w.Write(q.Rot.Y); w.Write(q.Rot.Z); w.Write(q.Rot.W);
                w.Write(q.Scale.X); w.Write(q.Scale.Y); w.Write(q.Scale.Z);
            }
            w.Write(e.ShowTimes.Length);
            for (int k = 0; k < e.ShowTimes.Length; k++) { w.Write(e.ShowTimes[k]); w.Write(e.Shown[k]); }
            w.Write(e.LookTimes.Length);
            for (int k = 0; k < e.LookTimes.Length; k++) { w.Write(e.LookTimes[k]); w.Write(e.Looks[k]); }
        }
    }

    private static WorldTrack ReadWorld(BinaryReader r)
    {
        double duration = r.ReadDouble();
        var looks = new WorldLook[r.ReadInt32()];
        for (int i = 0; i < looks.Length; i++) looks[i] = new WorldLook { Asset = r.ReadString() };
        return new WorldTrack(ReadEntities(r, looks.Length), looks, duration);
    }

    /// <summary>Objects written by <see cref="WriteEntities"/>, a look outside <paramref name="looks"/> dropped.</summary>
    internal static WorldEntity[] ReadEntities(BinaryReader r, int looks)
    {
        var entities = new WorldEntity[r.ReadInt32()];
        for (int i = 0; i < entities.Length; i++)
        {
            var e = new WorldEntity { Parent = r.ReadInt32(), Born = r.ReadDouble(), Died = r.ReadDouble() };
            int n = r.ReadInt32();
            e.Times = new double[n];
            e.Poses = new WorldPose[n];
            for (int k = 0; k < n; k++)
            {
                e.Times[k] = r.ReadDouble();
                e.Poses[k] = new WorldPose
                {
                    X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(),
                    Rot = new Godot.Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Scale = new Godot.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };
            }
            n = r.ReadInt32();
            e.ShowTimes = new double[n];
            e.Shown = new bool[n];
            for (int k = 0; k < n; k++) { e.ShowTimes[k] = r.ReadDouble(); e.Shown[k] = r.ReadBoolean(); }
            n = r.ReadInt32();
            e.LookTimes = new double[n];
            e.Looks = new int[n];
            for (int k = 0; k < n; k++)
            {
                e.LookTimes[k] = r.ReadDouble();
                int look = r.ReadInt32();
                e.Looks[k] = look >= 0 && look < looks ? look : -1;
            }
            if (e.Parent >= entities.Length || e.Parent < WorldEntity.Environment) e.Parent = -1;
            entities[i] = e;
        }
        return entities;
    }

    private static void WriteKeys(BinaryWriter w, CameraTrack cam)
    {
        w.Write(cam.Keys.Count);
        foreach (var k in cam.Keys)
        {
            w.Write(k.T); w.Write(k.E); w.Write(k.N); w.Write(k.Alt);
            w.Write(k.Qx); w.Write(k.Qy); w.Write(k.Qz); w.Write(k.Qw);
            w.Write(k.Lens); w.Write((byte)k.Ease); w.Write(k.LookAt);
        }
    }

    private static void ReadKeys(BinaryReader r, CameraTrack cam, int lanes)
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
            if (k.LookAt >= lanes) k.LookAt = -1;
            cam.Set(k, -1, replaceOptions: true);
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
        for (int i = 0; i < tracks; i++) p.Tracks.Add(ReadTrack(r, map, discrete.Length));

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

        if (version >= 5)
        {
            int worlds = r.ReadInt32();
            for (int i = 0; i < worlds; i++) p.Worlds.Add(ReadWorld(r));
        }

        int clips = r.ReadInt32();
        int maxId = 0;
        for (int i = 0; i < clips; i++)
        {
            int id = r.ReadInt32(), lane = r.ReadInt32(), track = r.ReadInt32();
            double inT = r.ReadDouble(), outT = r.ReadDouble(), start = r.ReadDouble();
            bool audio = version >= 2 && r.ReadBoolean();
            bool world = version >= 5 && r.ReadBoolean();
            bool valid = audio
                ? lane >= 0 && lane < p.AudioLanes.Count && track >= 0 && track < p.Audio.Count
                : world ? track >= 0 && track < p.Worlds.Count
                : lane >= 0 && lane < p.Lanes.Count && track >= 0 && track < p.Tracks.Count;
            if (!valid) continue;
            p.Clips.Add(new Clip { Id = id, Audio = audio, World = world, Lane = lane, Track = track, In = inT, Out = outT, Start = start });
            maxId = Math.Max(maxId, id);
        }
        p.SetNextId(maxId + 1);

        if (version >= 2)
        {
            int markers = r.ReadInt32();
            for (int i = 0; i < markers; i++) p.AddMarker(r.ReadDouble());
        }

        if (version == 3) ReadKeys(r, p.Camera, p.Lanes.Count);
        if (version >= 4)
        {
            int cameras = r.ReadInt32();
            p.Cameras.Clear();
            for (int i = 0; i < cameras; i++)
            {
                var cam = new CameraTrack { Name = r.ReadString() };
                ReadKeys(r, cam, p.Lanes.Count);
                if (version >= 6) ReadShot(r, cam, p.Lanes.Count);
                p.Cameras.Add(cam);
            }
            if (p.Cameras.Count == 0) p.Cameras.Add(new CameraTrack { Name = "Cam 1" });
            if (version >= 6)
            {
                int sequences = r.ReadInt32();
                p.Sequences.Clear();
                for (int i = 0; i < sequences; i++)
                {
                    var seq = new Sequence { Name = r.ReadString(), In = r.ReadDouble(), Out = r.ReadDouble() };
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        var c = new CameraCut { T = r.ReadDouble(), Camera = r.ReadInt32(), Transition = (TransitionKind)Math.Min(r.ReadByte(), (byte)TransitionKind.ZoomPunch), Duration = r.ReadDouble() };
                        if (c.Camera >= 0 && c.Camera < p.Cameras.Count) seq.Cuts.Add(c);
                    }
                    p.Sequences.Add(seq);
                }
                if (p.Sequences.Count == 0) p.Sequences.Add(new Sequence());
                p.ActiveSequence = r.ReadInt32();
            }
            else
            {
                // before sequences (#776): the one cut list is the sequence "Main"
                int cuts = r.ReadInt32();
                for (int i = 0; i < cuts; i++)
                {
                    double t = r.ReadDouble();
                    int cam = r.ReadInt32();
                    if (cam >= 0 && cam < p.Cameras.Count) p.CutTo(t, cam, -1);
                }
            }
        }
        return p;
    }
}
