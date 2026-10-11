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
        public int Still;   // ticks since it last moved or changed
    }

    /// <summary>Ticks an object rests before it is looked at only every <see cref="RestEvery"/>th tick.</summary>
    public const int RestAfter = 60, RestEvery = 4;

    /// <summary>The first ring written to (the one the convenience constructor made).</summary>
    public WorldRing Ring => _rings[0];
    /// <summary>
    /// Every ring the same reads go into (#775): the replay buffer's and a Record / Stop session's,
    /// so the world is read once a tick however many recordings run.
    /// </summary>
    private readonly List<WorldRing> _rings = new();
    /// <summary>Mean cost of a tick, µs, and the ticks it is over (for the logs and the checks).</summary>
    public double TickMicros => _ticks == 0 ? 0 : _tickTime.Elapsed.TotalMilliseconds * 1000 / _ticks;
    private readonly System.Diagnostics.Stopwatch _tickTime = new();
    private long _ticks;
    private int _tick;
    /// <summary>Every look recorded so far; tracks copy the ones they use.</summary>
    public List<WorldLook> Looks { get; } = new();
    public int Live => _ents.Count;

    private readonly Dictionary<ulong, Ent> _byNode = new();
    private readonly List<Ent> _ents = new();
    private readonly List<Ent> _gone = new();
    private readonly Dictionary<(ulong Mesh, ulong Override, ulong Surfaces, float Range, int Shadow), int> _lookIndex = new();
    private int _nextId = 1, _pass;
    private double _nextWalk;
    // the world's state (#775): the hour of the day and the sea state, as one undrawn entity
    private int _environment;
    private WorldPose _lastEnvironment;

    public WorldRecorder() { }

    /// <summary>A recorder writing into one ring of its own, <paramref name="window"/> seconds long.</summary>
    public WorldRecorder(double window) => _rings.Add(new WorldRing(window));

    /// <summary>
    /// <paramref name="ring"/> written to from now on, starting with everything alive now, as it
    /// stands, so it begins whole (a session starting mid-game).
    /// </summary>
    public void Attach(WorldRing ring, double now)
    {
        if (_rings.Contains(ring)) return;
        _rings.Add(ring);
        // made in order: a group always before its parts
        foreach (var e in _ents) ring.Spawn(now, e.Id, e.Group?.Id ?? -1, e.Look, e.Last, e.Shown);
        if (_environment != 0) ring.Spawn(now, _environment, WorldEntity.Environment, -1, _lastEnvironment, false);
    }

    public void Detach(WorldRing ring) => _rings.Remove(ring);

    private void EmitSpawn(double t, int id, int parent, int look, in WorldPose pose, bool shown) { foreach (var r in _rings) r.Spawn(t, id, parent, look, pose, shown); }
    private void EmitMove(double t, int id, in WorldPose pose) { foreach (var r in _rings) r.Move(t, id, pose); }
    private void EmitShow(double t, int id, bool shown) { foreach (var r in _rings) r.Show(t, id, shown); }
    private void EmitLook(double t, int id, int look) { foreach (var r in _rings) r.Look(t, id, look); }
    private void EmitGone(double t, int id) { foreach (var r in _rings) r.Gone(t, id); }

    public void Clear()
    {
        foreach (var r in _rings) r.Clear();
        _byNode.Clear();
        _ents.Clear();
        _nextWalk = 0;
        _environment = 0;
    }

    /// <summary>One tick at <paramref name="now"/>: new and gone meshes every half second, every pose that moved.</summary>
    public void Tick(double now, WorldOrigin origin)
    {
        _tickTime.Start();
        _ticks++;
        _tick++;
        if (now >= _nextWalk)
        {
            _nextWalk = now + 0.5;
            Walk(now, origin);
        }
        foreach (var e in _ents)
        {
            // at rest for two seconds: a look every few ticks (a parked car, a shut door, a wall)
            if (e.Still >= RestAfter && (_tick + e.Id) % RestEvery != 0) continue;
            if (!GodotObject.IsInstanceValid(e.Node) || !e.Node.IsInsideTree()) { _gone.Add(e); continue; }
            e.Still++;
            var pose = PoseOf(e, origin);
            if (pose.Moved(e.Last))
            {
                // waking from a rest: the last pose it held, a tick back, so it does not glide from long ago
                if (e.Still > RestAfter) EmitMove(now - 1.0 / Channels.Rate, e.Id, e.Last);
                EmitMove(now, e.Id, pose);
                e.Last = pose;
                e.Still = 0;
            }
            if (e.Group != null)
            {
                bool shown = e.Node.IsVisibleInTree();
                if (shown != e.Shown) { EmitShow(now, e.Id, shown); e.Shown = shown; e.Still = 0; }
            }
        }
        Drop(now);
        Environment(now);
        _tickTime.Stop();
    }

    private void Environment(double now)
    {
        if (World.DayNight.Instance is not { } sky) return;
        var pose = WorldPose.Identity with { X = sky.Hour, Y = World.WaterField.SeaState, Z = Occasions.OccasionPrecip.Snowfall };
        if (_environment == 0)
        {
            _environment = _nextId++;
            EmitSpawn(now, _environment, WorldEntity.Environment, -1, pose, false);
            _lastEnvironment = pose;
        }
        else if (pose.Moved(_lastEnvironment))
        {
            EmitMove(now, _environment, pose);
            _lastEnvironment = pose;
        }
    }

    private void Drop(double now)
    {
        foreach (var e in _gone)
        {
            if (!_ents.Remove(e)) continue;
            EmitGone(now, e.Id);
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
            // what draws: meshes, and the lights and particles a car's lamps or a fire make
            if (child is MeshInstance3D { Mesh: not null } or Light3D and not DirectionalLight3D or GpuParticles3D)
            {
                group ??= AddGroup(root, now, origin);
                Part((Node3D)child, group, now, origin);
            }
            if (child.GetChildCount() > 0) WalkParts(root, child, ref group, now, origin);
        }
    }

    private Ent AddGroup(Node3D node, double now, WorldOrigin origin)
    {
        var e = new Ent { Id = _nextId++, Key = node.GetInstanceId(), Node = node };
        e.Last = PoseOf(e, origin);
        EmitSpawn(now, e.Id, -1, -1, e.Last, true);
        _byNode[e.Key] = e;
        _ents.Add(e);
        return e;
    }

    private void Part(Node3D mesh, Ent group, double now, WorldOrigin origin)
    {
        int look = mesh is MeshInstance3D m ? LookOf(m) : TemplateLookOf(mesh);
        if (_byNode.TryGetValue(mesh.GetInstanceId(), out var e))
        {
            e.Seen = _pass;
            if (look != e.Look) { EmitLook(now, e.Id, look); e.Look = look; }
            return;
        }
        e = new Ent { Id = _nextId++, Key = mesh.GetInstanceId(), Node = mesh, Group = group, Look = look, Seen = _pass, Shown = mesh.IsVisibleInTree() };
        e.Last = PoseOf(e, origin);
        EmitSpawn(now, e.Id, group.Id, look, e.Last, e.Shown);
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

    /// <summary>What a light or a particle emitter looks like now: the settings that show.</summary>
    private readonly record struct TemplateKey(int Kind, Color Color, float Energy, float Range, float Angle, bool Shadow,
        ulong Material, ulong Mesh, int Amount, bool Emitting);
    private readonly Dictionary<TemplateKey, int> _templateIndex = new();

    /// <summary>
    /// A light or particle emitter's look: a detached copy of the node as it is now (no script, no
    /// groups or signals), which a ghost duplicates. A new copy only when what shows changes (a lamp
    /// switched on, smoke starting), checked on the walk.
    /// </summary>
    private int TemplateLookOf(Node3D node)
    {
        var key = node switch
        {
            OmniLight3D o => new TemplateKey(1, o.LightColor, o.LightEnergy, o.OmniRange, 0, o.ShadowEnabled, 0, 0, 0, false),
            SpotLight3D sp => new TemplateKey(2, sp.LightColor, sp.LightEnergy, sp.SpotRange, sp.SpotAngle, sp.ShadowEnabled, 0, 0, 0, false),
            GpuParticles3D g => new TemplateKey(3, default, (float)g.Lifetime, 0, 0, false,
                g.ProcessMaterial?.GetInstanceId() ?? 0, g.DrawPass1?.GetInstanceId() ?? 0, g.Amount, g.Emitting),
            Light3D l => new TemplateKey(4, l.LightColor, l.LightEnergy, 0, 0, l.ShadowEnabled, 0, 0, 0, false),
            _ => default,
        };
        if (_templateIndex.TryGetValue(key, out int i)) return i;
        var copy = (Node3D)node.Duplicate(0);
        copy.Transform = Transform3D.Identity;
        copy.Visible = true;
        Looks.Add(new WorldLook { Template = copy });
        _templateIndex[key] = Looks.Count - 1;
        return Looks.Count - 1;
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
