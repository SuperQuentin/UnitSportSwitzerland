using Godot;

namespace UnitSport.Avatar;

/// <summary>The silhouette: where the glass is and how the roof runs into the tail.</summary>
public enum BodyShape
{
    /// <summary>Three-door hatchback, roof carried back to a near-vertical hatch (AE86 hatch, Civic).</summary>
    Hatchback,
    /// <summary>Two-door notchback: short roof, separate boot (AE86 coupe, S13, Integra coupe).</summary>
    Coupe,
    /// <summary>Long raked glass hatch down to a short tail (RX-7s, 180SX, Supra, GT-R R32 is a Coupe).</summary>
    Fastback,
    /// <summary>Four doors and a boot (Impreza, Lancer Evolution).</summary>
    Sedan,
    /// <summary>Open two-seater, no roof (Roadster, S2000).</summary>
    Roadster,
    /// <summary>Mid-engined: cab forward, long rear deck (MR2, NSX).</summary>
    Midship,
}

/// <summary>A rear wing, from none to a GT wing on tall stands.</summary>
public enum WingSize { None, Lip, Small, Big, Gt }

/// <summary>
/// How the doors open: front-hinged and swinging out (every real car in the catalog), rear-hinged
/// (suicide), up about the front hinge (scissor, the Lambo), up and out (butterfly), or up about
/// the roof (gull-wing). A four-door's rear doors only ever swing out, front- or rear-hinged.
/// </summary>
public enum DoorStyle { Conventional, Suicide, Scissor, Butterfly, GullWing }

/// <summary>
/// Everything a <see cref="CarRig"/> needs to draw one car: shape, real dimensions and livery.
/// Lengths in metres; colours authored in sRGB like every palette here.
/// </summary>
public sealed record CarBody
{
    public BodyShape Shape { get; init; } = BodyShape.Coupe;
    public float Length { get; init; } = 4.3f;
    public float Width { get; init; } = 1.7f;
    public float Height { get; init; } = 1.3f;
    public float WheelRadius { get; init; } = 0.3f;
    public Color Paint { get; init; } = new(0.9f, 0.9f, 0.9f);
    /// <summary>Two-tone: the lower body, sills and bumpers (the AE86 "panda" black). Null = paint.</summary>
    public Color? Lower { get; init; }
    /// <summary>A bonnet in another colour (carbon, primer). Null = paint.</summary>
    public Color? Bonnet { get; init; }
    public Color Rim { get; init; } = new(0.78f, 0.79f, 0.82f);
    public bool PopUps { get; init; }
    public WingSize Wing { get; init; }
    public bool Scoop { get; init; }

    // ---- garage parts (Player/CarTuning); the defaults are the catalog look ----
    /// <summary>Under the front bumper: 0 nothing, 1 a lip, 2 a splitter.</summary>
    public int FrontAero { get; init; }
    public bool Diffuser { get; init; }
    public bool Skirts { get; init; }
    /// <summary>How much lower than stock the body sits, m; the wheels stay where they are.</summary>
    public float Drop { get; init; }
    /// <summary>Window glass. Null = the plain blue-grey glass.</summary>
    public Color? Glass { get; init; }
    /// <summary>Neon under the car. Null = none.</summary>
    public Color? Underglow { get; init; }
    public DoorStyle Doors { get; init; }
    /// <summary>Bigger rims in the same rolling radius (a thinner sidewall): 0 stock, 1, 2.</summary>
    public int RimSize { get; init; }
    /// <summary>Wide, low racing slicks.</summary>
    public bool Slicks { get; init; }
}

/// <summary>
/// A drivable car's visual: body, four wheels that turn and steer, brake lights, doors on hinges
/// that swing open (<see cref="DoorsOpen"/>). Origin on the
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
    /// <summary>Which doors are open, one bit each (<see cref="DoorLeft"/> .. <see cref="DoorRearRight"/>); each eases there.</summary>
    public byte DoorsOpen { get; set; }

    public const byte DoorLeft = 1, DoorRight = 2, DoorRearLeft = 4, DoorRearRight = 8;
    /// <summary>These are right-hand-drive cars: the driver gets in and out on the right.</summary>
    public const byte DriverDoor = DoorRight;
    /// <summary>Seconds for a door to swing fully open or shut.</summary>
    private const float DoorTime = 0.4f;

    private Node3D _body = null!;
    private CarDoor[] _doors = System.Array.Empty<CarDoor>();
    private Node3D[] _doorPivots = System.Array.Empty<Node3D>();
    private float[] _doorOpen = System.Array.Empty<float>();
    private readonly Node3D[] _steer = new Node3D[4];   // FL, FR, RL, RR
    private readonly Node3D[] _spin = new Node3D[4];
    private StandardMaterial3D _tailMaterial = null!;

    public static CarRig Create(CarBody body, float wheelbase)
    {
        var rig = new CarRig { Name = "Car" };
        rig.Assemble(CarMeshBuilder.Build(body, wheelbase));
        return rig;
    }

    private void Assemble(CarParts p)
    {
        var body = HumanMeshBuilder.Material();
        var lamp = TrafficMeshBuilder.LampMaterial();
        _tailMaterial = TrafficMeshBuilder.LampMaterial();

        // pitch about the middle of the car at roughly the centre of mass height
        // (lowered: the whole body drops, the wheels stay on the road)
        _body = new Node3D { Name = "Body", Position = new Vector3(0, 0.5f - p.Drop, 0) };
        AddChild(_body);
        var offset = new Vector3(0, -0.5f, 0);
        _body.AddChild(new MeshInstance3D { Name = "Shell", Mesh = p.Body, MaterialOverride = body, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = lamp, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = _tailMaterial, Position = offset });
        if (p.Glow is { } glow)
            // the neon strip is in the lamp mesh; this is the pool of its colour on the road
            _body.AddChild(new OmniLight3D
            {
                Name = "Underglow", LightColor = glow, LightEnergy = 1.5f, OmniRange = 2.6f,
                Position = new Vector3(0, -0.3f, 0),
            });

        // each door on its own hinge, named like the wheels (deterministic, the same on every peer)
        _doors = p.Doors;
        _doorPivots = new Node3D[_doors.Length];
        _doorOpen = new float[_doors.Length];
        for (int i = 0; i < _doors.Length; i++)
        {
            var pivot = new Node3D { Name = _doors[i].Name, Position = _doors[i].Hinge + offset };
            pivot.AddChild(new MeshInstance3D { Mesh = _doors[i].Mesh, MaterialOverride = body });
            _body.AddChild(pivot);
            _doorPivots[i] = pivot;
        }

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

    /// <summary>The bit of the door whose middle is nearest a world point, and how far it is.</summary>
    public (byte Bit, float Distance) NearestDoor(Vector3 point)
    {
        (byte Bit, float Distance) best = (0, float.MaxValue);
        foreach (var door in _doors)
        {
            float dist = _body.ToGlobal(door.Centre + new Vector3(0, -0.5f, 0)).DistanceTo(point);
            if (dist < best.Distance) best = (door.Bit, dist);
        }
        return best;
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
        float step = (float)delta / DoorTime;
        for (int i = 0; i < _doors.Length; i++)
        {
            float target = (DoorsOpen & _doors[i].Bit) != 0 ? 1f : 0f;
            if (_doorOpen[i] == target) continue;
            _doorOpen[i] = Mathf.MoveToward(_doorOpen[i], target, step);
            _doorPivots[i].Quaternion = Quaternion.Identity.Slerp(_doors[i].Open, Mathf.SmoothStep(0f, 1f, _doorOpen[i]));
        }
        ApplyTail();
    }
}
