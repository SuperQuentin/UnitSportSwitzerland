using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions;

namespace UnitSport.Avatar;

/// <summary>
/// Interactive viewer for the procedurally built models, one at a time on a plain backdrop.
///
/// <para>
/// <c>godot --path . -- --models</c> — Left/Right (or A/D) switch model, drag orbits, wheel zooms.
/// </para>
/// </summary>
public partial class ModelViewer : Node3D
{
    private static readonly (string Name, Func<Node3D> Make)[] Models =
    {
        ("Human - standing", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(0)))),
        ("Human - running", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(2), HumanPose.Running))),
        ("Human - tucked", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(3), HumanPose.Tucked))),
        ("Human - spread", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(5), HumanPose.Spread))),
        ("Road bike", () => Mesh(BikeMeshBuilder.Build())),
        ("Cyclist", () => { var c = Cyclist.Create(4); c.CadenceRpm = 78; return c; }),
        ("Skier", () => Mesh(SkierMeshBuilder.BuildSkier(HumanPalette.ForRider(6), SkiPalette.ForRider(6)))),
        ("Wingsuit", () => Mesh(AircraftMeshBuilder.Wingsuit(HumanPalette.ForRider(1)))),
        ("Parachute", () => Mesh(AircraftMeshBuilder.Canopy(HumanPalette.ForRider(1), paraglider: false))),
        ("Paraglider", () => Mesh(AircraftMeshBuilder.Canopy(HumanPalette.ForRider(1), paraglider: true))),
        ("Helicopter", () => Mesh(AircraftMeshBuilder.Helicopter(new Color(0.8f, 0.1f, 0.1f)))),
        ("Plane", () => Mesh(AircraftMeshBuilder.Plane(new Color(0.9f, 0.9f, 0.88f), new Color(0.8f, 0.1f, 0.1f)))),
        ("Car", () => Traffic(TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[0], van: false))),
        ("Van", () => Traffic(TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[1], van: true))),
        ("Train carriage", () => Traffic(TrafficMeshBuilder.Carriage(
            new Color(0.78f, 0.1f, 0.1f), new Color(0.95f, 0.95f, 0.95f), 18f, false, true, false))),

        // occasions (#18)
        ("Hat - witch", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(1), hat: Headwear.WitchHat))),
        ("Hat - pumpkin head", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(2), hat: Headwear.PumpkinHead))),
        ("Hat - Santa", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(3), hat: Headwear.SantaHat))),
        ("Hat - reindeer antlers", () => Mesh(HumanMeshBuilder.Build(HumanPalette.ForRider(4), hat: Headwear.ReindeerAntlers))),
        ("Jack-o'-lantern", () => Prop(HalloweenOccasion.Lantern)),
        ("Field pumpkin", () => Prop(HalloweenOccasion.FieldPumpkin)),
        ("Treat bowl", () => Prop(HalloweenOccasion.TreatBowl)),

        // held items (the rest are a plain box in the item's colour)
        ("Item - binoculars", () => Item(ItemId.Binoculars)),
        ("Item - camera", () => Item(ItemId.Camera)),
        ("Item - GPS", () => Item(ItemId.Gps)),
        ("Item - Swiss flag", () => Item(ItemId.SwissFlag)),
        ("Item - energy bar", () => Item(ItemId.EnergyBar)),
        ("Item - water bottle", () => Item(ItemId.WaterBottle)),
        ("Planted flag", () => Mesh(ItemDefs.PlantedFlagMesh())),
    };

    private static readonly StandardMaterial3D Body = HumanMeshBuilder.Material();

    private int _index;
    private Node3D? _model;
    private Camera3D _camera = null!;
    private Label _label = null!;
    private Vector3 _target;
    private float _yaw = 0.6f, _pitch = -0.25f, _distance = 5f;

    public static bool Requested() => CmdArgs.Has("--models");

    public override void _Ready()
    {
        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(Mathf.DegToRad(-42), Mathf.DegToRad(-35), 0),
            LightEnergy = 1.1f,
        });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.44f, 0.50f, 0.56f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.62f),
                AmbientLightEnergy = 0.85f,
            },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(40, 40) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.38f, 0.31f) },
        });

        _camera = new Camera3D { Fov = 40, Current = true };
        AddChild(_camera);

        var ui = new CanvasLayer();
        _label = new Label { Position = new Vector2(12, 8) };
        ui.AddChild(_label);
        AddChild(ui);

        Display(0);
    }

    private void Display(int index)
    {
        _index = (index % Models.Length + Models.Length) % Models.Length;
        _model?.QueueFree();
        _model = Models[_index].Make();
        AddChild(_model);   // Cyclist builds its meshes in _Ready, so frame after adding

        var box = new Aabb();
        bool first = true;
        foreach (var mi in _model.FindChildren("*", nameof(MeshInstance3D), true, false)
                     .Cast<MeshInstance3D>().Prepend(_model as MeshInstance3D).OfType<MeshInstance3D>())
        {
            var b = mi.Transform * mi.GetAabb();
            box = first ? b : box.Merge(b);
            first = false;
        }
        _target = box.GetCenter();
        _distance = Mathf.Max(box.Size.Length() * 1.4f, 0.3f);

        _label.Text = $"{Models[_index].Name}   ({_index + 1}/{Models.Length})   " +
                      $"{box.Size.X:0.00} x {box.Size.Y:0.00} x {box.Size.Z:0.00} m\n" +
                      "Left/Right: switch   drag: orbit   wheel: zoom";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Right or Key.D:
                Display(_index + 1); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Left or Key.A:
                Display(_index - 1); break;
            case InputEventMouseMotion m when (m.ButtonMask & MouseButtonMask.Left) != 0:
                _yaw -= m.Relative.X * 0.01f;
                _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.01f, -1.5f, 1.5f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _distance *= 0.9f; break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _distance *= 1.1f; break;
        }
    }

    public override void _Process(double delta)
    {
        var offset = new Vector3(0, 0, _distance).Rotated(Vector3.Right, _pitch).Rotated(Vector3.Up, _yaw);
        _camera.Position = _target + offset;
        // built by hand rather than LookAt: right = forward x up, see the handedness gotcha
        var forward = -offset.Normalized();
        var right = forward.Cross(Vector3.Up).Normalized();
        _camera.Basis = new Basis(right, right.Cross(forward), -forward);
    }

    private static MeshInstance3D Mesh(ArrayMesh mesh) => new() { Mesh = mesh, MaterialOverride = Body };

    private static MeshInstance3D Item(ItemId id) => Mesh(ItemDefs.HandMesh(id)!);

    // same shader the occasions place props with; vertex alpha marks the parts that glow
    private static MeshInstance3D Prop(PropKind kind)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ps1_prop.gdshader") };
        material.SetShaderParameter("flicker", kind.Candle ? 1f : 0f);
        FogUniforms.Apply(material);
        return new MeshInstance3D { Mesh = kind.Mesh, MaterialOverride = material };
    }

    private static Node3D Traffic((ArrayMesh Body, ArrayMesh Lamps) m)
    {
        var node = Mesh(m.Body);
        node.AddChild(new MeshInstance3D { Mesh = m.Lamps, MaterialOverride = TrafficMeshBuilder.LampMaterial() });
        return node;
    }
}
