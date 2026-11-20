using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The stones on another body's ground beyond the boulders (#2016, <see cref="ScatterField"/>): Mars' rocks by its
/// thermal-inertia guess and its pebbles, the regolith's pebbles, Europa's and Ganymede's ice blocks, Titan's cobbles. One
/// layer a field, each made on a worker within its reach of the camera when the camera has moved its move distance, laid
/// on the ground's height a third sunk, drawn as one MultiMesh a shape (<see cref="ScatterShapes"/>), lit as the ground
/// (<c>body_rock.gdshader</c>), solid from the field's radius within its solid reach (spheres for rocks, boxes for ice
/// blocks: the walker and the vehicles stop at them). Never rebuilt per frame: a layer moves only after the camera did.
/// The same on every peer: a function of the field's seed and the ground.
/// </summary>
public partial class BodyScatter : Node
{
    /// <summary>The live ones, by body (for the checks).</summary>
    public static readonly Dictionary<BodyId, BodyScatter> Active = new();

    private static readonly StringName LightStrength = "light_strength", LightTint = "light_tint", AirTau = "air_tau",
        LunarParam = "lunar", Sheen = "sheen", SkyFill = "sky_fill";

    private readonly BodyGlobe _globe;
    private readonly WorldOrigin _origin;
    private readonly Layer[] _layers;
    private (float Strength, Vector3 Tint) _lightWritten = (-1f, Vector3.Zero);

    /// <summary>One field round the camera: what is in place and what is being made.</summary>
    private sealed class Layer(ScatterField field, ShaderMaterial material, Aabb[] shapes)
    {
        public readonly ScatterField Field = field;
        public readonly ShaderMaterial Material = material;
        public readonly Aabb[] Shapes = shapes;
        public Node3D? Node;
        public DVec3 Centre;
        public bool Has;
        public int Epoch = -1;
        public Task<Made>? Task;
        public int Built, Drawn, Solid;
    }

    private sealed record Made(DVec3 Centre, List<Transform3D>[] Instances, List<Color>[] Tints, List<(Transform3D At, Vector3 Size, bool Box)> Solid);

    /// <summary>Each field's name, and how many stones it draws and has solid now (the checks' log).</summary>
    public IEnumerable<(string Name, int Built, int Drawn, int Solid)> Counts()
    {
        foreach (var l in _layers) yield return (l.Field.Of.Name, l.Built, l.Drawn, l.Solid);
    }

    /// <summary>Whether the body has any scatter of its own.</summary>
    public bool Any => _layers.Length > 0;

    public BodyScatter(BodyGlobe globe, WorldOrigin origin)
    {
        _globe = globe;
        _origin = origin;
        Name = $"Scatter_{globe.Body.Name}";
        var body = globe.Body;
        var shader = GD.Load<Shader>("res://shaders/body_rock.gdshader");
        var fields = ScatterField.For(body.Id, body.Shape.A, SurfaceProfile.For(body.Id, body.Shape.A));
        _layers = new Layer[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            var kind = fields[i].Of.Kind;
            var m = new ShaderMaterial { Shader = shader };
            m.SetShaderParameter(AirTau, BodyGlobe.AirTauOf(body));
            m.SetShaderParameter(LunarParam, kind == ScatterField.Kind.Rock ? globe.LunarAt(BodyGlobe.GroundGrey) : 0f);
            m.SetShaderParameter(Sheen, kind == ScatterField.Kind.IceBlock ? 0.35f : 0f);
            m.SetShaderParameter(SkyFill, SkyFillOf(body));
            // the shapes' boxes, read here on the main thread (a worker never touches a Godot mesh)
            var boxes = new Aabb[ScatterShapes.Variants];
            for (int s = 0; s < boxes.Length; s++) boxes[s] = ScatterShapes.Mesh(kind, s).GetAabb();
            _layers[i] = new Layer(fields[i], m, boxes);
        }
    }

    public override void _EnterTree() => Active[_globe.Body.Id] = this;

    public override void _ExitTree()
    {
        if (Active.TryGetValue(_globe.Body.Id, out var me) && me == this) Active.Remove(_globe.Body.Id);
    }

    public override void _Process(double delta)
    {
        if (_layers.Length == 0) return;
        foreach (var l in _layers)
            if (l.Task is { IsCompleted: true } done)
            {
                l.Task = null;
                if (done.IsCompletedSuccessfully) Commit(l, done.Result);
                else GD.PrintErr($"[scatter] {l.Field.Of.Name}: {done.Exception?.GetBaseException().Message}");
            }
        if (_origin.Body != _globe.Body.Id || _origin.Frame.Axes is not { } axes || _globe.Ground is not { } ground
            || GetViewport()?.GetCamera3D() is not { } cam)
        {
            foreach (var l in _layers) Drop(l);
            return;
        }
        // the ground's light (#2024), written on a change
        var light = ground.SceneLight;
        if (light != _lightWritten)
        {
            _lightWritten = light;
            foreach (var l in _layers)
            {
                l.Material.SetShaderParameter(LightStrength, light.Strength);
                l.Material.SetShaderParameter(LightTint, light.Tint);
            }
        }
        var heights = ground.Heights;
        var c = cam.GlobalPosition;
        var e = axes.ToEcef(c.X, c.Y, c.Z);
        var geo = _globe.Body.Shape.ToGeodetic(e);
        double groundH = heights.At(heights.NormalUnder(e));
        double over = geo.H - groundH;
        // the build's centre is the ground under the camera: a camera 150 m up still gets the stones solid round its foot
        var foot = _globe.Body.Shape.ToEcef(geo with { H = groundH });
        var p = new DVec3(foot.X, foot.Y, foot.Z);
        foreach (var l in _layers)
        {
            if (l.Node != null && _origin.Epoch != l.Epoch) Place(l, axes);
            // made while the camera is near enough to see them: a few of their reaches over the ground
            if (over > l.Field.Of.Reach * 2.5)
            {
                Drop(l);
                continue;
            }
            if (l.Task != null || (l.Has && (p - l.Centre).Length < l.Field.Of.MoveM)) continue;
            var field = l.Field;
            var body = _globe.Body;
            var shapes = l.Shapes;
            l.Task = Task.Run(() => Make(field, heights, body, p, shapes));
        }
    }

    /// <summary>A field's stones round a body-fixed point: each shape's instances (relative to the point), tints and colliders; on a worker.</summary>
    private static Made Make(ScatterField field, BodyHeights heights, CelestialBody body, DVec3 at, Aabb[] shapes)
    {
        var spec = field.Of;
        var stones = field.Near(at.X, at.Y, at.Z, spec.Reach);
        int count = stones.Count;
        var placed = new (Transform3D T, int Variant, Color Tint, bool Solid, Vector3 Size, bool Box)[count];
        var (rock, spread) = Palette(body.Id, spec);
        Parallel.For(0, count, i =>
        {
            var s = stones[i];
            var n = heights.NormalUnder(new EcefPos(s.X, s.Y, s.Z));
            double dx = s.X - at.X, dy = s.Y - at.Y, dz = s.Z - at.Z;
            double dist = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            // the ground drawn there: the near square's every crater close by, the fine square's past it
            double ground = dist < 160 ? heights.At(n) : heights.At(n, BodyGround.FineCraterM);
            var g = body.Shape.ToGeodetic(new EcefPos(s.X, s.Y, s.Z));
            double r = s.Radius;
            // its base sunk: shapes stand on about −0.6 of their radius, laid a quarter of it under the ground
            var place = body.Shape.ToEcef(g with { H = ground + r * 0.35 });
            var up = new Vector3((float)n.X, (float)n.Y, (float)n.Z).Normalized();
            ulong h = s.Shape;
            int variant = (int)(h % ScatterShapes.Variants);
            float yaw = (h >> 8 & 0xFFFF) / 65535f * Mathf.Tau, tilt = (h >> 24 & 0xFF) / 255f * 0.25f;
            var across = Mathf.Abs(up.Z) < 0.9f ? up.Cross(Vector3.Back).Normalized() : up.Cross(Vector3.Right).Normalized();
            var frame = new Basis(across, up, across.Cross(up)).Orthonormalized();
            frame = new Basis(up, yaw) * new Basis(frame.X, tilt) * frame;
            float rr = (float)r * (0.9f + 0.2f * ((h >> 32 & 0xFF) / 255f));
            var local = new Vector3((float)(place.X - at.X), (float)(place.Y - at.Y), (float)(place.Z - at.Z));
            float v = 1f - spread + 2f * spread * ((h >> 48 & 0xFF) / 255f);
            var tint = new Color(rock.R * v, rock.G * v, rock.B * v);
            bool solid = spec.SolidReach > 0 && r >= spec.SolidFrom && local.Length() <= spec.SolidReach;
            var box = shapes[variant];
            placed[i] = (new Transform3D(frame * Basis.FromScale(Vector3.One * rr), local), variant, tint, solid,
                box.Size * rr, spec.Kind == ScatterField.Kind.IceBlock);
        });
        var instances = new List<Transform3D>[ScatterShapes.Variants];
        var tints = new List<Color>[ScatterShapes.Variants];
        for (int i = 0; i < instances.Length; i++) { instances[i] = new(); tints[i] = new(); }
        var solidList = new List<(Transform3D, Vector3, bool)>();
        foreach (var s in placed)
        {
            instances[s.Variant].Add(s.T);
            tints[s.Variant].Add(s.Tint);
            if (!s.Solid) continue;
            // the collider's own transform: unscaled, at the shape's box centre
            var turn = s.T.Basis.Orthonormalized();
            var centre = s.T * shapes[s.Variant].GetCenter();
            solidList.Add((new Transform3D(turn, centre), s.Size, s.Box));
        }
        return new Made(at, instances, tints, solidList);
    }

    /// <summary>The sky's fill on a stone's shadow side, a share of the direct light: a body with air (Mars, Titan, Venus) some, else none.</summary>
    public static float SkyFillOf(CelestialBody body) => body.Air != null ? 0.18f : 0f;

    /// <summary>A body's stone colour for a field (linear, before the light), and how much a stone's own varies (±).</summary>
    private static (Color Rock, float Spread) Palette(BodyId body, ScatterField.Spec spec) => (body, spec.Kind) switch
    {
        // Mars: dark basalt under a coat of its dust, the pebbles dustier
        (BodyId.Mars, _) when spec.Name == "rocks" => (new Color(0.26f, 0.16f, 0.11f), 0.25f),
        (BodyId.Mars, _) => (new Color(0.33f, 0.2f, 0.14f), 0.15f),
        (BodyId.Europa, _) => (new Color(0.8f, 0.82f, 0.84f), 0.08f),
        (BodyId.Ganymede, _) => (new Color(0.62f, 0.6f, 0.57f), 0.12f),
        (BodyId.Titan, _) => (new Color(0.66f, 0.58f, 0.46f), 0.1f),
        (BodyId.Callisto, _) => (new Color(0.33f, 0.31f, 0.29f), 0.15f),
        // the regolith's grey (the Moon, Mercury, Phobos, Deimos)
        _ => (new Color(0.4f, 0.39f, 0.38f), 0.15f),
    };

    /// <summary>The world centre of the solid stone nearest a world point, and its collider's half size (null: none solid).</summary>
    public (Vector3 At, float Radius, string Field)? NearestSolid(Vector3 world)
    {
        (Vector3, float, string)? best = null;
        float bestD = float.MaxValue;
        foreach (var l in _layers)
        {
            if (l.Node?.GetNodeOrNull<StaticBody3D>("Solid") is not { } solid) continue;
            foreach (var child in solid.GetChildren())
                if (child is CollisionShape3D shape && shape.GlobalPosition.DistanceTo(world) is var d && d < bestD)
                {
                    bestD = d;
                    float r = shape.Shape switch { SphereShape3D sp => sp.Radius, BoxShape3D b => b.Size.Y / 2, _ => 0 };
                    best = (shape.GlobalPosition, r, l.Field.Of.Name);
                }
        }
        return best;
    }

    /// <summary>Main thread: a layer's new stones in (drawn, solid), then the old ones out.</summary>
    private void Commit(Layer l, Made m)
    {
        if (_origin.Body != _globe.Body.Id || _origin.Frame.Axes is not { } axes) return;
        var node = new Node3D { Name = l.Field.Of.Name };
        var kind = l.Field.Of.Kind;
        // the small stones cast no shadow: a few centimetres, thousands of them
        bool casts = l.Field.Of.MaxRadius > 0.3;
        int drawn = 0;
        for (int i = 0; i < ScatterShapes.Variants; i++)
        {
            var list = m.Instances[i];
            if (list.Count == 0) continue;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
                Mesh = ScatterShapes.Mesh(kind, i), InstanceCount = list.Count,
            };
            for (int k = 0; k < list.Count; k++)
            {
                mm.SetInstanceTransform(k, list[k]);
                mm.SetInstanceColor(k, m.Tints[i][k]);
            }
            node.AddChild(new MultiMeshInstance3D
            {
                Name = $"Shape{i}", Multimesh = mm, MaterialOverride = l.Material,
                CastShadow = casts ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                // the instances' box is round the field's centre: never culled by Godot's guess
                ExtraCullMargin = (float)l.Field.Of.Reach,
            });
            drawn += list.Count;
        }
        var body = new StaticBody3D { Name = "Solid" };
        foreach (var (at, size, box) in m.Solid)
            body.AddChild(new CollisionShape3D
            {
                // a rock: a sphere a little inside its faces; an ice block: its box
                Shape = box ? new BoxShape3D { Size = size * 0.92f } : new SphereShape3D { Radius = Mathf.Min(size.X, size.Z) * 0.42f },
                Transform = at,
            });
        node.AddChild(body);
        var old = l.Node;
        l.Node = node;
        l.Centre = m.Centre;
        l.Has = true;
        AddChild(node);
        Place(l, axes);
        old?.QueueFree();
        l.Built++;
        l.Drawn = drawn;
        l.Solid = m.Solid.Count;
        if (l.Built == 1 || l.Built % 20 == 0)
            GD.Print(System.FormattableString.Invariant(
                $"[scatter] {_globe.Body.Name} {l.Field.Of.Name} {l.Built}: {drawn} within {l.Field.Of.Reach:F0} m, {m.Solid.Count} solid within {l.Field.Of.SolidReach:F0} m"));
    }

    /// <summary>A layer from the body's frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(Layer l, FrameAxes axes)
    {
        if (l.Node == null) return;
        l.Epoch = _origin.Epoch;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(new EcefPos(l.Centre.X, l.Centre.Y, l.Centre.Z));
        l.Node.GlobalTransform = new Transform3D(new Basis(Turn(new EcefPos(1, 0, 0)), Turn(new EcefPos(0, 1, 0)), Turn(new EcefPos(0, 0, 1))),
            new Vector3((float)ox, (float)oy, (float)oz));
    }

    private static void Drop(Layer l)
    {
        l.Node?.QueueFree();
        l.Node = null;
        l.Has = false;
        l.Drawn = l.Solid = 0;
    }
}
