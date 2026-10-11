using Godot;

namespace UnitSport.Movie;

/// <summary>
/// Where one recorded world object was at one instant (#775). A group (a car, a bird, a door) holds
/// its LV95 position in <see cref="X"/> = E, <see cref="Y"/> = N, <see cref="Z"/> = altitude, so an
/// origin shift never touches it; a part (one mesh of the group) holds its position in the group's
/// own frame. The rotation and scale are the Godot basis's (world axes for a group, the group's axes
/// for a part).
/// </summary>
public struct WorldPose
{
    public double X, Y, Z;
    public Quaternion Rot;
    public Vector3 Scale;

    public static readonly WorldPose Identity = new() { Rot = Quaternion.Identity, Scale = Vector3.One };

    /// <summary>Whether two poses differ by more than a millimetre, about 0.05° or a hundredth of a percent of scale.</summary>
    public readonly bool Moved(in WorldPose o) =>
        Math.Abs(X - o.X) > 1e-3 || Math.Abs(Y - o.Y) > 1e-3 || Math.Abs(Z - o.Z) > 1e-3
        || Math.Abs(Rot.Dot(o.Rot)) < 1 - 1e-7f
        || (Scale - o.Scale).LengthSquared() > 1e-8f;

    public static WorldPose Lerp(in WorldPose a, in WorldPose b, float k) => new()
    {
        X = a.X + (b.X - a.X) * k,
        Y = a.Y + (b.Y - a.Y) * k,
        Z = a.Z + (b.Z - a.Z) * k,
        Rot = a.Rot.Dot(b.Rot) < 0 ? a.Rot.Slerp(-b.Rot, k) : a.Rot.Slerp(b.Rot, k),
        Scale = a.Scale.Lerp(b.Scale, k),
    };
}

/// <summary>
/// What a recorded mesh drew with (#775): the mesh and its materials, shared by every part that drew
/// the same. Tracks refer to looks by index, so a thousand frames of a car hold one mesh, not a
/// thousand.
/// </summary>
public sealed class WorldLook
{
    /// <summary>
    /// The file it is saved as in the movies' looks folder, once a project holding it was saved (or
    /// the one it was loaded from); null until then.
    /// </summary>
    public string? Asset;
    /// <summary>A light or particle emitter (#775): a detached node a ghost duplicates, instead of a mesh.</summary>
    public Node3D? Template;
    public Mesh? Mesh;
    public Material? Override;
    /// <summary>Per-surface override materials, null where the mesh's own is used.</summary>
    public Material?[] Surfaces = Array.Empty<Material?>();
    public float RangeEnd;
    public GeometryInstance3D.ShadowCastingSetting Shadow = GeometryInstance3D.ShadowCastingSetting.On;
}

/// <summary>
/// One recorded world object (#775): a group or one of its parts, from <see cref="Born"/> to
/// <see cref="Died"/> (track times). Its pose is kept only when it moved, so a parked car costs one
/// frame however long it stands.
/// </summary>
public sealed class WorldEntity
{
    /// <summary>
    /// <see cref="Parent"/> of the one entity that is not drawn but is the world's state (#775): its
    /// pose's <see cref="WorldPose.X"/> is the hour of the day, <see cref="WorldPose.Y"/> the sea state.
    /// </summary>
    public const int Environment = -2;

    /// <summary>The group this part belongs to (an index into the track's entities), -1 for a group, or <see cref="Environment"/>.</summary>
    public int Parent = -1;
    public bool IsGroup => Parent == -1;
    public bool IsEnvironment => Parent == Environment;
    public double Born, Died = double.PositiveInfinity;
    public double[] Times = Array.Empty<double>();
    public WorldPose[] Poses = Array.Empty<WorldPose>();
    /// <summary>Visibility changes: shown or hidden from each time on; the first is at <see cref="Born"/>.</summary>
    public double[] ShowTimes = Array.Empty<double>();
    public bool[] Shown = Array.Empty<bool>();
    /// <summary>Look changes, the same way (parts only).</summary>
    public double[] LookTimes = Array.Empty<double>();
    public int[] Looks = Array.Empty<int>();

    public bool AliveAt(double t) => t >= Born && t < Died;

    /// <summary>
    /// The pose at <paramref name="t"/>: between two frames recorded one tick apart it is
    /// interpolated; across a longer gap the object stood still, so it holds the earlier one.
    /// </summary>
    public WorldPose PoseAt(double t)
    {
        int i = Last(Times, t);
        if (i < 0) return Poses[0];
        if (i + 1 >= Times.Length) return Poses[i];
        double gap = Times[i + 1] - Times[i];
        if (gap > WorldTrack.MaxGap) return Poses[i];
        return WorldPose.Lerp(Poses[i], Poses[i + 1], (float)Math.Clamp((t - Times[i]) / gap, 0, 1));
    }

    /// <summary>The environment's hour and sea state at <paramref name="t"/>: the hour the short way round midnight.</summary>
    public (double Hour, float Sea) EnvironmentAt(double t)
    {
        int i = Last(Times, t);
        if (i < 0) return (Poses[0].X, (float)Poses[0].Y);
        if (i + 1 >= Times.Length || Times[i + 1] - Times[i] > WorldTrack.MaxGap) return (Poses[i].X, (float)Poses[i].Y);
        double k = Math.Clamp((t - Times[i]) / (Times[i + 1] - Times[i]), 0, 1);
        double a = Poses[i].X, b = Poses[i + 1].X;
        if (b - a > 12) b -= 24;
        else if (a - b > 12) b += 24;
        double hour = (a + (b - a) * k + 24) % 24;
        return (hour, (float)(Poses[i].Y + (Poses[i + 1].Y - Poses[i].Y) * k));
    }

    public bool ShownAt(double t)
    {
        int i = Last(ShowTimes, t);
        return i >= 0 && Shown[i];
    }

    public int LookAt(double t)
    {
        int i = Last(LookTimes, t);
        return i >= 0 ? Looks[i] : Looks.Length > 0 ? Looks[0] : -1;
    }

    /// <summary>The index of the last time at or before <paramref name="t"/>, or -1.</summary>
    internal static int Last(double[] times, double t)
    {
        if (times.Length == 0 || t < times[0]) return -1;
        int i = Array.BinarySearch(times, t);
        return i >= 0 ? i : ~i - 1;
    }
}

/// <summary>
/// The recorded world (#775): every object a system drew (cars, birds, doors, dropped items, …)
/// over a stretch of time, with times local from 0. Sampling is a pure function of time, like an
/// <see cref="ActorTrack"/>, so the studio scrubs it either way.
/// </summary>
public sealed class WorldTrack
{
    /// <summary>Two frames further apart than this (about one and a half ticks) were a rest, not a motion.</summary>
    public const double MaxGap = 1.5 / Channels.Rate;

    public readonly WorldEntity[] Entities;
    public readonly WorldLook[] Looks;
    public readonly double Duration;

    public WorldTrack(WorldEntity[] entities, WorldLook[] looks, double duration)
    {
        Entities = entities;
        Looks = looks;
        Duration = duration;
    }
}

/// <summary>
/// The last minutes of the world (#775), as the events that changed it: an object appearing, moving,
/// showing or hiding, changing look, going. Events older than the window fold into the state the
/// window starts with, the way <see cref="ReplayRing"/> folds a player's change-only properties.
/// One growing ring of structs: once it has grown to a busy minute it allocates nothing more.
/// </summary>
public sealed class WorldRing
{
    private enum Kind : byte { Spawn, Move, Show, Look, Gone }

    private struct Event
    {
        public double T;
        public int Id, Parent, Look;
        public Kind Kind;
        public bool Shown;
        public WorldPose Pose;
    }

    /// <summary>An object as it stood at the window's start.</summary>
    private struct State
    {
        public int Parent, Look;
        public bool Shown;
        public WorldPose Pose;
    }

    private readonly Dictionary<int, State> _base = new();
    private Event[] _events = new Event[4096];
    private int _head, _count;   // _head: the oldest event
    private double _oldest = double.NegativeInfinity, _first = double.NaN;

    /// <summary>Seconds kept; older events fold into the starting state.</summary>
    public double Window { get; set; }
    public int Count => _count;
    /// <summary>Objects alive at the window's start.</summary>
    public int BaseCount => _base.Count;

    public WorldRing(double window) => Window = window;

    public void Spawn(double t, int id, int parent, int look, in WorldPose pose, bool shown) =>
        Add(new Event { T = t, Id = id, Parent = parent, Look = look, Kind = Kind.Spawn, Shown = shown, Pose = pose });
    public void Move(double t, int id, in WorldPose pose) => Add(new Event { T = t, Id = id, Kind = Kind.Move, Pose = pose });
    public void Show(double t, int id, bool shown) => Add(new Event { T = t, Id = id, Kind = Kind.Show, Shown = shown });
    public void Look(double t, int id, int look) => Add(new Event { T = t, Id = id, Kind = Kind.Look, Look = look });
    public void Gone(double t, int id) => Add(new Event { T = t, Id = id, Kind = Kind.Gone });

    public void Clear()
    {
        _base.Clear();
        _head = _count = 0;
        _oldest = double.NegativeInfinity;
        _first = double.NaN;
    }

    private void Add(in Event e)
    {
        if (_count == _events.Length)
        {
            var grown = new Event[_events.Length * 2];
            for (int k = 0; k < _count; k++) grown[k] = _events[(_head + k) % _events.Length];
            _events = grown;
            _head = 0;
        }
        _events[(_head + _count) % _events.Length] = e;
        _count++;
        if (double.IsNaN(_first)) _first = e.T;
        // what fell out of the window folds into where everything stood at its start
        double edge = e.T - Window;
        while (_count > 0 && _events[_head].T < edge)
        {
            Fold(_base, _events[_head]);
            _head = (_head + 1) % _events.Length;
            _count--;
        }
        _oldest = Math.Max(_oldest, edge);
    }

    private static void Fold(Dictionary<int, State> state, in Event e)
    {
        switch (e.Kind)
        {
            case Kind.Spawn:
                state[e.Id] = new State { Parent = e.Parent, Look = e.Look, Shown = e.Shown, Pose = e.Pose };
                break;
            case Kind.Gone:
                state.Remove(e.Id);
                break;
            default:
                if (!state.TryGetValue(e.Id, out var s)) break;
                if (e.Kind == Kind.Move) s.Pose = e.Pose;
                else if (e.Kind == Kind.Show) s.Shown = e.Shown;
                else s.Look = e.Look;
                state[e.Id] = s;
                break;
        }
    }

    /// <summary>
    /// The world from just after <paramref name="after"/> (absolute time) to <paramref name="now"/>,
    /// as a track whose time 0 is <paramref name="start"/>; null when nothing was recorded then.
    /// Allocates: it runs on a grab, never per frame.
    /// </summary>
    public WorldTrack? Slice(double after, double now, IReadOnlyList<WorldLook> looks, out double start)
    {
        start = Math.Max(after, Math.Max(_oldest, _first));
        if (double.IsNaN(_first) || now - start < MovieProject.MinLength) return null;

        // where everything stood at the start, then what happened after
        var state = new Dictionary<int, State>(_base);
        int first = 0;
        while (first < _count && _events[(_head + first) % _events.Length].T <= start)
            Fold(state, _events[(_head + first++) % _events.Length]);

        var builders = new Dictionary<int, Builder>();
        foreach (var (id, s) in state) builders[id] = Builder.From(s, 0);
        for (int k = first; k < _count; k++)
        {
            ref readonly var e = ref _events[(_head + k) % _events.Length];
            double t = e.T - start;
            if (e.Kind == Kind.Spawn)
            {
                if (builders.TryGetValue(e.Id, out var old)) old.Died = t;   // an id reused: never, but safe
                builders[e.Id] = Builder.From(new State { Parent = e.Parent, Look = e.Look, Shown = e.Shown, Pose = e.Pose }, t);
                continue;
            }
            if (!builders.TryGetValue(e.Id, out var b) || b.Died < double.PositiveInfinity) continue;
            switch (e.Kind)
            {
                case Kind.Move:
                    // a part's last pose before it moves again after a rest: kept, so the rest holds still
                    b.Times.Add(t); b.Poses.Add(e.Pose); break;
                case Kind.Show: b.ShowTimes.Add(t); b.Shown.Add(e.Shown); break;
                case Kind.Look: b.LookTimes.Add(t); b.Looks.Add(e.Look); break;
                case Kind.Gone: b.Died = t; break;
            }
        }

        // ids become indices; a part points at its group's index; looks are renumbered to the ones used
        var ids = builders.Keys.OrderBy(id => id).ToList();
        var index = new Dictionary<int, int>(ids.Count);
        for (int i = 0; i < ids.Count; i++) index[ids[i]] = i;
        var lookIndex = new Dictionary<int, int>();
        var usedLooks = new List<WorldLook>();
        int Remap(int look)
        {
            if (look < 0 || look >= looks.Count) return -1;
            if (!lookIndex.TryGetValue(look, out int i)) { i = usedLooks.Count; lookIndex[look] = i; usedLooks.Add(looks[look]); }
            return i;
        }
        var entities = new WorldEntity[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            var b = builders[ids[i]];
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
        return new WorldTrack(entities, usedLooks.ToArray(), now - start);
    }

    private sealed class Builder
    {
        public int Parent;
        public double Born, Died = double.PositiveInfinity;
        public readonly List<double> Times = new(), ShowTimes = new(), LookTimes = new();
        public readonly List<WorldPose> Poses = new();
        public readonly List<bool> Shown = new();
        public readonly List<int> Looks = new();

        public static Builder From(in State s, double t)
        {
            var b = new Builder { Parent = s.Parent, Born = t };
            b.Times.Add(t); b.Poses.Add(s.Pose);
            b.ShowTimes.Add(t); b.Shown.Add(s.Shown);
            if (s.Parent >= 0) { b.LookTimes.Add(t); b.Looks.Add(s.Look); }
            return b;
        }
    }
}
