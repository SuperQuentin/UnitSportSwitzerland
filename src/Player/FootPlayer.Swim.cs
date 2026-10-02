using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// Swimming, diving and the air reserve (#301), on the <see cref="WaterField"/> of #299.
///
/// <para>
/// An on-foot state, entered when the water at the chest is deeper than standing depth (or on
/// falling into water deep enough) and left by wading out or climbing out (the mantle). At the
/// surface the body floats with its feet <see cref="SwimFloat"/> under the moving surface and the
/// water's own velocity added, so a swell lifts and carries it. Crouch dives, Jump rises (a lunge
/// at the surface), and under water the stroke goes where the view looks; idle, buoyancy brings
/// the swimmer back up. Water drag (linear and quadratic) brakes a plunge, so a high dive into
/// deep water is free and one into shallow water still hits the bed hard.
/// </para>
///
/// <para>
/// Public for the boats (#302): <see cref="IsSwimming"/> and <see cref="StartSwimming"/>.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>On foot, in the water: <see cref="Anim"/> = (stroke speed, stroke phase, <see cref="SwimStyle"/>, 0).</summary>
    public const int PoseSwim = 4;

    /// <summary>Feet under the surface while floating: the eye 0.26 m clear of it, the back at the waterline when prone.</summary>
    private const float SwimFloat = 1.42f;
    /// <summary>Feet this deep under the surface: off them, swimming (the chest is under).</summary>
    private const float SwimEnter = 1.35f;
    /// <summary>Standing on the bed with the water no deeper than this over the feet: wading again.</summary>
    private const float SwimLeave = 1.15f;
    /// <summary>The height above the feet the figure is laid down about: the chest, at the waterline.</summary>
    private const float SwimPivot = 1.25f;
    /// <summary>Water drag on the velocity relative to where the stroke and the water want it: 1/s and 1/m.</summary>
    private const float SwimDragLinear = 2.5f, SwimDragQuad = 0.25f;
    private const float DiveSpeed = 1.5f, RiseSpeed = 1.7f, IdleRise = 0.45f;
    /// <summary>Jump at the surface: a kick up out of the water, for a ledge just out of reach.</summary>
    private const float LungeSpeed = 2.6f, LungeTime = 0.55f;
    /// <summary>Hitting the bed faster than this hurts, as a landing does (<see cref="UpdateCameraFeel"/>).</summary>
    private const float HardBed = 11f;

    /// <summary>Seconds of air in a full breath (the reserve while the head is under).</summary>
    public const float AirMax = 45f;
    /// <summary>A sprint stroke under water uses air this much faster.</summary>
    private const float AirSprintDrain = 1.7f;
    /// <summary>Seconds of air won back per second with the head out.</summary>
    private const float AirRefill = 9f;
    /// <summary>Health lost per second with no air left.</summary>
    public const float DrownDamage = 15f;

    /// <summary>Stroke pace, m/s: Game swims faster, as it runs faster.</summary>
    public float SwimSpeed => Rideable.Arcade ? 1.5f : 1.15f;
    /// <summary>The sprint stroke (Sprint held).</summary>
    public float SprintSwimSpeed => Rideable.Arcade ? 2.4f : 1.9f;

    private bool _swimming;
    private float _swimLunge;
    private float _swimLay;
    private float _swimPhase;
    private float _swimMove, _swimClimb;
    private float _drownTick;
    private bool _wasSwimFloor;
    private bool _swimSprint;
    private float _swimDrawPhase, _swimSeenPhase = float.NaN;
    private ulong _swimDrawnFrame;
    private static readonly System.Random SwimRng = new(301);

    /// <summary>
    /// In the water, swimming (on every peer: a remote copy reads it from the replicated pose).
    /// Items are holstered while it lasts.
    /// </summary>
    public bool IsSwimming => IsMultiplayerAuthority()
        ? _swimming
        : PoseKind == PoseSwim && RideKindId == (int)RideKind.OnFoot;

    /// <summary>Seconds of air left (owner only): drains with the head under water, refills above it.</summary>
    public float Air { get; private set; } = AirMax;

    /// <summary>The eye is under the surface (owner only).</summary>
    public bool HeadUnderwater { get; private set; }

    /// <summary>Metres from the surface down to the feet while swimming (owner only), 0 otherwise.</summary>
    public float SwimDepth { get; private set; }

    /// <summary>Went into the water, 0 (waded in) .. 1 (a big plunge). Owner only.</summary>
    public event Action<float>? Splashed;
    /// <summary>A stroke's hand went in (owner only): the swish.</summary>
    public event Action? Stroked;
    /// <summary>Back at the surface after a long time under (owner only): the gasp.</summary>
    public event Action? Gasped;

    /// <summary>
    /// Puts this player in the water at <paramref name="at"/> (feet), swimming from the next physics
    /// step, with <paramref name="velocity"/>. For whatever drops a player into water: falling off a
    /// jetski, a capsized boat, a sunk car. Gets off any ride first. False (and nothing changes) when
    /// there is no water there, or this peer does not own the player, or it is a seated passenger.
    /// </summary>
    public bool StartSwimming(Vector3 at, Vector3 velocity)
    {
        if (!IsMultiplayerAuthority() || RidingWith != 0) return false;
        if (!WaterField.TryLevelAt(at, out float level)) return false;
        if (_ride != null) ApplyRide(RideKind.OnFoot, velocity);
        if (Ragdolled) EndRagdoll();
        _mantling = false;
        GlobalPosition = at;
        Velocity = velocity;
        BeginSwim(level, velocity);
        return true;
    }

    /// <summary><see cref="StartSwimming(Vector3, Vector3)"/> at the surface over <paramref name="at"/>, floating, still.</summary>
    public bool StartSwimmingAtSurface(Vector3 at) =>
        WaterField.TryLevelAt(at, out float level) && StartSwimming(at with { Y = level - SwimFloat }, Vector3.Zero);

    /// <summary>Probes: the air left, as if this long had been spent under already.</summary>
    internal void DebugSetAir(float seconds) => Air = Mathf.Clamp(seconds, 0f, AirMax);

    /// <summary>Probes and screenshots: third or first person for this run (V's switch, never saved to the settings).</summary>
    internal void DebugThirdPerson(bool third)
    {
        if (_thirdPerson == third || XR.XrSession.Active) return;
        _thirdPerson = third;
        if (!third && _camera != null) _camera.Transform = new Transform3D(Basis.Identity, new Vector3(0, EyeHeight, 0));
        _pivotY = float.NaN;
        _armBlend = 1f;
        RefreshVisual(force: true);
    }

    /// <summary>On foot under water (thrown out of a seat, put back at a spot): up to the surface, swimming.</summary>
    private void SurfaceIfInWater()
    {
        if (!IsMultiplayerAuthority() || _ride != null || RidingWith != 0) return;
        if (WaterField.TryLevelAt(GlobalPosition, out float level) && level - GlobalPosition.Y > SwimEnter && DepthAt(level) > SwimEnter)
            StartSwimming(GlobalPosition with { Y = level - SwimFloat }, Velocity with { Y = 0 });
    }

    /// <summary>Water depth (surface to the ground under it) at this player, or a lake's worth when nothing is loaded.</summary>
    private float DepthAt(float level) =>
        Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float bed) ? level - bed : 100f;

    private void BeginSwim(float level, Vector3 velocity)
    {
        if (_sliding) { _sliding = false; SetBodyHeight(StandHeight); }
        _swimming = true;
        _swimLunge = 0f;
        _fallSpeed = 0f;
        _wasSwimFloor = false;
        _swimLay = 0f;
        DanceId = 0;
        FloorSnapLength = 0f;
        // a plunge throws spray; wading in, hardly any
        float strength = Mathf.Clamp((-velocity.Y - 1.5f) / 9f + velocity.Length() * 0.01f, 0f, 1f);
        if (strength > 0.04f)
        {
            Splashed?.Invoke(strength);
            SplashAt(GlobalPosition with { Y = level }, strength, sound: false);
        }
    }

    /// <summary>Out of the water (or onto a ride): the walk takes over.</summary>
    private void EndSwim()
    {
        if (!_swimming) return;
        _swimming = false;
        HeadUnderwater = false;
        SwimDepth = 0f;
        if (_ride == null) FloorSnapLength = 0.5f;
        PoseKind = PoseStride;
        BodyPose = Transform3D.Identity;
        Anim = default;
    }

    /// <summary>Getting onto a ride ends the swim; the next breath is a full one.</summary>
    private void LeaveWater()
    {
        EndSwim();
        Air = AirMax;
        _drownTick = 0f;
    }

    /// <summary>
    /// The water's turn in the on-foot physics, before the walk: true while swimming (the walk must
    /// not run). Starts the swim when the water is deep enough, ends it on wading or climbing out.
    /// </summary>
    private bool SwimPhysics(float dt, bool onFloor)
    {
        if (Indoors) { EndSwim(); return false; }
        bool wet = WaterField.TryLevelAt(GlobalPosition, out float level);
        float sub = wet ? level - GlobalPosition.Y : float.NegativeInfinity;
        if (!_swimming)
        {
            TickAir(dt, false, false);
            if (!wet) return false;
            bool fallingIn = !onFloor && Velocity.Y < -1f && sub > 0.05f && DepthAt(level) > SwimEnter;
            if (sub <= SwimEnter && !fallingIn) return false;
            BeginSwim(level, Velocity);
        }
        else if (!wet || sub < -0.6f || (onFloor && sub < SwimLeave))
        {
            // off the water's edge, thrown clear of it, or standing in the shallows: walking
            EndSwim();
            return false;
        }
        StepSwim(dt, level, sub);
        return true;
    }

    private void StepSwim(float dt, float level, float sub)
    {
        bool limp = KnockedOut || Npc;
        var input = limp ? Vector2.Zero : PlayerInput.Move;
        if (_stunTimer > 0) { _stunTimer -= dt; input = Vector2.Zero; }
        bool sprintDown = !limp && PlayerInput.Held(PlayerInput.Sprint);
        if (sprintDown && !_sprintHeld && PlayerInput.LastDevice == InputDevice.Gamepad) _sprintLatch = true;
        _sprintHeld = sprintDown;
        if (input.LengthSquared() < 0.09f) _sprintLatch = false;
        bool sprint = sprintDown || _sprintLatch;
        bool dive = !limp && PlayerInput.Held(PlayerInput.CrouchSlide);
        bool jumpDown = !limp && PlayerInput.Held(PlayerInput.Jump);
        bool jumpPressed = jumpDown && !_jumpHeld;
        _jumpHeld = jumpDown;
        _crouchHeld = dive;

        float amount = Mathf.Min(input.Length(), 1f);
        var yaw = new Basis(Vector3.Up, _viewYaw);
        var flat = amount > 0.01f ? (yaw * new Vector3(input.X, 0, input.Y)).Normalized() : Vector3.Zero;
        bool scripted = false;
        if (WalkControls?.Invoke() is { } walk)
        {
            amount = Mathf.Min(walk.Wish.Length(), 1f);
            flat = amount > 0.01f ? walk.Wish.Normalized() : Vector3.Zero;
            sprint = walk.Run;
            scripted = true;
        }

        float eye = GlobalPosition.Y + EyeHeight;
        bool under = eye < level - (HeadUnderwater ? 0f : 0.08f);
        bool deep = sub > SwimFloat + 0.3f;
        float pace = (sprint ? SprintSwimSpeed : SwimSpeed) * amount;

        // the stroke: flat at the surface; under water, or heading down from it looking down, along the look
        bool lookSteers = !scripted && amount > 0.01f && (deep || under || (input.Y < -0.3f && _pitch < -0.6f));
        var wish = lookSteers
            ? (yaw * new Basis(Vector3.Right, _pitch) * new Vector3(input.X, 0, input.Y)).Normalized() * pace
            : flat * pace;

        // --- climbing out: push into a ledge in reach and the mantle pulls you up --------
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
        if (!deep && !limp && _wallCoyote > 0 && flat != Vector3.Zero && flat.Dot(-_coyoteNormal) > 0.5f
            && TryBeginMantle(-_coyoteNormal, Mathf.Max(pace, 1.5f)))
        {
            EndSwim();
            return;
        }

        // --- up and down ------------------------------------------------------------------
        var v = Velocity;
        _swimLunge = Mathf.Max(0f, _swimLunge - dt);
        if (jumpPressed && !deep && _swimLunge <= 0f)
        {
            v.Y = LungeSpeed;
            _swimLunge = LungeTime;
            Stroked?.Invoke();
        }
        var water = WaterField.Velocity(GlobalPosition.X, GlobalPosition.Z, WaterField.Now);
        // the water carries a body at its surface; deeper down the waves' orbits die away
        float surfaceW = Mathf.Clamp(1f - (sub - SwimFloat) / 1.5f, 0f, 1f);
        float spring = (level - SwimFloat - GlobalPosition.Y) * 2.5f + water.Y;
        float wishY;
        bool vertical = true;
        if (dive) wishY = Mathf.Min(wish.Y, -DiveSpeed * (sprint ? 1.4f : 1f));
        else if (jumpDown && sub > SwimFloat + 0.15f) wishY = Mathf.Max(wish.Y, RiseSpeed);
        else if (lookSteers && Mathf.Abs(wish.Y) > 0.2f) wishY = wish.Y;
        else
        {
            // floating: on the waves at the surface, rising slowly back to it from below; just under
            // it (a crest went over), catching up with it, or a rising swell would leave it behind
            vertical = false;
            wishY = sub <= SwimFloat + 0.35f ? spring : Mathf.Max(IdleRise, Mathf.Min(spring, 1.5f) * surfaceW);
        }
        // nobody swims up out of the water: at the surface an upward stroke stops at it
        if (vertical && sub <= SwimFloat + 0.35f) wishY = Mathf.Min(wishY, spring);

        var target = new Vector3(wish.X + water.X * surfaceW, wishY, wish.Z + water.Z * surfaceW);
        // the water brakes a plunge from the moment the feet are in; a body bobbing up out of it falls back
        if (_swimLunge > 0f || (sub < SwimFloat - 0.35f && v.Y > -1.5f))
        {
            // out of the water (a lunge, a bob over a crest): falling, steering only
            v.Y -= Gravity * dt;
            float k = 1f - Mathf.Exp(-SwimDragLinear * dt);
            v.X += (target.X - v.X) * k;
            v.Z += (target.Z - v.Z) * k;
        }
        else
        {
            var rel = v - target;
            rel *= Mathf.Exp(-(SwimDragLinear + SwimDragQuad * rel.Length()) * dt);
            v = target + rel;
        }

        float vyBefore = v.Y;
        Velocity = v;
        MoveAndSlide();

        // a plunge into shallow water ends on the bed: as hard as the water has not braked it
        bool floor = IsOnFloor();
        if (floor && !_wasSwimFloor && -vyBefore > HardBed)
        {
            Landed?.Invoke(-vyBefore);
            TakeDamage((-vyBefore - HardBed) * 9f, 0, DamageCause.Fall);
        }
        _wasSwimFloor = floor;
        _fallSpeed = 0f;

        // what the figure shows: its own stroke, not the water carrying it
        var own = Velocity - water * surfaceW;
        _swimMove = new Vector2(own.X, own.Z).Length();
        _swimClimb = own.Y;
        _swimSprint = sprint && amount > 0.1f;

        HeadUnderwater = under;
        SwimDepth = sub;
        TickAir(dt, under, _swimSprint);

        if (_thirdPerson) FaceTravel(dt, flat != Vector3.Zero ? flat : new Vector3(wish.X, 0, wish.Z));
        UpdateCameraFeel(dt, sprint, false);
    }

    /// <summary>The reserve: drains with the head under, refills above, and drowning damage once it is empty.</summary>
    private void TickAir(float dt, bool under, bool sprinting)
    {
        if (under) Air = Mathf.Max(0f, Air - dt * (sprinting ? AirSprintDrain : 1f));
        else
        {
            if (_airWasUnder && Air < AirMax * 0.5f) Gasped?.Invoke();
            Air = Mathf.Min(AirMax, Air + AirRefill * dt);
        }
        _airWasUnder = under;
        if (under && Air <= 0f)
        {
            _drownTick -= dt;
            if (_drownTick <= 0f)
            {
                _drownTick = 1f;
                TakeDamage(DrownDamage, 0, DamageCause.Drown);
            }
        }
        else _drownTick = 0f;
    }

    private bool _airWasUnder;

    /// <summary>
    /// The owner's swimming pose, published like the walk's (<see cref="PublishFootPose"/>): the
    /// style from what the swimmer does, the stroke phase, and the body laid down along its travel.
    /// </summary>
    private void PublishSwimPose(float dt)
    {
        DanceId = 0;
        _airTime = 0f;
        bool under = HeadUnderwater && SwimDepth > SwimFloat + 0.3f;
        var style = KnockedOut ? SwimStyle.Tread
            : under ? SwimStyle.Under
            : _swimMove > 0.35f ? SwimStyle.Crawl
            : SwimStyle.Tread;
        float before = _swimPhase;
        if (!KnockedOut) _swimPhase = HumanMeshBuilder.AdvanceSwim(_swimPhase, style, _swimMove, dt);
        // a hand goes in twice a crawl cycle, once a breaststroke one
        if (style == SwimStyle.Crawl && Mathf.FloorToInt(before * 2f) != Mathf.FloorToInt(_swimPhase * 2f)) Stroked?.Invoke();
        else if (style == SwimStyle.Under && before < 0.15f && _swimPhase >= 0.15f) Stroked?.Invoke();

        float lay = style switch
        {
            SwimStyle.Crawl => -1.45f,
            // along its travel: straight down is head down, straight up is upright
            SwimStyle.Under => _swimMove + Mathf.Abs(_swimClimb) > 0.3f ? Mathf.Atan2(_swimClimb, _swimMove) - Mathf.Pi * 0.5f : -0.35f,
            // floating face down, out cold; treading water, nearly upright
            _ => KnockedOut ? -1.5f : -0.12f,
        };
        _swimLay = Mathf.Lerp(_swimLay, lay, 1f - Mathf.Exp(-5f * dt));
        PoseKind = PoseSwim;
        Anim = new Vector4(_swimMove, _swimPhase, (float)style, 0f);
        var pivot = new Vector3(0, SwimPivot, 0);
        BodyPose = new Transform3D(Basis.Identity, pivot) * new Transform3D(new Basis(Vector3.Right, _swimLay), Vector3.Zero)
                   * new Transform3D(Basis.Identity, -pivot);
    }

    /// <summary>
    /// Draws the swimming figure from the published pose, owner and remote alike (from
    /// <see cref="ApplyFootPose"/>). A remote copy integrates the stroke between updates, splashes
    /// when it goes in and is heard stroking.
    /// </summary>
    private void ApplySwimFigure()
    {
        if (_walker == null) return;
        float dt = (float)GetProcessDeltaTime();
        var style = (SwimStyle)Mathf.Clamp(Mathf.RoundToInt(Anim.Z), 0, 2);
        ulong frame = Engine.GetProcessFrames();
        bool fresh = _swimDrawnFrame != 0 && frame - _swimDrawnFrame > 3;
        _swimDrawnFrame = frame;
        if (IsMultiplayerAuthority()) _swimDrawPhase = _swimPhase;
        else
        {
            float before = _swimDrawPhase;
            if (Anim.Y != _swimSeenPhase) _swimDrawPhase = _swimSeenPhase = Anim.Y;
            else _swimDrawPhase = HumanMeshBuilder.AdvanceSwim(_swimDrawPhase, style, Anim.X, dt);
            if (fresh && WaterField.TryLevelAt(GlobalPosition, out float level)) SplashAt(GlobalPosition with { Y = level }, 0.6f, sound: true);
            else if (style == SwimStyle.Crawl && Mathf.FloorToInt(before * 2f) != Mathf.FloorToInt(_swimDrawPhase * 2f))
                PlayWaterAt(GlobalPosition + Vector3.Up * SwimFloat, Audio.SfxSynth.StrokeBank, -10f);
        }
        _poseWait += dt;
        var key = new FootPoseKey(_walker, PoseSwim, 1000f * (int)style + Mathf.Round(Anim.X * 10f), _swimDrawPhase,
            ItemArmPose.None, 0f, null, Hat, _walkPalette);
        if (key != _poseKey && !HoldRemoteFigure())
        {
            _poseKey = key;
            _mountsKey = key;
            _poseWait = 0f;
            _walker.Mesh = HumanMeshBuilder.BuildSwim(_walkPalette, style, _swimDrawPhase, Hat, _poseMesh ??= new ArrayMesh());
            _poseMounts = HumanMeshBuilder.MountsForSwim(style, _swimDrawPhase);
        }
        _walker.Transform = BodyPose * FlinchPose(dt);
        // holstered: nothing in the hands while swimming
        HandLocal = null;
        PlaceBack(_poseMounts);
    }

    // ---- getting into the water from a ride or a crash ----------------------------------------

    /// <summary>
    /// A wingsuit or a canopy coming down onto water deep enough to swim in: off it and swimming,
    /// the plunge braked by the water (no crash). False otherwise: the flight goes on.
    /// </summary>
    private bool FlyerIntoWater(Flyer flyer)
    {
        if (flyer.IsVehicle || !WaterField.TryLevelAt(GlobalPosition, out float level) || level - GlobalPosition.Y < 0.2f) return false;
        if (DepthAt(level) < SwimEnter) return false;   // a puddle: the ground's, as before
        var velocity = _flight.Velocity;
        ApplyRide(RideKind.OnFoot, velocity);
        BeginSwim(level, velocity);
        Announced?.Invoke("SPLASHDOWN", true);
        return true;
    }

    /// <summary>A bike or skis ridden into water deeper than the chest: off, swimming. False otherwise.</summary>
    private bool RideIntoWater()
    {
        if (_ride is not (Bicycle or Skis) || !WaterField.TryLevelAt(GlobalPosition, out float level)) return false;
        if (level - GlobalPosition.Y <= SwimEnter) return false;
        var velocity = Velocity;
        ApplyRide(RideKind.OnFoot, velocity);
        BeginSwim(level, velocity);
        Announced?.Invoke("SPLASH!", false);
        return true;
    }

    /// <summary>A crash ragdoll that went into deep water: it stops tumbling and floats, swimming.</summary>
    private bool RagdollIntoWater()
    {
        if (_ragdoll == null || _ragdoll.Age < 0.25f) return false;
        var pelvis = _ragdoll.Pelvis;
        if (!WaterField.TryLevelAt(pelvis, out float level) || level - pelvis.Y < 0.2f) return false;
        if (Terrain != null && Terrain.TryGetHeight(pelvis, out float bed) && level - bed < SwimEnter) return false;
        var velocity = _ragdoll.Velocity;
        EndRagdoll();
        _stunTimer = Mathf.Max(_stunTimer, 1.2f);
        StartSwimming(pelvis with { Y = Mathf.Min(pelvis.Y - 0.95f, level - SwimFloat) }, velocity);
        return true;
    }

    // ---- splash and sound ------------------------------------------------------------------

    private static StandardMaterial3D? _sprayMaterial;
    private static BoxMesh? _sprayMesh;

    /// <summary>
    /// Spray thrown up where a body went in (every peer, its own copy), and with <paramref name="sound"/>
    /// the splash heard there (the owner hears its own through <c>PlayerFeel</c>, not spatial).
    /// </summary>
    private void SplashAt(Vector3 at, float strength, bool sound)
    {
        if (DisplayServer.GetName() == "headless" || !IsInsideTree()) return;
        _sprayMaterial ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.94f, 1f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        };
        _sprayMesh ??= new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.09f) };
        var spray = new CpuParticles3D
        {
            Name = "Splash", TopLevel = true, Position = at + Vector3.Up * 0.05f,
            Amount = 24 + (int)(96 * strength), Lifetime = 0.6f + 0.7f * strength, OneShot = true, Explosiveness = 0.9f,
            Emitting = true,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring, EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = 0.45f + 0.4f * strength, EmissionRingInnerRadius = 0.15f, EmissionRingHeight = 0.05f,
            Direction = Vector3.Up, Spread = 22f + 18f * (1f - strength),
            InitialVelocityMin = 1.5f + 3f * strength, InitialVelocityMax = 3f + 7f * strength,
            Gravity = new Vector3(0, -9.8f, 0), DampingMin = 0.3f, DampingMax = 1.2f,
            ScaleAmountMin = 0.6f, ScaleAmountMax = 1.6f,
            Mesh = _sprayMesh, MaterialOverride = _sprayMaterial,
        };
        AddChild(spray);
        GetTree().CreateTimer(2.5).Timeout += spray.QueueFree;
        if (sound) PlayWaterAt(at, Audio.SfxSynth.SplashBank, Mathf.LinearToDb(0.35f + 0.65f * strength));
    }

    /// <summary>A water sound where it happened, heard spatially by this peer.</summary>
    private void PlayWaterAt(Vector3 at, Audio.SfxBank bank, float volumeDb)
    {
        if (DisplayServer.GetName() == "headless" || !IsInsideTree()) return;
        var (stream, pitch, db) = bank.Pick(SwimRng);
        var voice = new AudioStreamPlayer3D
        {
            Stream = stream, PitchScale = pitch, VolumeDb = volumeDb + db,
            UnitSize = 8f, MaxDistance = 120f, Bus = Audio.SfxBus.Name,
            TopLevel = true, Position = at, Autoplay = true,
        };
        voice.Finished += voice.QueueFree;
        AddChild(voice);
    }
}
