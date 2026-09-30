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
public sealed class Truck : Rideable, IEngined
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
        var driven = System.Array.Find(spec.Sections.SelectMany(s => s.Axles).ToArray(), a => a.Driven) ?? spec.Sections[0].Axles[^1];
        WheelRadius = Tyre.Radius(driven.Tyre) * 0.97f;   // loaded: ~3% squat
        Box = new HeavyDriveline(spec, WheelRadius);
        if (TrailerCatalog.For(trailerCode) is { } t && t.Couples == spec.Takes && t.Couples != Coupling.None)
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

    public bool Accepts(TrailerSpec t) => Spec.Takes != Coupling.None && t.Couples == Spec.Takes;

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
    /// <summary>A probe's choice of shifting, over Settings.</summary>
    public HeavyShift? ShiftOverride { get; set; }

    /// <summary>What the box does with the player's hands: a converter bus knows only automatic and sequential; a box without six gates has no H-pattern.</summary>
    public HeavyShift EffectiveMode
    {
        get
        {
            var mode = ShiftOverride ?? Core.GameSettings.Current.HeavyGearbox;
            if (Spec.Box == Transmission.TorqueConverter) return mode == HeavyShift.Automatic ? mode : HeavyShift.Sequential;
            if (!Spec.SixGates && mode is HeavyShift.HPattern or HeavyShift.HPatternSplitter) return HeavyShift.SequentialClutch;
            return mode;
        }
    }

    public bool Headlights { get; set; }
    /// <summary>Passenger doors open, one bit per door (<see cref="HeavyLook.Doors"/>).</summary>
    public byte DoorsOpen { get; set; }
    public int DoorCount => Spec.Look.Doors.Length;
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
    public bool Reversing => Box.Gear < 0;
    public float TyreSlide => Train.TyreSlide;
    /// <summary>Accumulated wheel rotation of each section's wheels, rad.</summary>
    public readonly float[] WheelSpin = new float[4];
    public float AccelX { get; private set; }

    /// <summary>The gear as the dash shows it: N, R, A7 (automatic), 4H (a gate and the splitter), 7.</summary>
    public string GearLabel => Box.Gear == 0 ? "N" : Box.Gear < 0 ? "R" : EffectiveMode switch
    {
        HeavyShift.Automatic => $"A{Box.Gear}",
        HeavyShift.HPattern or HeavyShift.HPatternSplitter => $"{Box.Gate}{(Box.Splitter == 1 ? "H" : "L")}",
        _ => Box.Gear.ToString(),
    };

    private float _steer;

    public override RideKind Kind => Spec.Kind;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 400f;

    public override Vector3 FirstPersonEye
    {
        get
        {
            var s = Spec.Sections[0];
            var (y, at) = Spec.Class switch
            {
                HeavyClass.Tractor => (2.55f, 1.25f),
                HeavyClass.Rigid => (2.45f, 1.2f),
                HeavyClass.Coach => (2.3f, 1.1f),
                _ => (2.05f, 1.2f),
            };
            // left-hand drive: the driver's left is −X
            return new Vector3(-(s.Width * 0.5f - 0.6f), y, -(Train.Bodies[0].CgAt - at));
        }
    }
    public override float EyeHeight => 2.6f;
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

    /// <summary>The driver's door: a cab's on the left, a bus's front door on the right.</summary>
    public override Vector3 EntryPoint
    {
        get
        {
            var s = Spec.Sections[0];
            float at = IsBus ? Spec.Look.Doors.FirstOrDefault().At : 1.4f;
            return new Vector3((IsBus ? 1f : -1f) * (s.Width * 0.5f + 0.4f), 0f, -(Train.Bodies[0].CgAt - at));
        }
    }
    public override bool ExitLeft => !IsBus;

    public override (Vector3 Centre, Vector3 Size) ParkedBox => Measured((Kind, "s0"), _ => HeavyRig.Create(Spec, 0, 0.5f));

    /// <summary>The rig of section <paramref name="k"/> of this train.</summary>
    public HeavyRig SectionRig(int k) => k < OwnSections
        ? HeavyRig.Create(Spec, k, Load)
        : HeavyRig.CreateTrailer(Trailer!, k - OwnSections, TrailerCatalog.Load(TrailerCode));

    /// <summary>The bounds of section <paramref name="k"/>'s rig in its own node space, measured once.</summary>
    public (Vector3 Centre, Vector3 Size) SectionBox(int k) => k < OwnSections
        ? Measured((Kind, k), _ => HeavyRig.Create(Spec, k, 0.5f))
        : Measured(("trailer", TrailerCatalog.Index(TrailerCode), k - OwnSections), _ => HeavyRig.CreateTrailer(Trailer!, k - OwnSections, 1f));

    public override Node3D BuildVisual(int riderIndex) => HeavyRig.Create(Spec, 0, Load);

    /// <summary>Parked: every section, posed at the angles it was left at.</summary>
    public override Node3D BuildParkedVisual(int riderIndex)
    {
        var root = HeavyRig.Create(Spec, 0, Load);
        for (int k = 1; k < Train.Count; k++)
        {
            var rig = SectionRig(k);
            rig.Name = $"Section{k}";
            rig.Transform = NodeLocal(k);
            root.AddChild(rig);
        }
        return root;
    }

    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
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
        if (mode == HeavyShift.Automatic)
        {
            if (Box.Gear >= 0 && Mathf.Abs(u) < 0.5f && input.Brake > 0.3f && input.Throttle < 0.05f) Box.WantsReverse = true;
            else if (Box.Gear < 0 && u > -0.5f && input.Throttle > 0.3f) Box.WantsReverse = false;
        }
        bool swap = mode == HeavyShift.Automatic && Box.Gear < 0;
        float pedal = swap ? input.Brake : input.Throttle;
        float brake = swap ? input.Throttle : input.Brake;
        if (swap && u > 0.5f) { brake = Mathf.Max(brake, pedal); pedal = 0f; }
        Braking = brake > 0.05f;

        // a heavy rack: slower to wind on than a car's, and far less lock asked for at speed
        float rate = Mathf.Abs(input.Steer) < Mathf.Abs(_steer) || input.Steer * _steer < 0 ? 3.2f : 1.9f;
        _steer = Mathf.MoveToward(_steer, input.Steer, rate * dt);
        float lockScale = 1f / (1f + Mathf.Max(Mathf.Abs(u), 0f) / 9f);
        float delta = -_steer * Spec.MaxSteer * lockScale;
        SteerAngle = delta;

        var (drive, retard) = Box.Step(new DriveDemand(pedal, brake, input.Handbrake, u, ground.Grade, Train.Mass, EngineRunning), dt);

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
        motion.Speed = Mathf.Sqrt(u2 * u2 + w2 * w2);
        motion.Slip = motion.Speed > 0.05f ? Mathf.Atan2(w2, u2) : (Box.Gear < 0 ? Mathf.Pi : 0f);
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

        // a bus comes up off its knees when it pulls away
        if (Kneeling && motion.Speed > 1.5f) Kneeling = false;
        if (DoorsOpen != 0 && motion.Speed > 1.5f) DoorsOpen = 0;
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

    /// <summary>Front-wheel angle, wheel spin rate, rpm, and in W the lamps, doors, kneel and destination as bits.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(SteerAngle, motion.Speed * Mathf.Cos(motion.Slip) / WheelRadius, Rpm01, PackFlags());

    public int PackFlags() => (Braking ? PoseBrake : 0) | (Headlights ? PoseLights : 0) | (Reversing ? PoseReverse : 0)
        | (Kneeling ? PoseKneel : 0) | ((DoorsOpen & 15) << PoseDoorShift) | ((Destination & 0xFF) << PoseDestShift);

    public void UnpackFlags(int flags)
    {
        Braking = (flags & PoseBrake) != 0;
        Headlights = (flags & PoseLights) != 0;
        _remoteReverse = (flags & PoseReverse) != 0;
        Kneeling = (flags & PoseKneel) != 0;
        DoorsOpen = (byte)((flags >> PoseDoorShift) & 15);
        Destination = (flags >> PoseDestShift) & 0xFF;
    }

    private bool _remoteReverse;
    private float _remoteSpin;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        _remoteSpin += pose.Y * dt;
        UnpackFlags(Mathf.RoundToInt(pose.W));
        SteerAngle = pose.X;
        for (int k = 0; k < WheelSpin.Length; k++) WheelSpin[k] = _remoteSpin;
        _remoteRpm = Mathf.Lerp(Spec.IdleRpm, Spec.Redline, pose.Z);
        if (visual is HeavyRig rig) Dress(rig, 0, _remoteReverse);
    }

    private float _remoteRpm;
    /// <summary>A remote copy's engine speed, from the owner's pose.</summary>
    public float RemoteRpm => _remoteRpm;

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is HeavyRig rig) Dress(rig, 0, Reversing);
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
        rig.Destination = DestinationText;
        rig.BodyRoll = k < Train.Count && k > 0 ? Mathf.Clamp(-Train.Bodies[k].Accel.Y * 0.02f * Train.Bodies[k].CgHeight, -0.08f, 0.08f) : 0f;
    }
}
