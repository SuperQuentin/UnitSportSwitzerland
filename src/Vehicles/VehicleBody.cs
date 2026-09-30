using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Player;
using UnitSport.Terrain;
using Plane = UnitSport.Player.Plane;

namespace UnitSport.Vehicles;

/// <summary>
/// A vehicle standing (or falling, or burning) in the world with nobody in it.
///
/// <para>
/// While someone drives, the vehicle lives inside their <see cref="FootPlayer"/>: that body
/// already runs the proven ride and flight physics, the cameras and the tricks. The moment they
/// get out — or it crashes — its state (<see cref="VehicleState"/>) is handed to one of these, and
/// the vehicle carries on by itself: a bike freewheels to a stop and falls over, a plane abandoned
/// at full throttle flies on until it meets a mountain, a helicopter left in the air winds down
/// and drops. Getting in hands the state back. From the outside it is one continuous vehicle.
/// </para>
///
/// <para>
/// Physics runs on the authority only (whoever left it there) — the same client-authoritative
/// model the players use — and sleeps once the vehicle comes to rest, so a parked car park costs
/// nothing. Every peer watches <see cref="Wrecked"/>: the frame it flips, it explodes there too.
/// </para>
/// </summary>
public partial class VehicleBody : CharacterBody3D
{
    public const string Group = "vehicles";

    public RideKind Kind { get; private set; }
    public Rideable Ride { get; private set; } = null!;

    [Export] public bool Wrecked { get; set; }
    [Export] public float Health { get; set; }
    [Export] public bool EngineOn { get; set; }
    /// <summary>The craft's attitude, for a remote copy that only receives position and yaw.</summary>
    [Export] public Quaternion Tilt { get; set; } = Quaternion.Identity;
    /// <summary>Rotor / prop spool as the authority simulates it; a remote copy used to guess it from <see cref="EngineOn"/>.</summary>
    [Export] public float Spool { get; set; }

    public ChunkManager? Terrain { get; set; }

    /// <summary>Seconds since this became a wreck, on this peer — for despawning.</summary>
    public double WreckAge { get; private set; }

    /// <summary>Seconds with no player within range — for despawning.</summary>
    public double LonelyFor { get; set; }

    public long Owner { get; private set; }

    private VehicleState _initial;
    private RideMotion _motion;
    private FlightMotion _flight;
    private Node3D? _visual;
    private float _bikeRoll;
    private float _restTime;
    private bool _asleep;
    private MultiplayerSynchronizer? _sync;
    private bool _anchored;
    private bool _charred;
    private bool _wasWrecked;
    private EngineSynth? _engineSound;
    private GpuParticles3D? _fire, _smoke;
    private readonly List<PhysicsBody3D> _ignoring = new();

    public static VehicleBody Create(VehicleState state, ChunkManager? terrain)
    {
        var v = new VehicleBody
        {
            Name = string.IsNullOrEmpty(state.Name) ? $"veh_local_{Interlocked.Increment(ref _localCounter)}" : state.Name,
            Terrain = terrain,
            _initial = state,
            Kind = state.Kind,
            Ride = Rideable.Create(state.Kind) ?? new Bicycle(),
            Wrecked = state.Wrecked,
            Health = state.Health,
            EngineOn = state.EngineOn,
            Owner = state.Owner,
        };
        if (state.Owner > 0) v.SetMultiplayerAuthority((int)state.Owner);
        return v;
    }

    private static int _localCounter;

    private bool Headless => DisplayServer.GetName() == "headless";

    public override void _Ready()
    {
        CollisionMask |= World.TreeColliders.Layer;   // a runaway car stops at a trunk
        AddToGroup(Group);
        // parked across an open doorway, it is seen on both sides of it
        AddToGroup(Interiors.DoorwayGhosts.Group);
        var s = _initial;
        // A hand's breadth up. The terrain collision is a one-sided heightfield, and a box whose
        // bottom starts exactly on it — a vehicle parked from where the rider stood — begins a
        // hair inside it and falls straight through the world (measured: a parked bike 125 m
        // under the mountain five seconds later). From just above, it settles onto it.
        Position = s.Position + Vector3.Up * 0.15f;
        Rotation = new Vector3(0, s.Yaw, 0);
        Velocity = s.Velocity;

        var box = Ride.ParkedBox;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.Centre });
        FloorMaxAngle = Mathf.DegToRad(50f);

        _motion = new RideMotion { Speed = new Vector2(s.Velocity.X, s.Velocity.Z).Length(), Yaw = s.Yaw };
        if (Ride is Flyer flyer)
        {
            flyer.Begin(ref _flight, s.Velocity, s.Yaw);
            _flight.Control = s.Throttle;
            // a vehicle left running keeps turning; one left in the air is already up to speed
            _flight.Spool = s.EngineOn ? 1f : 0f;
        }

        var replication = new SceneReplicationConfig();
        foreach (var prop in new[] { ".:position", ".:rotation", ".:velocity", ".:Wrecked", ".:Health", ".:EngineOn", ".:Tilt", ".:Spool" })
            replication.AddProperty(prop);
        // states that change a few times per life of a vehicle go reliably on change; the motion
        // at 20 Hz while it moves (every frame before, for a bike standing in a field for hours)
        foreach (var prop in new[] { ".:Wrecked", ".:Health", ".:EngineOn" })
            replication.PropertySetReplicationMode(prop, SceneReplicationConfig.ReplicationMode.OnChange);
        var sync = _sync = new MultiplayerSynchronizer
        {
            Name = "Sync", RootPath = new NodePath(".."), ReplicationConfig = replication,
            ReplicationInterval = 0.05f,
        };
        sync.SetMultiplayerAuthority(GetMultiplayerAuthority());
        AddChild(sync);

        // Placed by the dedicated server itself (VehicleManager.Place): the server has no ground
        // collision to simulate it on, so it stands exactly where it was put, asleep, until a
        // player claims it. The hand's breadth above is for a body that falls onto the ground.
        if (Net.NetworkManager.DedicatedServer && IsMultiplayerAuthority())
        {
            Position = s.Position;
            _asleep = true;
            sync.ReplicationInterval = 2f;
        }

        if (!Headless)
        {
            _visual = Ride.BuildParkedVisual((int)Math.Max(1, Owner));
            _visual.Name = "Visual";
            AddChild(_visual);
            Hurtbox.Fit(_visual);
            if (Ride is Helicopter or Plane or IEngined)
            {
                var profile = Ride is IEngined parked ? parked.Sound
                    : Ride is Helicopter ? EngineProfile.Turboshaft : EngineProfile.PistonAero;
                _engineSound = new EngineSynth(profile, spatial: true, seed: (int)Math.Max(1, Owner));
                AddChild(_engineSound);
            }
        }

        _wasWrecked = Wrecked;
        if (Wrecked)
        {
            Char();
            // A fresh wreck (a crash just now) goes up where every peer can see it. One spawned
            // for a late joiner has long since stopped exploding.
            if (VehicleState.Now - s.SpawnedAt < 3.0) Detonate();
        }

        if (!IsMultiplayerAuthority()) SetPhysicsProcess(false);
        else SetAnchored(true);

        // whoever just got out is right beside (or inside) the box: ignore them while they clear it
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is PhysicsBody3D body && body.GlobalPosition.DistanceTo(Position) < 15f)
            {
                AddCollisionExceptionWith(body);
                _ignoring.Add(body);
            }
    }

    public override void _ExitTree() => SetAnchored(false);

    /// <summary>
    /// A moving vehicle needs ground loaded under it with collision, like a player does, or an
    /// abandoned plane flies out of the streamed world and falls through where terrain should be.
    /// Parked, it lets go of that.
    /// </summary>
    private void SetAnchored(bool on)
    {
        if (Terrain == null || on == _anchored) return;
        _anchored = on;
        if (on) Terrain.AddAnchor(this, collision: true);
        else Terrain.RemoveAnchor(this);
    }

    /// <summary>What this vehicle is right now, for handing it to a driver.</summary>
    /// <remarks>
    /// Heading from the body's own yaw, which is replicated: the server captures vehicles it
    /// never simulated, so their flight state there is whatever they were parked with.
    /// </remarks>
    public VehicleState Capture() => new(Kind, GlobalPosition,
        Rotation.Y, Velocity, Health, EngineOn, Wrecked,
        _flight.Control, VehicleState.Now, Owner, Name, _initial.Headlights, _initial.RoofOpen);

    public override void _PhysicsProcess(double delta)
    {
        if (_asleep) return;
        float dt = (float)delta;
        _life += dt;
        if (_life > SettleTime && _ignoring.Count > 0)
        {
            foreach (var body in _ignoring)
                if (IsInstanceValid(body)) RemoveCollisionExceptionWith(body);
            _ignoring.Clear();
        }

        // hold still until the ground is there; a vehicle dropped over unstreamed terrain would
        // otherwise fall through the world before it arrived
        if (Terrain != null && !Terrain.HasCollisionAt(GlobalPosition)) return;

        // the player's safety net, for vehicles: never under the terrain surface
        if (Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float ground) && GlobalPosition.Y < ground - 1f)
        {
            GlobalPosition = GlobalPosition with { Y = ground + 0.2f };
            Velocity = Velocity with { Y = 0f };
            _flight.Velocity = _flight.Velocity with { Y = 0f };
        }

        bool onFloor = IsOnFloor();
        if (Wrecked) StepWreck(dt, onFloor);
        else if (Ride is Flyer flyer) StepFlyer(dt, onFloor, flyer);
        else StepRolling(dt, onFloor);

        // at rest long enough: sleep, and stop asking for collision
        bool still = onFloor && Velocity.LengthSquared() < 0.04f && _flight.Spool < 0.05f;
        _restTime = still ? _restTime + dt : 0f;
        if (_restTime > 1f)
        {
            _asleep = true;
            Velocity = Vector3.Zero;
            // asleep it cannot move until someone claims it (a new node): a heartbeat is enough
            if (_sync != null) _sync.ReplicationInterval = 2f;
            SetAnchored(false);
        }
    }

    private void StepWreck(float dt, bool onFloor)
    {
        var v = Velocity;
        v.Y = onFloor ? Mathf.Min(v.Y, 0f) : v.Y - Rideable.Gravity * dt;
        var flat = new Vector3(v.X, 0, v.Z).MoveToward(Vector3.Zero, (onFloor ? 8f : 0.5f) * dt);
        Velocity = new Vector3(flat.X, v.Y, flat.Z);
        MoveAndSlide();
    }

    /// <summary>
    /// A craft with nobody at the controls: stick centred, nothing on the collective. A plane keeps
    /// whatever throttle it was left at; a helicopter has no pilot to hold the collective, so it
    /// is flown as if its engine were off — it winds down and autorotates to the ground.
    /// </summary>
    /// <summary>Seconds since this vehicle was put into the world.</summary>
    private float _life;
    private const float SettleTime = 1f;

    private void StepFlyer(float dt, bool onFloor, Flyer flyer)
    {
        float clearance = Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float g) ? GlobalPosition.Y - g : 999f;
        // on the ground nobody leaves a plane without its brakes on (lever fully back is the
        // brake); in the air the lever stays where the pilot left it
        var input = new FlightInput(Vector2.Zero, 0f, 0f, 0f, onFloor ? 1f : 0f, false, false, _flight.Yaw,
            Engine: EngineOn && Ride is not Helicopter, Piloted: false);
        flyer.Fly(input, new FlightEnv(onFloor, clearance), dt, ref _flight);
        Rotation = new Vector3(0, _flight.Yaw, 0);

        Velocity = _flight.Velocity;
        MoveAndSlide();

        var real = GetRealVelocity();
        var lost = _flight.Velocity - real;
        float impact = IsOnFloor()
            ? Mathf.Max(new Vector2(lost.X, lost.Z).Length(), -_flight.Velocity.Y - 6f)
            : lost.Length();
        // The first second is not evidence of anything: the pilot who just got out is standing
        // in or against the box, and the solver shoving the two apart reads as an 80 m/s impact
        // — measured, and it blew up every vehicle the moment it was left.
        if (_life < SettleTime)
        {
            // Still take on the ground's word for how it is really moving, or a plane settling
            // onto its wheels "stalls" in the model for that second, builds 13 m/s of sink, and
            // the first counted frame calls that a crash. The solver's shove is the exception:
            // a difference that size is not motion, so it is not adopted.
            // Only ever slower: a real motion FASTER than the model is the solver pushing the box
            // out of a slope it started in (an 11 m plane on a hillside), and taking that on as
            // speed launched a parked plane off the mountain to crash 2.5 s later.
            if (real.Length() <= _flight.Velocity.Length() + 0.5f)
            {
                _flight.Velocity = real;
                _flight.Spin = 0f;
            }
            Velocity = _flight.Velocity;
            ApplyPose(flyer);
            return;
        }
        if (impact > flyer.CrashSpeed) { Explode(); return; }
        if (impact > 4f)
        {
            Health -= (impact - 4f) * 10f;
            if (Health <= 0f) { Explode(); return; }
        }
        if (lost.LengthSquared() > 4f) _flight.Velocity = real;
        ApplyPose(flyer);
    }

    private void ApplyPose(Flyer flyer)
    {
        var attitude = _flight.Attitude == default ? Basis.Identity : _flight.Attitude;
        Tilt = attitude.Orthonormalized().GetRotationQuaternion();
        if (_visual != null) flyer.Pose(_visual, _flight.Yaw, _flight);
    }

    /// <summary>A riderless bike: it rolls on, slows, and falls over.</summary>
    private void StepRolling(float dt, bool onFloor)
    {
        _motion.Speed = Mathf.MoveToward(_motion.Speed, 0f, (onFloor ? 3f : 0.3f) * dt);
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        var v = heading * _motion.Speed;
        v.Y = onFloor ? Mathf.Min(Velocity.Y, 0f) : Velocity.Y - Rideable.Gravity * dt;
        Velocity = v;
        MoveAndSlide();
        var real = GetRealVelocity();
        _motion.Speed = Mathf.Min(_motion.Speed, new Vector2(real.X, real.Z).Length() + 0.5f);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (Wrecked && !_wasWrecked) { _wasWrecked = true; Char(); Detonate(); }
        if (Wrecked) WreckAge += delta;

        if (_visual == null) return;

        if (!IsMultiplayerAuthority() && Ride is Flyer remoteFlyer)
            remoteFlyer.Pose(_visual, Rotation.Y, new FlightMotion { Attitude = new Basis(Tilt) });

        if (Ride is Bicycle)
        {
            // tipped over once it stops, like any bike left without its rider
            float target = Velocity.Length() < 1.5f ? 1.35f : 0f;
            _bikeRoll = Mathf.Lerp(_bikeRoll, target, 1f - Mathf.Exp(-4f * dt));
            _visual.Rotation = new Vector3(0, 0, _bikeRoll);
        }

        // what the engine and rotor are doing, as far as this peer can know
        if (IsMultiplayerAuthority()) Spool = _flight.Spool;
        float spool = Wrecked ? 0f : Spool;
        if (Ride is Flyer f && !Wrecked) f.AnimateFlight(_visual, _flight with { Spool = spool }, dt);
        if (_visual is Avatar.CarRig rig)
        {
            // a driverless car rolls to a stop on its own wheels; it never tips, so no roll here
            rig.WheelSpin += Velocity.Length() / 0.3f * dt;
            rig.SteerAngle = 0f;
            rig.BodyPitch = 0f;
            rig.BrakeLights = false;
            // left as the driver left them; nobody can switch them from outside, so the spawn
            // data is enough and nothing more is replicated
            rig.Headlights = _initial.Headlights && !Wrecked;
            rig.RoofOpen = _initial.RoofOpen;
        }
        if (_engineSound != null && Ride is IEngined)
            // ticking over while it rolls; a car at rest is asleep and silent
            _engineSound.Set(0f, 0f, 0.2f, EngineOn && !Wrecked && !_asleep ? 0.1f : 0f);
        else if (_engineSound != null)
        {
            _engineSound.Set(spool, spool, 0.5f, spool * 0.7f);
        }

        // the fire burns out after half a minute; the smoke lingers until the wreck is cleared
        if (_fire != null && WreckAge > 30) _fire.Emitting = false;
        if (_smoke != null && WreckAge > 75) _smoke.Emitting = false;
    }

    /// <summary>Blows it up: the flag every peer watches. Only the authority calls this.</summary>
    public void Explode()
    {
        if (Wrecked) return;
        Wrecked = true;
        Health = 0f;
        EngineOn = false;
        // a little of the momentum survives the blast
        Velocity = Velocity * 0.4f + Vector3.Up * 4f;
    }

    private void Detonate()
    {
        if (Headless || GetParent() == null) return;
        Explosion.Spawn(GetParent(), GlobalPosition + Vector3.Up * 1f);
    }

    /// <summary>Burnt: the paint goes to soot, and the fire and smoke start.</summary>
    private void Char()
    {
        if (_charred || _visual == null) return;
        _charred = true;
        var soot = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(0.2f, 0.18f, 0.16f),
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            Roughness = 1f,
        };
        foreach (var mesh in _visual.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Append(_visual as MeshInstance3D))
            if (mesh != null) mesh.MaterialOverride = soot;

        _fire = Explosion.Burst("Fire", 40, 0.8f, 0.7f, additive: true, explosiveness: 0f,
            velocity: (0.5f, 2f), gravity: 3f, spread: 15f, oneShot: false,
            ramp: new[] { new Color(1f, 0.8f, 0.3f, 1f), new Color(1f, 0.35f, 0.05f, 0.8f), new Color(0.1f, 0.05f, 0f, 0f) });
        _fire.Position = Vector3.Up * 1f;
        AddChild(_fire);
        _smoke = Explosion.Burst("Smoke", 30, 6f, 2.5f, additive: false, explosiveness: 0f,
            velocity: (1f, 2.5f), gravity: 2f, spread: 12f, oneShot: false,
            ramp: new[] { new Color(0.12f, 0.11f, 0.1f, 0.7f), new Color(0.3f, 0.3f, 0.3f, 0f) });
        _smoke.Position = Vector3.Up * 2f;
        AddChild(_smoke);
    }
}
