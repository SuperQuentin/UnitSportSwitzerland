using Godot;

namespace UnitSport.Avatar;

/// <summary>The body shape a <see cref="CarRig"/> builds.</summary>
public enum CarStyle { Coupe86, RotaryFd, Rally4wd }

/// <summary>
/// A drivable car's visual: body, four wheels that turn and steer, brake lights. Origin on the
/// ground under the middle of the wheelbase, facing −Z like every node (built +Z, turned by
/// <see cref="MeshScratch.Build"/>). The owner sets the properties; the rig applies them in
/// <c>_Process</c>.
/// </summary>
public partial class CarRig : Node3D
{
    /// <summary>Front road-wheel angle, radians, + = left.</summary>
    public float SteerAngle { get; set; }
    /// <summary>Accumulated wheel rotation, radians; positive rolls forward.</summary>
    public float WheelSpin { get; set; }
    /// <summary>Body pitch, radians, + = nose up (squat under power, dive under braking is −).</summary>
    public float BodyPitch { get; set; }
    public bool BrakeLights { get; set; }

    private Node3D _body = null!;
    private readonly Node3D[] _steer = new Node3D[4];   // FL, FR, RL, RR
    private readonly Node3D[] _spin = new Node3D[4];
    private StandardMaterial3D _tailMaterial = null!;

    public static CarRig Create(CarStyle style, Color paint)
    {
        var rig = new CarRig { Name = "Car" };
        rig.Assemble(CarMeshBuilder.Build(style, paint));
        return rig;
    }

    private void Assemble(CarParts p)
    {
        var body = HumanMeshBuilder.Material();
        var lamp = TrafficMeshBuilder.LampMaterial();
        _tailMaterial = TrafficMeshBuilder.LampMaterial();

        // pitch about the middle of the car at roughly the centre of mass height
        _body = new Node3D { Name = "Body", Position = new Vector3(0, 0.5f, 0) };
        AddChild(_body);
        var offset = new Vector3(0, -0.5f, 0);
        _body.AddChild(new MeshInstance3D { Name = "Shell", Mesh = p.Body, MaterialOverride = body, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = lamp, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = _tailMaterial, Position = offset });

        string[] names = { "FL", "FR", "RL", "RR" };
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2, left = i % 2 == 0;
            // node space faces −Z, so the front axle is at −Z and left is −X
            var pivot = new Node3D
            {
                Name = "Wheel" + names[i],
                Position = new Vector3((left ? -1f : 1f) * p.HalfTrack, p.WheelRadius, front ? -p.FrontAxleZ : -p.RearAxleZ),
            };
            AddChild(pivot);
            var spin = new Node3D { Name = "Spin" };
            spin.AddChild(new MeshInstance3D { Mesh = p.Wheel, MaterialOverride = body });
            pivot.AddChild(spin);
            _steer[i] = pivot;
            _spin[i] = spin;
        }
        ApplyTail();
    }

    private void ApplyTail() =>
        _tailMaterial.AlbedoColor = BrakeLights ? Colors.White : new Color(0.42f, 0.42f, 0.42f);

    public override void _Process(double delta)
    {
        if (_body == null) return;
        _body.Rotation = new Vector3(BodyPitch, 0, 0);   // + rotates −Z (the nose) up
        for (int i = 0; i < 4; i++)
        {
            _steer[i].Rotation = new Vector3(0, i < 2 ? SteerAngle : 0f, 0);   // + yaw turns −Z toward −X: left
            _spin[i].Rotation = new Vector3(-WheelSpin, 0, 0);                  // top edge moves toward −Z, forward
        }
        ApplyTail();
    }
}
