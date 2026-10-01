using Godot;
using UnitSport.Avatar;
using UnitSport.Terrain;
using UnitSport.Vehicles;

namespace UnitSport.Combat;

/// <summary>
/// Something to shoot at offline: a red target plane on a banked circuit, or flying a straight
/// line for the probe. Local to each client and never replicated, like the traffic — which is also
/// why hitting one needs no authority check. Kinematic (an <see cref="AnimatableBody3D"/>), so the
/// tracers' ray queries hit it and a player who rams one crashes like into anything else.
/// </summary>
public partial class TargetDrone : AnimatableBody3D, Core.IOriginShiftAware
{
    public const string Group = "combat_drones";
    private const float MaxHealth = 60f;
    /// <summary>Kept at least this far above the terrain, m.</summary>
    private const float MinClearance = 60f;

    public Vector3 Velocity { get; private set; }
    public float Health { get; private set; } = MaxHealth;

    private ChunkManager? _terrain;
    private bool _circuit, _clockwise;
    private Vector3 _centre;
    private float _radius, _speed, _angle;

    public static TargetDrone Circuit(ChunkManager? terrain, Vector3 centre, float radius, float speed,
        float angle, bool clockwise) => new()
    {
        Name = "Drone", _terrain = terrain, _circuit = true, _centre = centre, _radius = radius,
        _speed = speed, _angle = angle, _clockwise = clockwise,
    };

    public static TargetDrone Straight(Vector3 at, Vector3 velocity) => new()
    {
        Name = "Drone", _centre = at, Velocity = velocity,
    };

    /// <summary>The origin moved (#185): so did the point it circles or flies on from.</summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        _centre = shift.Point(_centre);
        Velocity = shift.Direction(Velocity);
    }

    public override void _Ready()
    {
        AddToGroup(Group);
        TopLevel = true;
        // moved by setting its transform; synced to physics it would revert each write until the
        // next physics frame, and read back where it was
        SyncToPhysics = false;
        // wings included, so a round through a wingtip counts
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(9f, 1.4f, 7f) } });
        AddChild(new MeshInstance3D
        {
            Mesh = AircraftMeshBuilder.Plane(new Color(0.85f, 0.15f, 0.1f), new Color(0.95f, 0.95f, 0.9f)),
            MaterialOverride = HumanMeshBuilder.Material(),
            Position = new Vector3(0, -1.3f, 0),   // the mesh stands on its wheels; the body is its middle
        });
        Position = Place(0f);   // top level: its position is global
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var before = GlobalPosition;
        var at = Place(dt);
        Velocity = (at - before) / Mathf.Max(dt, 1e-4f);

        var fwd = Velocity.LengthSquared() > 1f ? Velocity.Normalized() : -GlobalBasis.Z;
        var up = Vector3.Up;
        if (_circuit)
        {
            // banked for the turn it is flying: tan φ = v² / (g·r)
            var inward = (_centre - at) with { Y = 0 };
            float bank = Mathf.Atan(_speed * _speed / (Player.Rideable.Gravity * _radius));
            up = Vector3.Up * Mathf.Cos(bank) + inward.Normalized() * Mathf.Sin(bank);
        }
        GlobalTransform = new Transform3D(Player.Flyer.Orient(fwd, up, Vector3.Up), at);
    }

    private Vector3 Place(float dt)
    {
        if (!_circuit)
        {
            _centre += Velocity * dt;
            return _centre;
        }
        _angle += (_clockwise ? -1f : 1f) * _speed / _radius * dt;
        var p = _centre + new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle)) * _radius;
        // a slow climb and dive around the circuit, never into the hillside
        p.Y = _centre.Y + 25f * Mathf.Sin(_angle * 2f);
        if (_terrain != null && _terrain.TryGetHeight(p, out float ground))
            p.Y = Mathf.Max(p.Y, ground + MinClearance);
        return p;
    }

    /// <summary>Takes a round. Returns true on the round that destroys it.</summary>
    public bool TakeHit(float damage)
    {
        if (Health <= 0f) return false;
        Health -= damage;
        if (Health > 0f) return false;
        Explosion.Spawn(GetParent(), GlobalPosition);
        QueueFree();
        return true;
    }
}
