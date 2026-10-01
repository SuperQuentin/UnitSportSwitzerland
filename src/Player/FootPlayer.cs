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

    /// <summary>
    /// The preset on the car being driven (<see cref="CarSetups"/> id; 0 = stock, and 0 for anything
    /// but a car). Replicated beside <see cref="RideKindId"/>, whose meaning it depends on, and reset
    /// with it in <see cref="ApplyRide"/>; <see cref="SetCarSetup"/> changes it.
    /// </summary>
    [Export] public int CarSetupId { get; set; }

    /// <summary>
    /// The garage parts on the car being driven (<see cref="CarTuning"/> bits; 0 = stock, and 0 for
    /// anything but a car). Replicated beside <see cref="RideKindId"/>, whose meaning it depends on,
    /// and reset with it in <see cref="ApplyRide"/>: a new car comes stock.
    /// </summary>
    [Export] public long TuningBits { get; set; }

    /// <summary>The car's open doors (<see cref="Avatar.CarRig.DoorLeft"/>..), replicated so everyone sees them swing.</summary>
    [Export] public byte DoorsOpen { get; set; }

    // --- held item (see src/Items) ---
    /// <summary>
    /// What is in the player's hand, as an <see cref="Items.ItemId"/>. Replicated for the same
    /// reason <see cref="RideKindId"/> is: other players should see the binoculars, not an empty
    /// hand held up to a face. The inventory itself is local and is never sent.
    /// </summary>
    [Export] public int HeldItemId { get; set; }

    /// <summary>
    /// The CD a radio in the hand plays (<c>Items.RadioPlay</c>), empty when silent; written by
    /// the owner from the stack's data. Replicated so everyone near hears it (#168).
    /// </summary>
    [Export] public string HeldRadio { get; set; } = "";

    /// <summary>
    /// The live station the car or truck this player drives is tuned to (<c>Audio.Live.Stations</c>,
    /// 0 = off), set by the driver (U / P). Replicated so everyone near hears it, in sync (#179).
    /// </summary>
    [Export] public int CarRadio { get; set; }

    /// <summary>The station this body's vehicle plays, 0 when it drives none or the radio is off.</summary>
    public int PlayingCarRadio => RidingWith == 0 && CarRadio > 0 && HasCarRadio((RideKind)RideKindId) ? CarRadio : 0;

    /// <summary>Cars, trucks and buses have a radio; bikes, mounts, aircraft and trailers do not.</summary>
    public static bool HasCarRadio(RideKind kind) =>
        CarCatalog.For(kind) != null || kind != RideKind.Trailer && HeavyCatalog.For(kind) != null;

    /// <summary>Local driver: the radio was retuned, with the station's name, for a line on screen.</summary>
    public event Action<string>? CarRadioTuned;

    // --- pose, replicated (see _Ready) ---
    // Everything a remote copy draws comes from these three, written by the owner every frame.
    // Before them a remote peer rebuilt the pose from the transform stream alone, so it never saw a
    // lean, a trick, a bail, a craft's attitude, a spinning rotor, a turning crank, a slide or a
    // jump, and each peer ran its own gait phase, so the feet did not match.

    /// <summary>
    /// The body visual's local transform: lean and bank, trick flips and spins, a bail, a craft's
    /// attitude, the landing squash and a stunned figure lying flat — all of it, whatever computed
    /// it, in one value the remote copy applies as-is rather than re-deriving.
    /// </summary>
    [Export] public Transform3D BodyPose { get; set; } = Transform3D.Identity;

    /// <summary>On foot: <see cref="PoseStride"/>, <see cref="PoseAir"/> or <see cref="PoseTucked"/>.</summary>
    [Export] public int PoseKind { get; set; }

    /// <summary>
    /// On foot: (ground speed, gait phase). Mounted: whatever the <see cref="Rideable"/> writes in
    /// <see cref="Rideable.WritePose"/> and reads back in <see cref="Rideable.AnimateRemote"/> —
    /// cadence and crank angle for the bike, spool and throttle for a craft. A new mount that has
    /// moving parts plugs in there and never touches the sync code.
    /// </summary>
    [Export] public Vector4 Anim { get; set; }

    // --- transform, replicated (see _Ready and Net/RemoteInterpolator) ---
    // Not `position`/`rotation` themselves: a remote copy would snap to every packet, which at
    // 150 km/h is a 0.7-2 m hop and a freeze-then-jump whenever one is late. The owner writes these
    // with its own clock; a remote copy interpolates them, and the server's proxy copy applies them.

    [Export] public Vector3 NetPos { get; set; }
    [Export] public Vector3 NetVel { get; set; }
    [Export] public float NetYaw { get; set; }

    /// <summary>The owner's clock when <see cref="NetPos"/> was taken. Replicated LAST, so its
    /// setter sees a complete state.</summary>
    [Export]
    public double NetTime
    {
        get => _netTime;
        // a new state, not the server relaying the last one again (it does, at 30 Hz, whether or not
        // the owner still sends): only that tells a live sender from a crashed one
        set { if (value != _netTime) LastNetState = Time.GetTicksMsec() / 1000.0; _netTime = value; OnNetState(); }
    }
    private double _netTime;

    /// <summary>World velocity, for local and remote copies alike (a remote's <c>Velocity</c> is always zero).</summary>
    public Vector3 WorldVelocity => IsMultiplayerAuthority() ? Velocity : NetVel;

    /// <summary>The server's copy of a client's player: data only, never drawn or simulated.</summary>
    public bool NetProxy { get; private set; }

    private readonly Net.RemoteInterpolator _interp = new();
    private MultiplayerSynchronizer? _sync, _relayNear, _relayFar, _vis;

    /// <summary>A server-owned copy of <paramref name="source"/>'s properties, sent at <paramref name="interval"/>.</summary>
    private static MultiplayerSynchronizer MakeRelay(string name, SceneReplicationConfig source, bool spawn, float interval)
    {
        var config = new SceneReplicationConfig();
        foreach (var prop in source.GetProperties())
        {
            config.AddProperty(prop);
            config.PropertySetSpawn(prop, spawn);
            config.PropertySetReplicationMode(prop, source.PropertyGetReplicationMode(prop));
        }
        var relay = new MultiplayerSynchronizer
        {
            Name = name, RootPath = new NodePath(".."), ReplicationConfig = config, ReplicationInterval = interval,
        };
        relay.SetMultiplayerAuthority(1);
        return relay;
    }

    /// <summary>Server: re-evaluates who gets this player's near and far streams.</summary>
    public void RefreshRelays()
    {
        _relayNear?.UpdateVisibility();
        _relayFar?.UpdateVisibility();
    }
    private Net.InterestService? _interest;

    private void OnNetState()
    {
        if (!IsInsideTree() || NetProxy)
        {
            // spawn state, or the server's proxy: exactly where the owner says, no smoothing
            Position = NetPos;
            Rotation = new Vector3(0, NetYaw, 0);
            return;
        }
        if (IsMultiplayerAuthority()) return;
        double now = Time.GetTicksUsec() / 1e6;
        _interp.BeginCorrection(now);
        _interp.Push(_netTime, now, NetPos, NetVel, NetYaw);
        _interp.EndCorrection();
    }

    /// <summary>
    /// What a player node stands for in interest and races: a peer id, or a race NPC's entrant id
    /// (<c>npc_&lt;owner&gt;_&lt;n&gt;</c> → <c>-(owner * 1000 + n)</c>, negative). Null for anything else.
    /// An NPC is its own interest target: who sees it depends on where IT is, not on whichever
    /// client happens to simulate it (issue #50).
    /// </summary>
    public static long? NetId(string name)
    {
        if (long.TryParse(name, out long id)) return id;
        var parts = name.Split('_');
        return parts.Length == 3 && parts[0] == "npc" && long.TryParse(parts[1], out long owner)
            && int.TryParse(parts[2], out int n) ? Net.PlayerReplication.NpcId(owner, n) : null;
    }

    /// <summary>
    /// A race NPC's current simulator: the client that runs its physics and publishes its state,
    /// which is simply this node's multiplayer authority. The server moves it from one client to
    /// another (<see cref="World.RaceNpcs"/>, issue #50). A peer that spawns the NPC after a move
    /// gets the current one in the spawn state (a spawn-only property of <c>Sync</c>): the spawn
    /// data still names the client that asked for the NPC.
    /// </summary>
    [Export]
    public int SimPeer
    {
        get => GetMultiplayerAuthority();
        set { if (value > 0 && value != GetMultiplayerAuthority()) SetSimulator(value); }
    }

    /// <summary>Server: when the proxy last received a state from its simulator (seconds, engine clock).</summary>
    public double LastNetState { get; private set; } = Time.GetTicksMsec() / 1000.0;

    private bool _netUp;

    /// <summary>
    /// A remote copy that heard nothing for this long stops being solid (30 states a second are
    /// expected): at racing speed the field behind reaches a frozen car within 2-3 s.
    /// </summary>
    private const double SilentSeconds = 1.0;

    /// <summary>
    /// Moves a race NPC to another simulator, on this peer. The node AND its <c>Sync</c> change
    /// authority (children do not follow the parent). The peer that becomes the simulator takes
    /// the body over from the last replicated state — where it is drawn right now, the replicated
    /// velocity, the mount — so nothing jumps; a peer that stops being it turns the body into a
    /// remote copy.
    /// </summary>
    public void SetSimulator(int peer)
    {
        bool was = IsMultiplayerAuthority();
        SetMultiplayerAuthority(peer, false);
        _sync?.SetMultiplayerAuthority(peer);
        LastNetState = Time.GetTicksMsec() / 1000.0;   // the new simulator gets a fresh grace period
        if (!_netUp || NetProxy) return;   // spawn state inside _Ready, or the server's data proxy
        bool now = IsMultiplayerAuthority();
        if (now == was) { _interp.NewSender(); return; }   // another remote sender: another clock
        if (now)
        {
            var kind = (RideKind)RideKindId;
            _remoteRide = null;
            SetRemoteEngine(null);
            Rotation = new Vector3(0, Rotation.Y, 0);
            if (kind != RideKind.OnFoot)
            {
                ApplyRide(kind, NetVel, TuningBits, CarSetupId);
                if (_ride is { IsVehicle: true } machine) { EngineOn = true; VehicleHealth = machine.MaxHealth; }
            }
            else { _ride = null; Velocity = NetVel; }
            RefreshVisual(force: true);   // the authority animates its own mount's visual
            // it is on the ground already: no drop-onto-terrain pass, unless nothing is solid here yet
            _placed = Terrain?.HasCollisionAt(GlobalPosition) != false;
            _body.Disabled = false;
            Visible = true;
            SetPhysicsProcess(true);
            Terrain?.AddAnchor(this, collision: true);
            _sync?.UpdateVisibility();
        }
        else
        {
            RideControls = null;
            _ride = null;
            Velocity = Vector3.Zero;
            SetPhysicsProcess(false);
            Terrain?.RemoveAnchor(this);
            _interp.NewSender();
            FitRemoteBody((RideKind)RideKindId);
        }
        GD.Print($"[npc] {Name} now simulated by peer {peer}{(now ? " (here)" : "")}");
    }

    /// <summary>Server: re-evaluates whether this player exists on <paramref name="viewer"/>.</summary>
    public void RefreshNetVisibility(long viewer) => _vis?.UpdateVisibility((int)viewer);

    public const int PoseStride = 0, PoseAir = 1, PoseTucked = 2;

    /// <summary>
    /// The hat on the figure, as an <see cref="Avatar.Headwear"/> (occasions, #18). Replicated like
    /// <see cref="HeldItemId"/>; set on the owner by <c>Occasions.OccasionHats</c>.
    /// </summary>
    [Export] public int HeadwearId { get; set; }

    private Avatar.Headwear Hat => (Avatar.Headwear)HeadwearId;
    private Avatar.Headwear _poseHat;

    /// <summary>
    /// The figure's right hand in this node's local space, or null when no figure is drawn
    /// (first person on foot, or mounted). Updated whenever the body mesh is posed.
    /// </summary>
    public Transform3D? HandLocal { get; private set; }

    /// <summary>
    /// What the item hand is doing with the held item: 0 idle, 1 aim, 2 use. Replicated next to
    /// <see cref="HeldItemId"/>; written by the owner (<c>ItemController</c>). Every peer derives the
    /// arm pose (<see cref="Avatar.ItemArmPose"/>) from it plus the held item's kind.
    /// </summary>
    [Export] public int ItemAction { get; set; }

    /// <summary>
    /// 0 not dancing, 1 dancing to the nearest playing radio. Replicated like <see cref="ItemAction"/>;
    /// toggled by the owner with E beside a radio (<see cref="TryInteract"/>). The beat itself is
    /// never replicated: every peer reads it off the radio and the shared clock
    /// (<c>Items.RadioBody.BeatAt</c>), which is what keeps everyone on the same step.
    /// </summary>
    [Export] public int DanceId { get; set; }

    /// <summary>The arm pose currently drawn and its 0..1 blend, eased in and out on every peer.</summary>
    public Avatar.ItemArmPose DrawnArmPose => _itemArmCur;
    public float DrawnArmBlend => _itemArmBlend;
    private Avatar.ItemArmPose _itemArmCur;
    private float _itemArmBlend;
    private bool _itemArmInit;

    /// <summary>The dance's 0..1 ease, and the radio it follows (kept through the ease-out).</summary>
    private float _danceWeight;
    private Items.RadioBody? _danceRadio;

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

    /// <summary>
    /// 0..1: an item held ready to throw wants the close over-the-shoulder camera (set every frame by
    /// <see cref="Items.ItemController"/>, like <see cref="FovOverride"/>). In first person the view
    /// is lent to third person for as long as it lasts, pulled out of the head and back.
    /// </summary>
    public float ThrowAim { get; set; }

    /// <summary>Camera tremble in radians, set every frame (a fully wound-up throw shakes).</summary>
    public float CameraShake { get; set; }

    /// <summary>The throw camera's eased weight.</summary>
    private float _throwBlend;

    /// <summary>Third person lent to a throw from first person: given back when the throw camera has eased out.</summary>
    private bool _borrowedThird;

    /// <summary>In a car's, a truck's or a bus's driver's seat, looking out through the windscreen (not the chase camera, not the garage's orbit).</summary>
    public bool InCockpit => !_thirdPerson && HasCockpit && ShowroomYaw == null && SeatIndex == 0;

    /// <summary>What is ridden has a driver's seat with a cockpit (#69 cars, #157 trucks and buses).</summary>
    private bool HasCockpit => _ride is Car or Truck;

    private Rideable? _ride;
    private RideMotion _motion;
    private float _slipCam;
    private Node3D? _visual;
    private RideKind _visualKind = RideKind.OnFoot;
    private int _visualSetup;
    private long _visualTuning;
    /// <summary>Owner: seconds until every door open while getting in (the driver's, any left open) shuts.</summary>
    private float _shutDriverIn;
    /// <summary>Doors shut by themselves above this speed, m/s (20 km/h).</summary>
    private const float DoorsShutSpeed = 20f / 3.6f;

    /// <summary>
    /// The garage's orbit of the chase camera round the car, radians from behind it, or null for the
    /// normal chase view. Set by <see cref="Vehicles.GarageUi"/> while it is open.
    /// </summary>
    public float? ShowroomYaw { get; set; }

    /// <summary>The replicated pose properties, in one place for the synchronizer and <c>--synccheck</c>.</summary>
    public static readonly string[] PoseProperties = { ".:BodyPose", ".:PoseKind", ".:Anim", ".:TrainPose" };

    // --- figure animation ---
    private MeshInstance3D? _walker;
    private Avatar.HumanPalette _walkPalette = Avatar.HumanPalette.Default;
    private float _stridePhase;
    /// <summary>Remote: the last replicated gait phase, so a fresh one is taken and a repeat integrated.</summary>
    private float _seenPhase = float.NaN;
    /// <summary>Owner: the stunned figure's lean toward the ground, eased.</summary>
    private float _downRot;
    /// <summary>Remote: a private instance of what the owner rides, for its <see cref="Rideable.AnimateRemote"/> state.</summary>
    private Rideable? _remoteRide;
    private Audio.EngineSynth? _remoteEngine;

    /// <summary>Free look while riding. Steering owns the body's yaw, so the eyes get their own.</summary>
    private float _lookYaw;
    /// <summary>Seconds since the mouse or the right stick last looked: the cockpit's look springs back only once they let go.</summary>
    private float _lookIdle;
    /// <summary>Cockpit: the head's sway from the car's accelerations, eased (node space: +X right, +Z back).</summary>
    private Vector3 _headSway;

    // --- third person on foot ---
    /// <summary>
    /// World yaw of the on-foot view. The mouse and stick turn this, not the body: in first
    /// person the body is set to it every frame, which is the old behaviour exactly; in third
    /// person the body turns to face where it is going instead, and the camera orbits freely.
    /// Movement is always relative to this, so "forward" is into the screen either way.
    /// </summary>
    private float _viewYaw;

    /// <summary>Third person (over the shoulder / chase) or first. Toggled with V / R3, saved.</summary>
    private bool _thirdPerson = Core.GameSettings.Current.ThirdPerson && !XR.XrSession.Active;

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
    /// <summary>Smoothed commanded-minus-achieved ground speed, m/s.</summary>
    private float _shortfall;

    /// <summary>How fast the smoothed real speed follows the measured one, per second.</summary>
    private const float ImpactResponse = 6f;

    /// <summary>A shortfall smaller than this is the ground, not a wall. m/s.</summary>
    private const float ImpactTolerance = 1.0f;

    /// <summary>How hard an impact bleeds the model's speed, m/s². A crash, not a brake.</summary>
    private const float ImpactDecel = 25f;

    /// <summary>What is being ridden, or null on foot. Read by the HUD and the picker.</summary>
    public RideKind Ride => (RideKind)RideKindId;

    /// <summary>The drawn body — figure, machine or craft — or null in first person on foot.</summary>
    public Node3D? Visual => _visual;

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
    public bool IsViewing => _camera is { Current: true } || (_camera != null && XR.XrSession.Anchor == _camera);

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
        RememberSafe(at);
    }

    /// <summary>
    /// Steps through an open doorway into the space on the other side: the same stride carried
    /// across by the door's map, so position, heading and momentum all come through, and nothing
    /// resets the way a teleport does. <paramref name="interiorKey"/> is null going out.
    /// </summary>
    public void CrossDoor(string? interiorKey, Vector3 at, float turn, Basis map)
    {
        InteriorKey = interiorKey;
        GlobalPosition = at;
        Velocity = map * Velocity;
        _viewYaw += turn;
        // mounted, the machine's heading is what sets the body's yaw every step
        _motion.Yaw += turn;
        Rotation = new Vector3(Rotation.X, Rotation.Y + turn, Rotation.Z);
        RememberSafe(at);
        _pivotY = float.NaN;
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
        RememberSafe(p);
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
    /// Mounted on anything that rolls or slides, the box it passes a doorway with (a garage's or a
    /// barn's, <see cref="Interiors.InteriorManager"/>): half its width and length and its height,
    /// measured from its model. Null on foot, and flying: a craft never goes through a door.
    /// </summary>
    public (float HalfWidth, float HalfLength, float Height)? DoorwayBox =>
        _ride is { } ride and not Flyer
            ? (ride.ParkedBox.Size.X / 2, ride.ParkedBox.Size.Z / 2, Mathf.Max(ride.ParkedBox.Size.Y, ride.BodyHeight))
            : null;

    /// <summary>
    /// For the flight probe: put an already-mounted craft in the air at a position and velocity,
    /// as if it had taken off. A plane needs a runway and a paraglider a launch slope, and neither
    /// is what a test of the flight model is about.
    /// </summary>
    public void DebugLaunch(Vector3 position, Vector3 velocity)
    {
        GlobalPosition = position;
        // placed by hand, so it need not wait for terrain under it (a probe over no terrain at all)
        _placed = true;
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
    /// <summary>The space <see cref="_lastSafe"/> was recorded in: an interior key, or null outside.</summary>
    private string? _safeSpace;
    private double _safeTimer;
    private float _deadTimer;

    /// <summary>Seconds left in which a fresh ride's contacts are not impacts (see ApplyRide).</summary>
    private float _settle;
    private const float SettleTime = 1f;

    /// <summary>How close a parked vehicle has to be to get into it, m.</summary>
    public const float EnterReach = 3.5f;

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

    /// <summary>
    /// A race NPC (<see cref="World.RaceNpc"/>), set before the node enters the tree. Its authority
    /// copy is simulated like a player's (physics, replication, <see cref="RideControls"/>) but has
    /// no camera, no feel layer and reads no input: the owner's keys drive the owner, not its NPCs.
    /// </summary>
    public bool Npc { get; set; }

    /// <summary>
    /// Replaces the move stick on foot, when set: a world-space wish (length up to 1) and whether to
    /// run. The scripted runner (<see cref="AutoPilot"/>) walks through it, as a rider through
    /// <see cref="RideControls"/>.
    /// </summary>
    public Func<(Vector3 Wish, bool Run)>? WalkControls { get; set; }

    private Camera3D? _camera;
    private CollisionShape3D _body = null!;
    private CapsuleShape3D _capsule = null!;
    private CapsuleShape3D? _standProbe;
    private float _pitch;

    /// <summary>Additive camera pitch (rad, + = up) from a recoil; decays on its own. Never enters <see cref="LookPitch"/>.</summary>
    private float _punch;

    /// <summary>Kicks the view up by <paramref name="radians"/>; it settles back in about a quarter of a second.</summary>
    public void Punch(float radians) => _punch = Mathf.Min(_punch + radians, 0.16f);

    private float _jolt;

    /// <summary>The body rocks back from a shot fired by this figure (every peer runs it from the Shot event; decays on its own).</summary>
    public void BodyJolt(float strength = 1f) => _jolt = Mathf.Clamp(strength, 0f, 1.5f);

    /// <summary>Where the eyes are, world space: the origin of anything you fire (third person's camera is metres behind it).</summary>
    public Vector3 EyePosition => GlobalPosition + Vector3.Up * EyeHeight;
    /// <summary>The view's pitch (radians, + up), clamped as the mouse would; for probes that aim.</summary>
    public float LookYaw { get => _viewYaw; set => _viewYaw = value; }
    public float LookPitch { get => _pitch; set => _pitch = ClampPitch(value); }
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
    /// A teleport that also turns a mount: the body at <paramref name="at"/>, stopped, facing
    /// <paramref name="yaw"/>, put down on the ground once it is there. Setting <c>Rotation</c> alone
    /// does not turn a ridden vehicle: its step writes the rotation back from its own heading.
    /// </summary>
    public void PlaceAt(Vector3 at, float yaw)
    {
        RequestReplacement();
        GlobalPosition = at;
        Rotation = new Vector3(0, yaw, 0);
        _motion.Yaw = yaw;
        _motion.YawRate = 0f;
        _motion.Slip = 0f;
        _viewYaw = yaw;
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
        if (!IsMultiplayerAuthority() && !NetProxy)
            GD.Print($"[net] player {Name} left view");
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
        replication.AddProperty(".:NetPos");
        replication.AddProperty(".:NetVel");
        replication.AddProperty(".:NetYaw");
        // What you are riding travels with where you are. Without it a remote client sees a
        // figure sprinting down a descent at 60 km/h in a running pose.
        replication.AddProperty(".:RideKindId");
        replication.AddProperty(".:CarSetupId");
        replication.AddProperty(".:TuningBits");
        replication.AddProperty(".:DoorsOpen");
        replication.AddProperty(".:TrailerCode");
        // a passenger's host and seat (#158): drawn and moved from the host's copy on every peer
        replication.AddProperty(".:RidingWith");
        replication.AddProperty(".:SeatIndex");
        replication.AddProperty(".:HeldItemId");
        replication.AddProperty(".:ItemAction");
        foreach (var prop in PoseProperties) replication.AddProperty(prop);
        replication.AddProperty(".:HeadwearId");
        replication.AddProperty(".:DanceId");
        replication.AddProperty(".:HeldRadio");
        replication.AddProperty(".:CarRadio");
        if (Npc)
        {
            // spawn-only: a peer spawning this NPC after a handoff must learn who simulates it now
            replication.AddProperty(".:SimPeer");
            replication.PropertySetReplicationMode(".:SimPeer", SceneReplicationConfig.ReplicationMode.Never);
        }
        replication.AddProperty(".:NetTime");   // last: its setter consumes the whole state
        // integers change a few times a minute: sent reliably when they change, not 30 times a second
        foreach (var prop in new[] { ".:RideKindId", ".:CarSetupId", ".:TuningBits", ".:DoorsOpen", ".:TrailerCode", ".:RidingWith", ".:SeatIndex", ".:HeldItemId", ".:ItemAction", ".:PoseKind", ".:HeadwearId", ".:DanceId", ".:HeldRadio", ".:CarRadio" })
            replication.PropertySetReplicationMode(prop, SceneReplicationConfig.ReplicationMode.OnChange);
        NetPos = Position;
        NetYaw = Rotation.Y;
        var sync = new MultiplayerSynchronizer
        {
            // deterministic name: replication matches nodes by path across peers, and
            // auto-generated names (@MultiplayerSynchronizer@N) differ per process
            Name = "Sync",
            RootPath = new NodePath(".."),
            ReplicationConfig = replication,
            // 30 Hz is plenty once the receiver interpolates; the frame rate was the old rate,
            // which is 144 packets a second per viewer from a fast machine
            ReplicationInterval = 1f / 30f,
        };
        _sync = sync;
        // the synchronizer's own authority decides who sends; children added after the
        // parent's SetMultiplayerAuthority default to server authority
        sync.SetMultiplayerAuthority(GetMultiplayerAuthority());
        _interest = GetNodeOrNull<Net.InterestService>("../../" + Net.InterestService.NodeName);
        long netId = NetId(Name) ?? 0;

        // The owner sends its state ONCE, to the server. It used to send one copy per viewer for
        // the server to relay — N·(N−1) packets into the server's socket, which overflowed at a
        // 32-car race start (thousands of UDP drops a second). The server rebroadcasts below.
        // Added on every copy: it is only consulted while this peer is the authority, which a
        // race NPC can become later (#50).
        sync.AddVisibilityFilter(Callable.From((long peer) => peer == 1));
        // a race NPC's spawn state may name another simulator than the spawn data: it is applied
        // here (SimPeer), so everything below sees the current authority
        AddChild(sync);
        NetProxy = !IsMultiplayerAuthority() && Net.NetworkManager.DedicatedServer;

        // The server's two rebroadcasts of that state (Net/InterestService decides who gets
        // which): viewers within 300 m or in the same race at 30 Hz, viewers further away who can
        // still see this player at 6 Hz — at that distance nobody can tell once interpolated.
        // Server-owned, so the server alone decides who is sent what, and the spawn and the
        // stream can never disagree about whether a peer has this node.
        _relayNear = MakeRelay("RelayNear", replication, spawn: true, 1f / 30f);
        _relayFar = MakeRelay("RelayFar", replication, spawn: false, 1f / 6f);
        if (NetProxy && netId != 0)
        {
            // peer 0 = "everyone?": must be no, or Godot broadcasts; the owner has its own copy —
            // for a race NPC its CURRENT simulator, which the server can change (#50)
            _relayNear.AddVisibilityFilter(Callable.From((long peer) =>
                peer != 0 && peer != GetMultiplayerAuthority() && _interest?.RelaysNear(peer, netId) == true));
            _relayFar.AddVisibilityFilter(Callable.From((long peer) =>
                peer != 0 && peer != GetMultiplayerAuthority() && _interest?.RelaysFar(peer, netId) == true));
        }
        AddChild(_relayNear);
        AddChild(_relayFar);

        // Whether this player EXISTS on a peer is the spawner's call, and Godot only consults
        // synchronizers the server has authority over for that (SceneReplicationInterface::
        // _update_spawn_visibility skips the rest) — "Sync" above is the owner's. So a second,
        // empty one, owned by the server, carries the vision decision: out of sight, the node is
        // despawned on that peer, and spawned back with the owner's current state on return.
        _vis = new MultiplayerSynchronizer
        {
            Name = "Vis",
            RootPath = new NodePath(".."),
            ReplicationConfig = new SceneReplicationConfig(),
            // it carries no data: without this Godot would still ask its filter every frame
            // for every peer, 30 000 managed calls a second at 32 players
            ReplicationInterval = 3600f,
            DeltaInterval = 3600f,
        };
        _vis.SetMultiplayerAuthority(1);
        if (NetProxy && netId != 0)
            // peer 0 is Godot asking "visible to everyone?": the answer must be no, or it
            // broadcasts and never asks per peer. A race NPC always exists on its simulator.
            _vis.AddVisibilityFilter(Callable.From((long peer) => peer != 0
                && (peer == GetMultiplayerAuthority() || _interest?.ServerSees(peer, netId) != false)));
        AddChild(_vis);
        _netUp = true;

        AddToGroup(Group);
        // drawn on both sides of a doorway it is stepping through
        AddToGroup(Interiors.DoorwayGhosts.Group);

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

        if (IsMultiplayerAuthority() && Npc)
        {
            // no camera to anchor the streamer: the body asks for its own ground and trunks
            Terrain?.AddAnchor(this, collision: true);
            SetProcessUnhandledInput(false);
            // spawned here after a handoff (#50): the spawn state put the mount in RideKindId, not under the body
            if (RideKindId != (int)RideKind.OnFoot && _ride == null) ApplyRide((RideKind)RideKindId, NetVel, TuningBits, CarSetupId);
        }
        else if (IsMultiplayerAuthority())
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

        if (NetProxy)
        {
            // the server relays and reads positions; it never draws or animates anyone
            _body.Disabled = true;
            SetProcess(false);
            SetPhysicsProcess(false);
            return;
        }

        if (!IsMultiplayerAuthority())
            GD.Print($"[net] player {Name} came into view at {GlobalPosition.Round()}");

        RefreshVisual();

        // every copy draws what is in the hand; only the local one also has a viewmodel
        AddChild(new Items.HeldItemVisual(this) { Name = "HeldItem" });

        if (IsMultiplayerAuthority() && !Npc && _interest != null)
            Callable.From(() => _interest.ReportView(Core.GameSettings.Current.CameraFar, BaseFov)).CallDeferred();
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
        // a car is redrawn when its preset or garage parts change too (the garage's live preview, a remote tune)
        if (!force && _visual != null && kind == _visualKind && TuningBits == _visualTuning && CarSetupId == _visualSetup && TrailerCode == _visualTrailer) return;

        _visual?.QueueFree();
        _visual = null;
        _walker = null;
        HandLocal = null;
        _visualKind = kind;
        _visualSetup = CarSetupId;
        _visualTuning = TuningBits;
        if (!IsMultiplayerAuthority()) FitRemoteBody(kind);
        // the sections behind a truck's cab: their own bodies, whatever else is drawn
        FitSections(kind);

        // an NPC keeps its jersey whoever simulates it: the colours of the client that asked for it
        int rider = Npc && NetId(Name) is long npcId && npcId < 0 ? (int)Net.PlayerReplication.NpcOwner(npcId) : GetMultiplayerAuthority();

        if (kind == RideKind.OnFoot)
        {
            // first person: nothing to draw, except in VR for the monitor's third-person view (#186)
            if (IsMultiplayerAuthority() && !Npc && !_thirdPerson && !XR.XrSession.Active) return;
            _walkPalette = Avatar.HumanPalette.ForRider(rider);
            _walker = new MeshInstance3D
            {
                Name = "Body",
                Mesh = Avatar.HumanMeshBuilder.BuildStride(_walkPalette, 0f, 0f, hat: Hat),
                MaterialOverride = Avatar.HumanMeshBuilder.Material(),
            };
            if (IsMultiplayerAuthority() && !Npc && !_thirdPerson) _walker.Layers = XR.XrSession.SpectatorOnlyLayer;
            _visual = _walker;
        }
        else
        {
            _walker = null;
            _visual = (_ride ?? CarSetups.Ride(kind, CarSetupId, TuningBits))?.BuildVisual(rider);
        }

        if (_visual != null)
        {
            _visual.Name = "Body";
            AddChild(_visual);
            // a machine is far bigger than the capsule it moves with; shots hit what is drawn
            if (kind != RideKind.OnFoot) Hurtbox.Fit(_visual);
            FitHull(kind == RideKind.OnFoot ? null : _ride ?? Rideable.Create(kind));
            // a craft's mesh is not authored level (the wingsuit is an upright figure); pose it
            // level for a remote copy, which only receives position and yaw
            if (_ride == null && Rideable.Create(kind) is Flyer remoteFlyer)
                remoteFlyer.Pose(_visual, 0f, default);
        }
    }

    /// <summary>
    /// Someone else's car must block like a car, not like the 0.3 m pedestrian capsule their node
    /// started as: the owner resizes its own body in ApplyRide, which never runs on this copy.
    /// </summary>
    private void FitRemoteBody(RideKind kind)
    {
        if (_capsule == null) return;
        var ride = kind == RideKind.OnFoot ? null : Rideable.Create(kind);
        _capsule.Radius = ride?.BodyRadius ?? BodyRadius;
        SetBodyHeight(ride?.BodyHeight ?? StandHeight);
    }

    /// <summary>Bottom of a hull, above the ground: bumps of the 1 m lattice must not catch it.</summary>
    private const float HullLift = 0.45f;

    /// <summary>Where the hull splits into body and cabin (or frame and rider), as a share of the height.</summary>
    private const float HullCut = 0.55f;

    private readonly CollisionShape3D?[] _hull = new CollisionShape3D?[2];
    private readonly Vector3[] _hullCentre = new Vector3[2];
    private bool _hullLeans;

    /// <summary>
    /// A car or a motorbike collides as what is DRAWN, never as the capsule that carries it: the
    /// user's rule is that nothing ever goes into another model. The capsule (radius 0.85 for a car)
    /// rides the ground — it glides over the terrain lattice where a box would snag — but two
    /// capsules only meet 1.7 m apart, and 4.2 m cars sank a third into each other.
    ///
    /// The hull is two boxes measured from the actual mesh of this very model (<see
    /// cref="Avatar.MeshBounds.Split"/>): the body below the belt line and the cabin above it — or a
    /// motorbike's frame and its rider — from <see cref="HullLift"/> up. Every model gets its own
    /// size (an AE86 is not an NSX), and <see cref="AlignHull"/> moves the boxes with the body's
    /// pose every frame. Built on the owner (its own physics, trees, walls) and on every remote copy
    /// so others hit what they see.
    /// </summary>
    private void FitHull(Rideable? ride)
    {
        bool wants = ride is { IsVehicle: true } and not Flyer && _visual != null;
        if (!wants)
        {
            for (int i = 0; i < 2; i++) { _hull[i]?.QueueFree(); _hull[i] = null; }
            return;
        }
        // measured at rest: the pose is applied per frame, so the visual's own transform is undone
        var pose = _visual!.Transform;
        _visual.Transform = Transform3D.Identity;
        var (lower, upper) = ride!.HullBoxes ?? Avatar.MeshBounds.Split(_visual, HullCut);
        _visual.Transform = pose;
        _hullLeans = ride is not (Car or Truck);   // lean-steered: yaw and pitch only (see AlignHull)
        var parts = new[] { lower, upper };
        for (int i = 0; i < 2; i++)
        {
            var box = parts[i];
            float bottom = Mathf.Max(box.Position.Y, ride!.HullLift);
            float top = box.End.Y;
            if (top - bottom < 0.1f || box.Size.X < 0.05f) { _hull[i]?.QueueFree(); _hull[i] = null; continue; }
            _hull[i] ??= new CollisionShape3D { Name = i == 0 ? "HullLow" : "HullHigh" };
            _hull[i]!.Shape = new BoxShape3D { Size = new Vector3(box.Size.X, top - bottom, box.Size.Z) };
            _hullCentre[i] = new Vector3(box.GetCenter().X, (top + bottom) / 2f, box.GetCenter().Z);
            if (_hull[i]!.GetParent() == null) AddChild(_hull[i]);
        }
        AlignHull();
    }

    /// <summary>
    /// Moves the hull with the body's pose (drift yaw, pitch over a crest, a flip). A leaning
    /// two-wheeler keeps its hull upright: rolled 50° into a bend, a box starting at 0.45 m would
    /// put its inside corner on the road and snag every corner.
    /// </summary>
    private void AlignHull()
    {
        if (_hull[0] == null && _hull[1] == null) return;
        var pose = BodyPose;
        if (_hullLeans)
        {
            var fwd = pose.Basis.Z;
            float yaw = Mathf.Atan2(fwd.X, fwd.Z);
            float pitch = -Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f));
            pose = new Transform3D(Basis.FromEuler(new Vector3(pitch, yaw, 0)), pose.Origin);
        }
        for (int i = 0; i < 2; i++)
            if (_hull[i] != null) _hull[i]!.Transform = pose * new Transform3D(Basis.Identity, _hullCentre[i]);
    }

    /// <summary>
    /// Remote players get no physics, so the replicated ride kind has to be polled. It changes
    /// perhaps twice a minute; comparing an int per frame is cheaper than an RPC to announce it.
    /// </summary>
    public override void _Process(double delta)
    {
        _punch *= Mathf.Exp(-12f * (float)delta);
        _jolt *= Mathf.Exp(-9f * (float)delta);
        if (IsMultiplayerAuthority())
        {
            NetPos = Position;
            NetVel = Velocity;
            NetYaw = Rotation.Y;
            NetTime = Time.GetTicksUsec() / 1e6;
            float dt = (float)delta;
            ApplyStickLook(dt);
            if (RidingWith != 0)
            {
                // a passenger (#158): in its seat on the host's vehicle, looking out of it
                UpdateSeated();
                UpdateSeatCamera(dt);
                return;
            }
            if (_ride != null)
            {
                if (_visual != null)
                {
                    if (_ride is Flyer f) f.AnimateFlight(_visual, _flight, dt);
                    else _ride.Animate(_visual, _motion, dt);
                    BodyPose = _visual.Transform;
                    AlignHull();
                    Anim = _ride.WritePose(_visual, _motion, _flight);
                    if (_ride is Truck heavy) PublishTrain(heavy);
                }
                UpdateSeated();   // sat in it driverless, not at the wheel
                return;
            }

            // published whatever the view: first person draws no body, but everyone else does
            PublishFootPose(dt);
            StepThrowView(dt);

            // Render rate, not physics rate: the look has to answer the mouse the frame it
            // moves, the way rotating the body directly always did.
            if (!_thirdPerson)
            {
                Rotation = new Vector3(0, _viewYaw, 0);
                // the body only the monitor's third-person camera sees (#186)
                if (XR.XrSession.Active) ApplyFootPose();
            }
            else if (ScopeView && _camera != null)
            {
                // looking through something held to the eye: first person for as long as it lasts
                Rotation = new Vector3(0, _viewYaw, 0);
                _camera.Transform = new Transform3D(new Basis(Vector3.Right, _pitch + _punch),
                    new Vector3(0, EyeHeight + _landingDip, 0));
                if (_walker != null) _walker.Visible = false;
                HandLocal = null;
                _pivotY = float.NaN;
            }
            else
            {
                // borrowed from first person for a throw: the body only shows once the lens is out of the head
                if (_walker != null) _walker.Visible = !_borrowedThird || _throwBlend > 0.3f;
                ApplyFootPose();
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
        // A sender publishes 30 times a second, standing still or not: silent this long, it has
        // crashed or frozen, and ENet takes up to 30 s to say so. Its body, frozen mid-road, must
        // not be a wall the whole field piles into (#50: every car stopped on a crashed leader).
        bool silent = Time.GetTicksMsec() / 1000.0 - LastNetState > SilentSeconds;
        // a passenger has no body of its own: it is in the vehicle
        bool off = silent || RidingWith != 0;
        if (_body.Disabled != off) _body.Disabled = off;

        if (RidingWith != 0 && Host is { } host)
        {
            // where its host's copy is, exactly: not its own interpolated stream, which would shake in the seat
            Position = host.Position;
            Rotation = host.Rotation;
        }
        else if (_interp.HasData)
        {
            var (p, yaw) = _interp.Sample(Time.GetTicksUsec() / 1e6, (float)delta);
            Position = p;
            Rotation = new Vector3(0, yaw, 0);
        }
        RefreshVisual();
        AnimateRemote((float)delta);
        UpdateSeated();
    }

    /// <summary>
    /// Draws a remote player from the pose its owner published, never from anything this peer
    /// guesses. The gait phase is taken fresh whenever a new one arrives and only integrated in
    /// between (updates come at the network's rate, not every frame), so the feet land when the
    /// owner's do instead of on a phase this peer made up.
    /// </summary>
    private void AnimateRemote(float dt)
    {
        if (dt <= 0) return;
        var kind = (RideKind)RideKindId;
        if (kind == RideKind.OnFoot)
        {
            if (Anim.Y != _seenPhase) _stridePhase = _seenPhase = Anim.Y;
            else if (PoseKind == PoseStride) _stridePhase = Avatar.HumanMeshBuilder.AdvancePhase(_stridePhase, Anim.X, dt);
            ApplyFootPose();
            SetRemoteEngine(null);
            return;
        }
        if (_visual == null) return;
        if (_remoteRide?.Kind != kind) _remoteRide = Rideable.Create(kind);
        _visual.Transform = BodyPose;
        AlignHull();
        _remoteRide?.AnimateRemote(_visual, Anim, dt);
        AnimateRemoteSections(dt);
        if (_visual is Avatar.CarRig rig) { rig.DoorsOpen = DoorsOpen; rig.DriverShown = SeatIndex == 0; }
        else if (_visual is Avatar.HeavyRig heavyRig) heavyRig.DriverShown = SeatIndex == 0;
        SetRemoteEngine(_remoteRide as Flyer);
    }

    /// <summary>
    /// Another player's helicopter or plane is heard where it is, from the spool and throttle it
    /// publishes — the same sound a parked one makes (<c>VehicleBody</c>), driven like the pilot's own.
    /// </summary>
    private void SetRemoteEngine(Flyer? craft)
    {
        bool wanted = craft is { HasEngine: true } && DisplayServer.GetName() != "headless";
        if (!wanted)
        {
            _remoteEngine?.QueueFree();
            _remoteEngine = null;
            return;
        }
        bool heli = craft is Helicopter;
        var profile = heli ? Audio.EngineProfile.Turboshaft : Audio.EngineProfile.PistonAero;
        if (_remoteEngine?.Profile != profile)
        {
            _remoteEngine?.QueueFree();
            _remoteEngine = new Audio.EngineSynth(profile, spatial: true, seed: GetMultiplayerAuthority()) { Name = "RemoteEngine" };
            AddChild(_remoteEngine);
        }
        float spool = Anim.X, control = Anim.Y;
        if (heli) _remoteEngine.Set(spool, spool, 0.35f, 0.3f + 0.6f * spool);
        else _remoteEngine.Set(Mathf.Clamp((spool - 0.15f) / 0.85f, 0f, 1f), control, control,
            spool > 0.02f ? 0.35f + 0.45f * spool : 0f);
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
        HandLocal = _walker.Transform * new Transform3D(mounts.HandBasis, hand);
    }

    /// <summary>The arm pose the held item and <see cref="ItemAction"/> call for, the same on every peer.</summary>
    private Avatar.ItemArmPose TargetArmPose()
    {
        // a throw (#206): wound up with anything, and the follow-through once it has left the hand
        if (ItemAction == 3) return Avatar.ItemArmPose.ThrowWindup;
        if (ItemAction == 4) return Avatar.ItemArmPose.ThrowRelease;
        var def = Items.ItemDefs.Get((Items.ItemId)HeldItemId);
        if (def == null) return Avatar.ItemArmPose.None;
        bool aim = ItemAction == 1, use = ItemAction == 2;
        return def.Use switch
        {
            Items.ItemUse.Optic or Items.ItemUse.Photo => aim || use ? Avatar.ItemArmPose.TwoHandEye : Avatar.ItemArmPose.Hold,
            Items.ItemUse.Shoot => aim || use ? Avatar.ItemArmPose.ShoulderAim : Avatar.ItemArmPose.Hold,
            Items.ItemUse.Consume => use ? Avatar.ItemArmPose.Mouth : Avatar.ItemArmPose.Hold,
            Items.ItemUse.Wear => use ? Avatar.ItemArmPose.Mouth : Avatar.ItemArmPose.Hold,   // a hat goes up to the head
            Items.ItemUse.Place => use ? Avatar.ItemArmPose.Plant : Avatar.ItemArmPose.Hold,
            _ => Avatar.ItemArmPose.Hold,
        };
    }

    /// <summary>Eases the arm override toward the target pose: out of the old one, then into the new.</summary>
    private void StepArmPose(float dt)
    {
        var want = Ride == RideKind.OnFoot ? TargetArmPose() : Avatar.ItemArmPose.None;
        float target = want == Avatar.ItemArmPose.None ? 0f : 1f;
        if (!_itemArmInit)
        {
            if (!IsMultiplayerAuthority() && _netTime == 0) return;   // nothing received yet: what it holds is unknown
            // a peer that has only just started to draw this figure shows it as it is, not ramping up
            _itemArmInit = true;
            _itemArmCur = want;
            _itemArmBlend = target;
            return;
        }
        // the release whips straight out of the wind-up: no easing out of one into the other
        if (want == Avatar.ItemArmPose.ThrowRelease && _itemArmCur == Avatar.ItemArmPose.ThrowWindup) _itemArmCur = want;
        if (want != _itemArmCur && _itemArmBlend > 0.02f) target = 0f;   // leave the old pose first
        else if (want != _itemArmCur) _itemArmCur = want;
        _itemArmBlend = Mathf.MoveToward(_itemArmBlend, target, dt / 0.18f);
        if (_itemArmBlend <= 0f && want == Avatar.ItemArmPose.None) _itemArmCur = Avatar.ItemArmPose.None;
    }

    /// <summary>
    /// The owner's on-foot pose, every frame and whatever the view: the solved gait grounded, a
    /// mid-stride leap in the air, a crouch while sliding, the landing squash and a stunned figure
    /// lying flat. Written into the replicated <see cref="PoseKind"/>, <see cref="Anim"/> and
    /// <see cref="BodyPose"/>, which <see cref="ApplyFootPose"/> draws on this peer and every other.
    /// </summary>
    private void PublishFootPose(float dt)
    {
        // The owner decides when the dance is over: out of earshot, or doing anything else.
        // Remotes only ease out on what they receive.
        if (DanceId != 0 && !DanceAllowed()) DanceId = 0;
        _airTime = IsOnFloor() ? 0f : _airTime + dt;
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();

        PoseKind = _sliding ? PoseTucked : _airTime > 0.12f ? PoseAir : PoseStride;
        if (PoseKind == PoseStride) _stridePhase = Avatar.HumanMeshBuilder.AdvancePhase(_stridePhase, speed, dt);
        Anim = new Vector4(speed, _stridePhase, 0f, 0f);

        // Landing squash: the same spring that dips the first-person eye, spent on the body's
        // proportions instead. Volume is roughly kept, so it reads as knees taking the weight
        // rather than the figure shrinking.
        float squash = Mathf.Clamp(-_landingDip * 1.2f, 0f, 0.22f);
        // thrown, stunned or knocked out: flat on the ground
        float down = _stunTimer > 0 && IsOnFloor() ? -1.45f : 0f;
        _downRot = Mathf.Lerp(_downRot, down, 1f - Mathf.Exp(-10f * dt));
        BodyPose = new Transform3D(
            new Basis(Vector3.Right, _downRot) * Basis.FromScale(new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f)),
            new Vector3(0, Mathf.Abs(_downRot) * 0.12f, 0));
    }

    /// <summary>
    /// Whether the owner may go on dancing: on foot, upright, outdoors, and a radio still playing
    /// within a little more than the radius that let it start (hysteresis, so the edge of
    /// earshot does not flicker).
    /// </summary>
    private bool DanceAllowed() =>
        Ride == RideKind.OnFoot && !KnockedOut && !_sliding && !Indoors
        && Items.RadioManager.Instance?.NearestPlaying(GlobalPosition, Items.RadioManager.DanceRadius * 1.15f) != null;

    /// <summary>
    /// The beat-driven pose for this frame, or null. Everything comes off the replicated
    /// <see cref="DanceId"/>, the nearest playing radio and the shared clock, so the owner and every
    /// remote copy compute the same move on the same beat with nothing else replicated. The
    /// figure eases in and out over a quarter second, and keeps the last radio through the ease-out.
    /// </summary>
    private Avatar.DanceParams? StepDance(float dt)
    {
        Items.RadioBody? radio = null;
        bool want = DanceId != 0 && Ride == RideKind.OnFoot && !KnockedOut && !_sliding;
        if (want)
        {
            // a remote copy tolerates a wider ring: its position lags the owner's a little
            radio = Items.RadioManager.Instance?.NearestPlaying(GlobalPosition, Items.RadioManager.DanceRadius * 1.3f);
            want = radio != null;
        }
        _danceWeight = Mathf.MoveToward(_danceWeight, want ? 1f : 0f, dt * 4f);
        if (radio != null) _danceRadio = radio;
        if (_danceWeight <= 0.001f || _danceRadio == null || !IsInstanceValid(_danceRadio) || !_danceRadio.IsInsideTree()
            || !_danceRadio.BeatAt(Net.ClockSync.ServerNow, out float phase, out int beat, out int bar, out var style))
        {
            _danceRadio = null;
            _danceWeight = 0f;
            return null;
        }
        // The move changes every couple of bars, the same one on every peer: a hash of the bar
        // slot and the style, so a figure that joins mid-song lands on the move the others are on.
        int slot = Mathf.FloorToInt(bar / (float)Avatar.HumanMeshBuilder.BarsPerMove);
        uint h = (uint)slot * 2654435761u ^ (uint)style * 40503u;
        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
        int move = (int)(h % (uint)Avatar.HumanMeshBuilder.MoveCount(style));
        float barPhase = (beat - bar * 4 + phase) / 4f;
        return new Avatar.DanceParams(style, move, phase, barPhase, bar, _danceWeight);
    }

    /// <summary>Draws the on-foot figure from the published pose — the same code for the owner and every remote copy.</summary>
    private void ApplyFootPose()
    {
        if (_walker == null) return;
        // the two held poses are cached, so a hat put on or taken off (#18) rebuilds them
        if (_poseHat != Hat) { _slidePose = null; _airPose = null; _poseHat = Hat; }
        float dt = (float)GetProcessDeltaTime();
        StepArmPose(dt);
        var dance = StepDance(dt);
        var arm = _itemArmCur;
        float blend = _itemArmBlend;
        // dancing, the hands are the dance's, unless the item is actually being aimed or used
        if (dance != null && ItemAction == 0) { arm = Avatar.ItemArmPose.None; blend = 0f; }
        bool armed = arm != Avatar.ItemArmPose.None && blend > 0.001f;
        Avatar.HumanMeshBuilder.GaitMounts mounts;
        switch (PoseKind)
        {
            case PoseTucked:
                _walker.Mesh = armed ? Avatar.HumanMeshBuilder.BuildPosed(_walkPalette, Avatar.HumanPose.Tucked, arm, blend, Hat)
                    : _slidePose ??= Avatar.HumanMeshBuilder.Build(_walkPalette, Avatar.HumanPose.Tucked, hat: Hat);
                mounts = Avatar.HumanMeshBuilder.MountsForPose(Avatar.HumanPose.Tucked, arm, blend);
                break;
            case PoseAir:
                _walker.Mesh = armed ? Avatar.HumanMeshBuilder.BuildPosed(_walkPalette, Avatar.HumanPose.Running, arm, blend, Hat)
                    : _airPose ??= Avatar.HumanMeshBuilder.Build(_walkPalette, Avatar.HumanPose.Running, hat: Hat);
                mounts = Avatar.HumanMeshBuilder.MountsForPose(Avatar.HumanPose.Running, arm, blend);
                break;
            default:
                _walker.Mesh = Avatar.HumanMeshBuilder.BuildStride(_walkPalette, Anim.X, _stridePhase, hat: Hat, arm: arm, armBlend: blend, dance: dance);
                mounts = Avatar.HumanMeshBuilder.MountsFor(Anim.X, _stridePhase, arm, blend, dance);
                break;
        }
        _walker.Transform = BodyPose;
        if (_jolt > 0.01f)
        {
            // rock back about the hips (positive X turns the head toward +Z, behind a figure that faces -Z)
            var t = new Transform3D(new Basis(Vector3.Right, _jolt * 0.11f), Vector3.Zero);
            var hip = new Vector3(0, 0.95f, 0);
            _walker.Transform = BodyPose * new Transform3D(Basis.Identity, hip) * t * new Transform3D(Basis.Identity, -hip);
        }
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
    private const float ThrowCamHeight = 1.62f, ThrowCamOffset = 0.62f, ThrowCamDistance = 1.7f;

    /// <summary>
    /// Eases the throw camera toward <see cref="ThrowAim"/>, lending third person to a first-person
    /// player for as long as it is out (the body has to be built to be seen) and handing it back once
    /// the lens is home in the head.
    /// </summary>
    private void StepThrowView(float dt)
    {
        float want = ScopeView ? 0f : Mathf.Clamp(ThrowAim, 0f, 1f);
        _throwBlend = Mathf.Lerp(_throwBlend, want, 1f - Mathf.Exp(-(want > _throwBlend ? 9f : 7f) * dt));
        if (want == 0f && _throwBlend < 0.01f) _throwBlend = 0f;
        if (!_thirdPerson && want > 0f)
        {
            _borrowedThird = true;
            _thirdPerson = true;
            _pivotY = float.NaN;
            _armBlend = 1f;
            RefreshVisual(force: true);
        }
        else if (_borrowedThird && want == 0f && _throwBlend == 0f)
        {
            _borrowedThird = false;
            _thirdPerson = false;
            if (_camera != null) _camera.Transform = new Transform3D(Basis.Identity, new Vector3(0, EyeHeight, 0));
            RefreshVisual(force: true);
        }
    }

    private void UpdateThirdPersonCamera(float dt)
    {
        if (_camera == null) return;

        // The pivot drops with the slide, and its height is eased so a step, a kerb or the top
        // of a jump does not jerk the whole picture; far off (a teleport), it snaps.
        // A throw pulls the lens in close over the right shoulder (#206); lent from first person,
        // the arm grows out of the eye instead of shrinking from the chase distance.
        float tb = _throwBlend;
        float height = Mathf.Lerp(_borrowedThird ? EyeHeight : Mathf.Lerp(ShoulderHeight, 0.95f, _slideBlend), ThrowCamHeight, tb);
        float pivotTarget = GlobalPosition.Y + height;
        _pivotY = float.IsNaN(_pivotY) || Mathf.Abs(pivotTarget - _pivotY) > 6f
            ? pivotTarget
            : Mathf.Lerp(_pivotY, pivotTarget, 1f - Mathf.Exp(-10f * dt));
        var pivot = new Vector3(GlobalPosition.X, _pivotY, GlobalPosition.Z);

        var view = new Basis(Vector3.Up, _viewYaw) * new Basis(Vector3.Right, _pitch + _punch);
        if (CameraShake > 0f)
        {
            // two incommensurate wobbles per axis: a tremble, not a wave
            float now = (float)Time.GetTicksMsec() / 1000f;
            view = view * new Basis(Vector3.Right, CameraShake * (Mathf.Sin(now * 61f) + 0.6f * Mathf.Sin(now * 97f)))
                        * new Basis(Vector3.Up, CameraShake * (Mathf.Sin(now * 53f + 1f) + 0.6f * Mathf.Sin(now * 89f)));
        }

        // pulled back a little with speed, so a sprint and a slide feel like they cover ground
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();
        float distance = Mathf.Lerp(_borrowedThird ? 0f : ArmLength + Mathf.Clamp(speed / RunSpeed, 0f, 1.6f) * 0.6f, ThrowCamDistance, tb);

        var shoulder = pivot + view.X * Mathf.Lerp(_borrowedThird ? 0f : ShoulderOffset, ThrowCamOffset, tb);
        var wanted = shoulder + view.Z * distance;

        // cast from the body's centre, not the shoulder, so a wall at the player's right does not
        // leave the lens behind it
        float want = 1f;
        float span = Mathf.Max(0.01f, (wanted - pivot).Length());
        var space = GetWorld3D().DirectSpaceState;
        // An arm reaching back through an open doorway goes on in the space on the other side:
        // this side up to the sill (the building's shell there is the doorway, not a wall), then
        // the rest carried across by the door's map, where the lens ends up if it gets that far.
        float through = 2f;
        var across = Transform3D.Identity;
        if (Interiors.InteriorManager.Instance?.ArmThroughDoor(this, pivot, wanted, out float t, out var map, out var shell) == true)
        {
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            if (shell.IsValid) exclude.Add(shell);
            var sill = pivot.Lerp(wanted, t);
            var near = space.IntersectRay(PhysicsRayQueryParameters3D.Create(pivot, sill, CameraMask, exclude));
            if (near.Count > 0)
                want = Mathf.Clamp(((near["position"].AsVector3() - pivot).Length() - 0.25f) / span, 0.1f, 1f);
            else
            {
                through = t;
                across = map;
                var far = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    map * pivot.Lerp(wanted, Mathf.Min(1f, t + 0.1f / span)), map * wanted, CameraMask, exclude));
                if (far.Count > 0)
                    want = Mathf.Clamp((t * span + (far["position"].AsVector3() - map * sill).Length() - 0.25f) / span, 0.1f, 1f);
            }
        }
        else
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                pivot, wanted, CameraMask, new Godot.Collections.Array<Rid> { GetRid() }));
            if (hit.Count > 0)
                want = Mathf.Clamp(((hit["position"].AsVector3() - pivot).Length() - 0.25f) / span, 0.1f, 1f);
        }
        // snap in, ease out: late at a wall is a frame with the lens inside it
        _armBlend = want < _armBlend ? want : Mathf.Lerp(_armBlend, want, 1f - Mathf.Exp(-5f * dt));

        var position = pivot.Lerp(wanted, _armBlend) + Vector3.Up * _landingDip * 0.5f;
        var lens = new Transform3D(view, position);
        _camera.GlobalTransform = _armBlend > through ? across * lens : lens;
    }

    /// <summary>
    /// Switches first/third person, rebuilding the local body and saving the choice. At the wheel
    /// of a car, a truck or a bus it is a cycle of three: chase camera, the cockpit with your own
    /// arms and legs, the cockpit without them.
    /// </summary>
    private void ToggleView()
    {
        // mid-throw from first person: the lent view is given back first, then toggled as usual
        if (_borrowedThird)
        {
            _borrowedThird = false;
            _thirdPerson = false;
        }
        var settings = Core.GameSettings.Current;
        // VR is first person only (#186): at the wheel, V still shows or hides your own body
        if (XR.XrSession.Active)
        {
            if (HasCockpit)
            {
                settings.CockpitBody = !settings.CockpitBody;
                settings.Save();
            }
            return;
        }
        if (HasCockpit && !_thirdPerson && settings.CockpitBody)
        {
            settings.CockpitBody = false;
            settings.Save();
            return;
        }
        _thirdPerson = !_thirdPerson;
        if (HasCockpit && !_thirdPerson) settings.CockpitBody = true;
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
        if (RidingWith != 0) return TryLeaveSeat();
        if (_ride is { IsVehicle: true })
        {
            ExitVehicle();
            return true;
        }
        // what the view points at and the border outlines (#206): a dropped item is picked up
        if (_ride == null && !_mantling && _deadTimer <= 0 && Items.Highlight.Pointed is Items.DroppedItem dropped
            && IsInstanceValid(dropped) && Items.ItemController.Instance is { } items)
        {
            items.PickUp(dropped);
            return true;
        }
        // inside, E is the front door or nothing: no mount picker in a living room
        // (or the cupboard in front of you: searching comes first, the door is by the door),
        // but a car parked in the garage is got into like anywhere else
        if (Indoors)
        {
            if (_ride == null && !_mantling && _deadTimer <= 0 && Vehicles?.Nearest(GlobalPosition, EnterReach) is { } parked)
            {
                Vehicles.Claim(parked, EnterVehicle);
                return true;
            }
            var interiors = Interiors.InteriorManager.Instance;
            if (interiors?.AtExit(this) != true && Loot.LootService.Instance?.TrySearch(this) == true) return true;
            return interiors?.TryDoor(this) ?? true;
        }
        if (_ride != null || _mantling || _deadTimer > 0) return false;

        // a radio within reach, the one pointed at first: its panel (play a CD, burn one, pick it up)
        if ((Items.Highlight.Pointed as Items.RadioBody ?? Items.RadioManager.Instance?.Nearest(GlobalPosition, Items.RadioManager.Reach)) is { } radio)
        {
            Items.RadioUi.Instance?.Open(radio);
            return true;
        }

        var vehicle = Vehicles?.Nearest(GlobalPosition, EnterReach);
        // someone else's vehicle, being driven: a seat in it (#158), when it is nearer than a parked one
        if (OnlineSeats && DrivenVehicleInReach(EnterReach) is { } driven
            && (vehicle == null || driven.GlobalPosition.DistanceTo(GlobalPosition) < vehicle.GlobalPosition.DistanceTo(GlobalPosition)))
        {
            PassengerService.Instance!.AskSeat(driven);
            return true;
        }
        if (vehicle == null)
        {
            // music in earshot: E starts or stops the dance
            if (Items.RadioManager.Instance?.NearestPlaying(GlobalPosition, Items.RadioManager.DanceRadius) != null)
            {
                DanceId = DanceId == 0 ? 1 : 0;
                return true;
            }
            return IsOnFloor() && Interiors.InteriorManager.Instance?.TryDoor(this) == true;
        }
        Vehicles!.Claim(vehicle, EnterVehicle);
        return true;
    }

    /// <summary>Takes over a vehicle from the world: its position, heading, momentum and damage.</summary>
    private void EnterVehicle(VehicleState state)
    {
        if (_sliding) EndSlide();
        GlobalPosition = state.Position;
        Rotation = new Vector3(0, state.Yaw, 0);
        // the same car: its preset, its garage parts and whatever doors were left open come with it; the
        // driver's door opens to let them in, and once seated every door shuts (and stays shut:
        // nobody drives with a door open, see TryToggleCarDoor)
        ApplyRide(state.Kind, state.Velocity, state.Tuning, state.Setup);
        if (HeavyCatalog.For(state.Kind) != null && state.CreateRide() is Truck train)
        {
            // the truck as it was left: its trailer, its angles, its doors and display, its load
            _ride = train;
            TrailerCode = train.TrailerCode;
            RefreshVisual(force: true);
        }
        if (_ride is Car)
        {
            DoorsOpen = (byte)(state.DoorsOpen | Avatar.CarRig.DriverDoor);
            _shutDriverIn = 1f;
        }
        _flight.Control = state.Throttle;
        EngineOn = true;
        VehicleHealth = state.Health;
        if (_ride is Car car)
        {
            car.Headlights = state.Headlights;
            car.RoofOpen = state.RoofOpen && car.HasSoftTop;
        }
        CarRadio = state.Radio;
        _placed = true;
    }

    /// <summary>The vehicle as it is right now, to hand to the world.</summary>
    private VehicleState CaptureVehicle(bool wrecked)
    {
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        var velocity = _ride is Flyer
            ? _flight.Velocity
            : heading.Rotated(Vector3.Up, _motion.Slip) * _motion.Speed + Vector3.Up * Velocity.Y;
        return new VehicleState((RideKind)RideKindId, GlobalPosition,
            _ride is Flyer ? _flight.Yaw : Rotation.Y, velocity,
            wrecked ? 0f : VehicleHealth, EngineOn && !wrecked, wrecked, _flight.Control, VehicleState.Now,
            Headlights: _ride is Car { Headlights: true }, RoofOpen: _ride is Car { RoofOpen: true },
            Tuning: TuningBits, DoorsOpen: wrecked ? (byte)0 : DoorsOpen, Setup: CarSetupId,
            Train: _ride is Truck t ? t.TrailerCode : 0, Angles: _ride is Truck ta ? ta.Angles : default,
            Flags: _ride is Truck tf ? tf.PackFlags() & ~5 : 0, Load: _ride is Truck tl ? tl.Load : 0.5f,
            Radio: wrecked ? 0 : CarRadio);
    }

    /// <summary>
    /// Puts a preset (<see cref="CarSetups"/>) on the car being driven, at a standstill: the car is
    /// rebuilt from the catalog with it, garage parts, lights and roof kept. False when not in a car or moving.
    /// </summary>
    public bool SetCarSetup(int id)
    {
        id = CarSetups.Clamp(id);
        if (_ride is not Car old || CarCatalog.For(old.Kind) is null) return false;
        if (id == CarSetupId) return true;
        if (GroundSpeed > 2f) return false;
        var car = (Car)CarSetups.Ride(old.Kind, id, TuningBits)!;   // the garage parts stay on
        car.Headlights = old.Headlights;
        car.RoofOpen = old.RoofOpen;
        _ride = car;
        CarSetupId = id;
        RefreshVisual();
        return true;
    }

    /// <summary>
    /// The garage: puts these parts on the car being driven, at once (its live preview). The car is
    /// rebuilt from the catalog with them — new tyres are new tyres, wear and all.
    /// </summary>
    public void SetTuning(CarTuning tuning)
    {
        if (_ride is not Car car || CarCatalog.For(car.Kind) is not { } stock) return;
        _ride = new Car(CarSetups.For(CarSetupId).Apply(stock), tuning);   // over the preset (#40)
        TuningBits = tuning.Pack();
        RefreshVisual();
    }

    /// <summary>The garage parts on the car being driven; Stock when not in one.</summary>
    public CarTuning Tuning => _ride is Car car ? car.Tuning : default;

    /// <summary>
    /// G / pad X: works a car door without getting in: on foot beside a parked car, the door nearest
    /// you. Not from the seat — in a car the doors are shut. False when there is none in reach.
    /// </summary>
    public bool TryToggleCarDoor()
    {
        if (_ride != null || Vehicles?.Nearest(GlobalPosition, VehicleManager.DoorReach) is not { Rig: { } rig } vehicle)
            return false;
        var (bit, distance) = rig.NearestDoor(GlobalPosition);
        if (bit == 0 || distance > VehicleManager.DoorReach) return false;
        Vehicles.ToggleDoor(vehicle, bit);
        return true;
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
        // a left-hand-drive cab: out of the left door
        if (vehicle.ExitLeft) right = -right;
        // clear of the whole machine — past the wing of a plane, not 2 m into it
        float side = Mathf.Max(vehicle.BodyRadius, vehicle.ParkedBox.Size.X * 0.5f) + BodyRadius + 0.5f;
        // beside the door, not the middle: a bus's front door is six metres ahead of it
        var door = vehicle.EntryPoint == Vector3.Zero ? state.Position : ToGlobal(new Vector3(0, 0, vehicle.EntryPoint.Z));
        bool grounded = IsOnFloor();
        var ahead = -GlobalTransform.Basis.Z with { Y = 0 };
        ahead = ahead.LengthSquared() > 1e-6f ? ahead.Normalized() : Vector3.Forward;
        float end = vehicle.ParkedBox.Size.Z * 0.5f + BodyRadius + 0.3f;
        // out of a car through the driver's door: it opens, and shuts behind (unless left open)
        if (vehicle is Car && (state.DoorsOpen & Avatar.CarRig.DriverDoor) == 0)
            state = state with { DoorsOpen = (byte)(state.DoorsOpen | Avatar.CarRig.DriverDoor | VehicleState.DriverDoorShuts) };

        // with people aboard (or sat in it driverless) it is not parked: it rolls on with them (#158)
        if (OnlineSeats && (SeatIndex > 0 || Riders.Any())) PassengerService.Instance!.HostLeaving(state);
        else Vehicles?.Park(state);
        SeatIndex = 0;

        ApplyRide(RideKind.OnFoot, state.Velocity + right * 2f);
        GlobalPosition = FindExit(door, right, side, ahead, end, grounded);
    }

    /// <summary>
    /// A clear spot beside the vehicle: its right, else its left, else behind or in front of it (a
    /// car in a garage one car wide), else on top. In the air there is nothing to stand on either
    /// side, so the right side it is.
    /// </summary>
    private Vector3 FindExit(Vector3 at, Vector3 right, float side, Vector3 ahead, float end, bool grounded)
    {
        if (!grounded) return at + right * side;
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        foreach (var raw in new[] { at + right * side, at - right * side, at - ahead * end, at + ahead * end, at + Vector3.Up * 2.8f })
        {
            // on a slope the ground beside the seat is not at the seat's height: stand on it,
            // or the uphill side reads as blocked and the player is put on the vehicle's roof
            // (indoors the terrain is 3 km overhead: the floor is at the seat's height)
            var candidate = raw;
            if (raw.Y <= at.Y + 0.01f && !Indoors && Terrain != null && Terrain.TryGetHeight(raw, out float g))
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
        // everyone aboard goes out with the driver
        if (OnlineSeats && (SeatIndex > 0 || Riders.Any())) PassengerService.Instance!.Wrecked(state.Velocity);
        SeatIndex = 0;
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

    /// <summary>
    /// A round from a gun (<see cref="Combat.CombatManager"/>). A vehicle takes it for its
    /// occupant, and going to zero wrecks it with them inside; on foot or on equipment it is the
    /// player who is hit.
    /// </summary>
    public void ShotHit(float damage)
    {
        if (_ride is { IsVehicle: true })
        {
            VehicleHealth -= damage;
            if (VehicleHealth <= 0f) WreckVehicle();
            return;
        }
        TakeDamage(damage);
    }

    /// <summary>
    /// A bird strike (<see cref="Birds.BirdLife"/>): damage like a round, and a big enough bird
    /// through the propeller or the rotor stops the engine — the plane then glides and the
    /// helicopter autorotates, exactly as if it had been switched off.
    /// </summary>
    public void BirdStrike(float damage, bool engineOut)
    {
        Shaken?.Invoke(Mathf.Clamp(damage / 40f, 0.15f, 1f));
        bool stop = engineOut && _ride is { HasEngine: true } && EngineOn;
        ShotHit(damage);
        if (stop && _ride is { HasEngine: true })
        {
            EngineOn = false;
            EngineToggled?.Invoke(false);
            Announced?.Invoke("BIRD STRIKE — ENGINE OUT", false);
        }
        // a tit on the windscreen is a thud, not an event worth a banner
        else if (damage >= 1f) Announced?.Invoke("BIRD STRIKE", false);
    }

    /// <summary>Down after losing all health, until revived a few seconds later.</summary>
    public bool KnockedOut => _deadTimer > 0;

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
        if (HasSafeHere) GlobalPosition = _lastSafe + Vector3.Up * 0.5f;
        Velocity = Vector3.Zero;
        RequestReplacement();
    }

    private void RememberSafe(Vector3 at)
    {
        _lastSafe = at;
        _hasSafe = true;
        _safeSpace = InteriorKey;
    }

    /// <summary>
    /// A safe spot in the space the player is in now: one recorded inside a house is not a place
    /// to put someone who is outside (it is 3 km under the street), nor the reverse.
    /// </summary>
    private bool HasSafeHere => _hasSafe && _safeSpace == InteriorKey;

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
            RememberSafe(GlobalPosition);
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
    /// <param name="tuning">Garage parts, for a car taken back from the world; 0 (stock) for a new one.</param>
    /// <param name="setup">A car's preset (<see cref="CarSetups"/>), likewise.</param>
    private void ApplyRide(RideKind kind, Vector3 velocity, long tuning = 0, int setup = 0)
    {
        _ride = CarSetups.Ride(kind, CarSetups.Clamp(setup), tuning);
        // a truck or bus from the picker comes with the load chosen there
        if (_ride is Truck picked && !Mathf.IsEqualApprox(picked.Load, NextLoad)) _ride = new Truck(picked.Spec, 0, NextLoad);
        RideKindId = (int)kind;
        // the radio belongs to the vehicle: getting out leaves it tuned in the parked one
        CarRadio = 0;
        // the parts and the doors belong to one car: changing car (the picker), getting out or a
        // wreck leaves them with that car
        TuningBits = _ride is Car car ? car.Tuning.Pack() : 0;
        CarSetupId = _ride is Car withSetup ? withSetup.Spec.SetupId : 0;
        DoorsOpen = 0;
        _shutDriverIn = 0f;
        ShowroomYaw = null;
        TrailerCode = _ride is Truck fresh ? fresh.TrailerCode : 0;
        _truckPitch = 0f;
        // The pose travels with the kind, and each kind reads Anim its own way: left as it was, the
        // next update would hand a bike its rider's stride phase as a crank angle (seen: 0.93 rad).
        Anim = default;
        BodyPose = Transform3D.Identity;
        PoseKind = PoseStride;

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
        // a car stays on its wheels over a crest the way a suspension keeps it there; 0.5 m let every
        // Jura hump launch it for a second at 100 km/h, and a car in the air cannot steer
        FloorSnapLength = _ride switch { Flyer => 0.05f, Car or Motorbike or Truck => 1.2f, _ => 0.5f };

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
        _shortfall = 0f;
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

        // a passenger, or sat in one's own driverless vehicle: the driver's seat, if it is free (#158)
        if (@event.IsActionPressed(PlayerInput.TakeWheel) && !@event.IsEcho() && (RidingWith != 0 || SeatIndex > 0))
        {
            PassengerService.Instance?.AskWheel();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed(PlayerInput.EngineToggle) && !@event.IsEcho() && _ride is { HasEngine: true } && SeatIndex == 0)
        {
            EngineOn = !EngineOn;
            EngineToggled?.Invoke(EngineOn);
            GetViewport().SetInputAsHandled();
            return;
        }

        // not consumed when there is no door: G held is also gathering
        if (@event.IsActionPressed(PlayerInput.CarDoor) && !@event.IsEcho() && TryToggleCarDoor())
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_ride is Truck truck && HandleTruckInput(@event, truck))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if ((@event.IsActionPressed(PlayerInput.RadioNext) || @event.IsActionPressed(PlayerInput.RadioPrev)) && !@event.IsEcho()
            && _ride != null && SeatIndex == 0 && HasCarRadio((RideKind)RideKindId))
        {
            CarRadio = Audio.Live.Stations.Step(CarRadio, @event.IsActionPressed(PlayerInput.RadioNext) ? 1 : -1);
            CarRadioTuned?.Invoke(Audio.Live.Stations.Name(CarRadio));
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed(PlayerInput.LightsToggle) && !@event.IsEcho() && _ride is Car lit)
        {
            lit.Headlights = !lit.Headlights;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed(PlayerInput.RoofToggle) && !@event.IsEcho() && _ride is Car { HasSoftTop: true } open)
        {
            open.RoofOpen = !open.RoofOpen;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Mounted, the body's yaw belongs to the steering — a bicycle goes where it points,
            // and letting the mouse turn it would mean looking over your shoulder steered you
            // into the ditch. The mouse gets its own yaw, which recentres itself.
            float rate = 0.0022f * LookScale;
            _lookIdle = 0f;
            if (_ride != null && !LookSteersRide || RidingWith != 0)
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
        _lookIdle = 0f;

        if (_ride != null && !LookSteersRide || RidingWith != 0)
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
        : InCockpit
        ? Mathf.Clamp(pitch, -0.9f, 0.7f)
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
        // a passenger is carried by the vehicle it sits in (#158)
        if (RidingWith != 0) { RideAlong(); return; }
        // before any path runs, so none of them (mantle, a thrown-out NPC) can skip it
        if (RescueFromVoid(delta)) return;

        float dt = (float)delta;
        // inside another player (a shared spawn): ease apart rather than be shoved out (#203)
        SeparateFromPlayers(dt);
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

        if (Npc)
        {
            // thrown out of its car: it only falls and stands until it gets back in
            Velocity = new Vector3(0, onFloor ? 0f : velocity.Y - Gravity * dt, 0);
            MoveAndSlide();
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
        if (WalkControls?.Invoke() is { } walk)
        {
            moveAmount = Mathf.Min(walk.Wish.Length(), 1f);
            direction = moveAmount > 0.01f ? walk.Wish.Normalized() : Vector3.Zero;
            running = walk.Run;
        }
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
        // an open doorway: let through the building's shell to the sill, then carried across it
        var interiors = Interiors.InteriorManager.Instance;
        var before = GlobalPosition;
        interiors?.BeforeMove(this);
        MoveAndSlide();
        interiors?.AfterMove(this, before);

        // a slide that ran into a wall has no speed left to give
        if (_sliding && new Vector2(Velocity.X, Velocity.Z).Length() < SlideMinSpeed * 0.5f)
            EndSlide();

        if (_thirdPerson) FaceTravel(dt, direction);
        UpdateCameraFeel(dt, running, onFloor);
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

        // and a clear way there: the pull-up moves the body directly, so nothing else stops it.
        // Under a low ceiling (a car in a garage, a wall up to a room's ceiling) the rays above
        // start on its far side and take its top for a ledge: this sweep meets its underside.
        var rise = new Vector3(feet.X, top.Y + 0.08f, feet.Z);
        var to = top + forward * 0.3f + Vector3.Up * 0.05f;
        if (!ClearSweep(space, feet + Vector3.Up * 0.3f, rise, exclude)
            || !ClearSweep(space, rise, to, exclude))
            return false;

        _mantling = true;
        _mantleT = 0;
        _mantleForward = forward;
        _mantleFrom = feet;
        _mantleRise = rise;
        _mantleTo = to;
        _mantleExitSpeed = Mathf.Max(speed, 2.5f);
        Velocity = Vector3.Zero;
        Mantled?.Invoke();
        return true;
    }

    /// <summary>
    /// Whether a standing body (radius shaved 3 cm, clear of the face it is pressed against)
    /// moves from feet at <paramref name="from"/> to feet at <paramref name="to"/> without
    /// touching anything. The start is tested on its own: a cast ignores what it starts inside.
    /// </summary>
    private bool ClearSweep(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to,
        Godot.Collections.Array<Rid> exclude)
    {
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        var sweep = new PhysicsShapeQueryParameters3D
        {
            Shape = _standProbe,
            Transform = new Transform3D(Basis.Identity, from + Vector3.Up * (StandHeight * 0.5f + 0.03f)),
            CollisionMask = CollisionMask,
            Exclude = exclude,
        };
        if (space.IntersectShape(sweep, 1).Count > 0) return false;
        sweep.Motion = to - from;
        return space.CastMotion(sweep)[0] >= 1f;
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
        // winding up a throw: square up to where the view points, whatever the feet do
        if (_throwBlend > 0.05f)
        {
            Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, _viewYaw, 1f - Mathf.Exp(-18f * dt)), 0);
            return;
        }
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

    /// <summary>
    /// Outdoors with no terrain height known here, below this nothing is ground: the lowest in
    /// Switzerland is 193 m, and interiors are at <see cref="Interiors.InteriorManager.InteriorBaseY"/>.
    /// </summary>
    private const float VoidY = -500f;
    /// <summary>This far under an interior's ground floor, the player has fallen through it.</summary>
    private const float InteriorFallDepth = 10f;

    /// <summary>
    /// Safety net for a player who glitched through the world, on foot, mounted or flying:
    /// <list type="bullet">
    /// <item>outdoors, more than 2 m under the terrain: straight up onto it;</item>
    /// <item>outdoors, far below any ground and no height known here (the tile has not streamed, or
    /// there is no data): back to the last safe spot outside, or held here until the ground arrives;</item>
    /// <item>indoors, under the interior's floor: back where they last stood in it.</item>
    /// </list>
    /// Every rescue is a teleport (<see cref="RequestReplacement"/>): stopped, no fall charged, and
    /// put down on the ground by the placement pass. True when it moved the player.
    /// </summary>
    private bool RescueFromVoid(double delta)
    {
        _sinceSnapWarning += delta;
        Vector3 to;
        if (Indoors)
        {
            if (GlobalPosition.Y > Interiors.InteriorManager.InteriorBaseY - InteriorFallDepth) return false;
            if (HasSafeHere) to = _lastSafe + Vector3.Up * 0.5f;
            else
            {
                // nowhere known in here: out to the street above, and down onto it
                Interiors.InteriorManager.Instance?.Leave(this);
                InteriorKey = null;
                to = GlobalPosition with { Y = 0f };
            }
        }
        else if (Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float ground))
        {
            if (GlobalPosition.Y >= ground - 2f) return false;
            to = GlobalPosition with { Y = ground + 1f };
        }
        else
        {
            if (GlobalPosition.Y > VoidY) return false;
            to = HasSafeHere ? _lastSafe + Vector3.Up * 0.5f : GlobalPosition with { Y = 0f };
        }

        if (_sinceSnapWarning > 2)
        {
            _sinceSnapWarning = 0;
            GD.Print($"[player] {Name} fell through the world at {GlobalPosition.Round()}{(Indoors ? " indoors" : "")}, back to {to.Round()}");
        }
        RequestReplacement();
        _flight.Velocity = Vector3.Zero;
        GlobalPosition = to;
        // indoors the placement pass stands down: this is where the player stays
        if (Indoors) _placed = true;
        return true;
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

    /// <summary>The slipstream this vehicle rode last step, 0..<see cref="RideGround.MaxDraft"/>.</summary>
    public float Draft { get; private set; }

    /// <summary>Every other player on something, where it is and how it moves (a remote's replicated velocity).</summary>
    private IEnumerable<(Vector3, Vector3)> OtherVehicles()
    {
        foreach (var node in GetTree().GetNodesInGroup(Group))
            if (node is FootPlayer p && p != this && p.Ride != RideKind.OnFoot)
                yield return (p.GlobalPosition, p.WorldVelocity);
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
            Effort: PlayerInput.Held(PlayerInput.TuckBoost),
            // Space is a hop on a bike and the handbrake in a car
            Handbrake: _ride is { CanHop: false } && PlayerInput.Held(PlayerInput.Jump));

        // skiing with the body in VR (#186): lean, pole and tuck on top of the sticks
        if (_ride is Skis && XR.XrSession.Active && RideControls == null)
            input = input with
            {
                Steer = Mathf.Clamp(input.Steer + XR.XrSession.SkiSteer, -1f, 1f),
                Throttle = Mathf.Max(input.Throttle, XR.XrSession.SkiPole),
                Effort = input.Effort || XR.XrSession.SkiTuck,
            };

        // nobody at the wheel (the driver jumped out, #158): no pedal, the wheel let go
        if (SeatIndex != 0) input = new RideInput(0f, 0f, 0f, false);

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
            if (_rideAir > 0.15f && !Npc && PlayerInput.Held(PlayerInput.Trick))
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

        // a motorbike's or a car's grip depends on what is under it (cached lookup: road, else cover).
        // Not for a stock race NPC car: its racing line is planned on tarmac grip and may put two
        // wheels on the verge. An NPC given a preset (#40) races on the ground it is built for.
        var surface = (_ride is Motorbike or Truck || _ride is Car && (!Npc || CarSetupId != 0)) && Terrain != null
            ? Audio.Surfaces.At(Terrain, GlobalPosition, Indoors) : Audio.Surface.Asphalt;
        // a tow behind another vehicle: less air to push (cars and motorbikes read it)
        Draft = onFloor && _ride is Car or Motorbike or Truck && _motion.Speed > 10f
            ? RideGround.DraftBehind(GlobalPosition, heading.Rotated(Vector3.Up, _motion.Slip), OtherVehicles()) : 0f;
        if (_ride is Truck driving) PrepareTruck(driving);
        _ride!.Step(input, new RideGround(onFloor, grade, surface, Draft), dt, ref _motion);
        if (_ride is Truck driven && AfterTruckStep(driven)) return;
        if (_ride is Car)
        {
            // doors: once seated every door shuts, sooner if the car pulls away before then
            if (_shutDriverIn > 0f && ((_shutDriverIn -= dt) <= 0f || _motion.Speed > DoorsShutSpeed))
                _shutDriverIn = 0f;
            if (_shutDriverIn <= 0f) DoorsOpen = 0;
        }

        // Boost: the reward for air and tricks, spent as raw acceleration on top of the model.
        // Game profile only; in Sim the watts are the rider's, and nothing else may add to them.
        Boosting = Rideable.Arcade && _ride is not Truck && _bailTimer <= 0 && BoostMeter > 0.01f
            && !Npc && PlayerInput.Held(PlayerInput.Boost);
        if (Boosting)
        {
            _motion.Speed += BoostAccel * dt;
            BoostMeter = Mathf.Max(0f, BoostMeter - BoostDrain * dt);
        }

        // yaw changed, so the direction of travel has to be taken again
        Rotation = new Vector3(0, _motion.Yaw, 0);
        heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;

        // a drifting car travels at an angle to its nose; everything else has Slip = 0
        var travel = heading.Rotated(Vector3.Up, _motion.Slip);
        var velocity = Velocity;
        velocity.X = travel.X * _motion.Speed;
        velocity.Z = travel.Z * _motion.Speed;
        velocity.Y = onFloor ? Mathf.Min(velocity.Y, 0f) : velocity.Y - Gravity * dt;

        // Space hops: edge-triggered like the on-foot jump, so holding it does not bunny-hop
        // every frame, and only from the ground - there is nothing to push against in the air.
        // Speed and heading are untouched: a hop carries the bike's momentum, it does not add any.
        bool spaceDown = !Npc && PlayerInput.Held(PlayerInput.Jump);
        bool hop = spaceDown && !_jumpHeld && onFloor && _ride.CanHop;
        _jumpHeld = spaceDown;
        if (hop) { velocity.Y = RideJumpVelocity; Jumped?.Invoke(); }
        LastRideInput = input;

        Velocity = velocity;
        // a garage's or a barn's open doorway: let through the shell, then carried across the sill
        var interiors = Interiors.InteriorManager.Instance;
        var from = GlobalPosition;
        interiors?.BeforeMove(this);
        MoveAndSlide();
        interiors?.AfterMove(this, from);
        // the sections behind a truck's cab follow it, and report what they hit
        if (_ride is Truck train) StepSections(train, dt);

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
        //
        // The SHORTFALL is what is smoothed, not the speed: a smoothed speed lags any hard
        // acceleration by a/ImpactResponse, and past the tolerance that lag read as a wall — it
        // capped every launch at 6 m/s² (a motorbike measured 0-100 in 5.2 s instead of 3.3).
        var real = GetRealVelocity();
        float achieved = new Vector2(real.X, real.Z).Length();
        _shortfall = Mathf.Lerp(_shortfall, Mathf.Max(0f, _motion.Speed - achieved), 1f - Mathf.Exp(-ImpactResponse * dt));
        _realSpeed = _motion.Speed - _shortfall;
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
        if (_visual is Avatar.CarRig rig)
        {
            rig.DoorsOpen = DoorsOpen;
            rig.EngineRunning = EngineOn;
            var settings = Core.GameSettings.Current;
            rig.View = !InCockpit ? Avatar.CockpitView.Outside
                : settings.CockpitBody ? Avatar.CockpitView.Body : Avatar.CockpitView.Bare;
            rig.MirrorsOn = settings.CockpitMirrors;
            rig.DriverShown = SeatIndex == 0;
        }
        else if (_visual is Avatar.HeavyRig cab)
        {
            var settings = Core.GameSettings.Current;
            cab.DriverShown = SeatIndex == 0;
            cab.EngineRunning = EngineOn;
            cab.View = !InCockpit ? Avatar.CockpitView.Outside
                : settings.CockpitBody ? Avatar.CockpitView.Body : Avatar.CockpitView.Bare;
            cab.MirrorsOn = settings.CockpitMirrors;
        }
        // a bail lays the rider over on their side for as long as it lasts
        float roll = _bailTimer > 0 ? 1.35f : _motion.Lean;
        var basis = new Basis(Vector3.Up, _airSpin) * new Basis(Vector3.Right, _airPitch + _truckPitch)
            * new Basis(Vector3.Back, roll);
        // a truck pitches about its wheels on the ground, not about a rider's middle
        if (_ride is Truck)
        {
            _visual.Transform = new Transform3D(basis, Vector3.Zero);
            return;
        }
        var pivot = Vector3.Up * (_bailTimer > 0 ? 0.3f : 0.9f);
        _visual.Transform = new Transform3D(basis, pivot - basis * pivot);
    }

    private void UpdateRideCamera(float dt)
    {
        if (_visual != null)
            PoseRideVisual();

        if (_camera == null || _ride == null) return;
        // sat in it while it rolls on driverless: the view from that seat
        if (SeatIndex > 0) { UpdateSeatCamera(dt); return; }

        _lookIdle += dt;
        if (InCockpit && ((_visual as Avatar.CarRig)?.EyeFrame ?? (_visual as Avatar.HeavyRig)?.EyeFrame) is { } eyeFrame)
        {
            UpdateCockpitCamera(eyeFrame, dt);
            return;
        }

        // the free look springs back to centre, so letting go of the mouse puts the road ahead
        _lookYaw = Mathf.MoveToward(_lookYaw, 0f, 1.2f * dt);

        if (!_thirdPerson && ShowroomYaw == null)
        {
            // From the rider's own eye, leaning with the machine: the eye point is in the
            // visual's frame, which rolls about Z by the lean, so it has to roll with it or the
            // view would hang level while the handlebars tip under it. The horizon itself takes
            // only part of that roll — all of it at 40° of lean is a recipe for seasickness.
            // the visual is rolled by Rotation.Z = Lean, i.e. about +Z
            _camera.Position = new Basis(Vector3.Back, _motion.Lean) * _ride.FirstPersonEye;
            // in VR the eye stays level and the head looks for itself: you lean with your body (#186)
            _camera.Rotation = XR.XrSession.Active ? Vector3.Zero : new Vector3(_pitch, _lookYaw, _motion.Lean * 0.5f);
            ApplyRideFov(dt);
            return;
        }

        // proportional to the yaw rate, so a gentle bend barely moves it and a hairpin swings it
        // well out; eased, so the trail itself never snaps
        float lagTarget = Mathf.Clamp(-_motion.YawRate * 0.28f, -0.42f, 0.42f);
        _turnLag = Mathf.Lerp(_turnLag, lagTarget, 1f - Mathf.Exp(-3.5f * dt));
        // In a drift the camera swings part of the way toward where the car is going, so the
        // road stays in view while the nose points at the inside verge. Not when reversing.
        float slip = Mathf.Wrap(_motion.Slip, -Mathf.Pi, Mathf.Pi);
        _slipCam = Mathf.Lerp(_slipCam, Mathf.Abs(slip) < 1.4f ? slip * _ride.ChaseFollowsTravel : 0f, 1f - Mathf.Exp(-4f * dt));
        // the garage walks the camera all the way round the car instead
        float orbit = ShowroomYaw ?? _lookYaw + _turnLag + _slipCam + (_ride is Truck { } swing ? swing.ChaseSwing : 0f);

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
        float span = Mathf.Max(0.01f, (to - from).Length());
        var space = GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };
        ExcludeTrain(exclude);   // a truck's own trailer is not in the way
        // An arm reaching back through an open doorway (a car in a garage, looking out) goes on in
        // the space on the other side, as the third-person arm does: the lens ends up out there.
        float through = 2f;
        var across = Transform3D.Identity;
        if (Interiors.InteriorManager.Instance?.ArmThroughDoor(this, from, to, out float t, out var map, out var shell) == true)
        {
            if (shell.IsValid) exclude.Add(shell);
            var near = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from.Lerp(to, t), CameraMask, exclude));
            if (near.Count > 0)
                wanted = Mathf.Clamp((near["position"].AsVector3() - from).Length() / span * 0.85f, 0.15f, 1f);
            else
            {
                through = t;
                across = map;
                var far = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    map * from.Lerp(to, Mathf.Min(1f, t + 0.1f / span)), map * to, CameraMask, exclude));
                if (far.Count > 0)
                    wanted = Mathf.Clamp((t * span + (far["position"].AsVector3() - map * from.Lerp(to, t)).Length()) / span * 0.85f, 0.15f, 1f);
            }
        }
        else
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, CameraMask, exclude));
            // 0.85 keeps the lens off the rock face it just found
            if (hit.Count > 0)
                wanted = Mathf.Clamp((hit["position"].AsVector3() - from).Length() / span * 0.85f, 0.15f, 1f);
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
        _camera.Rotation = new Vector3(_pitch + _ride.ChasePitch, orbit, _motion.Lean * 0.35f);
        if (_chaseBlend > through) _camera.GlobalTransform = across * _camera.GlobalTransform;

        ApplyRideFov(dt);
    }

    /// <summary>
    /// The driver's own eye (<see cref="Avatar.CarRig.EyeFrame"/>, <see cref="Avatar.HeavyRig.EyeFrame"/>),
    /// on the vehicle's body so the dash stays put in the lens when the nose dips, moved by the seat
    /// settings. The head sways with the accelerations on a soft spring, leans a little into a look
    /// over the shoulder, and keeps half the body roll off the horizon. The look springs back only
    /// once the mouse or stick has let go of it, so a glance to the side can be held.
    /// </summary>
    private void UpdateCockpitCamera(Transform3D eye, float dt)
    {
        if (_camera == null || _ride == null) return;
        var settings = Core.GameSettings.Current;
        if (_lookIdle > 0.6f)
        {
            float back = 1f - Mathf.Exp(-5f * dt);
            _lookYaw = Mathf.Lerp(_lookYaw, 0f, back);
            _pitch = Mathf.Lerp(_pitch, _ride is Truck ? HeavyCockpitPitch : CockpitPitch, back);
        }

        var sway = Vector3.Zero;
        var (ax, ay) = _ride switch { Car car => (car.AccelX, car.AccelY), Truck truck => (truck.AccelX, truck.AccelY), _ => (0f, 0f) };
        if (settings.CockpitHeadMotion)
            // thrown back by acceleration and forward by braking (+AccelX forward, the head to +Z),
            // out of a bend (+AccelY left, the head to +X)
            sway = new Vector3(Mathf.Clamp(ay * 0.006f, -0.06f, 0.06f), 0f, Mathf.Clamp(ax * 0.005f, -0.05f, 0.05f));
        _headSway = _headSway.Lerp(sway, 1f - Mathf.Exp(-6f * dt));
        // looking over a shoulder, the head goes a little that way and forward, past the pillar
        var lean = new Vector3(-Mathf.Sin(_lookYaw) * 0.07f, 0f, -Mathf.Abs(Mathf.Sin(_lookYaw)) * 0.05f);
        var seat = new Vector3(0f, settings.SeatHeight, -settings.SeatForward);

        var head = new Basis(Vector3.Up, _lookYaw) * new Basis(Vector3.Right, _pitch)
            * new Basis(Vector3.Back, -_motion.Lean * 0.5f - _headSway.X * 1.5f);
        // in VR the real head looks and sways; the eye is the seat, fixed to the car (#186)
        if (XR.XrSession.Active)
        {
            head = Basis.Identity;
            lean = Vector3.Zero;
        }
        _camera.Transform = _visual!.Transform * new Transform3D(eye.Basis * head,
            eye.Origin + seat + (XR.XrSession.Active ? Vector3.Zero : _headSway) + lean);

        float t = Mathf.Clamp(_motion.Speed / _ride.FovSpeed, 0f, 1f);
        _camera.Fov = Mathf.Lerp(_camera.Fov, settings.CockpitFov + 6f * t * t, 1f - Mathf.Exp(-3f * dt));
    }

    /// <summary>Resting look from the seat: a touch down, so the bonnet and the dials share the view with the road.</summary>
    private const float CockpitPitch = -0.1f;
    /// <summary>A truck or bus: sat high over a flat wheel, the look rests lower, so the wheel and dials are in the view with the road.</summary>
    private const float HeavyCockpitPitch = -0.24f;

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
        if (!_thirdPerson && XR.XrSession.Active)
        {
            // no bob, dip, roll or pitch in VR: the eye moves only when the body does (#186)
            _camera.Position = new Vector3(0, eye, 0);
            _camera.Rotation = Vector3.Zero;
        }
        else if (!_thirdPerson)
        {
            _camera.Position = new Vector3(bobSide, eye + bobUp + _landingDip, 0);
            _camera.Rotation = new Vector3(_pitch + _punch, 0, roll + lean);
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
