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
    /// <summary>A car's open doors, one bit each (<see cref="CarRig.DoorLeft"/>..): left open for show, or by whoever just got out.</summary>
    [Export] public byte DoorsOpen { get; set; }
    /// <summary>A truck's joints as its authority rolls it on (#162), for the copies: its trailer swings where it does.</summary>
    [Export] public Vector3 TrainAngles { get; set; }

    public ChunkManager? Terrain { get; set; }

    /// <summary>Seconds since this became a wreck, on this peer — for despawning.</summary>
    public double WreckAge { get; private set; }

    /// <summary>Seconds with no player within range — for despawning.</summary>
    public double LonelyFor { get; set; }

    public long Owner { get; private set; }

    /// <summary>A parked bus's doors, one bit each (#162: open, they can be walked through; anyone works them by their buttons).</summary>
    public byte BusDoors => Ride is Truck { IsBus: true } ? DoorsOpen : (byte)0;

    /// <summary>A car's rig, for finding the door a player is at; null for anything else, or headless.</summary>
    public CarRig? Rig => _visual as CarRig;

    /// <summary>
    /// The drawn machine, for outlining it (#261), and the frame of its first section (a parked
    /// train's others are its children named <c>Section{k}</c>). Null on a headless peer, but for a
    /// parked truck or bus: an empty frame there, posed on the ground as the model would be (#162).
    /// </summary>
    public Node3D? Visual => _visual;

    private VehicleState _initial;
    private RideMotion _motion;
    private FlightMotion _flight;
    private Node3D? _visual;
    private float _bikeRoll;
    private float _heavySpin;
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
            // the car with its preset and its garage parts on, the truck with its trailer: they are
            // part of it
            Ride = state.CreateRide() ?? new Bicycle(),
            Wrecked = state.Wrecked,
            // a car's doors, or a bus's (kept in its flags while it is driven)
            DoorsOpen = (byte)((state.CreateRide() is Truck { IsBus: true } ? state.Flags >> 4 : state.DoorsOpen) & 15),
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
        AddChild(new CollisionShape3D { Name = "Hull", Shape = new BoxShape3D { Size = box.Size }, Position = box.Centre });
        // a parked train's trailer, a drawbar trailer's body: each section its own box, where it stands
        int extra = 0;
        foreach (var (pose, centre, size) in Ride.ExtraBoxes())
            AddChild(new CollisionShape3D
            {
                Name = $"Section{++extra}",
                Shape = new BoxShape3D { Size = size },
                Transform = pose * new Transform3D(Basis.Identity, centre),
            });
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
        foreach (var prop in new[] { ".:position", ".:rotation", ".:velocity", ".:Wrecked", ".:Health", ".:EngineOn", ".:Tilt", ".:Spool", ".:DoorsOpen", ".:TrainAngles" })
            replication.AddProperty(prop);
        // states that change a few times per life of a vehicle go reliably on change; the motion
        // at 20 Hz while it moves (every frame before, for a bike standing in a field for hours)
        foreach (var prop in new[] { ".:Wrecked", ".:Health", ".:EngineOn", ".:DoorsOpen" })
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
        else if (Ride is Truck or ParkedTrailer)
        {
            // Headless (the server, a check) nothing is drawn, but the frame the model would stand in
            // still matters: a parked bus's decks are walked in it, and its guests found by it
            // (#162). Its box rests on whatever it touches, a metre off the road at a door on a
            // crest; the frame is posed on the ground axle by axle, as the model is.
            _visual = new Node3D { Name = "Visual" };
            int sections = Ride is Truck train ? train.Train.Count : ((ParkedTrailer)Ride).Bodies.Count;
            for (int k = 1; k < sections; k++) _visual.AddChild(new Node3D { Name = $"Section{k}" });
            AddChild(_visual);
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

        // Someone just got out of this car: it arrives with their door open (in the spawn state,
        // so every peer starts with it open), and the authority shuts it behind them — unless they
        // had left it open on purpose, when the state does not ask for it. Opening it here instead
        // was never seen: the synchronizer only sends changes from the values it first sees.
        if (IsMultiplayerAuthority() && (s.DoorsOpen & VehicleState.DriverDoorShuts) != 0 && VehicleState.Now - s.SpawnedAt < 3.0)
            _shutDriverIn = 1f;

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
        _flight.Control, VehicleState.Now, Owner, Name, _initial.Headlights, _initial.RoofOpen, _initial.Tuning,
        Ride is Truck { IsBus: true } ? (byte)0 : DoorsOpen, _initial.Setup,
        _initial.Train, _initial.Angles,
        // a bus's doors as they are now, where a truck keeps them
        Ride is Truck { IsBus: true } ? (_initial.Flags & ~(15 << 4)) | ((DoorsOpen & 15) << 4) : _initial.Flags, _initial.Load,
        _initial.Radio, _initial.Cd);

    /// <summary>The live station its radio plays, as the driver left it (spawn data only: nobody tunes a parked car).</summary>
    public int Radio => _initial.Radio;

    /// <summary>The CD its stereo plays (<c>Items.RadioPlay</c>), as the driver left it; empty for none. Spawn data only (#211).</summary>
    public string Cd => _initial.Cd;

    /// <summary>A lone trailer standing here, waiting for a truck; null for anything else.</summary>
    public ParkedTrailer? Trailer => Ride as ParkedTrailer;

    /// <summary>Authority: seconds until the driver's door, open from getting out, shuts.</summary>
    private float _shutDriverIn;

    /// <summary>Opens or shuts one door. The authority's call: others ask <see cref="VehicleManager.ToggleDoor"/>.</summary>
    public void ToggleDoor(byte bit)
    {
        if (Wrecked || Ride is not (Car or Truck { IsBus: true })) return;
        DoorsOpen ^= (byte)(bit & 15);
        _shutDriverIn = 0f;   // a door someone chose to leave open stays open
    }

    private readonly HashSet<FootPlayer> _guests = new();

    public override void _PhysicsProcess(double delta)
    {
        // a parked bus: people walking in it or up to its doors do not shove it (#162)
        if (Ride.Walkable) FootPlayer.WatchGuests(this, Ride, _guests, new PhysicsBody3D[] { this }, k => k == 0 ? Visual ?? this : Visual?.GetNodeOrNull<Node3D>($"Section{k}"));
        float dt = (float)delta;
        _life += dt;
        if (_life > SettleTime && _ignoring.Count > 0)
            // a trailer just dropped stands over the truck that left it: it ignores it until that
            // has driven clear, not for a second
            _ignoring.RemoveAll(body =>
            {
                bool gone = !IsInstanceValid(body);
                bool clear = gone || Ride is not ParkedTrailer || body.GlobalPosition.DistanceTo(GlobalPosition) > 22f;
                if (clear && !gone) RemoveCollisionExceptionWith(body);
                return clear;
            });
        if (_asleep) return;

        // Parked in a garage or a barn: down where the interiors are, on a floor that is only there
        // while this peer has that interior built. Without it, hold still rather than fall; and the
        // terrain overhead is not ground to be rescued onto.
        bool inside = Interiors.InteriorManager.InInteriorSpace(GlobalPosition);
        if (inside && Interiors.InteriorManager.Instance?.LayoutAt(GlobalPosition) == null) return;

        // hold still until the ground is there; a vehicle dropped over unstreamed terrain would
        // otherwise fall through the world before it arrived
        if (!inside && Terrain != null && !Terrain.HasCollisionAt(GlobalPosition)) return;

        // the player's safety net, for vehicles: never under the terrain surface
        if (!inside && Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float ground) && GlobalPosition.Y < ground - 1f
            && !Terrain.InTunnel(GlobalPosition) && !Terrain.FloorBelow(this, GlobalPosition, GetRid()))
        {
            GlobalPosition = GlobalPosition with { Y = ground + 0.2f };
            Velocity = Velocity with { Y = 0f };
            _flight.Velocity = _flight.Velocity with { Y = 0f };
        }

        bool onFloor = IsOnFloor();
        if (Wrecked) StepWreck(dt, onFloor);
        else if (Ride is Flyer flyer) StepFlyer(dt, onFloor, flyer);
        else if (Ride.Driverless) StepDriverless(dt, onFloor);
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

    /// <summary>
    /// A car, a truck or a bus nobody drives, rolling on (#162): its own physics with no input, as
    /// it does with passengers aboard and nobody at the wheel (#158): no pedal, the wheel let go,
    /// the engine dragging, a truck's automatic holding it once it stops. It used to slide straight
    /// on, losing 3 m/s every second, whatever the road did. At rest it sleeps as before.
    /// </summary>
    private void StepDriverless(float dt, bool onFloor)
    {
        var normal = onFloor ? GetFloorNormal() : Vector3.Up;
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        // the slope along the way it points, as the ride feels it when driven
        float grade = onFloor ? -(heading.X * normal.X + heading.Z * normal.Z) / Mathf.Max(normal.Y, 0.15f) : 0f;
        bool inside = Interiors.InteriorManager.InInteriorSpace(GlobalPosition);
        var surface = Terrain != null ? Audio.Surfaces.At(Terrain, GlobalPosition, inside) : Audio.Surface.Asphalt;
        if (Ride is Truck truck)
        {
            // nobody's foot on anything: the engine only drags. Idling in drive, a bus's converter
            // would creep on for ever, with nobody aboard to stop it
            truck.EngineRunning = false;
            truck.Box.ClutchHeld = false;
        }
        _motion.Yaw = Rotation.Y;
        Ride.Step(new RideInput(0f, 0f, 0f, false), new RideGround(onFloor, grade, surface), dt, ref _motion);
        Rotation = new Vector3(0, _motion.Yaw, 0);
        heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        var v = heading.Rotated(Vector3.Up, _motion.Slip) * _motion.Speed;
        v.Y = onFloor ? Mathf.Min(Velocity.Y, 0f) : Velocity.Y - Rideable.Gravity * dt;
        Velocity = v;
        MoveAndSlide();
        // what it hit takes the speed it could not keep (a wall, a tree, another vehicle)
        var real = GetRealVelocity();
        float achieved = new Vector2(real.X, real.Z).Length();
        if (achieved < _motion.Speed - 1f) _motion.Speed = Mathf.Max(achieved, _motion.Speed - 25f * dt);
        if (Ride is Truck rolled) TrainAngles = rolled.Angles;
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

        if (_shutDriverIn > 0f && (_shutDriverIn -= dt) <= 0f) DoorsOpen &= unchecked((byte)~CarRig.DriverDoor);

        if (_visual == null) return;
        if (_visual is CarRig doors) doors.DoorsOpen = DoorsOpen;
        else if (_visual is HeavyRig bus && Ride is Truck { IsBus: true })
        {
            // the bus's leaves swing on its rig, an articulated one's on both halves
            bus.DoorsOpen = DoorsOpen;
            foreach (var half in bus.GetChildren().OfType<HeavyRig>()) half.DoorsOpen = DoorsOpen;
        }

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
        // a copy's trailer swings where its authority's does
        if (!IsMultiplayerAuthority() && Ride is Truck swung && TrainAngles != default) swung.SetAngles(TrainAngles);
        StandOnGround(dt);
        // standing still, a parked truck's rigs were dressed with the same values every frame, the
        // sections found by name each time (#221): once at rest is enough, again if it moves or burns
        bool restDressed = _dressedAtRest == Wrecked && Velocity == Vector3.Zero;
        if (_visual is HeavyRig heavy && Ride is Truck truck && !restDressed)
        {
            // parked as the driver left it: lamps, doors, the display; every section's wheels roll
            truck.UnpackFlags(_initial.Flags);
            _heavySpin += Velocity.Length() / truck.WheelRadius * dt;
            _dressedAtRest = Velocity == Vector3.Zero ? Wrecked : null;
            _heavySections ??= heavy.FindChildren("Section*", "", false, false).OfType<HeavyRig>().Prepend(heavy).ToArray();
            foreach (var section in _heavySections)
            {
                truck.Dress(section, 0, false);
                section.BrakeLights = false;
                section.Headlights = truck.Headlights && !Wrecked;
                section.WheelSpin = _heavySpin;
            }
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

    private float _standIn;

    /// <summary>
    /// Its frame (<see cref="Visual"/> and its sections) stands on the ground: false until the
    /// first pose after it appears, while the frame is still the body's own, wherever its box came
    /// to rest. A deck is not walked in before (#162): one built in the unposed frame and moved onto
    /// the ground a frame later swept the driver who had just got up onto its roof.
    /// </summary>
    public bool Posed { get; private set; }
    private bool _stoodAsleep;
    /// <summary>Wrecked or not when the parked rigs were last dressed standing still; null while it moves.</summary>
    private bool? _dressedAtRest;
    private HeavyRig[]? _heavySections;
    /// <summary>The drawn sections by index (0 the visual itself), found by name once.</summary>
    private Node3D?[]? _standSections;
    private PhysicsRayQueryParameters3D? _groundQuery;

    /// <summary>
    /// A truck, a bus or a trailer stands on the ground as it did when driven: every section pitched
    /// between its axles and its pin (<see cref="HeavyGround"/>), not dropped level. The drawn rigs
    /// follow at a few hertz (every frame while it rolls); its collision boxes take the pose once it
    /// is at rest — moved while it rolls, a pitched box would dig into the slope it slides on.
    /// </summary>
    private void StandOnGround(float dt)
    {
        IReadOnlyList<HeavyTrain.Body>? bodies = null;
        System.Func<int, Transform3D>? local = null;
        if (Ride is Truck t) { bodies = t.Train.Bodies; local = t.NodeLocal; }
        else if (Ride is ParkedTrailer p) { bodies = p.Bodies; local = p.NodeLocal; }
        if (bodies == null || local == null || _visual == null || Wrecked) return;
        bool rolling = Velocity.LengthSquared() > 0.01f;
        if (!rolling && (_standIn -= dt) > 0f) return;
        // at rest and stood: the ground under it is looked at again every 2 s, not 4 times a second
        // (a parked truck cast its rays for ever, #221); often enough for a road that streams in late
        _standIn = _stoodAsleep ? 2f : 0.25f;

        var poses = HeavyGround.Stand(GlobalTransform, bodies, local, Ground);
        if (_standSections?.Length != poses.Length)
            _standSections = Enumerable.Range(0, poses.Length).Select(k => k == 0 ? _visual : _visual.GetNodeOrNull<Node3D>($"Section{k}")).ToArray();
        _visual.GlobalTransform = poses[0];
        for (int k = 1; k < poses.Length; k++)
            if (_standSections[k] is { } rig) rig.GlobalTransform = poses[k];
        Posed = true;

        // the boxes, at rest only (and once more when it settles): sections from their own pose
        bool settled = _asleep || !IsMultiplayerAuthority();
        if (!settled) { _stoodAsleep = false; return; }
        if (_stoodAsleep) return;
        _stoodAsleep = true;
        if (GetNodeOrNull<CollisionShape3D>("Hull") is { } hull)
            hull.GlobalTransform = poses[0] * new Transform3D(Basis.Identity, Ride.ParkedBox.Centre);
        // the extra boxes are the sections behind, in order — after a semi-trailer's own running
        // gear, which is part of its first section
        bool gear = Ride is ParkedTrailer && bodies[0].Spec.Pivot != Coupling.Drawbar;
        int extra = 0;
        foreach (var (_, centre, _) in Ride.ExtraBoxes())
        {
            extra++;
            int section = Mathf.Min(gear ? extra - 1 : extra, poses.Length - 1);
            if (GetNodeOrNull<CollisionShape3D>($"Section{extra}") is { } shape)
                shape.GlobalTransform = poses[section] * new Transform3D(Basis.Identity, centre);
        }
    }

    /// <summary>The ground's height under a point: whatever is solid there (a road, a deck) but this vehicle, else the terrain.</summary>
    private float Ground(Vector3 p)
    {
        // one query for all its rays (a new one and a new exclude array per ray before, #221)
        var query = _groundQuery ??= new PhysicsRayQueryParameters3D { Exclude = new Godot.Collections.Array<Rid> { GetRid() } };
        query.From = p + Vector3.Up * 3f;
        query.To = p + Vector3.Down * 6f;
        query.CollisionMask = CollisionMask & ~World.TreeColliders.Layer;
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        // not a player: one standing in a parked bus by its front axle (up from the wheel, #162) was
        // read as the road, and the bus stood on their head, two metres up. Rare: a query of its own.
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer)
        {
            // (Exclude hands out a copy: added to in place, it would change nothing)
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var past = new PhysicsRayQueryParameters3D { From = query.From, To = query.To, CollisionMask = query.CollisionMask };
            for (int tries = 0; tries < 4 && hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer; tries++)
            {
                exclude.Add(hit["rid"].AsRid());
                past.Exclude = exclude;
                hit = GetWorld3D().DirectSpaceState.IntersectRay(past);
            }
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer) hit.Clear();
        }
        if (hit.Count > 0) return hit["position"].AsVector3().Y;
        return Terrain != null && Terrain.TryGetHeight(p, out float g) ? g : p.Y;
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
