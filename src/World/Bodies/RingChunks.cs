using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// Saturn's ring ice chunks round the camera (#1854): the ones <see cref="RingField"/> puts within <see cref="Reach"/>,
/// the same on every client (one seed, a function of the cell), made on a worker when the camera has moved
/// <see cref="MoveM"/> and dropped with the field behind it: generated and discarded on the fly. Drawn as one MultiMesh a
/// shape (<see cref="RingRocks"/>: turned, sized and tinted by each chunk's seed), solid within <see cref="ColliderReach"/>
/// (a sphere a chunk, a little inside its lumps). True scale round the camera, placed from doubles in Saturn's frame;
/// the slab round them (<c>ring_slab.gdshader</c>) stops its path at them, so they stand in its haze.
/// </summary>
public partial class RingChunks : Node
{
    /// <summary>How far round the camera chunks are made, metres.</summary>
    public const double Reach = 1500;

    /// <summary>How far round the build's centre chunks are solid, metres (past the next build's move).</summary>
    public const double ColliderReach = 500;

    /// <summary>A new field once the camera is this far from the last build's centre, metres.</summary>
    public const double MoveM = 250;

    private static readonly StringName SunDir = "sun_dir";

    /// <summary>The live one, for the checks.</summary>
    public static RingChunks? Active { get; private set; }

    /// <summary>How many fields were built; the chunks drawn and solid in the last.</summary>
    public int Built { get; private set; }
    public int Drawn { get; private set; }
    public int Solid { get; private set; }

    public RingField Field { get; }

    private readonly BodyGlobe _saturn;
    private readonly WorldOrigin _origin;
    private readonly ShaderMaterial _material = new() { Shader = GD.Load<Shader>("res://shaders/ring_chunk.gdshader") };
    private Node3D? _node;
    private DVec3 _centre;
    private bool _has;
    private int _epoch = -1;
    private Task<Made>? _task;
    private Vector3 _sunWritten;

    private sealed record Made(DVec3 Centre, List<Transform3D>[] Instances, List<Color>[] Tints, List<(Vector3 At, float Radius)> Solid);

    public RingChunks(BodyGlobe saturn, WorldOrigin origin)
    {
        _saturn = saturn;
        _origin = origin;
        Name = "RingChunks";
        Field = new RingField((ulong)BodyId.Saturn * 1_000_003, SaturnRings.Inner, SaturnRings.Outer, SaturnRings.HalfThickness, SaturnRings.Opacity);
    }

    public override void _EnterTree()
    {
        Active = this;
        SaturnRings.Preload();
        Styles.CameraGlobal.CloseContent = () => _near;
    }

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
        Styles.CameraGlobal.CloseContent = null;
    }

    /// <summary>The camera is among the rings: the camera's planes come back to the game's own (<see cref="Styles.CameraGlobal.CloseContent"/>).</summary>
    private bool _near;

    public override void _Process(double delta)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            if (done.IsCompletedSuccessfully) Commit(done.Result);
            else GD.PrintErr($"[rings] chunks: {done.Exception?.GetBaseException().Message}");
        }
        // near the rings the anchor is Saturn (its sphere of influence is 55 million km, the rings 140,000)
        if (_origin.Body != BodyId.Saturn || _origin.Frame.Axes is not { } axes || GetViewport()?.GetCamera3D() is not { } cam)
        {
            _near = false;
            Drop();
            return;
        }
        if (_node != null && _origin.Epoch != _epoch) Place(axes);
        if (_node != null && _saturn.Sun != Vector3.Zero && _saturn.Sun != _sunWritten)
            _material.SetShaderParameter(SunDir, _sunWritten = _saturn.Sun);

        var c = cam.GlobalPosition;
        var e = axes.ToEcef(c.X, c.Y, c.Z);
        var p = new DVec3(e.X, e.Y, e.Z);
        double r = System.Math.Sqrt(p.X * p.X + p.Y * p.Y);
        bool near = System.Math.Abs(p.Z) < Field.HalfThickness + Reach && r > Field.Inner - Reach && r < Field.Outer + Reach;
        _near = near;
        if (!near)
        {
            Drop();
            return;
        }
        if (_task != null || (_has && (p - _centre).Length < MoveM)) return;
        var field = Field;
        _task = Task.Run(() => Make(field, p));
    }

    /// <summary>The chunks round a point (body-fixed): each shape's instances (relative to the point), tints and colliders; on a worker.</summary>
    private static Made Make(RingField field, DVec3 at)
    {
        var chunks = field.Near(at.X, at.Y, at.Z, Reach);
        var instances = new List<Transform3D>[RingRocks.Variants];
        var tints = new List<Color>[RingRocks.Variants];
        for (int i = 0; i < RingRocks.Variants; i++) { instances[i] = new(); tints[i] = new(); }
        var solid = new List<(Vector3, float)>();
        foreach (var ch in chunks)
        {
            ulong s = ch.Shape;
            int variant = (int)(s % RingRocks.Variants);
            // a turn and a tint from the seed's bits: bright water ice, a few dusty ones
            float yaw = (s >> 8 & 0xFFFF) / 65535f * Mathf.Tau, pitch = (s >> 24 & 0xFFFF) / 65535f * Mathf.Tau, roll = (s >> 40 & 0xFF) / 255f * Mathf.Tau;
            var basis = Basis.FromEuler(new Vector3(pitch, yaw, roll)).Scaled(Vector3.One * (float)ch.Radius);
            var local = new Vector3((float)(ch.X - at.X), (float)(ch.Y - at.Y), (float)(ch.Z - at.Z));
            instances[variant].Add(new Transform3D(basis, local));
            float dust = (s >> 48 & 0xFF) / 255f;
            float v = 0.78f + 0.2f * ((s >> 56 & 0xFF) / 255f);
            tints[variant].Add(dust > 0.85f ? new Color(v * 0.82f, v * 0.74f, v * 0.64f) : new Color(v * 0.93f, v * 0.95f, v));
            if (local.Length() <= ColliderReach) solid.Add((local, (float)ch.Radius * 0.85f));
        }
        return new Made(at, instances, tints, solid);
    }

    /// <summary>Main thread: the new field in (drawn, solid), then the old one out.</summary>
    private void Commit(Made m)
    {
        if (_origin.Body != BodyId.Saturn || _origin.Frame.Axes is not { } axes) return;
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
        _material.SetShaderParameter(SunDir, _sunWritten = _saturn.Sun == Vector3.Zero ? Vector3.Up : _saturn.Sun);
        Built++;
        Drawn = drawn;
        Solid = m.Solid.Count;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[rings] chunks {Built}: {drawn} drawn within {Reach:F0} m, {m.Solid.Count} solid within {ColliderReach:F0} m, {System.Math.Sqrt(m.Centre.X * m.Centre.X + m.Centre.Y * m.Centre.Y) / 1000:F0} km from Saturn's centre, {m.Centre.Z:F0} m off the equator"));
    }

    /// <summary>The field from Saturn's frame into world space, in doubles from the anchor (once a re-anchor).</summary>
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
