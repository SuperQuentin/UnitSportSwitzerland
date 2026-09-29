using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// First-person on-foot controller tuned for human scale: WASD / left stick, mouse or right
/// stick look, Shift / L3 to run, Space / A to jump, Ctrl / B to slide, jump against a wall to
/// wall jump. Every control comes through <see cref="PlayerInput"/>'s actions.
///
/// Speeds are deliberately realistic rather than arcade — at the old 6–14 m/s a player
/// covered a 10 m building's frontage in under a second, which made the whole world feel
/// miniature. Walking at ~1.6 m/s against a 10 m facade is what sells the scale.
///
/// <para>
/// Sliding and wall jumping are the two exceptions to that realism, and they are deliberate:
/// both are *momentum* moves, and momentum is what alpine terrain has to offer. A slide
/// converts a descent into speed instead of the flat 4.6 m/s the walk cycle allows, and a
/// wall jump gets you out of the gullies and off the building faces that would otherwise be
/// dead ends. Neither adds a new top speed on flat ground — see <see cref="AirDrag"/>.
/// </para>
/// </summary>
public partial class FootPlayer : CharacterBody3D
{
    public const string Group = "players";

    [Export] public float SimWalkSpeed { get; set; } = 1.6f;   // ~5.8 km/h, brisk walk
    [Export] public float SimRunSpeed { get; set; } = 4.6f;    // ~16.6 km/h, steady run

    /// <summary>
    /// Walking and running pace for the active profile. Game runs at ~21 km/h: a game crosses a
    /// valley, and a realistic jog makes that a chore. The scale argument above still holds,
    /// because 5.8 m/s is a sprint a person recognises, not the 14 m/s giant of old.
    /// </summary>
    public float WalkSpeed => Rideable.Arcade ? 1.9f : SimWalkSpeed;
    public float RunSpeed => Rideable.Arcade ? 5.8f : SimRunSpeed;
    [Export] public float JumpVelocity { get; set; } = 4.2f;

    /// <summary>Speed a slide is entered at, if you were not already going faster.</summary>
    [Export] public float SlideSpeed { get; set; } = 7.0f;

    /// <summary>Upward kick of a wall jump. Slightly over a standing jump — it has to clear a lip.</summary>
    [Export] public float WallJumpUp { get; set; } = 4.6f;

    /// <summary>
    /// Vertical launch of a bunny hop on the bike or a pop on skis (m/s). Lower than a
    /// standing jump: a rider lifts the machine with them, and 3 m/s is a kerb, not a stunt.
    /// </summary>
    [Export] public float RideJumpVelocity { get; set; } = 3.2f;

    /// <summary>Push away from the wall. Above <see cref="RunSpeed"/>, so it actually launches.</summary>
    [Export] public float WallJumpPush { get; set; } = 5.4f;

    public ChunkManager? Terrain { get; set; }

    private const float Gravity = 9.81f;
    private const float EyeHeight = 1.68f;   // average adult eye level
    private const float BaseFov = 68f;
    private const float RunFov = 76f;
    private const float SlideFov = 86f;

    /// <summary>Ground speed is scaled down on steep ground — this is alpine terrain.</summary>
    private const float MaxClimbSlowdown = 0.45f;

    // --- body dimensions ---
    private const float StandHeight = 1.78f;
    private const float SlideHeight = 0.90f;
    private const float BodyRadius = 0.32f;
    private const float SlideEyeHeight = 0.72f;

    // --- slide tuning ---
    private const float SlideEntrySpeed = 2.6f;     // must be moving at least this fast to start
    private const float SlideMinSpeed = 2.0f;       // below this the slide gives out
    private const float SlideFriction = 2.4f;       // m/s² lost on flat ground
    private const float SlideSteer = 3.2f;          // lateral accel from A/D while sliding
    private const float SlideMaxTime = 3.0f;        // stops a downhill slide becoming a ski run
    private const float SlideCooldown = 0.35f;      // stops slide-spam being a movement mode

    // --- wall jump tuning ---
    /// <summary>
    /// A surface counts as a wall below this |normal.y| (~70° from horizontal). Deliberately
    /// stricter than <c>FloorMaxAngle</c>: a 55° scree slope is not floor, but bouncing off it
    /// like a climbing wall would look absurd.
    /// </summary>
    private const float WallMaxNormalY = 0.35f;
    private const int MaxWallJumps = 2;             // per airtime, reset on landing
    /// <summary>Two walls must differ by this much to both be jumpable — no ladder-climbing one flat face.</summary>
    private const float WallSimilarity = 0.85f;

    /// <summary>
    /// How long a wall is still jumpable after contact is lost. Measured, not guessed:
    /// pressed flat against a building face, the solver reports contact on roughly every
    /// other frame, so a strict same-frame test simply misses half of all attempts.
    /// </summary>
    private const float WallCoyoteTime = 0.18f;

    /// <summary>How early a jump press still counts, so arriving at a wall a frame late still works.</summary>
    private const float JumpBufferTime = 0.14f;

    // --- air momentum ---
    private const float AirSteer = 1.6f;    // how fast a launch can be aimed
    private const float AirDrag = 1.1f;     // m/s² bleeding a launch back to RunSpeed

    // --- mounts ---
    /// <summary>
    /// What the player is riding, as a <see cref="RideKind"/>. Replicated, so everyone else sees
    /// you on the bike rather than walking at 40 km/h.
    ///
    /// <para>
    /// An int and not the enum because Godot's replication moves Variants: an enum property is
    /// marshalled as its underlying integer anyway, and spelling that out here keeps the wire
    /// format explicit rather than dependent on how the binding happens to marshal an enum.
    /// </para>
    /// </summary>
    [Export] public int RideKindId { get; set; }

    // --- held item (see src/Items) ---
    /// <summary>
    /// What is in the player's hand, as an <see cref="Items.ItemId"/>. Replicated for the same
    /// reason <see cref="RideKindId"/> is: other players should see the binoculars, not an empty
    /// hand held up to a face. The inventory itself is local and is never sent.
    /// </summary>
    [Export] public int HeldItemId { get; set; }

    /// <summary>
    /// The figure's right hand in this node's local space, or null when no figure is drawn
    /// (first person on foot, or mounted). Updated whenever the body mesh is posed.
    /// </summary>
    public Transform3D? HandLocal { get; private set; }

    /// <summary>An item asking for a narrower view (binoculars, a camera's viewfinder); null for the normal FOV.</summary>
    public float? FovOverride { get; set; }

    /// <summary>
    /// Look from the eye even in third person, with the body hidden — raising binoculars to your
    /// face over a shoulder camera would otherwise zoom into the back of your own head.
    /// </summary>
    public bool ScopeView { get; set; }

    /// <summary>Multiplies mouse and stick look; an item zoomed to 10° turns it down so aim stays steady.</summary>
    public float LookScale { get; set; } = 1f;

    public bool IsFirstPerson => !_thirdPerson;

    private Rideable? _ride;
    private RideMotion _motion;
    private Node3D? _visual;
    private RideKind _visualKind = RideKind.OnFoot;

    // --- remote figure animation ---
    private MeshInstance3D? _walker;
    private Avatar.HumanPalette _walkPalette = Avatar.HumanPalette.Default;
    private float _stridePhase;
    private Vector3 _lastSeenPosition;
    private float _seenSpeed;

    /// <summary>Free look while riding. Steering owns the body's yaw, so the eyes get their own.</summary>
    private float _lookYaw;

    // --- third person on foot ---
    /// <summary>
    /// World yaw of the on-foot view. The mouse and stick turn this, not the body: in first
    /// person the body is set to it every frame, which is the old behaviour exactly; in third
    /// person the body turns to face where it is going instead, and the camera orbits freely.
    /// Movement is always relative to this, so "forward" is into the screen either way.
    /// </summary>
    private float _viewYaw;

    /// <summary>Third person (over the shoulder / chase) or first. Toggled with V / R3, saved.</summary>
    private bool _thirdPerson = Core.GameSettings.Current.ThirdPerson;

    /// <summary>Smoothed camera pivot height, so a step up or a hop does not jerk the view.</summary>
    private float _pivotY = float.NaN;

    /// <summary>Spring-arm length as a fraction of the wanted distance: snaps in at walls, eases out.</summary>
    private float _armBlend = 1f;

    /// <summary>Seconds airborne, so a single frame off a kerb does not flash the jump pose.</summary>
    private float _airTime;

    /// <summary>Fixed poses for the local body, built once: they do not animate, only swap.</summary>
    private ArrayMesh? _airPose, _slidePose;

    private const float ShoulderHeight = 1.55f;
    private const float ShoulderOffset = 0.55f;
    private const float ArmLength = 3.3f;

    /// <summary>How fast the body swings round to face the direction of travel, 1/s.</summary>
    private const float BodyTurnRate = 12f;

    /// <summary>Camera pull-in when the chase position is inside a hillside.</summary>
    private float _chaseBlend = 1f;

    /// <summary>
    /// How far the chase camera trails round the outside of the current turn, radians. Bolted
    /// dead behind, the camera turned exactly as fast as the bike, so a corner showed up as the
    /// whole world swinging round a rider who never moved in frame — the stiffness was half the
    /// camera. Trailing it lets you see the machine carve across the picture and then reel the
    /// view in after it.
    /// </summary>
    private float _turnLag;

    /// <summary>Smoothed real ground speed, for telling an impact from terrain roughness.</summary>
    private float _realSpeed;

    /// <summary>How fast the smoothed real speed follows the measured one, per second.</summary>
    private const float ImpactResponse = 6f;

    /// <summary>A shortfall smaller than this is the ground, not a wall. m/s.</summary>
    private const float ImpactTolerance = 1.0f;

    /// <summary>How hard an impact bleeds the model's speed, m/s². A crash, not a brake.</summary>
    private const float ImpactDecel = 25f;

    /// <summary>What is being ridden, or null on foot. Read by the HUD and the picker.</summary>
    public RideKind Ride => (RideKind)RideKindId;

    /// <summary>Ground speed, m/s — for a speedometer, and for the trainer link later.</summary>
    public float RideSpeed => _motion.Speed;

    // --- what the feel layer (PlayerFeel: sound, shake, particles, HUD) listens to ---
    /// <summary>Touched down; the argument is the downward speed that was arrested, m/s.</summary>
    public event Action<float>? Landed;
    public event Action? Jumped;
    public event Action? WallJumped;
    public event Action? SlideStarted;
    public event Action? Mantled;
    /// <summary>A mounted collision; the argument is the speed it took off, m/s.</summary>
    public event Action<float>? Impacted;

    /// <summary>Horizontal speed, m/s, whatever is carrying the player.</summary>
    public float GroundSpeed => _ride is Flyer ? _flight.Velocity.Length()
        : _ride != null ? _motion.Speed : new Vector2(Velocity.X, Velocity.Z).Length();

    /// <summary>The vehicle's live state (bank, lean, yaw rate) — zeroed on foot.</summary>
    public RideMotion Motion => _motion;

    /// <summary>The controls the vehicle saw last step, for pedal and brake sounds.</summary>
    public RideInput LastRideInput { get; private set; }

    /// <summary>The camera this player is looking through is the one on screen.</summary>
    public bool IsViewing => _camera is { Current: true };

    /// <summary>The building this player is inside (<see cref="Interiors.BuildingKey"/> text), or null outdoors.</summary>
    public string? InteriorKey { get; private set; }
    public bool Indoors => InteriorKey != null;

    /// <summary>
    /// Puts the player inside an interior. Everything that measures against the terrain — the
    /// drop-onto-the-ground pass, the under-the-terrain rescue, the base-jump height — has to stand
    /// down while this is set: the interior is thousands of metres under the ground it would test.
    /// </summary>
    public void EnterInterior(string key, Vector3 at, float yaw)
    {
        if (_sliding) EndSlide();
        InteriorKey = key;
        RequestReplacement();
        _placed = true;
        GlobalPosition = at;
        _viewYaw = yaw;
        Rotation = new Vector3(0, yaw, 0);
        _lastSafe = at;
        _hasSafe = true;
    }

    /// <summary>Back outside; null <paramref name="at"/> just drops the state (a teleport is moving us anyway).</summary>
    public void LeaveInterior(Vector3? at, float yaw)
    {
        InteriorKey = null;
        if (at is not { } p) return;
        RequestReplacement();
        GlobalPosition = p;
        _viewYaw = yaw;
        Rotation = new Vector3(0, yaw, 0);
        _lastSafe = p;
        _hasSafe = true;
    }

    /// <summary>Downward speed while mounted and airborne, for the ride landing.</summary>
    private float _rideFall;
    private bool _rideWasOnFloor = true;

    // --- tricks, landings and boost (mounted) ---
    /// <summary>
    /// Trick rotation of the rider and machine, radians: a flip about the pitch axis and a spin
    /// about the vertical. Visual only: the body keeps travelling along its heading, and what
    /// matters is whether the figure comes back round to upright before the wheels or skis
    /// touch down.
    /// </summary>
    private float _airPitch, _airSpin;
    private float _rideAir;
    private float _bailTimer;

    /// <summary>Boost, 0..1. Filled by air, clean landings and tricks; spent with Q / LB. Game profile.</summary>
    public float BoostMeter { get; private set; }
    public bool Boosting { get; private set; }

    // --- flight ---
    private FlightMotion _flight;
    private Vector3 _camFwd = Vector3.Forward;

    /// <summary>The flying craft's live state (velocity, attitude, spool) — zeroed on the ground.</summary>
    public FlightMotion Flight => _flight;

    /// <summary>Height above the terrain, m, updated while flying. For the HUD and proximity.</summary>
    public float Clearance { get; private set; }

    /// <summary>The vehicle instance, for the feel layer to read its tuning (null on foot).</summary>
    public Rideable? Vehicle => _ride;

    /// <summary>
    /// For the flight probe: put an already-mounted craft in the air at a position and velocity,
    /// as if it had taken off. A plane needs a runway and a paraglider a launch slope, and neither
    /// is what a test of the flight model is about.
    /// </summary>
    public void DebugLaunch(Vector3 position, Vector3 velocity)
    {
        GlobalPosition = position;
        _flight.Velocity = velocity;
        Velocity = velocity;
        // pointed where it is going: a dive is a dive, not level flight with a sink rate
        if (_ride is Plane && velocity.LengthSquared() > 1f)
        {
            _flight.Attitude = Flyer.Orient(velocity, Vector3.Up, Vector3.Forward);
            _flight.Airspeed = velocity.Length();
            _flight.Spin = 0f;
        }
        _settle = 0f;
    }

    /// <summary>Raise an announcement from outside (the feel layer's proximity score).</summary>
    public void Announce(string text, bool good) => Announced?.Invoke(text, good);

    /// <summary>Flying anything (wingsuit, canopy, helicopter, plane).</summary>
    public bool IsFlying => _ride is Flyer;

    /// <summary>The look input turns the craft instead of looking around it (helicopter).</summary>
    private bool LookSteersRide => _ride is Flyer { LookSteers: true };

    /// <summary>Base-jump deploy needs this much air under the feet, m.</summary>
    private const float DeployClearance = 12f;

    /// <summary>A landing verdict or trick name to show; true for a good one.</summary>
    public event Action<string, bool>? Announced;

    // --- vehicle state while driving, and the player's own health ---
    /// <summary>The engine of the vehicle being driven is running. Entering starts it.</summary>
    public bool EngineOn { get; private set; } = true;

    /// <summary>Hit points of the vehicle being driven; at zero it goes up.</summary>
    public float VehicleHealth { get; private set; } = 100f;

    public const float MaxHealth = 100f;
    public float Health { get; private set; } = MaxHealth;

    /// <summary>Hurt by this much (the feel layer flashes and shakes).</summary>
    public event Action<float>? Hurt;
    /// <summary>Shaken by something nearby — an explosion — at this strength, 0..1.</summary>
    public event Action<float>? Shaken;
    public event Action<bool>? EngineToggled;

    private double _sinceHurt = 99;

    /// <summary>
    /// Seconds since being thrown from a wreck. Its own explosion goes off beside the pilot, and
    /// at full strength it would kill every time; "thrown clear, hurt" is the design, so the hit
    /// is capped — a crash costs about half your health, and only kills someone already hurt.
    /// </summary>
    private double _ejected;
    private const float EjectBlastCap = 55f;
    private Vector3 _lastSafe;
    private bool _hasSafe;
    private double _safeTimer;
    private float _deadTimer;

    /// <summary>Seconds left in which a fresh ride's contacts are not impacts (see ApplyRide).</summary>
    private float _settle;
    private const float SettleTime = 1f;

    /// <summary>How close a parked vehicle has to be to get into it, m.</summary>
    private const float EnterReach = 3.5f;

    private const float FlipRate = 5.0f;         // rad/s: a backflip in ~1.3 s of air
    private const float SpinRate = 6.5f;         // rad/s: a 360 in ~1 s
    private const float SettleRate = 3.5f;       // rad/s: an unfinished rotation eases home
    private const float CleanAngle = 0.5f;       // within ~30 deg of upright: clean
    private const float SloppyAngle = 1.1f;      // within ~63 deg: ridden away from, badly
    private const float BoostAccel = 7f;         // m/s^2 while boosting
    private const float BoostDrain = 0.4f;       // meter per second

    /// <summary>
    /// Replaces the keyboard while mounted, when set.
    ///
    /// <para>
    /// A home trainer <i>is</i> an input device: it reports watts and cadence, which is what the
    /// throttle means to <see cref="Bicycle"/>. Routing it through here rather than into the
    /// vehicle keeps one movement path for a keyboard rider and a pedalling one, so a bug can
    /// only be in one of them. <see cref="RideProbe"/> uses the same seam to ride headlessly.
    /// </para>
    /// </summary>
    public Func<RideInput>? RideControls { get; set; }

    private Camera3D? _camera;
    private CollisionShape3D _body = null!;
    private CapsuleShape3D _capsule = null!;
    private CapsuleShape3D? _standProbe;
    private float _pitch;
    private bool _placed;
    private double _sinceSnapWarning = 99;

    // --- walk feel state ---
    private float _bobPhase;
    private float _bobStrength;
    private float _landingDip;
    private float _landingVelocity;
    private bool _wasOnFloor = true;
    private float _fallSpeed;
    private float _speedSmoothed;

    // --- slide / wall jump state ---
    private bool _sliding;
    private float _slideTime;
    private float _slideCooldown;
    private float _slideBlend;      // 0 standing, 1 fully down — drives the camera only
    private bool _jumpHeld;
    private bool _crouchHeld;

    /// <summary>
    /// Pad sprint is a click of L3 that lasts until the stick is let go, the convention on every
    /// console shooter: holding a stick button down while also steering with it is a cramp.
    /// </summary>
    private bool _sprintLatch;
    private bool _sprintHeld;
    private int _wallJumps;
    private Vector3 _lastWallNormal = Vector3.Zero;
    private float _wallCoyote;
    private Vector3 _coyoteNormal = Vector3.Zero;
    private float _jumpBuffer;

    /// <summary>
    /// Ground coyote time: a jump pressed just after running off an edge still counts. Without
    /// it a jump timed to a lip lands a frame late and the player simply walks off the edge,
    /// the commonest "the controls ate my input" complaint there is.
    /// </summary>
    private float _groundCoyote;

    /// <summary>Seconds of being dazed after a crash: no movement input, on foot.</summary>
    private float _stunTimer;
    private const float GroundCoyoteTime = 0.12f;

    // --- mantle: pulling up onto a ledge ---
    private bool _mantling;
    private float _mantleT;
    private Vector3 _mantleFrom, _mantleRise, _mantleTo, _mantleForward;
    private float _mantleExitSpeed;
    private const float MantleMin = 0.45f;   // below this it is a step, and floor snapping takes it
    private const float MantleMax = 2.1f;    // a hand over a wall a head taller than you
    private const float MantleTime = 0.36f;

    public Camera3D Camera => _camera!;

    /// <summary>True while sliding — for footstep/scrape audio and third-person poses.</summary>
    public bool IsSliding => _sliding;

    /// <summary>Rises to 1 with each footfall — hook sounds or footstep effects here.</summary>
    public float StepPhase => _bobPhase;

    /// <summary>
    /// Re-runs the drop-onto-the-ground pass. Called after a teleport, where the body has
    /// been put down over terrain that has not streamed in yet.
    /// </summary>
    public void RequestReplacement()
    {
        _placed = false;

        // a teleport while falling is not a landing: arriving must not charge for the fall
        _fallSpeed = 0f;
        Velocity = Vector3.Zero;

        // A teleport is not a descent: arriving somewhere new at 60 km/h because that is how
        // fast you left is how a rider ends up in a lake.
        _motion.Speed = 0f;

        // a teleport mid-slide would otherwise land you crouched in the new place, with the
        // capsule still short and no ground contact to end the slide against
        if (_sliding && _body is not null)
        {
            _sliding = false;
            _slideCooldown = 0;
            SetBodyHeight(StandHeight);
        }
    }

    /// <summary>
    /// A body is what needs ground under it, so it registers itself as a collision anchor
    /// rather than relying on whoever spawned it to remember - the ride probe did not.
    /// </summary>
    public override void _EnterTree()
    {
        if (Terrain != null && !Terrain.HasAnchor(this)) Terrain.AddAnchor(this, collision: true);
    }

    public override void _ExitTree()
    {
        Terrain?.RemoveAnchor(this);
        Explosion.Blast -= OnBlast;
    }

    /// <summary>
    /// What the camera pull-in rays test: everything the body collides with except tree trunks.
    /// A chase camera in a forest shoved into the rider's head by a trunk it can see past is
    /// worse than a trunk briefly between lens and rider — the tree shader dissolves that anyway.
    /// </summary>
    private uint CameraMask => CollisionMask & ~World.TreeColliders.Layer;

    public override void _Ready()
    {
        CollisionMask |= World.TreeColliders.Layer;   // trunks are solid (layer 2)
        // authority pushes its transform to everyone else (server relays)
        var replication = new SceneReplicationConfig();
        replication.AddProperty(".:position");
        replication.AddProperty(".:rotation");
        // What you are riding travels with where you are. Without it a remote client sees a
        // figure sprinting down a descent at 60 km/h in a running pose.
        replication.AddProperty(".:RideKindId");
        replication.AddProperty(".:HeldItemId");
        var sync = new MultiplayerSynchronizer
        {
            // deterministic name: replication matches nodes by path across peers, and
            // auto-generated names (@MultiplayerSynchronizer@N) differ per process
            Name = "Sync",
            RootPath = new NodePath(".."),
            ReplicationConfig = replication,
        };
        // the synchronizer's own authority decides who sends; children added after the
        // parent's SetMultiplayerAuthority default to server authority
        sync.SetMultiplayerAuthority(GetMultiplayerAuthority());
        // Interest management: this player's position goes only to peers in the same space —
        // the same interior, or both outdoors — plus the server, which relays and answers /tp.
        // The table is the server's (see InteriorManager), never this node's replicated state.
        if (IsMultiplayerAuthority())
            sync.AddVisibilityFilter(Callable.From((long peer) =>
                peer == 1 || Interiors.InteriorManager.Instance?.SameSpaceAsLocal(peer) != false));
        AddChild(sync);

        AddToGroup(Group);

        // kept in a field: sliding shrinks it, so the player fits under things a standing
        // body does not, and standing back up has to be tested against the world first
        _capsule = new CapsuleShape3D { Radius = BodyRadius, Height = StandHeight };
        _body = new CollisionShape3D
        {
            Shape = _capsule,
            Position = new Vector3(0, StandHeight * 0.5f, 0),
        };
        AddChild(_body);

        // alpine slopes are steep; without this you slide off anything interesting.
        // Floor snapping keeps contact when walking downhill instead of hopping.
        FloorMaxAngle = Mathf.DegToRad(52f);
        FloorSnapLength = 0.5f;
        FloorBlockOnWall = false;
        SlideOnCeiling = true;

        Terrain ??= GetNodeOrNull<ChunkManager>("/root/Main/World/Terrain");

        if (IsMultiplayerAuthority())
        {
            _camera = new Camera3D
            {
                // named explicitly: auto names differ per process, which this project has
                // already been bitten by on the networking side
                Name = "Camera",
                Position = new Vector3(0, EyeHeight, 0),
                Near = 0.08f,
                Far = Core.GameSettings.Current.CameraFar,
                Fov = BaseFov,
            };
            AddChild(_camera);
            _camera.Current = true;
            _viewYaw = Rotation.Y;
            Explosion.Blast += OnBlast;

            // sound, shake, speed lines, particles and the HUD - the local player only
            AddChild(new PlayerFeel(this));
        }
        else
        {
            // Remote player: a figure rather than a capsule, so you can tell which way someone
            // is facing. The transform still comes from the synchronizer; this is only what it
            // moves. Colour is derived from the peer id, which is unique and already agreed by
            // every client, so two players never end up in the same jersey.
            SetPhysicsProcess(false);
            SetProcessUnhandledInput(false);
        }

        RefreshVisual();

        // every copy draws what is in the hand; only the local one also has a viewmodel
        AddChild(new Items.HeldItemVisual(this) { Name = "HeldItem" });
    }

    /// <summary>
    /// Rebuilds the body mesh when the ride changes, or the view does.
    ///
    /// <para>
    /// The local player has no visual on foot in first person — that view is from inside their
    /// own head — but has one in third person, and always has one mounted, where there would
    /// otherwise be a bicycle-shaped hole where the rider is.
    /// </para>
    /// </summary>
    private void RefreshVisual(bool force = false)
    {
        var kind = (RideKind)RideKindId;
        if (!force && _visual != null && kind == _visualKind) return;

        _visual?.QueueFree();
        _visual = null;
        _walker = null;
        HandLocal = null;
        _visualKind = kind;

        int rider = GetMultiplayerAuthority();

        if (kind == RideKind.OnFoot)
        {
            if (IsMultiplayerAuthority() && !_thirdPerson) return;   // first person: nothing to draw
            _walkPalette = Avatar.HumanPalette.ForRider(rider);
            _walker = new MeshInstance3D
            {
                Name = "Body",
                Mesh = Avatar.HumanMeshBuilder.BuildStride(_walkPalette, 0f, 0f),
                MaterialOverride = Avatar.HumanMeshBuilder.Material(),
            };
            _visual = _walker;
        }
        else
        {
            _walker = null;
            _visual = (_ride ?? Rideable.Create(kind))?.BuildVisual(rider);
        }

        if (_visual != null)
        {
            _visual.Name = "Body";
            AddChild(_visual);
            // a craft's mesh is not authored level (the wingsuit is an upright figure); pose it
            // level for a remote copy, which only receives position and yaw
            if (_ride == null && Rideable.Create(kind) is Flyer remoteFlyer)
                remoteFlyer.Pose(_visual, 0f, default);
        }
    }

    /// <summary>
    /// Remote players get no physics, so the replicated ride kind has to be polled. It changes
    /// perhaps twice a minute; comparing an int per frame is cheaper than an RPC to announce it.
    /// </summary>
    public override void _Process(double delta)
    {
        if (IsMultiplayerAuthority())
        {
            float dt = (float)delta;
            ApplyStickLook(dt);
            if (_ride != null)
            {
                if (_visual != null)
                {
                    if (_ride is Flyer f) f.AnimateFlight(_visual, _flight, dt);
                    else _ride.Animate(_visual, _motion, dt);
                }
                return;
            }

            // Render rate, not physics rate: the look has to answer the mouse the frame it
            // moves, the way rotating the body directly always did.
            if (!_thirdPerson)
                Rotation = new Vector3(0, _viewYaw, 0);
            else if (ScopeView && _camera != null)
            {
                // looking through something held to the eye: first person for as long as it lasts
                Rotation = new Vector3(0, _viewYaw, 0);
                _camera.Transform = new Transform3D(new Basis(Vector3.Right, _pitch),
                    new Vector3(0, EyeHeight + _landingDip, 0));
                if (_walker != null) _walker.Visible = false;
                HandLocal = null;
                _pivotY = float.NaN;
            }
            else
            {
                if (_walker != null) _walker.Visible = true;
                AnimateLocalBody(dt);
                UpdateThirdPersonCamera(dt);
            }
            return;
        }

        // someone in another building (or out while we are in) is not here: hidden, and their
        // last replicated position must not stand in a doorway as an invisible wall
        bool here = Interiors.InteriorManager.Instance?.SameSpaceAsLocal(GetMultiplayerAuthority()) != false;
        if (Visible != here)
        {
            Visible = here;
            _body.Disabled = !here;
        }
        if (!here) return;

        RefreshVisual();
        AnimateRemoteWalk((float)delta);
    }

    /// <summary>
    /// Walks the remote figure's legs at whatever speed it is actually covering ground.
    ///
    /// <para>
    /// Speed is measured from the replicated position rather than sent: it is already implied by
    /// the transform stream, and a second synchronised property would only give the two ways to
    /// disagree. Smoothed, because that stream arrives at the network's rate rather than the
    /// frame rate, so the raw difference is zero on most frames and a spike on the rest.
    /// </para>
    /// </summary>
    private void AnimateRemoteWalk(float dt)
    {
        if (_walker == null || dt <= 0) return;

        var here = GlobalPosition;
        float measured = new Vector2(here.X - _lastSeenPosition.X, here.Z - _lastSeenPosition.Z)
            .Length() / dt;
        _lastSeenPosition = here;

        // reject the teleport-sized jumps a respawn or a rebase produces
        if (measured > 40f) measured = _seenSpeed;
        _seenSpeed = Mathf.Lerp(_seenSpeed, measured, 1f - Mathf.Exp(-6f * dt));

        _stridePhase = Avatar.HumanMeshBuilder.AdvancePhase(_stridePhase, _seenSpeed, dt);
        _walker.Mesh = Avatar.HumanMeshBuilder.BuildStride(_walkPalette, _seenSpeed, _stridePhase);
        PlaceHand(Avatar.HumanMeshBuilder.MountsFor(_seenSpeed, _stridePhase));
    }

    /// <summary>
    /// Records where the figure's right hand is, from the same rig the mesh was just built from,
    /// so a held item swings with the arm instead of floating beside it.
    /// </summary>
    private void PlaceHand(Avatar.HumanMeshBuilder.GaitMounts mounts)
    {
        if (_walker == null) { HandLocal = null; return; }
        // mounts are already turned to face -Z, where the figure's right is +X
        var hand = mounts.HandL.X > mounts.HandR.X ? mounts.HandL : mounts.HandR;
        HandLocal = _walker.Transform * new Transform3D(Basis.Identity, hand);
    }

    /// <summary>
    /// Poses the local on-foot body for third person: the solved gait while grounded, a
    /// mid-stride leap in the air, and a crouch while sliding — the same figure and the same
    /// gait remote players already see, so what you watch yourself do is what they see too.
    /// </summary>
    private void AnimateLocalBody(float dt)
    {
        if (_walker == null) return;

        _airTime = IsOnFloor() ? 0f : _airTime + dt;
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();

        Avatar.HumanMeshBuilder.GaitMounts mounts;
        if (_sliding)
        {
            _walker.Mesh = _slidePose ??= Avatar.HumanMeshBuilder.Build(_walkPalette, Avatar.HumanPose.Tucked);
            mounts = Avatar.HumanMeshBuilder.MountsForPose(Avatar.HumanPose.Tucked);
        }
        else if (_airTime > 0.12f)
        {
            _walker.Mesh = _airPose ??= Avatar.HumanMeshBuilder.Build(_walkPalette, Avatar.HumanPose.Running);
            mounts = Avatar.HumanMeshBuilder.MountsForPose(Avatar.HumanPose.Running);
        }
        else
        {
            _stridePhase = Avatar.HumanMeshBuilder.AdvancePhase(_stridePhase, speed, dt);
            _walker.Mesh = Avatar.HumanMeshBuilder.BuildStride(_walkPalette, speed, _stridePhase);
            mounts = Avatar.HumanMeshBuilder.MountsFor(speed, _stridePhase);
        }

        // Landing squash: the same spring that dips the first-person eye, spent on the body's
        // proportions instead. Volume is roughly kept, so it reads as knees taking the weight
        // rather than the figure shrinking.
        float squash = Mathf.Clamp(-_landingDip * 1.2f, 0f, 0.22f);
        _walker.Scale = new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f);

        // thrown, stunned or knocked out: flat on the ground
        float down = _stunTimer > 0 && IsOnFloor() ? -1.45f : 0f;
        _walker.Rotation = new Vector3(Mathf.Lerp(_walker.Rotation.X, down, 1f - Mathf.Exp(-10f * dt)), 0, 0);
        _walker.Position = new Vector3(0, Mathf.Abs(_walker.Rotation.X) * 0.12f, 0);
        PlaceHand(mounts);
    }

    /// <summary>
    /// Over-the-shoulder camera on foot: a spring arm from a point above the right shoulder,
    /// orbiting on the view yaw and pitch, pulled in when geometry is in the way.
    ///
    /// <para>
    /// Set in global space, because the body under it turns independently to face its travel —
    /// a camera parented to that turn would swing round every time the player changed direction.
    /// </para>
    /// </summary>
    private void UpdateThirdPersonCamera(float dt)
    {
        if (_camera == null) return;

        // The pivot drops with the slide, and its height is eased so a step, a kerb or the top
        // of a jump does not jerk the whole picture; far off (a teleport), it snaps.
        float pivotTarget = GlobalPosition.Y + Mathf.Lerp(ShoulderHeight, 0.95f, _slideBlend);
        _pivotY = float.IsNaN(_pivotY) || Mathf.Abs(pivotTarget - _pivotY) > 6f
            ? pivotTarget
            : Mathf.Lerp(_pivotY, pivotTarget, 1f - Mathf.Exp(-10f * dt));
        var pivot = new Vector3(GlobalPosition.X, _pivotY, GlobalPosition.Z);

        var view = new Basis(Vector3.Up, _viewYaw) * new Basis(Vector3.Right, _pitch);

        // pulled back a little with speed, so a sprint and a slide feel like they cover ground
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();
        float distance = ArmLength + Mathf.Clamp(speed / RunSpeed, 0f, 1.6f) * 0.6f;

        var shoulder = pivot + view.X * ShoulderOffset;
        var wanted = shoulder + view.Z * distance;

        // cast from the body's centre, not the shoulder, so a wall at the player's right does not
        // leave the lens behind it
        float want = 1f;
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
            pivot, wanted, CameraMask, new Godot.Collections.Array<Rid> { GetRid() }));
        if (hit.Count > 0)
        {
            float span = Mathf.Max(0.01f, (wanted - pivot).Length());
            want = Mathf.Clamp(((hit["position"].AsVector3() - pivot).Length() - 0.25f) / span, 0.1f, 1f);
        }
        // snap in, ease out: late at a wall is a frame with the lens inside it
        _armBlend = want < _armBlend ? want : Mathf.Lerp(_armBlend, want, 1f - Mathf.Exp(-5f * dt));

        var position = pivot.Lerp(wanted, _armBlend) + Vector3.Up * _landingDip * 0.5f;
        _camera.GlobalTransform = new Transform3D(view, position);
    }

    /// <summary>Switches first/third person, rebuilding the local body and saving the choice.</summary>
    private void ToggleView()
    {
        _thirdPerson = !_thirdPerson;
        Core.GameSettings.Current.ThirdPerson = _thirdPerson;
        Core.GameSettings.Current.Save();

        if (_ride == null)
        {
            // hand the view over without a jump: first person faced where the body did
            if (!_thirdPerson && _camera != null)
                _camera.Transform = new Transform3D(Basis.Identity, new Vector3(0, EyeHeight, 0));
            _pitch = Mathf.Clamp(_pitch, -1.2f, 1.2f);
        }
        _pivotY = float.NaN;
        _armBlend = 1f;
        RefreshVisual(force: true);
    }

    /// <summary>
    /// Mounts, dismounts, or swaps. Returns false when it cannot be done right now.
    ///
    /// <para>
    /// Refused in mid-air and while sliding, and refused above <see cref="Rideable.DismountSpeed"/>:
    /// stepping off skis at 70 km/h is not a dismount, and allowing it makes the descent
    /// consequence-free. Getting <i>on</i> is refused at speed for the same reason — a bike
    /// materialising under a sprinting player is how you clip through a wall.
    /// </para>
    /// </summary>
    public bool SetRide(RideKind kind)
    {
        if (kind == (RideKind)RideKindId) return true;
        if (!IsOnFloor() || _sliding || Indoors) return false;

        float speed = new Vector2(Velocity.X, Velocity.Z).Length();
        float limit = _ride?.DismountSpeed ?? RunSpeed + 0.5f;
        if (speed > limit) return false;

        ApplyRide(kind, Velocity);
        if (_ride is { IsVehicle: true } fresh)
        {
            EngineOn = true;
            VehicleHealth = fresh.MaxHealth;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------
    // getting in and out
    // ------------------------------------------------------------------------------------

    private VehicleManager? Vehicles => VehicleManager.Instance;

    /// <summary>
    /// E / Y. In a vehicle: get out. On foot next to one: get in. Otherwise false, and the caller
    /// opens the picker. Equipment (skis, canopies) is not a vehicle and is not left behind.
    /// </summary>
    public bool TryInteract()
    {
        if (_ride is { IsVehicle: true })
        {
            ExitVehicle();
            return true;
        }
        // inside, E is the front door or nothing: no mount picker in a living room
        // (or the cupboard in front of you: searching comes first, the door is by the door)
        if (Indoors)
        {
            var interiors = Interiors.InteriorManager.Instance;
            if (interiors?.AtExit(this) != true && Loot.LootService.Instance?.TrySearch(this) == true) return true;
            return interiors?.TryExit(this) ?? true;
        }
        if (_ride != null || _mantling || _deadTimer > 0) return false;

        var vehicle = Vehicles?.Nearest(GlobalPosition, EnterReach);
        if (vehicle == null)
            return IsOnFloor() && Interiors.InteriorManager.Instance?.TryEnter(this) == true;
        Vehicles!.Claim(vehicle, EnterVehicle);
        return true;
    }

    /// <summary>Takes over a vehicle from the world: its position, heading, momentum and damage.</summary>
    private void EnterVehicle(VehicleState state)
    {
        if (_sliding) EndSlide();
        GlobalPosition = state.Position;
        Rotation = new Vector3(0, state.Yaw, 0);
        ApplyRide(state.Kind, state.Velocity);
        _flight.Control = state.Throttle;
        EngineOn = true;
        VehicleHealth = state.Health;
        _placed = true;
    }

    /// <summary>The vehicle as it is right now, to hand to the world.</summary>
    private VehicleState CaptureVehicle(bool wrecked)
    {
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        var velocity = _ride is Flyer
            ? _flight.Velocity
            : heading * _motion.Speed + Vector3.Up * Velocity.Y;
        return new VehicleState((RideKind)RideKindId, GlobalPosition,
            _ride is Flyer ? _flight.Yaw : Rotation.Y, velocity,
            wrecked ? 0f : VehicleHealth, EngineOn && !wrecked, wrecked, _flight.Control, VehicleState.Now);
    }

    /// <summary>
    /// Gets out, anywhere — including in the air. The vehicle stays in the world with its
    /// momentum; the player steps out beside the seat with the same momentum and a small push
    /// clear, so jumping out of a helicopter at altitude is a skydive, and Jump opens the suit.
    /// </summary>
    public void ExitVehicle()
    {
        if (_ride is not { IsVehicle: true } vehicle) return;
        var state = CaptureVehicle(wrecked: false);
        var right = GlobalTransform.Basis.X with { Y = 0 };
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        // clear of the whole machine — past the wing of a plane, not 2 m into it
        float side = Mathf.Max(vehicle.BodyRadius, vehicle.ParkedBox.Size.X * 0.5f) + BodyRadius + 0.5f;
        bool grounded = IsOnFloor();

        Vehicles?.Park(state);

        ApplyRide(RideKind.OnFoot, state.Velocity + right * 2f);
        GlobalPosition = FindExit(state.Position, right, side, grounded);
    }

    /// <summary>
    /// A clear spot beside the vehicle: its right, else its left, else on top. In the air there
    /// is nothing to stand on either side, so the right side it is.
    /// </summary>
    private Vector3 FindExit(Vector3 at, Vector3 right, float side, bool grounded)
    {
        if (!grounded) return at + right * side;
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        foreach (var raw in new[] { at + right * side, at - right * side, at + Vector3.Up * 2.8f })
        {
            // on a slope the ground beside the seat is not at the seat's height: stand on it,
            // or the uphill side reads as blocked and the player is put on the vehicle's roof
            var candidate = raw;
            if (raw.Y <= at.Y + 0.01f && Terrain != null && Terrain.TryGetHeight(raw, out float g))
                candidate = raw with { Y = Mathf.Max(raw.Y, g) };
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = _standProbe,
                Transform = new Transform3D(Basis.Identity, candidate + Vector3.Up * (StandHeight * 0.5f + 0.1f)),
                CollisionMask = CollisionMask,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };
            if (GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0)
                return candidate + Vector3.Up * 0.1f;
        }
        return at + Vector3.Up * 3f;
    }

    /// <summary>
    /// The vehicle is destroyed with the player in it: it goes up where it stands, and the player
    /// is thrown clear — the blast itself does the hurting (<see cref="OnBlast"/>).
    /// </summary>
    private void WreckVehicle()
    {
        var state = CaptureVehicle(wrecked: true);
        var away = -state.Velocity with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : GlobalTransform.Basis.Z;

        Announced?.Invoke("WRECKED!", false);
        PlayerInput.Rumble(1f, 1f, 0.6f);
        ApplyRide(RideKind.OnFoot, state.Velocity * 0.25f + away * 5f + Vector3.Up * 7f);
        GlobalPosition = state.Position + Vector3.Up * 1.5f + away * 1.5f;
        _stunTimer = 1.5f;
        _ejected = 2.0;
        Vehicles?.Park(state);
        if (Vehicles == null) Explosion.Spawn(GetParent(), state.Position + Vector3.Up);
    }

    // ------------------------------------------------------------------------------------
    // health
    // ------------------------------------------------------------------------------------

    public void TakeDamage(float amount)
    {
        if (amount <= 0 || _deadTimer > 0) return;
        Health = Mathf.Max(0f, Health - amount);
        _sinceHurt = 0;
        Hurt?.Invoke(amount);
        PlayerInput.Rumble(0.6f, Mathf.Clamp(amount / 40f, 0.2f, 1f), 0.25f);
        if (Health <= 0f) Die();
    }

    /// <summary>Restores health (food, water). Returns false when there was nothing to restore.</summary>
    public bool Heal(float amount)
    {
        if (amount <= 0 || _deadTimer > 0 || Health >= MaxHealth - 0.01f) return false;
        Health = Mathf.Min(MaxHealth, Health + amount);
        return true;
    }

    /// <summary>
    /// Knocked out: down for a few seconds, then back on your feet where you last stood safely.
    /// Not a reload — the world, the wrecks and whatever you left parked are all still there.
    /// </summary>
    private void Die()
    {
        if (_ride is { IsVehicle: true }) WreckVehicle();
        else if (_ride != null) ApplyRide(RideKind.OnFoot, Velocity);
        Announced?.Invoke("KNOCKED OUT", false);
        _deadTimer = 3.5f;
        _stunTimer = 3.5f;
    }

    private void Revive()
    {
        Health = MaxHealth;
        if (_hasSafe) GlobalPosition = _lastSafe + Vector3.Up * 0.5f;
        Velocity = Vector3.Zero;
        RequestReplacement();
    }

    /// <summary>Regeneration, the safe spot to wake up at, and the respawn countdown.</summary>
    private void TickHealth(float dt, bool onFloor)
    {
        if (_ejected > 0) _ejected -= dt;
        _sinceHurt += dt;
        if (_sinceHurt > 6 && Health < MaxHealth && _deadTimer <= 0)
            Health = Mathf.Min(MaxHealth, Health + 12f * dt);

        _safeTimer += dt;
        if (_safeTimer > 2 && _ride == null && onFloor && Health > 30f && _stunTimer <= 0
            && Velocity.LengthSquared() < 40f)
        {
            _safeTimer = 0;
            _lastSafe = GlobalPosition;
            _hasSafe = true;
        }

        if (_deadTimer > 0)
        {
            _deadTimer -= dt;
            if (_deadTimer <= 0) Revive();
        }
    }

    /// <summary>Every explosion anywhere: this player decides what it cost them.</summary>
    private void OnBlast(Vector3 at)
    {
        float d = GlobalPosition.DistanceTo(at);
        if (d < Explosion.ShockRadius) Shaken?.Invoke(1f - d / Explosion.ShockRadius);
        if (d >= Explosion.DamageRadius) return;

        float t = 1f - d / Explosion.DamageRadius;
        if (_ride is { IsVehicle: true } && d > 2.5f)
        {
            // a vehicle takes the blast for its occupant, up to a point
            VehicleHealth -= 90f * t;
            if (VehicleHealth <= 0) WreckVehicle();
            return;
        }
        float damage = 75f * t;
        if (_ejected > 0) damage = Mathf.Min(damage, EjectBlastCap);
        TakeDamage(damage);
        if (_ride == null)
        {
            var away = (GlobalPosition - at) with { Y = 0 };
            away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
            Velocity += (away * 7f + Vector3.Up * 5f) * t;
            _stunTimer = Mathf.Max(_stunTimer, 1.0f * t);
        }
    }

    /// <summary>
    /// Switches what the player is travelling as, with no checks — <see cref="SetRide"/> does
    /// those for the picker; a base jump and a canopy opening call this directly mid-air.
    /// </summary>
    private void ApplyRide(RideKind kind, Vector3 velocity)
    {
        _ride = Rideable.Create(kind);
        RideKindId = (int)kind;

        // Momentum carries across the change: freewheeling to a halt and stepping off should
        // leave you walking, not standing still, and the reverse is what makes a rolling start
        // off a slide feel continuous.
        float speed = new Vector2(velocity.X, velocity.Z).Length();
        _motion = new RideMotion { Speed = speed, Yaw = Rotation.Y, Lean = 0f };
        // Getting off, the view carries on looking where it was — the body's heading plus
        // whatever free look was held — rather than snapping to the bike's nose.
        if (kind == RideKind.OnFoot) _viewYaw = Rotation.Y + _lookYaw;
        if (_ride is Flyer flyer)
        {
            flyer.Begin(ref _flight, velocity, Rotation.Y);
            _camFwd = -GlobalTransform.Basis.Z;
            // the helicopter turns to the camera; start the camera where the nose already is
            if (flyer.LookSteers) _viewYaw = Rotation.Y;
        }
        else _flight = default;
        // a craft skimming the ground must not be snapped onto it
        FloorSnapLength = _ride is Flyer ? 0.05f : 0.5f;

        // the body is the machine's size while in it — a helicopter is not a 0.3 m person
        if (_capsule != null && !_sliding)
        {
            float radius = _ride?.BodyRadius ?? BodyRadius;
            // A body that just grew starts partly inside a slope: lift it clear, and let it
            // settle before anything it touches counts as a crash (_settle). Without both, the
            // solver's shove out of the hillside read as a 20 m/s impact and wrecked a plane
            // the instant it was mounted.
            if (radius > _capsule.Radius + 0.05f) GlobalPosition += Vector3.Up * (radius - _capsule.Radius);
            _capsule.Radius = radius;
            SetBodyHeight(_ride?.BodyHeight ?? StandHeight);
        }
        _settle = SettleTime;
        Velocity = velocity;
        _lookYaw = 0f;
        _turnLag = 0f;
        _airPitch = _airSpin = _rideAir = _bailTimer = 0f;
        _pivotY = float.NaN;

        RefreshVisual();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        if (@event.IsActionPressed(PlayerInput.CameraToggle) && !@event.IsEcho())
        {
            ToggleView();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed(PlayerInput.EngineToggle) && !@event.IsEcho() && _ride is { HasEngine: true })
        {
            EngineOn = !EngineOn;
            EngineToggled?.Invoke(EngineOn);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Mounted, the body's yaw belongs to the steering — a bicycle goes where it points,
            // and letting the mouse turn it would mean looking over your shoulder steered you
            // into the ditch. The mouse gets its own yaw, which recentres itself.
            float rate = 0.0022f * LookScale;
            if (_ride != null && !LookSteersRide)
                _lookYaw = Mathf.Clamp(_lookYaw - motion.Relative.X * rate, -2.4f, 2.4f);
            else
                _viewYaw -= motion.Relative.X * rate;

            _pitch = ClampPitch(_pitch - motion.Relative.Y * rate);
        }
    }

    /// <summary>
    /// Right-stick look. The same two outlets as the mouse — body yaw on foot, free-look yaw when
    /// mounted — but integrated as a rate per frame, because a stick is held, not flicked.
    /// </summary>
    private void ApplyStickLook(float dt)
    {
        var look = PlayerInput.LookRate * LookScale;
        if (look == Vector2.Zero) return;

        if (_ride != null && !LookSteersRide)
            _lookYaw = Mathf.Clamp(_lookYaw - look.X * dt, -2.4f, 2.4f);
        else
            _viewYaw -= look.X * dt;

        _pitch = ClampPitch(_pitch - look.Y * dt);
    }

    /// <summary>
    /// Straight up and down in first person; narrower in third, where looking steeply up puts
    /// the lens in the ground and steeply down puts it on top of your own head.
    /// </summary>
    private float ClampPitch(float pitch) => _ride is Flyer
        ? Mathf.Clamp(pitch, -1.2f, 1.2f)
        : _thirdPerson && _ride == null
        ? Mathf.Clamp(pitch, -1.25f, 0.9f)
        : Mathf.Clamp(pitch, -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);

    public override void _PhysicsProcess(double delta)
    {
        // drop onto the terrain surface once its height data is available
        if (!_placed && !Indoors)
        {
            // and its collision: the fly camera does not ask for one, so the tile this body
            // was dropped onto may have a mesh and no ground to stand on for a few frames
            if (Terrain == null || !Terrain.TryGetHeight(GlobalPosition, out float g)
                || !Terrain.HasCollisionAt(GlobalPosition))
                return;
            GlobalPosition = new Vector3(GlobalPosition.X, Mathf.Max(GlobalPosition.Y, g + 1f), GlobalPosition.Z);
            _placed = true;
        }

        float dt = (float)delta;
        var velocity = Velocity;
        bool onFloor = IsOnFloor();
        TickHealth(dt, onFloor);

        // PlayerInput returns neutral while a text field has the keyboard, so typing in chat
        // does not walk the player around.
        if (_ride != null)
        {
            if (_ride is Flyer flyer) FlyPhysics(dt, onFloor, flyer);
            else RidePhysics(dt, onFloor);
            return;
        }

        if (_mantling)
        {
            StepMantle(dt);
            return;
        }

        var input = PlayerInput.Move;
        if (_stunTimer > 0)
        {
            _stunTimer -= dt;
            input = Vector2.Zero;
        }

        bool sprintDown = PlayerInput.Held(PlayerInput.Sprint);
        if (sprintDown && !_sprintHeld && PlayerInput.LastDevice == InputDevice.Gamepad) _sprintLatch = true;
        _sprintHeld = sprintDown;
        if (input.LengthSquared() < 0.09f) _sprintLatch = false;   // stick let go: back to a walk

        bool running = sprintDown || _sprintLatch;
        bool crouchHeld = PlayerInput.Held(PlayerInput.CrouchSlide);

        // Wall jumps and slide entries need the *edge*, not the state: the ground jump below
        // polls, so holding Space bunny-hops, and a polled wall jump would rocket you up a
        // cliff one physics frame at a time.
        //
        // Ctrl is edge-triggered for a related reason found by measuring it: a spent slide
        // ends at ~2 m/s, the walk accelerates you back over the entry threshold in about a
        // second, and a held key starts the next one — so holding Ctrl became a permanent
        // 7 m/s crouch-run. A slide is one move; you re-press to take another.
        bool spaceDown = PlayerInput.Held(PlayerInput.Jump);
        bool jumpPressed = spaceDown && !_jumpHeld;
        _jumpHeld = spaceDown;

        bool crouchPressed = crouchHeld && !_crouchHeld;
        _crouchHeld = crouchHeld;

        // A stick asks for part of the speed: tilt it halfway and you amble. The direction stays
        // unit length for the climb and wall-jump maths that use it.
        float moveAmount = Mathf.Min(input.Length(), 1f);
        // relative to the view, which is the body in first person and the camera in third
        var view = new Basis(Vector3.Up, _viewYaw);
        var direction = (view * new Vector3(input.X, 0, input.Y)).Normalized();
        if (_slideCooldown > 0) _slideCooldown -= dt;

        // Remember the last usable wall, and the last jump press, for a moment each. Contact
        // and input almost never line up on the same physics frame otherwise.
        _jumpBuffer = jumpPressed ? JumpBufferTime : Mathf.Max(0f, _jumpBuffer - dt);
        _wallCoyote = Mathf.Max(0f, _wallCoyote - dt);
        if (IsOnWall())
        {
            var contact = GetWallNormal();
            var flatContact = new Vector3(contact.X, 0, contact.Z);
            if (Mathf.Abs(contact.Y) <= WallMaxNormalY && flatContact.LengthSquared() > 0.0001f)
            {
                _coyoteNormal = flatContact.Normalized();
                _wallCoyote = WallCoyoteTime;
            }
        }

        // --- mantle -----------------------------------------------------------------------
        // Pushing into a wall with a ledge in reach: in the air it pulls you up on its own (jump
        // at a wall and you climb it, which is what a player means by jumping at it); on the
        // ground it takes a jump press, or walking into every garden wall would vault it.
        _groundCoyote = onFloor ? GroundCoyoteTime : Mathf.Max(0f, _groundCoyote - dt);
        if (!_sliding && _wallCoyote > 0 && direction != Vector3.Zero
            && direction.Dot(-_coyoteNormal) > 0.5f && (!onFloor || jumpPressed)
            && TryBeginMantle(-_coyoteNormal, new Vector2(velocity.X, velocity.Z).Length()))
        {
            _jumpBuffer = 0;
            return;
        }

        // --- enter / leave the slide -------------------------------------------------
        float flatSpeed = new Vector2(velocity.X, velocity.Z).Length();

        if (!_sliding && crouchPressed && onFloor && _slideCooldown <= 0
            && flatSpeed >= SlideEntrySpeed)
        {
            BeginSlide(ref velocity, flatSpeed);
        }

        if (_sliding)
        {
            _slideTime += dt;

            // A slide is committed movement: you steer it, you do not drive it. Only the
            // downhill pull and friction change your speed, which is the whole point —
            // it is how a descent turns into distance.
            if (onFloor)
            {
                var n = GetFloorNormal();
                var downhill = new Vector3(n.X, 0, n.Z);   // horizontal part of the normal points downhill
                velocity.X += downhill.X * Gravity * dt;
                velocity.Z += downhill.Z * Gravity * dt;

                // lateral steering only, so you can carve round a rock without pumping speed
                var lateral = view.X * input.X;
                velocity.X += lateral.X * SlideSteer * dt;
                velocity.Z += lateral.Z * SlideSteer * dt;

                var flat = new Vector3(velocity.X, 0, velocity.Z);
                float sp = flat.Length();
                if (sp > 0.001f)
                {
                    sp = Mathf.Max(0f, sp - SlideFriction * dt);
                    flat = flat.Normalized() * sp;
                    velocity.X = flat.X;
                    velocity.Z = flat.Z;
                }
                flatSpeed = sp;
            }

            bool wantEnd = jumpPressed || !crouchHeld || !onFloor
                || flatSpeed < SlideMinSpeed || _slideTime > SlideMaxTime;

            // Ending needs headroom, so releasing Ctrl inside a culvert keeps you down
            // rather than shoving the capsule up through the roof. That also means you
            // cannot jump out of a slide you could not stand up in.
            if (wantEnd && EndSlide() && jumpPressed)
            {
                velocity.Y = JumpVelocity;   // horizontal momentum is kept: only Y changes
                Jumped?.Invoke();
            }
        }

        // --- gravity and the ordinary jump -------------------------------------------
        if (!onFloor)
        {
            velocity.Y -= Gravity * dt;
            _fallSpeed = Mathf.Max(_fallSpeed, -velocity.Y);

            // Base jump: Jump again while falling, with height under you, and the wingsuit opens.
            // Not against a wall (that is a wall jump) and not on the way up (that is a hop).
            if (jumpPressed && _wallCoyote <= 0 && velocity.Y < -3f && _groundCoyote <= 0
                && Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float below)
                && GlobalPosition.Y - below > DeployClearance)
            {
                ApplyRide(RideKind.Wingsuit, velocity);
                Announced?.Invoke("WINGSUIT", true);
                return;
            }

            // just off an edge and not already rising from a jump: still a ground jump
            if (_groundCoyote > 0 && jumpPressed && velocity.Y <= 0.5f && !_sliding)
            {
                velocity.Y = JumpVelocity;
                _groundCoyote = 0;
                _jumpBuffer = 0;
                Jumped?.Invoke();
            }
            else if (_jumpBuffer > 0 && TryWallJump(ref velocity, direction)) _jumpBuffer = 0;
        }
        else
        {
            _wallJumps = 0;
            _lastWallNormal = Vector3.Zero;
            if (!_sliding && spaceDown)
            {
                velocity.Y = JumpVelocity;
                _groundCoyote = 0;
                // a held Space re-jumps on every landing; each is a jump worth hearing
                Jumped?.Invoke();
                _jumpBuffer = 0;   // spent here, so it cannot also fire a wall jump on the way up
            }
        }

        // --- ordinary walking / running ----------------------------------------------
        if (!_sliding)
        {
            float speed = (running ? RunSpeed : WalkSpeed) * moveAmount;

            // climbing costs speed: scale by how much of the move is uphill
            if (onFloor && direction != Vector3.Zero)
            {
                var floorNormal = GetFloorNormal();
                float climb = -direction.Dot(new Vector3(floorNormal.X, 0, floorNormal.Z));
                if (climb > 0)
                    speed *= Mathf.Lerp(1f, MaxClimbSlowdown, Mathf.Clamp(climb * 1.6f, 0f, 1f));
            }

            var flat = new Vector3(velocity.X, 0, velocity.Z);
            float sp = flat.Length();

            if (!onFloor && sp > RunSpeed * 1.05f)
            {
                // Airborne above running pace means a slide launch or a wall jump is in
                // flight. Steering it must not brake it, or every launch dies in the first
                // half second and the moves are pointless. Speed bleeds back to RunSpeed on
                // its own, so this adds distance, never a new top speed.
                if (direction != Vector3.Zero)
                    flat = (flat.Normalized() + direction * AirSteer * dt).Normalized() * sp;
                sp = Mathf.MoveToward(sp, RunSpeed, AirDrag * dt);
                flat = flat.Normalized() * sp;
                velocity.X = flat.X;
                velocity.Z = flat.Z;
            }
            else
            {
                // ease into the target velocity rather than snapping, so starts and stops read
                float accel = onFloor ? 12f : 2.5f;
                velocity.X = Mathf.MoveToward(velocity.X, direction.X * speed, accel * dt);
                velocity.Z = Mathf.MoveToward(velocity.Z, direction.Z * speed, accel * dt);
            }
        }

        Velocity = velocity;
        MoveAndSlide();

        // a slide that ran into a wall has no speed left to give
        if (_sliding && new Vector2(Velocity.X, Velocity.Z).Length() < SlideMinSpeed * 0.5f)
            EndSlide();

        if (_thirdPerson) FaceTravel(dt, direction);
        UpdateCameraFeel(dt, running, onFloor);
        ClampAboveTerrain(delta);
    }

    /// <summary>
    /// Looks for a ledge in front: open air above it at reach height, a walkable top between
    /// <see cref="MantleMin"/> and <see cref="MantleMax"/> above the feet, and room to stand on
    /// it. Starts the pull-up when all three hold.
    /// </summary>
    private bool TryBeginMantle(Vector3 forward, float speed)
    {
        var space = GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var feet = GlobalPosition;
        float reach = MantleMax + 0.25f;

        // nothing in the way at the height the hands reach over, or it is a wall, not a ledge
        var high = feet + Vector3.Up * reach;
        var over = high + forward * (BodyRadius + 0.45f);
        if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(high, over, CollisionMask, exclude)).Count > 0)
            return false;

        // down onto the top
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            over, over - Vector3.Up * (reach - MantleMin + 0.05f), CollisionMask, exclude));
        if (hit.Count == 0) return false;
        var top = hit["position"].AsVector3();
        if (hit["normal"].AsVector3().Y < 0.7f) return false;   // a slope, not something to stand on
        float height = top.Y - feet.Y;
        if (height < MantleMin || height > MantleMax) return false;

        // standing room once up there
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        var room = new PhysicsShapeQueryParameters3D
        {
            Shape = _standProbe,
            Transform = new Transform3D(Basis.Identity, top + Vector3.Up * (StandHeight * 0.5f + 0.06f)),
            CollisionMask = CollisionMask,
            Exclude = exclude,
        };
        if (space.IntersectShape(room, 1).Count > 0) return false;

        _mantling = true;
        _mantleT = 0;
        _mantleForward = forward;
        _mantleFrom = feet;
        _mantleRise = new Vector3(feet.X, top.Y + 0.08f, feet.Z);
        _mantleTo = top + forward * 0.3f + Vector3.Up * 0.05f;
        _mantleExitSpeed = Mathf.Max(speed, 2.5f);
        Velocity = Vector3.Zero;
        Mantled?.Invoke();
        return true;
    }

    /// <summary>
    /// The pull-up itself: straight up the face, then over the lip. Moved directly rather than
    /// through the solver: the capsule is pressed against the very wall it is climbing, which is
    /// exactly the contact MoveAndSlide exists to stop it passing.
    /// </summary>
    private void StepMantle(float dt)
    {
        _mantleT += dt / MantleTime;
        float t = Mathf.Min(_mantleT, 1f);
        const float split = 0.6f;
        GlobalPosition = t < split
            ? _mantleFrom.Lerp(_mantleRise, Smooth(t / split))
            : _mantleRise.Lerp(_mantleTo, Smooth((t - split) / (1 - split)));
        Velocity = Vector3.Zero;

        if (_thirdPerson) FaceTravel(dt, _mantleForward);
        UpdateCameraFeel(dt, false, false);

        if (_mantleT >= 1f)
        {
            _mantling = false;
            // step off the top still moving, so a mantle mid-run keeps the run going
            Velocity = _mantleForward * _mantleExitSpeed;
        }

        static float Smooth(float x) => x * x * (3 - 2 * x);
    }

    /// <summary>
    /// Third person: the body turns to face where it is going. Toward the input while there is
    /// some, so a turn starts the moment the stick moves rather than once the velocity has
    /// caught up; toward the velocity otherwise (a slide, a wall-jump launch); and it holds its
    /// heading standing still rather than spinning back to the camera.
    /// </summary>
    private void FaceTravel(float dt, Vector3 moveDirection)
    {
        var flat = new Vector3(Velocity.X, 0, Velocity.Z);
        Vector3 facing;
        if (!_sliding && moveDirection.LengthSquared() > 0.01f) facing = moveDirection;
        else if (flat.LengthSquared() > 0.25f) facing = flat.Normalized();
        else return;

        // -Z is forward for a node, so the yaw that points it along 'facing'
        float target = Mathf.Atan2(-facing.X, -facing.Z);
        // a little slower airborne: you can steer a jump, not pirouette in it
        float rate = IsOnFloor() ? BodyTurnRate : BodyTurnRate * 0.4f;
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, target, 1f - Mathf.Exp(-rate * dt)), 0);
    }

    /// <summary>Safety net: never end up under the terrain surface, on foot or mounted.</summary>
    private void ClampAboveTerrain(double delta)
    {
        if (Indoors || Terrain == null || !Terrain.TryGetHeight(GlobalPosition, out float ground)
            || GlobalPosition.Y >= ground - 2f) return;

        _sinceSnapWarning += delta;
        if (_sinceSnapWarning > 2)
        {
            _sinceSnapWarning = 0;
            GD.Print($"[player] {Name} below terrain ({GlobalPosition.Y:F1} < {ground:F1}), snapping up");
        }
        GlobalPosition = new Vector3(GlobalPosition.X, ground + 1f, GlobalPosition.Z);
        Velocity = Vector3.Zero;
        _motion.Speed = 0f;
    }

    /// <summary>
    /// One physics step on a vehicle. Gravity, terrain following and collision stay here — they
    /// are properties of the body, not of the bicycle — while the vehicle decides only how fast
    /// it is going and which way it points.
    /// </summary>
    /// <summary>
    /// One physics step flying. The craft owns the velocity; the body only carries it through
    /// the world and reports what it hit. The capsule stays upright and turns with the heading —
    /// the attitude (pitch, bank, the prone wingsuit) is all in the visual.
    /// </summary>
    private void FlyPhysics(float dt, bool onFloor, Flyer flyer)
    {
        bool typing = UiFocus.TextEntryActive;
        float tr = typing ? 0f : Mathf.Max(0f, Input.GetJoyAxis(0, JoyAxis.TriggerRight));
        float tl = typing ? 0f : Mathf.Max(0f, Input.GetJoyAxis(0, JoyAxis.TriggerLeft));
        bool jumpDown = PlayerInput.Held(PlayerInput.Jump);
        bool action = jumpDown && !_jumpHeld;
        _jumpHeld = jumpDown;
        bool downHeld = PlayerInput.Held(PlayerInput.CrouchSlide);

        var input = new FlightInput(
            Stick: PlayerInput.Move,
            Up: Mathf.Max(jumpDown ? 1f : 0f, tr),
            Down: Mathf.Max(downHeld ? 1f : 0f, tl),
            LeverUp: Mathf.Max(PlayerInput.Held(PlayerInput.Sprint) ? 1f : 0f, tr),
            LeverDown: Mathf.Max(downHeld ? 1f : 0f, tl),
            Action: action,
            Effort: PlayerInput.Held(PlayerInput.TuckBoost),
            ViewYaw: _viewYaw,
            Engine: EngineOn || !flyer.HasEngine);

        Clearance = Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float ground)
            ? GlobalPosition.Y - ground : 999f;

        var ev = flyer.Fly(input, new FlightEnv(onFloor, Clearance), dt, ref _flight);
        Rotation = new Vector3(0, _flight.Yaw, 0);

        if (ev == FlightEvent.None)
        {
            Velocity = _flight.Velocity;
            MoveAndSlide();

            // What the world took off the craft. A landing takes the vertical part, a wall the
            // rest; past the craft's limit that is a crash, below it the craft slides on.
            var real = GetRealVelocity();
            var lost = _flight.Velocity - real;
            // touching down onto a floor is not an impact: only its horizontal part counts,
            // plus a hard vertical arrival
            float impact = IsOnFloor()
                ? Mathf.Max(new Vector2(lost.X, lost.Z).Length(), -_flight.Velocity.Y - 6f)
                : lost.Length();
            if (_settle > 0f)
            {
                _settle -= dt;
                // take the ground's word for the motion, but not the solver's shove
                if (real.Length() <= _flight.Velocity.Length() + 0.5f) _flight.Velocity = real;
                Velocity = _flight.Velocity;
            }
            else if (impact > flyer.CrashSpeed) ev = FlightEvent.Crashed;
            else
            {
                // knocks below a crash still dent a vehicle, and enough of them finish it
                if (flyer.IsVehicle && impact > 4f)
                {
                    VehicleHealth -= (impact - 4f) * 10f;
                    Impacted?.Invoke(impact);
                    if (VehicleHealth <= 0f) ev = FlightEvent.Crashed;
                }
                if (lost.LengthSquared() > 4f) _flight.Velocity = real;
            }
        }

        switch (ev)
        {
            case FlightEvent.OpenCanopy:
                ApplyRide(RideKind.Parachute, _flight.Velocity);
                Announced?.Invoke("CANOPY", true);
                return;
            case FlightEvent.Landed:
                ApplyRide(RideKind.OnFoot, _flight.Velocity with { Y = 0 } * 0.3f);
                Landed?.Invoke(2f);
                return;
            case FlightEvent.Crashed:
                Crash(flyer);
                return;
        }

        if (_visual != null) flyer.Pose(_visual, _flight.Yaw, _flight);
        UpdateFlightCamera(dt, flyer);
        ClampAboveTerrain(dt);
    }

    /// <summary>
    /// A crash: the craft is gone and the pilot is on foot where it came down, dazed for a
    /// moment. Not a respawn — the world is the same one, and walking away from a wreck is a
    /// better story than a reload.
    /// </summary>
    private void Crash(Flyer flyer)
    {
        float speed = _flight.Velocity.Length();
        if (flyer.IsVehicle)
        {
            Impacted?.Invoke(Mathf.Max(speed, 8f));
            WreckVehicle();
            return;
        }
        // a wingsuit into the ground is the pilot hitting it, not a machine
        TakeDamage((speed - 8f) * 3.5f);
        Impacted?.Invoke(Mathf.Max(speed, 8f));
        Announced?.Invoke(flyer is Wingsuit ? "SPLAT!" : "CRASH!", false);
        PlayerInput.Rumble(1f, 1f, 0.5f);
        ApplyRide(RideKind.OnFoot, Vector3.Zero);
        _stunTimer = 1.5f;
    }

    /// <summary>
    /// Chase camera for a craft: behind the nose (or the heading, for level craft), eased so it
    /// swings after a turn rather than being welded to it, pulled in when terrain is in the way.
    /// </summary>
    private void UpdateFlightCamera(float dt, Flyer flyer)
    {
        if (_camera == null) return;

        Vector3 fwd;
        if (flyer.LookSteers)
        {
            // the helicopter camera is the look itself; the craft chases it
            fwd = new Basis(Vector3.Up, _viewYaw) * new Basis(Vector3.Right, _pitch * 0.8f) * Vector3.Forward;
        }
        else
        {
            _lookYaw = Mathf.MoveToward(_lookYaw, 0f, 1.2f * dt);
            var nose = flyer.CameraForward(_flight);
            _camFwd = _camFwd.Lerp(nose, 1f - Mathf.Exp(-3.5f * dt));
            if (_camFwd.LengthSquared() < 1e-4f) _camFwd = nose;
            _camFwd = _camFwd.Normalized();
            // free look orbits around the craft, and pitch looks up and down on top of it
            fwd = new Basis(Vector3.Up, _lookYaw) * _camFwd;
            var side = fwd.Cross(Vector3.Up);
            if (side.LengthSquared() > 1e-4f)
                fwd = new Basis(side.Normalized(), _pitch * 0.6f) * fwd;
        }
        fwd = fwd.Normalized();

        var pivot = GlobalPosition + Vector3.Up * flyer.CameraPivot;
        var wanted = pivot - fwd * flyer.CameraDistance + Vector3.Up * flyer.CameraHeight;

        float want = 1f;
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
            pivot, wanted, CameraMask, new Godot.Collections.Array<Rid> { GetRid() }));
        if (hit.Count > 0)
        {
            float span = Mathf.Max(0.01f, (wanted - pivot).Length());
            want = Mathf.Clamp(((hit["position"].AsVector3() - pivot).Length() - 0.4f) / span, 0.1f, 1f);
        }
        _chaseBlend = want < _chaseBlend ? want : Mathf.Lerp(_chaseBlend, want, 1f - Mathf.Exp(-4f * dt));
        var eye = pivot.Lerp(wanted, _chaseBlend);

        var look = pivot + fwd * 8f - eye;
        _camera.GlobalTransform = new Transform3D(
            Flyer.Orient(look, Vector3.Up, new Basis(Vector3.Up, _flight.Yaw) * Vector3.Forward), eye);

        float t = Mathf.Clamp(_flight.Velocity.Length() / flyer.FovSpeed, 0f, 1f);
        _camera.Fov = Mathf.Lerp(_camera.Fov, Mathf.Lerp(flyer.BaseFov, flyer.MaxFov, t * t),
            1f - Mathf.Exp(-3f * dt));
    }

    private void RidePhysics(float dt, bool onFloor)
    {
        // Triggers are analog, and the vehicles already take 0..1: half a trigger is half the
        // watts. Pushing the stick forward or back does the same, for anyone who expects it to.
        var stick = PlayerInput.Move;
        var input = RideControls?.Invoke() ?? new RideInput(
            Throttle: Mathf.Max(PlayerInput.Strength(PlayerInput.Throttle), Mathf.Max(0f, -stick.Y)),
            Brake: Mathf.Max(PlayerInput.Strength(PlayerInput.Brake), Mathf.Max(0f, stick.Y)),
            Steer: SteerInput(),
            Effort: PlayerInput.Held(PlayerInput.TuckBoost));

        // After a bail the rider is on the ground, not riding: no drive, no steering.
        if (_bailTimer > 0)
        {
            _bailTimer -= dt;
            input = new RideInput(0f, 1f, 0f, false);
        }

        // Tricks: hold the trick button in the air and the stick flips and spins instead of
        // steering. Let go and whatever rotation is left eases to the nearest whole turn, so a
        // trick released a touch early still lands; the landing grades whatever is left.
        if (!onFloor)
        {
            _rideAir += dt;
            if (_rideAir > 0.15f && PlayerInput.Held(PlayerInput.Trick))
            {
                _airPitch += stick.Y * FlipRate * dt;     // stick forward: nose down, a front flip
                _airSpin -= stick.X * SpinRate * dt;      // stick right: clockwise from above
                input = input with { Steer = 0f };
            }
            else
            {
                _airPitch = Mathf.MoveToward(_airPitch, NearestTurn(_airPitch), SettleRate * dt);
                _airSpin = Mathf.MoveToward(_airSpin, NearestTurn(_airSpin), SettleRate * dt);
            }
        }

        // The gradient the vehicle feels is the one along its own path, not the steepness of
        // the ground: a road that contours a mountainside is level to ride even though the
        // hillside it crosses is at 60%. Using the terrain slope directly would charge a rider
        // for every traverse.
        var normal = onFloor ? GetFloorNormal() : Vector3.Up;
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;

        float grade = onFloor
            ? -(heading.X * normal.X + heading.Z * normal.Z) / Mathf.Max(normal.Y, 0.15f)
            : 0f;

        _ride!.Step(input, new RideGround(onFloor, grade), dt, ref _motion);

        // Boost: the reward for air and tricks, spent as raw acceleration on top of the model.
        // Game profile only; in Sim the watts are the rider's, and nothing else may add to them.
        Boosting = Rideable.Arcade && _bailTimer <= 0 && BoostMeter > 0.01f
            && PlayerInput.Held(PlayerInput.Boost);
        if (Boosting)
        {
            _motion.Speed += BoostAccel * dt;
            BoostMeter = Mathf.Max(0f, BoostMeter - BoostDrain * dt);
        }

        // yaw changed, so the direction of travel has to be taken again
        Rotation = new Vector3(0, _motion.Yaw, 0);
        heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;

        var velocity = Velocity;
        velocity.X = heading.X * _motion.Speed;
        velocity.Z = heading.Z * _motion.Speed;
        velocity.Y = onFloor ? Mathf.Min(velocity.Y, 0f) : velocity.Y - Gravity * dt;

        // Space hops: edge-triggered like the on-foot jump, so holding it does not bunny-hop
        // every frame, and only from the ground - there is nothing to push against in the air.
        // Speed and heading are untouched: a hop carries the bike's momentum, it does not add any.
        bool spaceDown = PlayerInput.Held(PlayerInput.Jump);
        bool hop = spaceDown && !_jumpHeld && onFloor;
        _jumpHeld = spaceDown;
        if (hop) { velocity.Y = RideJumpVelocity; Jumped?.Invoke(); }
        LastRideInput = input;

        Velocity = velocity;
        MoveAndSlide();

        // Hitting something has to cost the speed, or the vehicle grinds along the wall at
        // 50 km/h and shoots off the moment the wall ends.
        //
        // GetRealVelocity, not Velocity: MoveAndSlide leaves Velocity projected along whatever
        // it hit, and against a slope too steep to climb that projection points up the face and
        // keeps most of its magnitude — so a skier jammed against a bank reported 22 km/h while
        // its position had not changed for twelve seconds.
        //
        // Smoothed and thresholded, though, because the instantaneous figure is not a clean
        // signal: the ground is a 2 m lattice, and climbing each bump costs a little forward
        // motion every single frame. Clamping to it directly compounds those dips — measured at
        // 107 m of riding down to 11 m on flat ground, a bike bled to walking pace by nothing
        // but the terrain's own roughness. Only a shortfall that persists is an impact.
        var real = GetRealVelocity();
        float achieved = new Vector2(real.X, real.Z).Length();
        _realSpeed = Mathf.Lerp(_realSpeed, achieved, 1f - Mathf.Exp(-ImpactResponse * dt));
        if (_realSpeed < _motion.Speed - ImpactTolerance)
        {
            float before = _motion.Speed;
            _motion.Speed = Mathf.Max(_realSpeed, _motion.Speed - ImpactDecel * dt);
            // felt in the hands in proportion to the speed the wall took off you
            PlayerInput.Rumble(0.4f, Mathf.Clamp((before - _motion.Speed) * 0.6f, 0f, 1f), 0.12f);
            Impacted?.Invoke(before - _motion.Speed);

            // A wall taken at speed throws the rider over the bars; the bike stays where it hit.
            if (_settle > 0f) _settle -= dt;
            else if (_ride is { IsVehicle: true } && before - _realSpeed > 9f)
            {
                float hit = before;
                var fwd = -GlobalTransform.Basis.Z with { Y = 0 };
                var state = CaptureVehicle(wrecked: false) with { Velocity = Vector3.Zero };
                Vehicles?.Park(state);
                Announced?.Invoke("THROWN OFF!", false);
                ApplyRide(RideKind.OnFoot, fwd.Normalized() * hit * 0.25f + Vector3.Up * 4f);
                GlobalPosition += Vector3.Up * 1.2f;
                _stunTimer = 1.2f;
                TakeDamage((hit - 8f) * 3f);
                return;
            }
        }

        // Landing mounted: the vertical speed the ground took, like the on-foot landing thump.
        bool nowOnFloor = IsOnFloor();
        if (!nowOnFloor) _rideFall = Mathf.Max(_rideFall, -Velocity.Y);
        else if (!_rideWasOnFloor)
        {
            if (_rideFall > 1.5f)
            {
                Landed?.Invoke(_rideFall);
                PlayerInput.Rumble(0.3f, Mathf.Clamp((_rideFall - 1.5f) / 6f, 0.1f, 1f), 0.15f);
            }
            if (_rideAir > 0.3f) GradeLanding();
            _rideFall = 0f;
            _rideAir = 0f;
            _airPitch = _airSpin = 0f;
        }
        _rideWasOnFloor = nowOnFloor;

        UpdateRideCamera(dt);
        ClampAboveTerrain(dt);
    }

    /// <summary>
    /// Chase camera. Third person, because you asked for a bicycle and a bicycle you cannot see
    /// is a first-person walk with a different speed.
    /// </summary>
    private static float NearestTurn(float a) => Mathf.Round(a / Mathf.Tau) * Mathf.Tau;

    /// <summary>
    /// Grades a landing by how far from upright the rider came down. Clean pays out (a speed
    /// kick and boost, more for every full rotation); sloppy is ridden away from at a cost; a
    /// bail puts the rider on the ground for a second.
    /// </summary>
    private void GradeLanding()
    {
        float pitchErr = Mathf.Abs(Mathf.Wrap(_airPitch, -Mathf.Pi, Mathf.Pi));
        float spinErr = Mathf.Abs(Mathf.Wrap(_airSpin, -Mathf.Pi, Mathf.Pi));
        float err = Mathf.Max(pitchErr, spinErr);
        int flips = Mathf.RoundToInt(Mathf.Abs(_airPitch) / Mathf.Tau);
        int spins = Mathf.RoundToInt(Mathf.Abs(_airSpin) / Mathf.Tau);

        if (err < CleanAngle)
        {
            int tricks = flips + spins;
            if (tricks > 0)
            {
                _motion.Speed += 1.5f * tricks;
                BoostMeter = Mathf.Min(1f, BoostMeter + 0.2f * tricks + 0.05f * _rideAir);
                Announced?.Invoke(TrickName(flips, spins), true);
            }
            else if (_rideAir > 0.8f)
            {
                BoostMeter = Mathf.Min(1f, BoostMeter + 0.1f + 0.05f * _rideAir);
                Announced?.Invoke($"CLEAN  {_rideAir:0.0} s", true);
            }
        }
        else if (err < SloppyAngle)
        {
            float lost = _motion.Speed * 0.45f;
            _motion.Speed -= lost;
            Impacted?.Invoke(lost);
            Announced?.Invoke("SLOPPY", false);
        }
        else
        {
            float lost = _motion.Speed;
            _motion.Speed = 0f;
            _bailTimer = 1.2f;
            Impacted?.Invoke(Mathf.Max(lost, 6f));
            Announced?.Invoke("BAIL!", false);
        }
    }

    private string TrickName(int flips, int spins)
    {
        var parts = new List<string>();
        if (flips > 0)
        {
            string flip = _airPitch < 0 ? "FRONTFLIP" : "BACKFLIP";
            parts.Add(flips > 1 ? $"DOUBLE {flip}" : flip);
        }
        if (spins > 0) parts.Add($"{spins * 360}");
        return string.Join("  ", parts) + "!";
    }

    /// <summary>
    /// Lean, trick rotation and a bail, applied to the rider and machine together. Rotated about
    /// the middle of the rider rather than the contact patch, or a flip would swing the whole
    /// bike through the ground.
    /// </summary>
    private void PoseRideVisual()
    {
        if (_visual == null) return;
        // a bail lays the rider over on their side for as long as it lasts
        float roll = _bailTimer > 0 ? 1.35f : _motion.Lean;
        var basis = new Basis(Vector3.Up, _airSpin) * new Basis(Vector3.Right, _airPitch)
            * new Basis(Vector3.Back, roll);
        var pivot = Vector3.Up * (_bailTimer > 0 ? 0.3f : 0.9f);
        _visual.Transform = new Transform3D(basis, pivot - basis * pivot);
    }

    private void UpdateRideCamera(float dt)
    {
        if (_visual != null)
            PoseRideVisual();

        if (_camera == null || _ride == null) return;

        // the free look springs back to centre, so letting go of the mouse puts the road ahead
        _lookYaw = Mathf.MoveToward(_lookYaw, 0f, 1.2f * dt);

        if (!_thirdPerson)
        {
            // From the rider's own eye, leaning with the machine: the eye point is in the
            // visual's frame, which rolls about Z by the lean, so it has to roll with it or the
            // view would hang level while the handlebars tip under it. The horizon itself takes
            // only part of that roll — all of it at 40° of lean is a recipe for seasickness.
            // the visual is rolled by Rotation.Z = Lean, i.e. about +Z
            _camera.Position = new Basis(Vector3.Back, _motion.Lean) * _ride.FirstPersonEye;
            _camera.Rotation = new Vector3(_pitch, _lookYaw, _motion.Lean * 0.5f);
            ApplyRideFov(dt);
            return;
        }

        // proportional to the yaw rate, so a gentle bend barely moves it and a hairpin swings it
        // well out; eased, so the trail itself never snaps
        float lagTarget = Mathf.Clamp(-_motion.YawRate * 0.28f, -0.42f, 0.42f);
        _turnLag = Mathf.Lerp(_turnLag, lagTarget, 1f - Mathf.Exp(-3.5f * dt));
        float orbit = _lookYaw + _turnLag;

        // both are local to the body, which is yaw-only, so the camera stays level
        var eye = new Vector3(0, _ride.EyeHeight, 0);
        var back = new Basis(Vector3.Up, orbit)
            * new Vector3(0, _ride.ChaseHeight, _ride.ChaseDistance);

        // Pull in when the camera would sit inside the hillside. Alpine terrain climbs behind
        // you constantly, so without this half of every ascent is spent looking at the inside
        // of a mountain.
        var basis = GlobalTransform.Basis;
        var from = GlobalPosition + basis * eye;
        var to = GlobalPosition + basis * back;

        float wanted = 1f;
        var query = PhysicsRayQueryParameters3D.Create(from, to,
            CameraMask, new Godot.Collections.Array<Rid> { GetRid() });
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0)
        {
            float span = Mathf.Max(0.01f, (to - from).Length());
            // 0.85 keeps the lens off the rock face it just found
            wanted = Mathf.Clamp((hit["position"].AsVector3() - from).Length() / span * 0.85f,
                0.15f, 1f);
        }

        // ease out, snap in: arriving late at a wall means a frame with the camera inside it
        _chaseBlend = wanted < _chaseBlend
            ? wanted
            : Mathf.Lerp(_chaseBlend, wanted, 1f - Mathf.Exp(-4f * dt));

        _camera.Position = eye.Lerp(back, _chaseBlend);

        // the camera banks a fraction of the machine's lean — enough to feel the turn, not
        // enough to tip the horizon over
        // Rotated by the same orbit angle as its position, so it still looks straight through the
        // rider's axis and they stay centred while the view swings.
        _camera.Rotation = new Vector3(_pitch, orbit, _motion.Lean * 0.35f);

        ApplyRideFov(dt);
    }

    /// <summary>FOV widens with speed — the cheapest, strongest sense of pace there is.</summary>
    private void ApplyRideFov(float dt)
    {
        if (_camera == null || _ride == null) return;
        // boost punches the FOV out, the cheapest way to make acceleration felt
        if (Boosting) _camera.Fov = Mathf.Lerp(_camera.Fov, _ride.MaxFov + 8f, 1f - Mathf.Exp(-4f * dt));
        float t = Mathf.Clamp(_motion.Speed / _ride.FovSpeed, 0f, 1f);
        _camera.Fov = Mathf.Lerp(_camera.Fov, Mathf.Lerp(_ride.BaseFov, _ride.MaxFov, t * t),
            1f - Mathf.Exp(-3f * dt));
    }

    /// <summary>A/D or the left stick as -1..1. Zero while a text field has the keyboard.</summary>
    private static float SteerInput() => PlayerInput.Steer;

    /// <summary>
    /// Drops into a slide, launching at <see cref="SlideSpeed"/> unless you arrived faster —
    /// a slide must never cost you speed you already had, or chaining one off a wall jump
    /// would be a downgrade.
    /// </summary>
    private void BeginSlide(ref Vector3 velocity, float flatSpeed)
    {
        _sliding = true;
        _slideTime = 0;
        SlideStarted?.Invoke();

        var flat = new Vector3(velocity.X, 0, velocity.Z);
        var dir = flatSpeed > 0.01f ? flat / flatSpeed : -GlobalTransform.Basis.Z;
        float launch = Mathf.Max(flatSpeed, SlideSpeed);
        velocity.X = dir.X * launch;
        velocity.Z = dir.Z * launch;

        SetBodyHeight(SlideHeight);
    }

    /// <summary>
    /// Stands back up. Returns false — and stays sliding — when something is in the way,
    /// which is what stops the capsule being forced through a tunnel roof or a bridge soffit.
    /// </summary>
    private bool EndSlide()
    {
        if (!HasHeadroom()) return false;

        _sliding = false;
        _slideCooldown = SlideCooldown;
        SetBodyHeight(StandHeight);
        return true;
    }

    private void SetBodyHeight(float height)
    {
        _capsule.Height = height;
        _body.Position = new Vector3(0, height * 0.5f, 0);
    }

    /// <summary>
    /// Tests whether a standing capsule fits where the crouched one is. The radius is shaved
    /// slightly so resting against a wall mid-slide does not read as "blocked".
    /// </summary>
    private bool HasHeadroom()
    {
        if (_capsule.Height >= StandHeight - 0.01f) return true;

        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _standProbe,
            Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * (StandHeight * 0.5f)),
            CollisionMask = CollisionMask,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };
        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0;
    }

    /// <summary>
    /// Kicks off a wall if one is being touched. Capped per airtime and refused on a wall too
    /// similar to the last one, so you cannot climb a single flat face like a ladder — but two
    /// opposing walls in a gully still chimney, which is the move worth having.
    /// </summary>
    private bool TryWallJump(ref Vector3 velocity, Vector3 lookDirection)
    {
        if (_wallJumps >= MaxWallJumps || _wallCoyote <= 0) return false;

        var away = _coyoteNormal;
        if (_lastWallNormal != Vector3.Zero && away.Dot(_lastWallNormal) > WallSimilarity) return false;

        // aim it slightly with the look/move direction, but never back into the wall
        var push = lookDirection != Vector3.Zero
            ? (away * 0.75f + lookDirection * 0.25f).Normalized()
            : away;
        if (push.Dot(away) < 0.35f) push = away;

        velocity.X = push.X * WallJumpPush;
        velocity.Z = push.Z * WallJumpPush;
        velocity.Y = WallJumpUp;

        _wallJumps++;
        WallJumped?.Invoke();
        _lastWallNormal = away;
        _wallCoyote = 0f;  // one jump per contact — the memory must not fire twice
        _fallSpeed = 0f;   // the wall arrested the fall; no landing thump is owed
        return true;
    }

    /// <summary>
    /// Head bob, landing impact and a running FOV kick. All of it is camera-only: the
    /// body never moves, so none of this can push the player through geometry.
    /// </summary>
    private void UpdateCameraFeel(float dt, bool running, bool onFloor)
    {
        if (_camera == null) return;

        float groundSpeed = new Vector2(Velocity.X, Velocity.Z).Length();
        _speedSmoothed = Mathf.Lerp(_speedSmoothed, groundSpeed, 1f - Mathf.Exp(-8f * dt));

        // the eye drops faster than it rises: going down should feel like a commitment,
        // coming up like recovering your feet
        float blendRate = _sliding ? 16f : 9f;
        _slideBlend = Mathf.Lerp(_slideBlend, _sliding ? 1f : 0f, 1f - Mathf.Exp(-blendRate * dt));

        // step cadence scales with speed, so running steps land faster and harder
        if (onFloor && !_sliding && groundSpeed > 0.15f)
        {
            float cadence = Mathf.Lerp(1.5f, 2.6f, Mathf.Clamp(groundSpeed / RunSpeed, 0f, 1f));
            _bobPhase += groundSpeed * cadence * dt;
            _bobStrength = Mathf.Lerp(_bobStrength, Mathf.Clamp(groundSpeed / RunSpeed, 0f, 1f),
                1f - Mathf.Exp(-6f * dt));
        }
        else
        {
            _bobStrength = Mathf.Lerp(_bobStrength, 0f, 1f - Mathf.Exp(-9f * dt));
        }

        // landing: convert the arrested fall into a downward dip that springs back
        if (onFloor && !_wasOnFloor)
        {
            _landingVelocity -= Mathf.Clamp(_fallSpeed * 0.055f, 0.02f, 0.42f);
            // a hop is barely felt, a drop off a wall is a thump
            if (_fallSpeed > 3f)
                PlayerInput.Rumble(0.2f, Mathf.Clamp((_fallSpeed - 3f) / 7f, 0.15f, 1f), 0.15f);
            if (_fallSpeed > 1.5f) Landed?.Invoke(_fallSpeed);
            // a 6 m drop is free, a 15 m one hurts a lot, a 25 m one is the end
            if (_fallSpeed > 11f && _ejected <= 0) TakeDamage((_fallSpeed - 11f) * 9f);
            _fallSpeed = 0f;
        }
        _wasOnFloor = onFloor;

        // critically damped spring back to rest
        _landingVelocity += -_landingDip * 90f * dt - _landingVelocity * 13f * dt;
        _landingDip += _landingVelocity * dt;

        // vertical bob is double-frequency (two footfalls per stride), lateral is single
        float bobUp = Mathf.Sin(_bobPhase * 2f) * 0.045f * _bobStrength;
        float bobSide = Mathf.Sin(_bobPhase) * 0.035f * _bobStrength;
        float roll = -Mathf.Sin(_bobPhase) * 0.010f * _bobStrength;

        // a slide banks the view into the turn — the only cue that you are steering rather
        // than being carried, since the input no longer changes your speed
        float lean = _slideBlend * -SteerInput() * 0.10f;
        float eye = Mathf.Lerp(EyeHeight, SlideEyeHeight, _slideBlend);

        // third person places its own camera in _Process; the bob, dip and FOV here still run
        // for it, because the body squash and the FOV kick read them
        if (!_thirdPerson)
        {
            _camera.Position = new Vector3(bobSide, eye + bobUp + _landingDip, 0);
            _camera.Rotation = new Vector3(_pitch, 0, roll + lean);
        }

        // slight FOV widening while running reads as effort without inducing sickness;
        // a slide pushes it further, because the speed is the whole reward
        float targetFov = running && groundSpeed > WalkSpeed * 1.2f ? RunFov : BaseFov;
        targetFov = Mathf.Lerp(targetFov, SlideFov, _slideBlend);
        // a held optic wins, and settles faster: a zoom that drifts in reads as lag
        if (FovOverride is { } zoom)
        {
            _camera.Fov = Mathf.Lerp(_camera.Fov, zoom, 1f - Mathf.Exp(-14f * dt));
            return;
        }
        _camera.Fov = Mathf.Lerp(_camera.Fov, targetFov, 1f - Mathf.Exp(-5f * dt));
    }
}
