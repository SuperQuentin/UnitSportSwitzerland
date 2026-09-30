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
}

/// <summary>
/// A drivable car's visual: body, four wheels that turn and steer, brake lights, headlights, and
/// on the cars that have them a soft top that folds and pop-up headlights. Origin on the
/// ground under the middle of the wheelbase, facing −Z like every node (built +Z, turned by
/// <see cref="MeshScratch.Build()"/>). The owner sets the properties; the rig applies them in
/// <c>_Process</c>, the roof and the pods moving there over a moment rather than snapping —
/// except on the first frame, so a car built with its top down does not fold it in front of you.
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
    /// <summary>Headlights on. On a car with pop-ups this also raises them.</summary>
    public bool Headlights { get; set; }
    /// <summary>Soft top down (and side glass wound down). Nothing on a car with a fixed roof.</summary>
    public bool RoofOpen { get; set; }

    /// <summary>Seconds for the top to fold and the pods to rise.</summary>
    private const float RoofTime = 2.2f, FlapTime = 0.6f;
    /// <summary>Of the roof's travel, where the side glass is fully down, and where the top starts to fold.</summary>
    private const float WindowsDown = 0.35f, FoldFrom = 0.25f;
    /// <summary>What is left of the top's height and length, folded behind the seats.</summary>
    private const float FoldedHeight = 0.22f, FoldedLength = 0.28f;
    /// <summary>What is left of a pod's height folded onto the nose: a lid about a centimetre thick.</summary>
    private const float FoldedPod = 0.08f;

    private Node3D _body = null!;
    private readonly Node3D[] _steer = new Node3D[4];   // FL, FR, RL, RR
    private readonly Node3D[] _spin = new Node3D[4];
    private StandardMaterial3D _tailMaterial = null!, _headMaterial = null!;
    private Node3D? _top, _windows, _cockpit, _flaps, _flapLamps;
    private float _roof, _pods;   // 0 closed .. 1 open
    private bool _settled;

    public static CarRig Create(CarBody body, float wheelbase)
    {
        var rig = new CarRig { Name = "Car" };
        rig.Assemble(CarMeshBuilder.Build(body, wheelbase));
        return rig;
    }

    private void Assemble(CarParts p)
    {
        var body = HumanMeshBuilder.Material();
        _headMaterial = TrafficMeshBuilder.LampMaterial();
        _tailMaterial = TrafficMeshBuilder.LampMaterial();

        // pitch about the middle of the car at roughly the centre of mass height
        _body = new Node3D { Name = "Body", Position = new Vector3(0, 0.5f, 0) };
        AddChild(_body);
        var offset = new Vector3(0, -0.5f, 0);
        _body.AddChild(new MeshInstance3D { Name = "Shell", Mesh = p.Body, MaterialOverride = body, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = _headMaterial, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = _tailMaterial, Position = offset });
        if (p.Top is { } top)
        {
            _top = new MeshInstance3D { Name = "SoftTop", Mesh = top.Mesh, MaterialOverride = body, Position = top.Pivot + offset };
            _windows = new MeshInstance3D { Name = "SideGlass", Mesh = p.Windows!.Mesh, MaterialOverride = body, Position = p.Windows.Pivot + offset };
            _cockpit = new MeshInstance3D { Name = "Cockpit", Mesh = p.Cockpit, MaterialOverride = body, Position = offset };
            _body.AddChild(_top);
            _body.AddChild(_windows);
            _body.AddChild(_cockpit);
        }
        if (p.Flaps is { } flaps)
        {
            _flaps = new Node3D { Name = "PopUps", Position = flaps.Pivot + offset };
            _flapLamps = new MeshInstance3D { Mesh = flaps.Lamp, MaterialOverride = _headMaterial };
            _flaps.AddChild(new MeshInstance3D { Mesh = flaps.Mesh, MaterialOverride = body });
            _flaps.AddChild(_flapLamps);
            _body.AddChild(_flaps);
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
        ApplyLamps();
    }

    private void ApplyLamps()
    {
        _tailMaterial.AlbedoColor = BrakeLights ? Colors.White : new Color(0.42f, 0.42f, 0.42f);
        _headMaterial.AlbedoColor = Headlights ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
    }

    /// <summary>Moves the top and pods toward what is asked, or straight there on the first frame.</summary>
    private void ApplyMovingParts(float dt)
    {
        float roof = RoofOpen && _top != null ? 1f : 0f, pods = Headlights ? 1f : 0f;
        _roof = _settled ? Mathf.MoveToward(_roof, roof, dt / RoofTime) : roof;
        _pods = _settled ? Mathf.MoveToward(_pods, pods, dt / FlapTime) : pods;
        _settled = true;

        if (_top != null && _windows != null)
        {
            // the glass winds down into the doors first, then the top folds back onto its hinge
            float down = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(_roof / WindowsDown, 0f, 1f));
            _windows.Visible = down < 0.99f;
            _windows.Scale = new Vector3(1f, Mathf.Max(1f - down, 0.01f), 1f);
            float fold = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((_roof - FoldFrom) / (1f - FoldFrom), 0f, 1f));
            _top.Scale = new Vector3(1f, Mathf.Lerp(1f, FoldedHeight, fold), Mathf.Lerp(1f, FoldedLength, fold));
            // the glass box around the seats does not hide them (boxes render inside out)
            _cockpit!.Visible = _roof > 0f;
        }
        if (_flaps != null && _flapLamps != null)
        {
            _flaps.Scale = new Vector3(1f, Mathf.Lerp(FoldedPod, 1f, Mathf.SmoothStep(0f, 1f, _pods)), 1f);
            _flapLamps.Visible = _pods > 0.15f;
        }
    }

    public override void _Process(double delta)
    {
        if (_body == null) return;
        _body.Rotation = new Vector3(BodyPitch, 0, 0);   // + rotates −Z (the nose) up
        for (int i = 0; i < 4; i++)
        {
            _steer[i].Rotation = new Vector3(0, i < 2 ? SteerAngle : 0f, 0);   // + yaw turns −Z toward −X: left
            _spin[i].Rotation = new Vector3(-WheelSpin, 0, 0);                  // top edge moves toward −Z, forward
        }
        ApplyMovingParts((float)delta);
        ApplyLamps();
    }
}
