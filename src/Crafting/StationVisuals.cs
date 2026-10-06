using Godot;
using UnitSport.Avatar;
using UnitSport.Items;

namespace UnitSport.Crafting;

/// <summary>
/// How the placed stations look (#272): the factories <see cref="PlacedObjects"/> draws
/// <see cref="PlacedKind.Campfire"/> and <see cref="PlacedKind.FieldWorkbench"/> with, and the meshes
/// the placing ghost shows. Low-poly <see cref="MeshScratch"/> boxes and tubes, origin on the ground.
/// </summary>
public static class StationVisuals
{
    /// <summary>The name of a burning campfire's light: probes look for it.</summary>
    public const string LightName = "FireLight";

    private static ArrayMesh? _fire, _ashes, _bench;

    /// <summary>A ring of stones around a teepee of logs.</summary>
    public static ArrayMesh CampfireMesh()
    {
        if (_fire != null) return _fire;
        var s = new MeshScratch();
        AppendStones(s);
        var wood = new Color(0.42f, 0.27f, 0.14f);
        for (int i = 0; i < 5; i++)
        {
            float a = i * Mathf.Tau / 5f + 0.3f;
            s.Tube(new Vector3(Mathf.Cos(a) * 0.30f, 0.03f, Mathf.Sin(a) * 0.30f), new Vector3(0, 0.42f, 0), 0.045f, 0.03f, wood, 6);
        }
        s.Box(new Vector3(0, 0.03f, 0), new Vector3(0.36f, 0.04f, 0.36f), new Color(0.12f, 0.10f, 0.09f));   // embers bed
        return _fire = s.Build();
    }

    /// <summary>What is left: the stones, a grey bed and two charred logs lying down.</summary>
    public static ArrayMesh AshesMesh()
    {
        if (_ashes != null) return _ashes;
        var s = new MeshScratch();
        AppendStones(s);
        s.Box(new Vector3(0, 0.02f, 0), new Vector3(0.6f, 0.03f, 0.6f), new Color(0.45f, 0.44f, 0.42f));
        var charred = new Color(0.10f, 0.09f, 0.08f);
        s.Tube(new Vector3(-0.25f, 0.06f, -0.08f), new Vector3(0.22f, 0.06f, 0.10f), 0.04f, charred, 6);
        s.Tube(new Vector3(-0.12f, 0.06f, 0.22f), new Vector3(0.10f, 0.06f, -0.24f), 0.035f, charred, 6);
        return _ashes = s.Build();
    }

    private static void AppendStones(MeshScratch s)
    {
        for (int i = 0; i < 9; i++)
        {
            float a = i * Mathf.Tau / 9f;
            float shade = 0.42f + 0.08f * ((i * 37) % 5) / 4f;
            s.Box(new Vector3(Mathf.Cos(a) * 0.48f, 0.07f, Mathf.Sin(a) * 0.48f), new Vector3(0.17f, 0.14f, 0.13f),
                new Color(shade, shade, shade * 0.95f), new Basis(Vector3.Up, -a));
        }
    }

    /// <summary>A small wooden bench: a thick top with a vice, four legs, a shelf under it.</summary>
    public static ArrayMesh WorkbenchMesh()
    {
        if (_bench != null) return _bench;
        var s = new MeshScratch();
        var top = new Color(0.66f, 0.46f, 0.25f);
        var leg = new Color(0.48f, 0.32f, 0.17f);
        s.Box(new Vector3(0, 0.84f, 0), new Vector3(1.3f, 0.07f, 0.62f), top);
        foreach (float x in new[] { -0.58f, 0.58f })
            foreach (float z in new[] { -0.25f, 0.25f })
                s.Box(new Vector3(x, 0.40f, z), new Vector3(0.07f, 0.80f, 0.07f), leg);
        s.Box(new Vector3(0, 0.22f, 0), new Vector3(1.2f, 0.04f, 0.52f), leg);   // the shelf
        var steel = new Color(0.30f, 0.32f, 0.35f);
        s.Box(new Vector3(0.48f, 0.93f, -0.24f), new Vector3(0.18f, 0.10f, 0.12f), steel);   // the vice
        s.Tube(new Vector3(0.48f, 0.93f, -0.30f), new Vector3(0.48f, 0.93f, -0.40f), 0.012f, steel, 6);
        s.Box(new Vector3(-0.3f, 0.89f, 0.1f), new Vector3(0.28f, 0.03f, 0.05f), new Color(0.75f, 0.12f, 0.1f));   // a saw's handle, for colour
        return _bench = s.Build();
    }

    /// <summary>The factory of <see cref="PlacedKind.Campfire"/>: burning or ashes, by the clock in its payload.</summary>
    public static Node3D Campfire(PlacedObject o) => new CampfireNode(o.Payload);

    /// <summary>The factory of <see cref="PlacedKind.FieldWorkbench"/>.</summary>
    public static Node3D Workbench(PlacedObject o)
    {
        var body = new StaticBody3D();
        body.AddChild(new MeshInstance3D { Mesh = WorkbenchMesh(), MaterialOverride = ItemDefs.Material });
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.3f, 0.9f, 0.62f) },
            Position = new Vector3(0, 0.45f, 0),
        });
        return body;
    }
}

/// <summary>
/// A placed campfire: logs, a flickering warm light, flames and smoke while it burns
/// (<see cref="CampfireClock"/>), then ashes. Checks the clock once a second.
/// </summary>
public partial class CampfireNode : StaticBody3D
{
    private readonly string _payload;
    private MeshInstance3D _mesh = null!;
    private Node3D? _burning;
    private OmniLight3D? _light;
    private float _check, _t;
    private readonly float _phase = GD.Randf() * 10f;

    public CampfireNode(string payload) => _payload = payload;
    public CampfireNode() : this("") { }

    /// <summary>Burning right now, as drawn.</summary>
    public bool Burning => _burning != null;

    public override void _Ready()
    {
        _mesh = new MeshInstance3D { MaterialOverride = ItemDefs.Material };
        AddChild(_mesh);
        // low, so it is something to aim at and to walk round rather than a wall
        AddChild(new CollisionShape3D
        {
            Shape = new CylinderShape3D { Radius = 0.58f, Height = 0.3f },
            Position = new Vector3(0, 0.15f, 0),
        });
        Refresh();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        _check -= dt;
        if (_check <= 0f)
        {
            _check = 1f;
            Refresh();
        }
        if (_light != null)
            _light.LightEnergy = 1.7f + 0.35f * Mathf.Sin(_t * 11f + _phase) + 0.2f * Mathf.Sin(_t * 23f + _phase * 2f);
    }

    private void Refresh()
    {
        bool burning = CampfireClock.Burning(_payload, World.WorldClock.EnvNow);
        if (burning == (_burning != null) && _mesh.Mesh != null) return;
        _mesh.Mesh = burning ? StationVisuals.CampfireMesh() : StationVisuals.AshesMesh();
        if (!burning)
        {
            _burning?.QueueFree();
            _burning = null;
            _light = null;
            return;
        }
        _burning = new Node3D { Name = "Fire" };
        _light = new OmniLight3D
        {
            Name = StationVisuals.LightName, Position = new Vector3(0, 0.6f, 0),
            LightColor = new Color(1f, 0.62f, 0.28f), LightEnergy = 1.7f, OmniRange = 9f, ShadowEnabled = false,
        };
        _burning.AddChild(_light);
        _burning.AddChild(Flames());
        _burning.AddChild(Smoke());
        AddChild(_burning);
    }

    private static CpuParticles3D Flames()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 0.92f, 0.45f, 1f));
        ramp.SetColor(1, new Color(0.9f, 0.18f, 0.05f, 0f));
        ramp.AddPoint(0.4f, new Color(1f, 0.55f, 0.12f, 0.9f));
        return new CpuParticles3D
        {
            Name = "Flames", Amount = 26, Lifetime = 0.7f, Position = new Vector3(0, 0.12f, 0),
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.14f,
            Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = 0.5f, InitialVelocityMax = 1.1f,
            Gravity = new Vector3(0, 0.6f, 0), ScaleAmountMin = 0.6f, ScaleAmountMax = 1.2f, ColorRamp = ramp,
            Mesh = new QuadMesh
            {
                Size = new Vector2(0.2f, 0.26f),
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                },
            },
        };
    }

    private static CpuParticles3D Smoke()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.35f, 0.33f, 0.32f, 0.55f));
        ramp.SetColor(1, new Color(0.6f, 0.6f, 0.6f, 0f));
        return new CpuParticles3D
        {
            Name = "Smoke", Amount = 14, Lifetime = 3.5f, Position = new Vector3(0, 0.6f, 0),
            Direction = Vector3.Up, Spread = 10f, InitialVelocityMin = 0.5f, InitialVelocityMax = 0.9f,
            Gravity = new Vector3(0.15f, 0.25f, 0), ScaleAmountMin = 0.5f, ScaleAmountMax = 1.4f, ColorRamp = ramp,
            Mesh = new QuadMesh
            {
                Size = new Vector2(0.5f, 0.5f),
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            },
        };
    }
}
