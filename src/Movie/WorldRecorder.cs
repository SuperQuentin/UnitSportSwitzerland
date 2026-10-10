using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// Records the world as it is drawn (#775): every <see cref="MeshInstance3D"/> under the objects
/// <see cref="ReplayWatch"/> knows, into a <see cref="WorldRing"/>. An object (a child of a watched
/// container, or a watched lone object) is a group whose LV95 pose is kept; each of its meshes is a
/// part whose pose is kept in the group's frame, so a car moving costs one pose a tick and its wheels
/// only when they turn. Nothing is written for something that did not move.
/// <para>
/// The tree is walked for new and gone meshes twice a second; poses are read every tick. Players are
/// skipped (their own recording, <see cref="ReplayRing"/>, puts them back as puppets), and so is
/// anything under one.
/// </para>
/// </summary>
public sealed class WorldRecorder
{
    private sealed class Ent
    {
        public int Id;
        public ulong Key;   // the node's instance id, read while it was alive
        public required Node3D Node;
        public Ent? Group;   // null: this is a group
        public WorldPose Last;
        public bool Shown;
        public int Look = -1;
        public int Seen;
    }

    public WorldRing Ring { get; }
    /// <summary>Every look recorded so far; tracks copy the ones they use.</summary>
    public List<WorldLook> Looks { get; } = new();
    public int Live => _ents.Count;

    private readonly Dictionary<ulong, Ent> _byNode = new();
    private readonly List<Ent> _ents = new();
    private readonly List<Ent> _gone = new();
    private readonly Dictionary<(ulong Mesh, ulong Override, ulong Surfaces, float Range, int Shadow), int> _lookIndex = new();
    private int _nextId = 1, _pass;
    private double _nextWalk;

    public WorldRecorder(double window) => Ring = new WorldRing(window);

    public void Clear()
    {
        Ring.Clear();
        _byNode.Clear();
        _ents.Clear();
        _nextWalk = 0;
    }

    /// <summary>One tick at <paramref name="now"/>: new and gone meshes every half second, every pose that moved.</summary>
    public void Tick(double now, WorldOrigin origin)
    {
        if (now >= _nextWalk)
        {
            _nextWalk = now + 0.5;
            Walk(now, origin);
        }
        foreach (var e in _ents)
        {
            if (!GodotObject.IsInstanceValid(e.Node) || !e.Node.IsInsideTree()) { _gone.Add(e); continue; }
            var pose = PoseOf(e, origin);
            if (pose.Moved(e.Last)) { Ring.Move(now, e.Id, pose); e.Last = pose; }
            if (e.Group != null)
            {
                bool shown = e.Node.IsVisibleInTree();
                if (shown != e.Shown) { Ring.Show(now, e.Id, shown); e.Shown = shown; }
            }
        }
        Drop(now);
    }

    private void Drop(double now)
    {
        foreach (var e in _gone)
        {
            if (!_ents.Remove(e)) continue;
            Ring.Gone(now, e.Id);
            _byNode.Remove(e.Key);
        }
        _gone.Clear();
    }

    // ---- the walk -----------------------------------------------------------------------------

    private void Walk(double now, WorldOrigin origin)
    {
        _pass++;
        foreach (var container in ReplayWatch.Containers)
        {
            if (!GodotObject.IsInstanceValid(container) || !container.IsInsideTree()) continue;
            int n = container.GetChildCount();
            for (int i = 0; i < n; i++)
                if (container.GetChild(i) is Node3D child && child is not FootPlayer) WalkGroup(child, now, origin);
        }
        foreach (var obj in ReplayWatch.Objects)
            if (GodotObject.IsInstanceValid(obj) && obj.IsInsideTree()) WalkGroup(obj, now, origin);

        foreach (var e in _ents)
            if (e.Seen != _pass) _gone.Add(e);
        // a group goes after its parts
        _gone.Sort((a, b) => (a.Group == null ? 1 : 0).CompareTo(b.Group == null ? 1 : 0));
        Drop(now);
    }

    private void WalkGroup(Node3D node, double now, WorldOrigin origin)
    {
        Ent? group = _byNode.GetValueOrDefault(node.GetInstanceId());
        WalkParts(node, node, ref group, now, origin);
        if (group != null) group.Seen = _pass;
    }

    private void WalkParts(Node3D root, Node at, ref Ent? group, double now, WorldOrigin origin)
    {
        int n = at.GetChildCount();
        for (int i = 0; i < n; i++)
        {
            var child = at.GetChild(i);
            if (child is FootPlayer) continue;
            if (child is MeshInstance3D mesh && mesh.Mesh != null)
            {
                group ??= AddGroup(root, now, origin);
                Part(mesh, group, now, origin);
            }
            if (child.GetChildCount() > 0) WalkParts(root, child, ref group, now, origin);
        }
    }

    private Ent AddGroup(Node3D node, double now, WorldOrigin origin)
    {
        var e = new Ent { Id = _nextId++, Key = node.GetInstanceId(), Node = node };
        e.Last = PoseOf(e, origin);
        Ring.Spawn(now, e.Id, -1, -1, e.Last, true);
        _byNode[e.Key] = e;
        _ents.Add(e);
        return e;
    }

    private void Part(MeshInstance3D mesh, Ent group, double now, WorldOrigin origin)
    {
        int look = LookOf(mesh);
        if (_byNode.TryGetValue(mesh.GetInstanceId(), out var e))
        {
            e.Seen = _pass;
            if (look != e.Look) { Ring.Look(now, e.Id, look); e.Look = look; }
            return;
        }
        e = new Ent { Id = _nextId++, Key = mesh.GetInstanceId(), Node = mesh, Group = group, Look = look, Seen = _pass, Shown = mesh.IsVisibleInTree() };
        e.Last = PoseOf(e, origin);
        Ring.Spawn(now, e.Id, group.Id, look, e.Last, e.Shown);
        _byNode[e.Key] = e;
        _ents.Add(e);
    }

    // ---- poses and looks ----------------------------------------------------------------------

    private static WorldPose PoseOf(Ent e, WorldOrigin origin)
    {
        Transform3D xf;
        if (e.Group == null)
        {
            xf = e.Node.GlobalTransform;
            var g = origin.ToGlobal(xf.Origin);
            return Pose(xf.Basis, g.E, g.N, g.Alt);
        }
        xf = e.Node.GetParent() == e.Group.Node
            ? e.Node.Transform
            : e.Group.Node.GlobalTransform.AffineInverse() * e.Node.GlobalTransform;
        return Pose(xf.Basis, xf.Origin.X, xf.Origin.Y, xf.Origin.Z);
    }

    private static WorldPose Pose(Basis b, double x, double y, double z)
    {
        var scale = b.Scale;
        return new WorldPose { X = x, Y = y, Z = z, Rot = b.Orthonormalized().GetRotationQuaternion(), Scale = scale };
    }

    private int LookOf(MeshInstance3D m)
    {
        var mesh = m.Mesh!;
        int surfaces = mesh.GetSurfaceCount();
        ulong hash = 0;
        for (int s = 0; s < surfaces; s++)
            if (m.GetSurfaceOverrideMaterial(s) is { } mat) hash = hash * 1_000_003 + mat.GetInstanceId() + (ulong)s;
        var key = (mesh.GetInstanceId(), m.MaterialOverride?.GetInstanceId() ?? 0, hash, m.VisibilityRangeEnd, (int)m.CastShadow);
        if (_lookIndex.TryGetValue(key, out int i)) return i;
        var overrides = new Material?[surfaces];
        for (int s = 0; s < surfaces; s++) overrides[s] = m.GetSurfaceOverrideMaterial(s);
        Looks.Add(new WorldLook
        {
            Mesh = mesh,
            Override = m.MaterialOverride,
            Surfaces = hash == 0 ? Array.Empty<Material?>() : overrides,
            RangeEnd = m.VisibilityRangeEnd,
            Shadow = m.CastShadow,
        });
        _lookIndex[key] = Looks.Count - 1;
        return Looks.Count - 1;
    }
}
