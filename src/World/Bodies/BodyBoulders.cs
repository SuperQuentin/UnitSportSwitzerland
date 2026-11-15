using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The boulders round fresh craters near the camera (#1849, <see cref="BoulderField"/>), on a body with generated craters
/// whose ground is built (the Moon, Mercury, Callisto, Phobos...): made on a worker within <see cref="Reach"/> of the camera
/// when it has moved <see cref="MoveM"/>, each laid on the ground's height there a third sunk, drawn as one MultiMesh a rock
/// shape (<see cref="RingRocks"/>'s lumps, flattened, the body's grey), solid within <see cref="ColliderReach"/>. The same on
/// every peer: the field is a function of the craters, the ground of the data and the seed.
/// </summary>
public partial class BodyBoulders : Node
{
    /// <summary>How far round the camera boulders are made, metres.</summary>
    public const double Reach = 400;

    /// <summary>How far round the build's centre they are solid, metres.</summary>
    public const double ColliderReach = 150;

    /// <summary>A new field once the camera is this far from the last build's centre, metres.</summary>
    public const double MoveM = 100;

    /// <summary>Made only while the camera is this close to the ground, metres.</summary>
    public const double NearM = 3_000;

    private static readonly StringName SunDir = "sun_dir", Shine = "ring_shine", Strength = "sun_strength";

    /// <summary>The live ones, by body (for the checks).</summary>
    public static readonly Dictionary<BodyId, BodyBoulders> Active = new();

    public int Built { get; private set; }
    public int Drawn { get; private set; }
    public int Solid { get; private set; }

    private readonly BodyGlobe _globe;
    private readonly WorldOrigin _origin;
    private readonly ShaderMaterial _material = new() { Shader = GD.Load<Shader>("res://shaders/ring_chunk.gdshader") };
    private BoulderField? _field;
    private Node3D? _node;
    private DVec3 _centre;
    private bool _has;
    private int _epoch = -1;
    private Task<Made>? _task;
    private Vector3 _sunWritten;
    private float _exposureWritten;

    private sealed record Made(DVec3 Centre, List<Transform3D>[] Instances, List<Color>[] Tints, List<(Vector3 At, float Radius)> Solid);

    public BodyBoulders(BodyGlobe globe, WorldOrigin origin)
    {
        _globe = globe;
        _origin = origin;
        Name = $"Boulders_{globe.Body.Name}";
        _material.SetShaderParameter(Shine, 0.04f);
    }

    public override void _EnterTree() => Active[_globe.Body.Id] = this;

    public override void _ExitTree()
    {
        if (Active.TryGetValue(_globe.Body.Id, out var me) && me == this) Active.Remove(_globe.Body.Id);
    }

    public override void _Process(double delta)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            if (done.IsCompletedSuccessfully) Commit(done.Result);
            else GD.PrintErr($"[boulders] {done.Exception?.GetBaseException().Message}");
        }
        if (_origin.Body != _globe.Body.Id || _origin.Frame.Axes is not { } axes || _globe.Ground?.Heights is not { } heights
            || GetViewport()?.GetCamera3D() is not { } cam)
        {
            Drop();
            return;
        }
        _field ??= heights.Craters is { } craters ? new BoulderField(craters, (ulong)_globe.Body.Id * 7_919 + 3) : null;
        if (_field == null) return;
        if (_node != null && _origin.Epoch != _epoch) Place(axes);
        if (_node != null && _globe.Sun != Vector3.Zero && _globe.Sun != _sunWritten) _material.SetShaderParameter(SunDir, _sunWritten = _globe.Sun);
        if (_globe.Exposure != _exposureWritten) _material.SetShaderParameter(Strength, _exposureWritten = _globe.Exposure);

        var c = cam.GlobalPosition;
        var e = axes.ToEcef(c.X, c.Y, c.Z);
        var shape = _globe.Body.Shape;
        double over = shape.ToGeodetic(e).H - heights.At(heights.NormalUnder(e));
        if (over > NearM)
        {
            Drop();
            return;
        }
        var p = new DVec3(e.X, e.Y, e.Z);
        if (_task != null || (_has && (p - _centre).Length < MoveM)) return;
        var field = _field;
        var body = _globe.Body;
        _task = Task.Run(() => Make(field, heights, body, p));
    }

    /// <summary>The boulders round a body-fixed point: each shape's instances (relative to the point), tints and colliders; on a worker.</summary>
    private static Made Make(BoulderField field, BodyHeights heights, CelestialBody body, DVec3 at)
    {
        var boulders = field.Near(at.X, at.Y, at.Z, Reach);
        var instances = new List<Transform3D>[RingRocks.Variants];
        var tints = new List<Color>[RingRocks.Variants];
        for (int i = 0; i < RingRocks.Variants; i++) { instances[i] = new(); tints[i] = new(); }
        var solid = new List<(Vector3, float)>();
        // the body's rock: the Moon's and Mercury's grey, Mars' rust
        var rock = body.Id == BodyId.Mars ? new Color(0.4f, 0.28f, 0.21f) : new Color(0.37f, 0.36f, 0.35f);
        foreach (var b in boulders)
        {
            var n = heights.NormalUnder(new EcefPos(b.X, b.Y, b.Z));
            var g = body.Shape.ToGeodetic(new EcefPos(b.X, b.Y, b.Z));
            double ground = heights.At(n);
            // a third sunk into the ground, along its up
            var place = body.Shape.ToEcef(g with { H = ground + b.Radius * 0.35 });
            var up = new Vector3((float)n.X, (float)n.Y, (float)n.Z).Normalized();
            ulong s = b.Shape;
            int variant = (int)(s % RingRocks.Variants);
            float yaw = (s >> 8 & 0xFFFF) / 65535f * Mathf.Tau, tilt = (s >> 24 & 0xFF) / 255f * 0.5f;
            // the rock's frame: its up the ground's, turned about it, a little tipped, flattened
            var across = Mathf.Abs(up.Z) < 0.9f ? up.Cross(Vector3.Back).Normalized() : up.Cross(Vector3.Right).Normalized();
            var basis = new Basis(across, up, across.Cross(up)).Orthonormalized();
            basis = new Basis(up, yaw) * new Basis(basis.X, tilt) * basis;
            float rr = (float)b.Radius;
            basis = basis * Basis.FromScale(new Vector3(rr, rr * 0.7f, rr));
            var local = new Vector3((float)(place.X - at.X), (float)(place.Y - at.Y), (float)(place.Z - at.Z));
            instances[variant].Add(new Transform3D(basis, local));
            float v = 0.85f + 0.3f * ((s >> 48 & 0xFF) / 255f);
            tints[variant].Add(new Color(rock.R * v, rock.G * v, rock.B * v));
            if (local.Length() <= ColliderReach) solid.Add((local, rr * 0.8f));
        }
        return new Made(at, instances, tints, solid);
    }

    /// <summary>The world centre of the solid boulder nearest a world point, and its collider's radius (null: none solid).</summary>
    public (Vector3 At, float Radius)? NearestSolid(Vector3 world)
    {
        if (_node?.GetNodeOrNull<StaticBody3D>("Solid") is not { } solid) return null;
        (Vector3, float)? best = null;
        float bestD = float.MaxValue;
        foreach (var child in solid.GetChildren())
            if (child is CollisionShape3D { Shape: SphereShape3D sphere } shape && shape.GlobalPosition.DistanceTo(world) is var d && d < bestD)
            {
                bestD = d;
                best = (shape.GlobalPosition, sphere.Radius);
            }
        return best;
    }

    /// <summary>Main thread: the new field in (drawn, solid), then the old one out.</summary>
    private void Commit(Made m)
    {
        if (_origin.Body != _globe.Body.Id || _origin.Frame.Axes is not { } axes) return;
        var node = new Node3D { Name = "Field" };
        int drawn = 0;
        for (int i = 0; i < RingRocks.Variants; i++)
        {
            var list = m.Instances[i];
            if (list.Count == 0) continue;
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = RingRocks.Mesh(i), InstanceCount = list.Count };
            for (int k = 0; k < list.Count; k++)
            {
                mm.SetInstanceTransform(k, list[k]);
                mm.SetInstanceColor(k, m.Tints[i][k]);
            }
            node.AddChild(new MultiMeshInstance3D
            {
                Name = $"Shape{i}", Multimesh = mm, MaterialOverride = _material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // the instances' box is round the field's centre: never culled by Godot's guess
                ExtraCullMargin = (float)Reach,
            });
            drawn += list.Count;
        }
        var body = new StaticBody3D { Name = "Solid" };
        foreach (var (at, radius) in m.Solid)
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = radius }, Position = at });
        node.AddChild(body);
        var old = _node;
        _node = node;
        _centre = m.Centre;
        _has = true;
        AddChild(node);
        Place(axes);
        old?.QueueFree();
        _material.SetShaderParameter(SunDir, _sunWritten = _globe.Sun == Vector3.Zero ? Vector3.Up : _globe.Sun);
        Built++;
        Drawn = drawn;
        Solid = m.Solid.Count;
        if (Built == 1 || Built % 20 == 0)
            GD.Print(System.FormattableString.Invariant($"[boulders] {_globe.Body.Name} field {Built}: {drawn} within {Reach:F0} m, {m.Solid.Count} solid within {ColliderReach:F0} m"));
    }

    /// <summary>The field from the body's frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(FrameAxes axes)
    {
        if (_node == null) return;
        _epoch = _origin.Epoch;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(new EcefPos(_centre.X, _centre.Y, _centre.Z));
        _node.GlobalTransform = new Transform3D(new Basis(Turn(new EcefPos(1, 0, 0)), Turn(new EcefPos(0, 1, 0)), Turn(new EcefPos(0, 0, 1))),
            new Vector3((float)ox, (float)oy, (float)oz));
    }

    private void Drop()
    {
        _node?.QueueFree();
        _node = null;
        _has = false;
    }
}
