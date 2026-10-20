using Godot;
using static UnitSport.Avatar.UfoMeshBuilder;

namespace UnitSport.Avatar;

/// <summary>
/// The mothership (#861): the saucer's shape at ~120 m (<see cref="UfoMeshBuilder.MothershipHull"/>),
/// scenery and an event prop for the UFO sightings (#863), not a mount. Its lit windows and rim
/// lamps turn slowly round it; <see cref="Beam"/> hangs the saucer's beam from its docking bay, at
/// the mothership's scale. No collision: it is something in the sky.
/// </summary>
public partial class MothershipRig : Node3D
{
    /// <summary>Turns of the lamp ring per second, rad/s.</summary>
    public float Spin { get; set; } = 0.08f;

    private Node3D _lights = null!;
    private MeshInstance3D _beam = null!;

    /// <summary>The docking bay's beam, at the mothership's scale.</summary>
    public bool Beam
    {
        get => _beam.Visible;
        set => _beam.Visible = value;
    }

    public static MothershipRig Create()
    {
        var rig = new MothershipRig { Name = "Mothership" };
        rig.AddChild(new MeshInstance3D { Name = "Hull", Mesh = HullMesh, MaterialOverride = HumanMeshBuilder.Material() });
        rig._lights = new Node3D { Name = "Lights" };
        rig._lights.AddChild(new MeshInstance3D { Mesh = LightsMesh, MaterialOverride = Lamps });
        rig.AddChild(rig._lights);
        rig.AddChild(rig._beam = new MeshInstance3D
        {
            Name = "Beam", Mesh = UfoRig.BeamMeshShared, MaterialOverride = UfoRig.BeamMaterial, Visible = false,
            Position = new Vector3(0, (Belly - 0.1f) * MothershipScale, 0), Scale = Vector3.One * 3f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return rig;
    }

    public override void _Process(double delta) => _lights.RotateY(Spin * (float)delta);

    private static ArrayMesh? _hull, _lightsMesh;
    private static ArrayMesh HullMesh => _hull ??= MothershipHull();
    private static ArrayMesh LightsMesh => _lightsMesh ??= MothershipLights();
    private static StandardMaterial3D? _lamps;
    private static StandardMaterial3D Lamps => _lamps ??= TrafficMeshBuilder.LampMaterial();
}
