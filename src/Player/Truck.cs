using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A truck or a bus (#70): a <see cref="HeavyTrain"/> of sections — its own, and a trailer's when
/// one is coupled — driven through a <see cref="HeavyDriveline"/>.
///
/// <para>
/// To <see cref="FootPlayer"/> it is a ride like a car: the first section IS the player's body,
/// moved by <see cref="RideMotion"/> (speed, slip, yaw rate) exactly as a car's is, so walls,
/// the camera, the network and getting in and out all work unchanged. The sections behind it are
/// described by their articulation angles (<see cref="Articulation"/>): the player places their own
/// collision bodies from those, and feeds back what those bodies hit
/// (<see cref="HeavyTrain.Contacts"/>).
/// </para>
/// </summary>
public sealed partial class Truck : Rideable, IEngined, IBed
{
    public HeavySpec Spec { get; }
    /// <summary>The coupled trailer, or null.</summary>
    public TrailerSpec? Trailer { get; private set; }
    /// <summary><see cref="TrailerCatalog.Code"/> of the coupled trailer (with its load), 0 for none.</summary>
    public int TrailerCode { get; private set; }
    /// <summary>The vehicle's own load, 0..1 of its payload (a rigid's cargo, a bus's passengers).</summary>
    public float Load { get; private set; }
    public HeavyTrain Train { get; } = new();
    public HeavyDriveline Box { get; }

    /// <summary>Joint k: section k+1's yaw minus section k's, rad (+ left), and its rate.</summary>
    public readonly float[] Articulation = new float[MaxJoints], ArticulationRate = new float[MaxJoints];
    public const int MaxJoints = 3;

    /// <summary>The driven wheels' rolling radius, m.</summary>
    public float WheelRadius { get; }

    public Truck(HeavySpec spec, int trailerCode = 0, float load = 0.5f)
    {
        Spec = spec;
        Load = Mathf.Clamp(load, 0f, 1f);
        // a combine's load is its grain tank (#494): empty until its flags say otherwise
        if (spec.TankItems > 0) Load = 0f;
        // the last driven axle: a tractor's big rear wheels set its gearing, not the smaller front ones (#494)
        var driven = System.Array.FindLast(spec.Sections.SelectMany(s => s.Axles).ToArray(), a => a.Driven) ?? spec.Sections[0].Axles[^1];
        WheelRadius = Tyre.Radius(driven.Tyre) * 0.97f;   // loaded: ~3% squat
        Box = new HeavyDriveline(spec, WheelRadius);
        if (TrailerCatalog.For(trailerCode) is { } t && spec.Accepts(t))
        {
            Trailer = t;
            TrailerCode = TrailerCatalog.Clean(trailerCode);
        }
        Rebuild();
        Box.Mode = EffectiveMode;
        Box.Reset(true);
    }

    private void Rebuild()
    {
        Train.Clear();
        foreach (var s in Spec.Sections) Train.Bodies.Add(new HeavyTrain.Body(s, s.PayloadMax * Load));
        if (Trailer != null)
            foreach (var s in Trailer.Sections) Train.Bodies.Add(new HeavyTrain.Body(s, s.PayloadMax * TrailerCatalog.Load(TrailerCode)));
        Train.ComputeLoads();
        Train.BrakeDesign = Spec.BrakeDecel / HeavyTrain.Gravity;
        for (int j = Train.Count - 1; j < MaxJoints; j++)
            if (j >= 0) { Articulation[j] = 0f; ArticulationRate[j] = 0f; }
        TrainLength = LayoutLength();
    }

    /// <summary>Sections of the vehicle itself, before any trailer's.</summary>
    public int OwnSections => Spec.Sections.Length;
    public int SectionCount => Train.Count;
    public SectionSpec Section(int k) => Train.Bodies[k].Spec;

    /// <summary>Bumper to the rear of the last section, straight, m.</summary>
    public float TrainLength { get; private set; }

    private float LayoutLength()
    {
        float front = 0f, rear = 0f;
        for (int k = 0; k < Train.Count; k++)
        {
            var (p, _) = Local(k, straight: true);
            var b = Train.Bodies[k];
            front = Mathf.Max(front, p.X + b.CgAt);
            rear = Mathf.Min(rear, p.X + b.CgAt - b.Spec.Length);
        }
        return front - rear;
    }

    // ---- coupling --------------------------------------------------------------------------

    public bool Accepts(TrailerSpec t) => Spec.Accepts(t);

    /// <summary>
    /// Hangs a trailer on the hitch, at the angles it stands at (<paramref name="angles"/>: its
    /// pivot on the hitch first, then its own). False when it does not fit this vehicle.
    /// </summary>
    public bool Couple(int code, Vector3 angles = default)
    {
        if (Trailer != null || TrailerCatalog.For(code) is not { } t || !Accepts(t)) return false;
        Trailer = t;
        TrailerCode = TrailerCatalog.Clean(code);
        Rebuild();
        int j = OwnSections - 1;
        for (int i = 0; i < t.Sections.Length && j + i < MaxJoints; i++)
        {
            Articulation[j + i] = Mathf.Clamp(angles[i], -t.Sections[i].MaxArticulation, t.Sections[i].MaxArticulation);
            ArticulationRate[j + i] = 0f;
        }
        return true;
    }

    /// <summary>
    /// Lets the trailer go: its code, where its first section stands in this vehicle's frame
    /// (<see cref="Local"/>), and its own articulation. The vehicle is its own again.
    /// </summary>
    public (int Code, Vector2 At, float Yaw, Vector3 Angles) Uncouple()
    {
        if (Trailer == null) return (0, default, 0f, default);
        int first = OwnSections;
        var (at, yaw) = Local(first);
        var angles = Vector3.Zero;
        for (int i = 1; i < Trailer.Sections.Length && first - 1 + i < MaxJoints; i++) angles[i - 1] = Articulation[first - 1 + i];
        int code = TrailerCode;
        Trailer = null;
        TrailerCode = 0;
        Rebuild();
        return (code, at, yaw, angles);
    }

    /// <summary>
    /// A boat trailer's boat launched or winched back aboard (#463): the same trailer, at the same
    /// angles, its load the boat or nothing. False when the coupled trailer carries no boat.
    /// </summary>
    public bool SetBoatAboard(bool aboard)
    {
        if (Trailer is not { Boat: not 0 }) return false;
        TrailerCode = TrailerCatalog.WithBoat(TrailerCode, aboard);
        Rebuild();   // the same sections: the joints keep their angles
        return true;
    }

    /// <summary>All the joint angles, for a parked vehicle's state.</summary>
    public Vector3 Angles => new(Articulation[0], Articulation[1], Articulation[2]);

    public void SetAngles(Vector3 a)
    {
        for (int j = 0; j < MaxJoints; j++)
        {
            float max = j + 1 < Train.Count ? Train.Bodies[j + 1].Spec.MaxArticulation : 0f;
            Articulation[j] = Mathf.Clamp(a[j], -max, max);
            ArticulationRate[j] = 0f;
        }
    }

    /// <summary>
    /// Where section <paramref name="k"/>'s centre of mass sits in the first section's frame (x
    /// forward, y left, from its centre of mass) and its yaw there, from the articulation angles.
    /// </summary>
    public (Vector2 At, float Yaw) Local(int k, bool straight = false)
    {
        var p = Vector2.Zero;
        float psi = 0f;
        for (int i = 1; i <= k && i < Train.Count; i++)
        {
            var parent = Train.Bodies[i - 1];
            var child = Train.Bodies[i];
            var joint = p + new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * parent.HitchZ;
            psi += straight ? 0f : Articulation[i - 1];
            p = joint - new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * child.PivotZ;
        }
        return (p, psi);
    }

    /// <summary>The same, as a node transform in the first section's node space (faces −Z, on the ground).</summary>
    public Transform3D NodeLocal(int k)
    {
        var (p, yaw) = Local(k);
        return new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(-p.Y, 0f, -p.X));
    }

    /// <summary>The hitch (fifth wheel, drawbar jaw) of the last section, in the first section's node space.</summary>
    public Vector3 HitchNode
    {
        get
        {
            int k = Train.Count - 1;
            var last = Train.Bodies[k];
            var (p, yaw) = Local(k);
            var at = p + new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw)) * last.HitchZ;
            return new Vector3(-at.Y, last.Spec.HitchHeight, -at.X);
        }
    }

    // ---- what the player works ----------------------------------------------------------------

    /// <summary>The ignition, set by the owner each step (<see cref="FootPlayer.EngineOn"/>).</summary>
    public bool EngineRunning { get; set; } = true;
    /// <summary>The automatic's selector from a wheel's H-shifter (#290), set by the driver each step; None: the pedals pick reverse.</summary>
    public DriveSelector Selector { get; set; }
    /// <summary>A probe's choice of shifting, over Settings.</summary>
    public HeavyShift? ShiftOverride { get; set; }

    /// <summary>What the box does with the player's hands: a converter bus knows only automatic and sequential; a box without six gates has no H-pattern.</summary>
    public HeavyShift EffectiveMode
    {
        get
        {
            var mode = ShiftOverride ?? Core.GameSettings.Current.HeavyGearbox;
            // a tractor's CVT (#494) has no clutch and no gates: the automatic is all there is
            if (Spec.Stepless) return HeavyShift.Automatic;
            if (Spec.Box == Transmission.TorqueConverter) return mode == HeavyShift.Automatic ? mode : HeavyShift.Sequential;
            if (!Spec.SixGates && mode is HeavyShift.HPattern or HeavyShift.HPatternSplitter) return HeavyShift.SequentialClutch;
            return mode;
        }
    }

    public bool Headlights { get; set; }
    /// <summary>Passenger doors open, one bit per door (<see cref="HeavyLook.Doors"/>).</summary>
    public byte DoorsOpen { get; set; }
    private bool _pulledAway;

    /// <summary>A door's button pressed (#162): that door opens or shuts; a stopped city bus kneels while any is open.</summary>
    public void ToggleDoor(int door)
    {
        if (door < 0 || door >= DoorCount) return;
        DoorsOpen ^= (byte)(1 << door);
        if (Spec.Class != HeavyClass.Coach && !Spec.Farm && !_pulledAway) Kneeling = DoorsOpen != 0;
    }
    public int DoorCount => Spec.Look.Doors.Length;
    /// <summary>
    /// A tipping body raised (#494, #613): the fourth door bit, which no cab door uses, so it travels in
    /// the pose and parks with the vehicle; pulling away drops it with the doors.
    /// </summary>
    public const byte TipBit = 8;
    public bool Tipped
    {
        get => (DoorsOpen & TipBit) != 0;
        set => DoorsOpen = (byte)(value ? DoorsOpen | TipBit : DoorsOpen & ~TipBit);
    }
    // ---- a pallet in a tipper's body (#615) -------------------------------------------------------
    public bool HasBed => Spec.Body == TruckBody.Tipper;
    /// <summary>A tipper's pallet: in its pose and parked flags where a bus keeps its destination (a tipper has none), two bits more above the throttle.</summary>
    public int BedLoad { get; set; }
    public bool BedUp => Tipped;
    private BedShape? _bed;
    public BedShape Bed => _bed ??= Avatar.TruckMeshBuilder.TipperBed(Spec, Load);
    public int FlagsWithBed(int flags, int bedLoad) =>
        !HasBed ? flags : (flags & ~(0xFF << PoseDestShift) & ~(3 << PoseBedHighShift)) | BedBits(bedLoad);
    private static int BedBits(int bedLoad) => ((bedLoad & 0xFF) << PoseDestShift) | (((bedLoad >> 8) & 3) << PoseBedHighShift);
    /// <summary>What a tipper's published pose (or parked flags) says is in its body: what the server checks a tip-out against.</summary>
    public static int BedInPose(Vector4 pose) => BedInFlags(Mathf.RoundToInt(pose.W));
    public static int BedInFlags(int flags) => ((flags >> PoseDestShift) & 0xFF) | (((flags >> PoseBedHighShift) & 3) << 8);

    /// <summary>A mixer discharging (#613): its drum turned backwards, its chute out. The tipper's work bit.</summary>
    public bool Discharging => Spec.Body == TruckBody.Mixer && Tipped;

    /// <summary>
    /// A mixer's drum speed, rad/s (#613): turning while the engine runs, 2 to 12 rpm with the engine's
    /// speed as the hydraulic pump it drives does; backwards and faster while it discharges. 0 for any
    /// other body, or with the engine off.
    /// </summary>
    public float DrumRate(bool engineOn, float rpm01)
    {
        if (Spec.Body != TruckBody.Mixer || !engineOn) return 0f;
        float r = Mathf.Clamp(rpm01, 0f, 1f);
        float rpm = Discharging ? -(4f + 10f * r) : 2f + 10f * r;
        return rpm * Mathf.Tau / 60f;
    }

    /// <summary>Lowered on the door side for boarding (buses).</summary>
    public bool Kneeling { get; set; }
    /// <summary>Index into <see cref="HeavyLook.Destinations"/>.</summary>
    public int Destination { get; set; }
    public string DestinationText => Spec.Look.Destinations.Length == 0 ? "" : Spec.Look.Destinations[Mathf.PosMod(Destination, Spec.Look.Destinations.Length)];
    public bool IsBus => Spec.Class is HeavyClass.CityBus or HeavyClass.ArticulatedBus or HeavyClass.Coach;

    // ---- readouts ----
    public float Rpm => Box.EngineRpm;
    public float Rpm01 => Box.Rpm01;
    public int Gear => Box.Gear;
    public float Throttle => Box.Throttle;
    public EngineProfile Sound => _sound ??= EngineProfile.For(Spec.Engine, Spec.IdleRpm, Spec.Redline);
    private EngineProfile? _sound;
    /// <summary>Front road-wheel angle, rad, + left.</summary>
    public float SteerAngle { get; private set; }
    public bool Braking { get; private set; }
    /// <summary>Stopped and held on the brakes by the box, waiting for the throttle.</summary>
    public bool HillHold { get; private set; }
    public bool Reversing => Box.Gear < 0;
    public float TyreSlide => Train.TyreSlide;
    /// <summary>Accumulated wheel rotation of each section's wheels, rad.</summary>
    public readonly float[] WheelSpin = new float[4];
    /// <summary>The cab's acceleration in its own frame, last step (+X forward, +Y left), m/s²: the cockpit's head motion.</summary>
    public float AccelX { get; private set; }
    public float AccelY { get; private set; }
    /// <summary>The pedals as the driver's feet hold them (after the automatic's reverse swap, without the hill hold), 0..1.</summary>
    public float ThrottlePedal { get; private set; }
    public float BrakePedal { get; private set; }

    /// <summary>The gear as the dash shows it: N, R, A7 (automatic), 4H (a gate and the splitter), 7.</summary>
    public string GearLabel => Box.Gear == 0 ? (Selector == DriveSelector.Park ? "P" : "N") : Box.Gear < 0 ? "R" : EffectiveMode switch
    {
        HeavyShift.Automatic => $"A{Box.Gear}",
        HeavyShift.HPattern or HeavyShift.HPatternSplitter => $"{Box.Gate}{(Box.Splitter == 1 ? "H" : "L")}",
        _ => Box.Gear.ToString(),
    };

    private float _steer;
    private bool _reverseArmed = true;   // a truck taken at rest may back straight out

    public override RideKind Kind => Spec.Kind;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 400f;
    /// <summary>Lock to lock through the cab's ratio: ~1800° for a 0.78 rad box, what a truck wheel is set to.</summary>
    public override float WheelLock => Core.SteeringWheel.LockOverride ?? 2f * Spec.MaxSteer * HeavyCockpit.SteerRatio;
    /// <summary>The cab's ratio, unless <c>--wheellock</c> sets another lock.</summary>
    private float Ratio => WheelLock * 0.5f / Spec.MaxSteer;
    public override Core.WheelFeel Feel => _feel;
    private Core.WheelFeel _feel;

    public override Vector3 FirstPersonEye
    {
        get
        {
            var s = Spec.Sections[0];
            // the army's two cars have a seat derived from their own cab (#714)
            if (Spec.Class is HeavyClass.Offroader or HeavyClass.Transporter)
            {
                var (ex, ey, eat) = Avatar.ArmyMeshBuilder.Eye(Spec);
                return new Vector3(ex, ey, -(Train.Bodies[0].CgAt - eat));
            }
            var (y, at) = Spec.Class switch
            {
                HeavyClass.Tractor => (2.55f, 1.25f),
                HeavyClass.Rigid => (2.45f, 1.2f),
                HeavyClass.Coach => (2.3f, 1.1f),
                HeavyClass.Pickup => (1.68f, 2.9f),
                HeavyClass.FarmTractor => (Avatar.FarmMeshBuilder.TractorEyeY, Avatar.FarmMeshBuilder.TractorEyeAt),
                HeavyClass.Combine => (Avatar.FarmMeshBuilder.CombineEyeY, Avatar.FarmMeshBuilder.CombineEyeAt),
                _ => (2.05f, 1.2f),
            };
            // a tractor's and a combine's seat is in the middle of the cab (#494)
            if (Spec.Farm) return new Vector3(0f, y, -(Train.Bodies[0].CgAt - at));
            // left-hand drive: the driver's left is −X
            return new Vector3(-(s.Width * 0.5f - 0.6f), y, -(Train.Bodies[0].CgAt - at));
        }
    }
    public override float EyeHeight => Spec.Class switch
    {
        HeavyClass.Pickup or HeavyClass.Offroader => 1.8f,
        HeavyClass.Transporter => 2.3f,
        HeavyClass.FarmTractor => Avatar.FarmMeshBuilder.TractorEyeY,
        HeavyClass.Combine => Avatar.FarmMeshBuilder.CombineEyeY,
        _ => 2.6f,
    };
    // behind the whole train and high enough to see over it: a semi's camera sits ~24 m back
    public override float ChaseDistance => 5f + TrainLength * 0.95f;
    public override float ChaseHeight => 3.5f + TrainLength * 0.28f;
    public override float ChasePitch => -0.32f;

    /// <summary>How far round the chase camera swings to stay behind the last section, rad: its yaw against the cab's.</summary>
    public float ChaseSwing
    {
        get
        {
            float sum = 0f;
            for (int j = 0; j < Train.Count - 1 && j < MaxJoints; j++) sum += Articulation[j];
            return sum * 0.6f;
        }
    }
    public override float ChaseFollowsTravel => 0.1f;
    public override float BaseFov => 68f;
    public override float MaxFov => 80f;
    public override float FovSpeed => 30f;
    public override float BodyRadius => 1.05f;
    public override float BodyHeight => 2.2f;
    public override float DismountSpeed => 1.5f;
    public override float HullLift => 0.6f;

    /// <summary>
    /// A tractor collides as its cab and, behind it, its chassis only up to the fifth wheel's
    /// plate: cut by height, its lower box ran 2.2 m high to the back of the frame, and it could
    /// neither back under a trailer's nose nor stand clear of one it had just dropped.
    /// </summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes
    {
        get
        {
            if (Spec.Class == HeavyClass.Pickup) return PickupHull;
            if (Spec.Farm) return FarmHull;
            if (Spec.Class != HeavyClass.Tractor) return null;
            var s = Spec.Sections[0];
            float cg = Train.Bodies[0].CgAt, cab = 2.35f;
            var chassis = new Aabb(new Vector3(-s.Width * 0.47f, 0f, cab - cg), new Vector3(s.Width * 0.94f, s.HitchHeight - 0.08f, s.Length - cab));
            var cabin = new Aabb(new Vector3(-s.Width * 0.5f, 0f, -cg), new Vector3(s.Width, s.Height, cab));
            return (chassis, cabin);
        }
    }

    /// <summary>
    /// The pickup (#463) collides as its body to the bonnet and bed rails, and above that only as its
    /// cab: measured, the upper box ran the whole length to the roof, a wall over the bonnet and the
    /// bed that stopped a trailer's nose and a walker alike.
    /// </summary>
    private (Aabb, Aabb) PickupHull
    {
        get
        {
            var s = Spec.Sections[0];
            float cg = Train.Bodies[0].CgAt, w = s.Width - 0.1f;
            var body = new Aabb(new Vector3(-w * 0.5f, 0f, -cg), new Vector3(w, Avatar.PickupMeshBuilder.BodyTop, s.Length));
            float cab0 = Avatar.PickupMeshBuilder.CabFrom, cab1 = Avatar.PickupMeshBuilder.CabTo;
            var cab = new Aabb(new Vector3(-w * 0.5f, Avatar.PickupMeshBuilder.BodyTop, cab0 - cg),
                new Vector3(w, s.Height - Avatar.PickupMeshBuilder.BodyTop, cab1 - cab0));
            return (body, cab);
        }
    }

    /// <summary>The driver's door: a cab's on the left, a bus's front door on the right.</summary>
    public override Vector3 EntryPoint
    {
        get
        {
            var s = Spec.Sections[0];
            float at = IsBus ? Spec.Look.Doors.FirstOrDefault().At : Spec.Class switch
            {
                HeavyClass.Pickup => 2.6f,
                HeavyClass.Offroader or HeavyClass.Transporter => Avatar.ArmyMeshBuilder.DoorAt(Spec.Class),
                HeavyClass.FarmTractor => Avatar.FarmMeshBuilder.TractorDoorAt,
                HeavyClass.Combine => Avatar.FarmMeshBuilder.CombineDoorAt,
                _ => 1.4f,
            };
            return new Vector3((IsBus ? 1f : -1f) * (s.Width * 0.5f + 0.4f), 0f, -(Train.Bodies[0].CgAt - at));
        }
    }
    public override bool ExitLeft => !IsBus;

    public override (Vector3 Centre, Vector3 Size) ParkedBox => Solid(Measured((Kind, "s0"), _ => HeavyRig.Create(Spec, 0, 0.5f)), 0);

    /// <summary>What the body may stand out past its spec width (rubbing strips, wheel nuts), m.</summary>
    private const float BodyFlare = 0.05f;

    /// <summary>
    /// No wider than the body: the mirrors are part of the shell's mesh, and measured with them a
    /// 2.55 m Citaro was a 3.23 m box from the ground to the roof over all of its 12.6 m — 34 cm of
    /// invisible wall down each side. They are out on their arms at 2.0–2.8 m, over a walker's head.
    /// </summary>
    public override Aabb Solid(Aabb measured, int section)
    {
        if (section >= OwnSections) return measured;   // a trailer has no mirrors
        // a combine's header is wider than its body (#494): the body and its wheels here, the header
        // a box of its own (ExtraBoxes), or getting out puts the driver 3.9 m out, past the cab door's reach
        float half = Spec.Class == HeavyClass.Combine && section == 0
            ? Mathf.Max(Spec.Sections[0].Width * 0.5f, Spec.Sections[0].Track * 0.5f + 0.45f)
            : Spec.Sections[section].Width * 0.5f + BodyFlare;
        float left = Mathf.Max(measured.Position.X, -half), right = Mathf.Min(measured.End.X, half);
        if (right <= left) return measured;
        return new Aabb(measured.Position with { X = left }, measured.Size with { X = right - left });
    }

    private (Vector3 Centre, Vector3 Size) Solid((Vector3 Centre, Vector3 Size) box, int section)
    {
        var solid = Solid(new Aabb(box.Centre - box.Size * 0.5f, box.Size), section);
        return (solid.GetCenter(), solid.Size);
    }

    /// <summary>The rig of section <paramref name="k"/> of this train.</summary>
    public HeavyRig SectionRig(int k) => k < OwnSections
        ? HeavyRig.Create(Spec, k, Load)
        : HeavyRig.CreateTrailer(Trailer!, k - OwnSections, TrailerCatalog.Load(TrailerCode));

    /// <summary>The bounds of section <paramref name="k"/>'s rig in its own node space, measured once.</summary>
    public (Vector3 Centre, Vector3 Size) SectionBox(int k) => k < OwnSections
        ? Solid(Measured((Kind, k), _ => HeavyRig.Create(Spec, k, 0.5f)), k)
        : Measured(("trailer", TrailerCatalog.Index(TrailerCode), k - OwnSections), _ => HeavyRig.CreateTrailer(Trailer!, k - OwnSections, 1f));

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) => HeavyRig.Create(Spec, 0, Load, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    /// <summary>Every seat of the truck's own sections (a bus's both halves), the driver's first (#158).</summary>
    public override SeatAnchor[] Seats => Model.Seats;

    /// <summary>
    /// The bus's saloon, each half of it, for walking about in (#162); none on a truck. With a boat
    /// trailer, its cradle too (#463), numbered as the train's section.
    /// </summary>
    public override VehicleDeck[] Decks
    {
        get
        {
            if (Trailer == null) return Model.Decks;
            var key = (Kind, TrailerCatalog.Index(TrailerCode));
            if (_trainDecks.TryGetValue(key, out var known)) return known;
            var decks = Model.Decks.Concat(TrailerDecks(Trailer, TrailerCode).Select(d => d with { Section = d.Section + OwnSections })).ToArray();
            return _trainDecks[key] = decks;
        }
    }

    private static readonly Dictionary<(RideKind, int), VehicleDeck[]> _trainDecks = new();
    private static readonly Dictionary<int, VehicleDeck[]> _trailerDecks = new();

    /// <summary>A trailer's own decks (a boat trailer's cradle), by its sections, read once from a throwaway build.</summary>
    public static VehicleDeck[] TrailerDecks(TrailerSpec trailer, int code)
    {
        int index = TrailerCatalog.Index(code);
        if (_trailerDecks.TryGetValue(index, out var known)) return known;
        var decks = new List<VehicleDeck>();
        for (int k = 0; k < trailer.Sections.Length; k++)
        {
            var rig = HeavyRig.CreateTrailer(trailer, k, 0f);
            if (rig.Deck != null) decks.Add(rig.Deck);
            rig.Free();
        }
        return _trailerDecks[index] = decks.ToArray();
    }

    /// <summary>The pickup's doors are a car's, worked one by one (#463), and the army's cars' (#714); a bus's open together.</summary>
    public bool CarDoors => Spec.Class is HeavyClass.Pickup or HeavyClass.Offroader or HeavyClass.Transporter || Spec.Farm;

    private static readonly Dictionary<RideKind, (SeatAnchor[] Seats, VehicleDeck[] Decks)> _models = new();

    /// <summary>The seats and decks of this kind's sections, read once from a throwaway build of each.</summary>
    private (SeatAnchor[] Seats, VehicleDeck[] Decks) Model
    {
        get
        {
            if (_models.TryGetValue(Kind, out var known)) return known;
            var seats = new List<SeatAnchor>();
            var decks = new List<VehicleDeck>();
            for (int k = 0; k < Spec.Sections.Length; k++)
            {
                var rig = HeavyRig.Create(Spec, k, 0.5f);
                seats.AddRange(rig.Seats);
                if (rig.Deck != null) decks.Add(rig.Deck);
                rig.Free();
            }
            return _models[Kind] = (seats.ToArray(), decks.ToArray());
        }
    }

    public override bool Driverless => true;

    /// <summary>A seat in the cab's frame, the train as it stands (a bus's rear half where its joint puts it).</summary>
    public override Vector3 SeatPosition(int i) => Seats[i].Section == 0 ? Seats[i].Hip : NodeLocal(Seats[i].Section) * Seats[i].Hip;

    /// <summary>Parked: every section, posed at the angles it was left at.</summary>
    public override Node3D BuildParkedVisual(int riderIndex)
    {
        var root = HeavyRig.Create(Spec, 0, Load);
        DressFarm(root, 0);
        for (int k = 1; k < Train.Count; k++)
        {
            var rig = SectionRig(k);
            rig.Name = $"Section{k}";
            rig.Transform = NodeLocal(k);
            DressFarm(rig, k);
            root.AddChild(rig);
        }
        return root;
    }

    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        // the combine's header, across the front, wider than its body (raised or lowered: to 2 m up)
        if (Spec.Class == HeavyClass.Combine)
        {
            float cg = Train.Bodies[0].CgAt, from = Avatar.FarmMeshBuilder.HeaderFrom, to = Avatar.FarmMeshBuilder.HeaderTo;
            yield return (Transform3D.Identity, new Vector3(0f, 1f, -cg + (from + to) * 0.5f),
                new Vector3(Avatar.FarmMeshBuilder.HeaderWidth, 2f, to - from));
        }
        for (int k = 1; k < Train.Count; k++)
        {
            var (centre, size) = SectionBox(k);
            yield return (NodeLocal(k), centre, size);
        }
    }

    // ---- driving ------------------------------------------------------------------------------

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        var mode = EffectiveMode;
        bool arcade = Arcade;
        Box.Mode = mode;
        Box.Arcade = arcade;
        Train.Arcade = arcade;

        float u = motion.Speed * Mathf.Cos(motion.Slip);
        float w = motion.Speed * Mathf.Sin(motion.Slip);

        // the automatic's pedals, as on the cars: the brake held at a standstill is reverse, and in
        // reverse the brake pedal drives
        // Reverse is armed only once the truck is at rest with the brake let go (the hill hold keeps
        // it there): a brake held down to a stop only stops it, or holding the pedal at a red light
        // would back 40 t into whatever is behind — or down the hill it was crawling up.
        if (Mathf.Abs(u) > 0.5f) _reverseArmed = false;
        else if (Mathf.Abs(u) < 0.3f && input.Brake < 0.1f) _reverseArmed = true;
        // a wheel's H-shifter as the selector (#290): P R N D from the lever, not from the pedals. R
        // rolling forward is N until the truck is nearly stopped; P sets the spring brakes once slow
        var selector = mode == HeavyShift.Automatic ? Selector : DriveSelector.None;
        Box.HoldNeutral = selector is DriveSelector.Neutral or DriveSelector.Park
            || selector == DriveSelector.Reverse && Box.Gear >= 0 && Mathf.Abs(u) >= 0.5f;
        if (selector != DriveSelector.None) Box.WantsReverse = selector == DriveSelector.Reverse;
        else if (mode == HeavyShift.Automatic)
        {
            if (Box.Gear >= 0 && Mathf.Abs(u) < 0.5f && input.Brake > 0.3f && input.Throttle < 0.05f && _reverseArmed) Box.WantsReverse = true;
            else if (Box.Gear < 0 && u > -0.5f && input.Throttle > 0.3f) Box.WantsReverse = false;
        }
        bool swap = mode == HeavyShift.Automatic && Box.Gear < 0 && selector == DriveSelector.None;
        float pedal = swap ? input.Brake : input.Throttle;
        float brake = swap ? input.Throttle : input.Brake;
        if (swap && u > 0.5f) { brake = Mathf.Max(brake, pedal); pedal = 0f; }
        // hill hold, as an automated box has it: stopped with no pedal down, the service brakes
        // hold the truck until the throttle is pressed (with the clutch pedal, that is the driver's job)
        HillHold = mode is HeavyShift.Automatic or HeavyShift.Sequential && Mathf.Abs(u) < 0.3f
            && pedal < 0.02f && brake < 0.05f && ground.OnFloor;
        ThrottlePedal = pedal;
        BrakePedal = brake;
        if (HillHold) brake = 0.35f;
        Braking = brake > 0.05f && !HillHold;

        float delta;
        if (!float.IsNaN(input.WheelAngle))
        {
            // a steering wheel (#68): the box follows the driver's hands through the cab's ratio, to the stop
            delta = Mathf.Clamp(-input.WheelAngle / Ratio, -Spec.MaxSteer, Spec.MaxSteer);
            _steer = -delta / Spec.MaxSteer;
        }
        else
        {
            // a heavy rack: slower to wind on than a car's, and far less lock asked for at speed
            float rate = Mathf.Abs(input.Steer) < Mathf.Abs(_steer) || input.Steer * _steer < 0 ? 3.2f : 1.9f;
            _steer = Mathf.MoveToward(_steer, input.Steer, rate * dt);
            float lockScale = 1f / (1f + Mathf.Max(Mathf.Abs(u), 0f) / 9f);
            delta = -_steer * Spec.MaxSteer * lockScale;
        }
        SteerAngle = delta;

        PrepareFarm(dt, u);
        bool parked = selector == DriveSelector.Park && Mathf.Abs(u) < 1.5f;
        var (drive, retard) = Box.Step(new DriveDemand(pedal, brake, input.Handbrake || parked, u, ground.Grade, Train.Mass, EngineRunning), dt);

        float grip = Spec.Grip * SurfaceFactor(ground.Surface) * (arcade ? 1.15f : 1f);

        // Game: stretch braking and a stability control against a train folding up. The first
        // pin folding faster and faster (angle and rate the same way) at speed is a jackknife starting.
        float stretch = 0f;
        if (arcade && Train.Count > 1)
        {
            float g = Articulation[0], gr = ArticulationRate[0];
            if (g * gr > 0f && Mathf.Abs(gr) > 0.08f && Mathf.Abs(u) > 4f) stretch = Mathf.Clamp(Mathf.Abs(gr) * 3f, 0f, 1f);
        }

        // Game: the retarder's slip control lets it take only part of what the drive axle grips, so
        // it slows the train without taking the drive axle's side grip
        if (arcade) retard = Mathf.Min(retard, 0.45f * grip * Train.DrivenLoad);

        Train.Pose(u, w, motion.YawRate, Articulation, ArticulationRate);
        var c = new TrainControls
        {
            Steer = delta, Drive = drive, Retard = retard * (stretch > 0f ? 0.3f : 1f),
            Brake = Box.ServiceBrake * (stretch > 0f ? 1f - 0.4f * stretch : 1f), Spring = Box.SpringBrakes, Stretch = stretch,
            Grip = grip, Grade = ground.Grade, Draft = ground.Draft, OnFloor = ground.OnFloor,
            AbsLimit = arcade ? 0.8f : 0.95f, YawAssist = arcade ? 8f : 0f, FoldDamping = arcade ? 4e5f : 0f,
        };
        Train.Step(c, dt, 8);
        Train.Read(Articulation, ArticulationRate);

        var b0 = Train.Bodies[0];
        float u2 = b0.V.Dot(b0.Forward), w2 = b0.V.Dot(b0.Left);
        AccelX = b0.Accel.X;
        AccelY = b0.Accel.Y;
        motion.Speed = Mathf.Sqrt(u2 * u2 + w2 * w2);
        // the direction of travel down to any speed at all: taken as forward below a crawl (the
        // car's rule), a truck starting to roll back down a hill was turned round every frame and
        // never moved
        motion.Slip = motion.Speed > 1e-4f ? Mathf.Atan2(w2, u2) : (Box.Gear < 0 ? Mathf.Pi : 0f);
        motion.YawRate = b0.W;
        motion.Yaw += b0.Psi;
        motion.Bank = 0f;
        // the body rolls out of a bend on its springs: a few degrees at a real truck's limit
        motion.Lean = Mathf.Clamp(-b0.Accel.Y * 0.02f * b0.CgHeight, -0.08f, 0.08f);

        for (int k = 0; k < Train.Count && k < WheelSpin.Length; k++)
        {
            var b = Train.Bodies[k];
            WheelSpin[k] += b.V.Dot(b.Forward) / WheelRadius * dt;
        }

        // the wheel's feel: the front (fully steered) axles of the tractor or the bus's first section
        float fy = 0f, alpha = 0f, peak = 0f;
        int steered = 0;
        for (int i = 0; i < b0.Spec.Axles.Length; i++)
        {
            if (b0.Spec.Axles[i].Steer < 0.99f) continue;
            fy += b0.AxleFy[i];
            alpha += b0.AxleAlpha[i];
            peak += b0.StaticLoad[i] * Spec.Grip;
            steered++;
        }
        // big tyres on stiff springs pass less of the road than a car's
        var (road, roadHz) = Core.WheelFeel.RoadFrom(ground.OnFloor ? 0.7f * CarSetups.Roughness(ground.Surface) : 0f, u2);
        _feel = new Core.WheelFeel(
            ground.OnFloor && steered > 0 ? Core.WheelFeel.Aligning(fy, alpha / steered, peak, u2) : 0f,
            road, roadHz,
            // assisted, but six tonnes on the front axle still scrub when turned on the spot
            ground.OnFloor ? Core.WheelFeel.WeightFrom(0.45f, u2) : 0f);

        // a bus shuts its doors and comes up off its knees as it pulls away; a door opened after
        // that (a passenger's button, #162) stays open, at their own risk
        bool moving = motion.Speed > 1.5f;
        // a farm machine's kneel is its implement or header (#494): it works on the move
        if (moving && !_pulledAway && !Spec.Farm) { DoorsOpen = 0; Kneeling = false; }
        _pulledAway = moving;
    }

    /// <summary>
    /// A truck tyre's grip off tarmac, as a share of its tarmac grip: its deep tread and M+S
    /// compound hold on gravel and snow better than a car's road tyre, and nothing holds on ice.
    /// </summary>
    public static float SurfaceFactor(Surface surface) => surface switch
    {
        Surface.Gravel => 0.8f,
        Surface.Rock => 0.9f,
        Surface.Grass or Surface.Forest => 0.6f,
        Surface.Water => 0.55f,
        Surface.Snow => 0.4f,
        Surface.Ice => 0.13f,
        _ => 1f,
    };

    // ---- what others see ------------------------------------------------------------------------

    private const int PoseBrake = 1, PoseLights = 2, PoseReverse = 4, PoseKneel = 8, PoseDoorShift = 4, PoseDestShift = 8;
    /// <summary>The throttle pedal in eighths (#157: the driver's foot others see), above the destination's byte.</summary>
    private const int PoseThrottleShift = 16, PoseThrottleSteps = 7;
    /// <summary>A tipper's pallet's top two bits (#615), above the throttle; its low eight are the destination's byte.</summary>
    private const int PoseBedHighShift = 19;

    /// <summary>Front-wheel angle, wheel spin rate, rpm, and in W the lamps, doors, kneel, destination and throttle as bits.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        // a mixer's copy turns its drum only with the engine on (#613): an engine off reads as −1
        new(SteerAngle, motion.Speed * Mathf.Cos(motion.Slip) / WheelRadius,
            Spec.Body == TruckBody.Mixer && !EngineRunning ? -1f : Rpm01, PackFlags());

    public int PackFlags() => (Braking ? PoseBrake : 0) | (Headlights ? PoseLights : 0) | (Reversing ? PoseReverse : 0)
        | (Kneeling ? PoseKneel : 0) | ((DoorsOpen & 15) << PoseDoorShift) | (HasBed ? BedBits(BedLoad) : (Destination & 0xFF) << PoseDestShift)
        | (Mathf.RoundToInt(Mathf.Clamp(ThrottlePedal, 0f, 1f) * PoseThrottleSteps) << PoseThrottleShift)
        // a combine's grain tank (#494), where a bus has its destination: sacks and crop
        | (Spec.TankItems > 0 ? Farming.MachineLoad.TankFlags(Tank) : 0);

    /// <summary>Whether a truck's published pose has its work bit up: the tipper's body, the mixer's discharge (#613).</summary>
    public static bool TippedInPose(Vector4 pose) => ((Mathf.RoundToInt(pose.W) >> PoseDoorShift) & TipBit) != 0;

    public void UnpackFlags(int flags)
    {
        Braking = (flags & PoseBrake) != 0;
        Headlights = (flags & PoseLights) != 0;
        _remoteReverse = (flags & PoseReverse) != 0;
        Kneeling = (flags & PoseKneel) != 0;
        DoorsOpen = (byte)((flags >> PoseDoorShift) & 15);
        Destination = HasBed || Spec.TankItems > 0 ? 0 : (flags >> PoseDestShift) & 0xFF;
        BedLoad = HasBed ? BedInFlags(flags) : 0;
        if (Spec.TankItems > 0) SetTank(Farming.MachineLoad.TankFromFlags(flags), keepPartial: true);
        ThrottlePedal = ((flags >> PoseThrottleShift) & PoseThrottleSteps) / (float)PoseThrottleSteps;
        BrakePedal = Braking ? 1f : 0f;
    }

    private bool _remoteReverse;
    private float _remoteSpin;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        _remoteSpin += pose.Y * dt;
        UnpackFlags(Mathf.RoundToInt(pose.W));
        SteerAngle = pose.X;
        for (int k = 0; k < WheelSpin.Length; k++) WheelSpin[k] = _remoteSpin;
        _remoteRpm = Mathf.Lerp(Spec.IdleRpm, Spec.Redline, Mathf.Max(pose.Z, 0f));
        if (visual is not HeavyRig rig) return;
        Dress(rig, 0, _remoteReverse);
        rig.DrumSpeed = DrumRate(pose.Z >= 0f, pose.Z);
        // the cockpit seen through the glass: the wheel and feet, the dials from the rpm and wheel
        // speed; the gear display, the air and the lamps that are not replicated stay as they are
        DressCockpit(rig, Mathf.Abs(pose.Y) * WheelRadius * 3.6f, _remoteRpm);
    }

    private float _remoteRpm;
    /// <summary>A remote copy's engine speed, from the owner's pose.</summary>
    public float RemoteRpm => _remoteRpm;

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not HeavyRig rig) return;
        Dress(rig, 0, Reversing);
        rig.DrumSpeed = DrumRate(EngineRunning, Rpm01);
        DressCockpit(rig, motion.Speed * Mathf.Cos(motion.Slip) * 3.6f, Rpm);
        rig.Clutch = Box.ClutchPedal;
        rig.Gear = GearLabel;
        rig.Air = Box.AirTank;
        rig.SpringBrakes = Box.SpringBrakes;
        rig.Retarder = Box.RetarderLevel;
    }

    /// <summary>The cockpit's wheel, pedals and dials (#157), on the owner and every copy alike.</summary>
    private void DressCockpit(HeavyRig rig, float kmh, float rpm)
    {
        rig.WheelTurn = SteerAngle * Ratio;
        rig.Throttle = ThrottlePedal;
        rig.Brake = BrakePedal;
        rig.SpeedKmh = kmh;
        rig.Rpm = rpm;
    }

    /// <summary>Puts this train's state on the rig of section <paramref name="k"/>.</summary>
    public void Dress(HeavyRig rig, int k, bool reversing)
    {
        rig.SteerAngle = SteerAngle;
        rig.WheelSpin = k < WheelSpin.Length ? WheelSpin[k] : WheelSpin[0];
        rig.BrakeLights = Braking;
        rig.Headlights = Headlights;
        rig.ReverseLights = reversing;
        rig.DoorsOpen = DoorsOpen;
        rig.Kneeling = Kneeling;
        rig.Tipped = Tipped;
        if (k == 0) rig.BedLoad = BedLoad;
        rig.Discharging = Discharging;
        rig.Destination = DestinationText;
        rig.BodyRoll = k < Train.Count && k > 0 ? Mathf.Clamp(-Train.Bodies[k].Accel.Y * 0.02f * Train.Bodies[k].CgHeight, -0.08f, 0.08f) : 0f;
        DressFarm(rig, k);
    }
}
