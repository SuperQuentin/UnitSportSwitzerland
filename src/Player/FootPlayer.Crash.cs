using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Thrown out of a vehicle in a crash (#214): through the windscreen of a car, over the bars of a
/// bike, as a <see cref="Player.Ragdoll"/>, with a crash camera that follows the body and bones
/// that crack when it lands. See <c>docs/notes/player/crash-ragdoll.md</c>.
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// On foot and limp. <see cref="Anim"/> then carries the launch — xyz the velocity, w the
    /// tumble rate plus <see cref="RagdollMark"/> — and every peer simulates its own copy from it.
    /// </summary>
    public const int PoseRagdoll = 3;
    /// <summary>Added to the spin in Anim.w: a ride's own Anim still in flight never reads as a launch.</summary>
    private const float RagdollMark = 100f;
    /// <summary>Speed (m/s) a vehicle has to lose against something solid to throw its rider: about 43 km/h.</summary>
    private const float ThrowSpeed = 12f;
    /// <summary>The most bones one crash breaks: past this, the cracks are only thuds.</summary>
    private const int MaxBreaks = 8;

    private Ragdoll? _ragdoll;
    private MeshInstance3D? _ragdollMesh;
    private float _ragdollClock, _crackCooldown;
    private int _bonesBroken;
    private readonly Random _crashRng = new();

    // a remote copy: the driver's seat as last drawn, so its ragdoll starts where the figure sat
    private DriverSeat? _seenSeat;
    private Transform3D _seenSeatFrame;
    private double _seenSeatAt;

    // the crash camera, owner only
    private bool _crashCam;
    private float _crashTime, _crashOut, _crashFromFov;
    private Vector3 _crashAnchor, _crashLook, _crashSide;
    private Transform3D _crashFrom;
    // in VR: a still spot to stand and watch from, cut to behind a blink
    private Camera3D? _vrEye;
    private float _vrSinceCut, _vrBlind, _vrCheck;

    /// <summary>Limp after a crash: no control until the body comes to rest.</summary>
    public bool Ragdolled => _ragdoll != null;
    /// <summary>Bones broken in the last crash (checks).</summary>
    public int CrashBones => _bonesBroken;
    /// <summary>This copy's ragdoll hips, or null (checks).</summary>
    public Vector3? RagdollPelvis => _ragdoll?.Pelvis;

    private uint RagdollMask => CollisionMask & ~Hurtbox.Layer;

    /// <summary>
    /// The owner's vehicle stopped dead (<see cref="RidePhysics"/>): the machine stays where it
    /// hit, the rider goes on. A car throws its driver through the windscreen.
    /// </summary>
    private void ThrowFromVehicle(float hit)
    {
        var fwd = (-GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        bool car = _visual is CarRig;
        // the joints before the visual goes with the ride
        var joints = SeatedJoints();
        var state = CaptureVehicle(wrecked: false) with { Velocity = Vector3.Zero };
        Vehicles?.Park(state);
        if (Npc)
        {
            // an NPC has nobody watching it fall: thrown clear, as it always was
            Announced?.Invoke("THROWN OFF!", false);
            ApplyRide(RideKind.OnFoot, fwd * hit * 0.25f + Vector3.Up * 4f);
            GlobalPosition += Vector3.Up * 1.2f;
            _stunTimer = 1.2f;
            TakeDamage((hit - 8f) * 3f, 0, DamageCause.Crash);
            return;
        }

        Announced?.Invoke(car ? "THROUGH THE WINDSCREEN!" : "THROWN OFF!", false);
        // on at most of the speed it hit at, and up: over a low wall, into a tree
        var launch = fwd * hit * 0.75f + Vector3.Up * (3f + hit * 0.18f);
        ApplyRide(RideKind.OnFoot, launch);
        StartRagdoll(joints, launch, Mathf.Clamp(hit * 0.35f, 3f, 10f), car);
        BeginCrashCamera(fwd);
        TakeDamage((hit - 8f) * 2f, 0, DamageCause.Crash);
    }

    /// <summary>
    /// The origin moved (#185) mid-crash: the ragdoll's points, the crash camera's spots and the
    /// last seat drawn are world positions kept across frames. (The ragdoll's mesh and the VR eye
    /// are top-level nodes, which the shifter moves itself.)
    /// </summary>
    private void ShiftCrash(OriginShift shift)
    {
        _ragdoll?.Apply(shift);
        _crashAnchor = shift.Point(_crashAnchor);
        _crashLook = shift.Point(_crashLook);
        _crashSide = shift.Direction(_crashSide);
        _crashFrom = shift.Apply(_crashFrom);
        _seenSeatFrame = shift.Apply(_seenSeatFrame);
    }

    /// <summary>The figure as it sits in the vehicle, world space: the driver's seat in a car, a rider's crouch otherwise.</summary>
    private Vector3[] SeatedJoints() => _visual is CarRig rig
        ? DriverWorldJoints(rig.DriverSeat, GlobalTransform * _visual.Transform * rig.DriverFrame)
        : PoseWorldJoints();

    private static Vector3[] DriverWorldJoints(DriverSeat seat, Transform3D frame)
    {
        var joints = HumanMeshBuilder.DriverJoints(seat);
        for (int i = 0; i < joints.Length; i++) joints[i] = frame * Flip(joints[i]);
        return joints;
    }

    private Vector3[] PoseWorldJoints()
    {
        var joints = HumanMeshBuilder.PoseJoints(HumanPose.Cycling);
        for (int i = 0; i < joints.Length; i++) joints[i] = GlobalTransform * Flip(joints[i]);
        return joints;
    }

    /// <summary>Author space (+Z forward) to a node's (−Z forward), and back: a half turn about Y.</summary>
    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private void StartRagdoll(Vector3[] joints, Vector3 launch, float spin, bool throughGlass)
    {
        // tumbling forward: head over heels about the axis square to the throw
        var axis = Vector3.Up.Cross(launch with { Y = 0 });
        if (axis.LengthSquared() < 1e-4f) axis = GlobalTransform.Basis.X;
        _ragdoll = new Ragdoll(joints, launch, axis.Normalized() * spin, GetRid(), RagdollMask);
        _ragdoll.Struck += OnRagdollStruck;
        _ragdollClock = 0f;
        _crackCooldown = 0.15f;
        _bonesBroken = 0;
        if (IsMultiplayerAuthority())
        {
            PoseKind = PoseRagdoll;
            Anim = new Vector4(launch.X, launch.Y, launch.Z, spin + RagdollMark);
            BodyPose = Transform3D.Identity;
            // not a safe place to wake up at, and no control: held until the body is still
            _stunTimer = Mathf.Max(_stunTimer, 0.5f);
        }
        if (_ragdollMesh == null)
        {
            _ragdollMesh = new MeshInstance3D { Name = "Ragdoll", TopLevel = true, MaterialOverride = HumanMeshBuilder.FigureMaterial() };
            AddChild(_ragdollMesh);
        }
        _ragdollMesh.Visible = true;

        var head = joints[(int)HumanMeshBuilder.Joint.HeadBase];
        PlayAt(head, Audio.SfxSynth.BoneBreakBank, 0f);
        if (!throughGlass) return;
        var fwd = (launch with { Y = 0 }).Normalized();
        PlayAt(head + fwd * 0.4f, Audio.SfxSynth.GlassBank, 2f);
        SpawnGlass(head + fwd * 0.5f, launch);
    }

    /// <summary>
    /// The ragdoll's frame, on every peer: a remote copy starts and stops with the owner's
    /// <see cref="PoseKind"/>; each copy steps, is drawn, and a remote one is steered onto where
    /// the owner's body is. False when there is no ragdoll.
    /// </summary>
    private bool TickRagdoll(float dt)
    {
        if (!IsMultiplayerAuthority())
        {
            bool want = PoseKind == PoseRagdoll && RideKindId == (int)RideKind.OnFoot;
            if (want && _ragdoll == null && Anim.W > RagdollMark)
            {
                bool car = _seenSeat != null && Time.GetTicksMsec() / 1000.0 - _seenSeatAt < 0.5;
                var joints = car ? DriverWorldJoints(_seenSeat!, _seenSeatFrame) : PoseWorldJoints();
                StartRagdoll(joints, new Vector3(Anim.X, Anim.Y, Anim.Z), Anim.W - RagdollMark, car);
            }
            else if (!want && _ragdoll != null) EndRagdoll();
        }
        if (_ragdoll == null || _ragdollMesh == null) return false;

        _ragdollClock += dt;
        _crackCooldown -= dt;
        _ragdoll.Step(dt * SlowMotion(_ragdollClock), GetWorld3D().DirectSpaceState, GroundAt);
        if (!IsMultiplayerAuthority())
        {
            // the owner's body is pinned to its hips, so the replicated position is where they are
            var off = GlobalPosition - _ragdoll.Pelvis;
            _ragdoll.Shift(off.LengthSquared() > 64f ? off : off * (1f - Mathf.Exp(-2f * dt)));
        }

        // into deep water: it stops tumbling and floats (#301)
        if (IsMultiplayerAuthority() && RagdollIntoWater()) return false;

        var pelvis = _ragdoll.Pelvis;
        var points = _ragdoll.Points;
        Span<Vector3> local = stackalloc Vector3[HumanMeshBuilder.JointCount];
        // relative to the hips, and pre-flipped: the mesh builder turns everything a half turn on the way out
        for (int i = 0; i < local.Length; i++) local[i] = Flip(points[i] - pelvis);
        _ragdollMesh.Mesh = HumanMeshBuilder.BuildJoints(FigurePalette(GetMultiplayerAuthority()), local, Hat);
        _ragdollMesh.GlobalTransform = new Transform3D(Basis.Identity, pelvis);
        if (_walker != null) _walker.Visible = false;

        if (IsMultiplayerAuthority() && _ragdoll.Resting) EndRagdoll();
        return true;
    }

    /// <summary>
    /// A beat of slow motion as the body leaves the car, the same on every peer: real time at
    /// first (through the glass), a third of it for the flight's first half second, then back.
    /// </summary>
    private static float SlowMotion(float t) =>
        t < 0.12f ? 1f : t < 0.55f ? 0.3f : Mathf.Lerp(0.3f, 1f, Mathf.Clamp((t - 0.55f) / 0.45f, 0f, 1f));

    private float? GroundAt(Vector3 p) =>
        !Indoors && Terrain != null && Terrain.TryGetHeight(p, out float h) ? h : null;

    /// <summary>
    /// Back in control: lying where the body came to rest, head where its head is, then up after
    /// a moment — the stunned figure flat on the ground the game already had.
    /// </summary>
    private void EndRagdoll()
    {
        if (_ragdoll == null) return;
        var rag = _ragdoll;
        _ragdoll = null;
        rag.Struck -= OnRagdollStruck;
        if (_ragdollMesh != null) _ragdollMesh.Visible = false;
        if (_walker != null) _walker.Visible = true;
        if (!IsMultiplayerAuthority()) return;

        var pelvis = rag.Pelvis;
        var up = rag.Points[(int)HumanMeshBuilder.Joint.HeadTop] - pelvis;
        Velocity = Vector3.Zero;
        if (up.Y > 0.45f)
        {
            // came to rest upright (slumped against a wall, on its feet): stood where its feet are
            var p = rag.Points;
            float feet = Mathf.Min(Mathf.Min(p[(int)HumanMeshBuilder.Joint.AnkleL].Y, p[(int)HumanMeshBuilder.Joint.AnkleR].Y),
                Mathf.Min(p[(int)HumanMeshBuilder.Joint.ToeL].Y, p[(int)HumanMeshBuilder.Joint.ToeR].Y));
            GlobalPosition = pelvis with { Y = feet - 0.06f };
            _stunTimer = 0.8f;
        }
        else
        {
            var head = up with { Y = 0 };
            head = head.LengthSquared() > 1e-4f ? head.Normalized() : -GlobalTransform.Basis.Z;
            // lying down (PublishFootPose) puts the head 0.95 m along the body's −Z from its feet
            Rotation = new Vector3(0, Mathf.Atan2(-head.X, -head.Z), 0);
            GlobalPosition = pelvis - head * 0.95f + Vector3.Down * 0.1f;
            _downRot = -1.45f;
            _stunTimer = 1.6f;
        }
        PoseKind = PoseStride;
        Anim = default;
        if (_bonesBroken > 0) Announced?.Invoke(_bonesBroken == 1 ? "1 BONE BROKEN" : $"{_bonesBroken} BONES BROKEN", false);
        EndCrashCamera();
    }

    /// <summary>A point of the body hit something: a crack and real damage when hard, a thud when not.</summary>
    private void OnRagdollStruck(Vector3 at, float speed)
    {
        if (_crackCooldown > 0f) return;
        bool owner = IsMultiplayerAuthority();
        if (speed > 7f && _bonesBroken < MaxBreaks)
        {
            _crackCooldown = 0.14f;
            _bonesBroken++;
            PlayAt(at, Audio.SfxSynth.BoneBreakBank, Mathf.Clamp(speed - 12f, -6f, 3f));
            PlayAt(at, Audio.SfxSynth.ImpactBank, -6f);
            if (!owner) return;
            Shaken?.Invoke(Mathf.Clamp(speed / 16f, 0.45f, 0.95f));
            PlayerInput.Rumble(0.7f, 1f, 0.2f);
            TakeDamage(Mathf.Min(3f + speed * 0.3f, 9f));
        }
        else
        {
            _crackCooldown = 0.08f;
            PlayAt(at, Audio.SfxSynth.ImpactBank, Mathf.Clamp(-16f + speed * 1.5f, -16f, -2f));
            if (owner) Shaken?.Invoke(0.3f);
        }
    }

    /// <summary>A one-shot where it happened, heard by everyone near: every peer runs its own ragdoll and plays its own.</summary>
    private void PlayAt(Vector3 at, Audio.SfxBank bank, float volumeDb)
    {
        var (stream, pitch, db) = bank.Pick(_crashRng);
        var voice = new AudioStreamPlayer3D
        {
            Stream = stream, PitchScale = pitch, VolumeDb = volumeDb + db,
            UnitSize = 10f, MaxDistance = 250f, Bus = Audio.SfxBus.Name,
            TopLevel = true, Position = at, Autoplay = true,
        };
        voice.Finished += voice.QueueFree;
        AddChild(voice);
    }

    /// <summary>The windscreen's shards, flung on with the body.</summary>
    private void SpawnGlass(Vector3 at, Vector3 launch)
    {
        float speed = launch.Length();
        var shards = new CpuParticles3D
        {
            Name = "Glass", TopLevel = true, Position = at,
            Amount = 80, Lifetime = 1.8f, OneShot = true, Explosiveness = 0.95f, Emitting = true,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(0.6f, 0.25f, 0.1f),
            Direction = launch.Normalized(), Spread = 30f,
            InitialVelocityMin = speed * 0.35f, InitialVelocityMax = speed * 0.85f,
            Gravity = new Vector3(0, -9.8f, 0), DampingMin = 0.5f, DampingMax = 1.5f,
            ScaleAmountMin = 0.5f, ScaleAmountMax = 1.5f,
            Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.006f, 0.05f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.78f, 0.92f, 1f, 0.75f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(shards);
        GetTree().CreateTimer(2.5).Timeout += shards.QueueFree;
    }

    // ---- the crash camera ----

    /// <summary>
    /// A cut to beside the crash, low and a few metres out, watching the body come through the
    /// glass and zooming to keep it the same size as it flies off; then a chase behind where it
    /// tumbles; then, when it lies still, a blend back to the normal view.
    /// </summary>
    private void BeginCrashCamera(Vector3 fwd)
    {
        if (_camera == null || !IsMultiplayerAuthority() || _ragdoll == null) return;
        if (XR.XrSession.Active) { BeginVrCrashView(fwd); return; }
        var body = _ragdoll.Centre;
        var side = Vector3.Up.Cross(fwd).Normalized();
        var space = GetWorld3D().DirectSpaceState;
        // either side, whichever has room; behind and above if neither does
        var tries = new[]
        {
            (Spot: body + side * 5.5f - fwd * 2f + Vector3.Up * 1.3f, Side: side),
            (Spot: body - side * 5.5f - fwd * 2f + Vector3.Up * 1.3f, Side: -side),
            (Spot: body - fwd * 7f + Vector3.Up * 3.5f, Side: side),
        };
        float best = -1f;
        foreach (var (spot, s) in tries)
        {
            var reach = CameraReach(space, body, spot);
            float d = reach.DistanceTo(body);
            if (d > best) { best = d; _crashAnchor = reach; _crashSide = s; }
            if (d > 3.5f) break;
        }
        _crashLook = body;
        _crashTime = 0f;
        _crashOut = 0f;
        _crashCam = true;
    }

    /// <summary>As far toward <paramref name="to"/> as the lens can go from <paramref name="from"/>: stopped short of a wall or a tree.</summary>
    private Vector3 CameraReach(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, RagdollMask, new Godot.Collections.Array<Rid> { GetRid() }));
        if (hit.Count == 0) return to;
        var at = hit["position"].AsVector3();
        return at + (from - at).Normalized() * 0.4f;
    }

    private bool Sees(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to) =>
        space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, RagdollMask, new Godot.Collections.Array<Rid> { GetRid() })).Count == 0;

    private void UpdateCrashCamera(float dt)
    {
        if (_vrEye != null) { UpdateVrCrashView(dt); return; }
        if (!_crashCam || _camera == null || _ragdoll == null) return;
        _crashTime += dt;
        var body = _ragdoll.Centre;
        _crashLook = _crashLook.Lerp(body, 1f - Mathf.Exp(-12f * dt));
        float dist = _crashAnchor.DistanceTo(body);
        float fov;
        if (_crashTime < 1.8f && dist < 15f)
        {
            // held where it cut to, zooming to keep the body about two metres across the frame
            fov = Mathf.Clamp(Mathf.RadToDeg(2f * Mathf.Atan(1.6f / Mathf.Max(dist, 0.5f))), 24f, 62f);
        }
        else
        {
            // then a chase from the side it is already watching from: five metres off and a little
            // above, never straight down on the body, pulled in short of walls
            var side = (_crashAnchor - body) with { Y = 0 };
            side = side.LengthSquared() > 0.04f ? side.Normalized() : _crashSide;
            var from = body + Vector3.Up * 0.8f;
            var wanted = CameraReach(GetWorld3D().DirectSpaceState, from, body + side * 5.5f + Vector3.Up * 2.2f);
            var space = GetWorld3D().DirectSpaceState;
            if (wanted.DistanceTo(body) < 3f)
            {
                // boxed in on this side (a wall, the car): swing round a quarter turn and try again
                var round = new Vector3(-side.Z, 0, side.X);
                wanted = CameraReach(space, from, body + round * 5.5f + Vector3.Up * 2.2f);
            }
            // the body down behind something (the bonnet, a kerb): look down on it from higher up
            if (!Sees(space, wanted, body))
                wanted = CameraReach(space, from, body + side * 3.5f + Vector3.Up * 4.5f);
            _crashAnchor = _crashAnchor.Lerp(wanted, 1f - Mathf.Exp(-2.5f * dt));
            fov = 55f;
        }
        _camera.Fov = Mathf.Lerp(_camera.Fov, fov, 1f - Mathf.Exp(-5f * dt));
        var look = _crashLook - _crashAnchor;
        if (look.LengthSquared() > 0.01f && Mathf.Abs(look.Normalized().Y) < 0.99f)
            _camera.GlobalTransform = new Transform3D(Basis.Identity, _crashAnchor).LookingAt(_crashLook, Vector3.Up);
    }

    /// <summary>The body is still: the normal view takes over, blended from where the crash camera was.</summary>
    private void EndCrashCamera()
    {
        if (_vrEye != null) { EndVrCrashView(); return; }
        if (!_crashCam || _camera == null) { _crashCam = false; return; }
        _crashCam = false;
        _crashFrom = _camera.GlobalTransform;
        _crashFromFov = _camera.Fov;
        _crashOut = 1f;
        // third person picks up looking the way the crash camera did; first person, along the body
        var f = -_crashFrom.Basis.Z;
        _viewYaw = _thirdPerson ? Mathf.Atan2(-f.X, -f.Z) : Rotation.Y;
        _lookYaw = 0f;
        _pitch = _thirdPerson ? Mathf.Clamp(Mathf.Asin(Mathf.Clamp(f.Y, -1f, 1f)), -0.7f, 0.3f) : 0f;
    }

    /// <summary>After the normal camera has placed itself this frame: eased in from the crash camera's last shot.</summary>
    private void BlendOutCrashCamera(float dt)
    {
        if (_crashOut <= 0f || _camera == null) return;
        _crashOut = Mathf.Max(0f, _crashOut - dt / 0.9f);
        float t = 1f - _crashOut;
        t = t * t * (3f - 2f * t);
        _camera.GlobalTransform = _crashFrom.InterpolateWith(_camera.GlobalTransform, t);
        _camera.Fov = Mathf.Lerp(_crashFromFov, _camera.Fov, t);
    }

    // ---- the crash in VR ----
    //
    // The flat crash camera flies, zooms and shakes: in a headset any of those is a lurch the inner
    // ear does not agree with. In VR the player is instead stood at a still spot beside the crash,
    // level, at standing eye height, facing the body, and watches it go with their own head — the
    // rig takes a camera that is not a player's as an anchor followed in position only, with the
    // heading it had when adopted. Every change of spot happens behind a blink (XrRig.Blink).

    private void BeginVrCrashView(Vector3 fwd)
    {
        if (_ragdoll == null || _camera == null) return;
        var body = _ragdoll.Centre;
        var side = Vector3.Up.Cross(fwd).Normalized();
        _vrEye = new Camera3D { Name = "CrashEye", Far = _camera.Far, Near = _camera.Near };
        // not under the player: the rig would take it for the player's own eye and turn it with the head
        var holder = new Node3D { Name = "CrashView", TopLevel = true };
        AddChild(holder);
        holder.AddChild(_vrEye);
        PlaceVrEye(body, new[] { side, -side, -fwd });
        _vrEye.Current = true;
        XR.XrSession.Rig?.Blink();
    }

    /// <summary>
    /// Stands the eye 5.5 m from the body in the first of <paramref name="sides"/> with a clear
    /// view, the ground under it plus standing eye height, turned (yaw only) to face the body.
    /// </summary>
    private void PlaceVrEye(Vector3 body, Vector3[] sides)
    {
        if (_vrEye == null) return;
        var space = GetWorld3D().DirectSpaceState;
        Vector3 best = body + sides[0] * 5.5f + Vector3.Up * 1.65f;
        float bestRoom = -1f;
        foreach (var side in sides)
        {
            var flat = (side with { Y = 0 }).Normalized();
            var spot = CameraReach(space, body + Vector3.Up * 0.8f, body + flat * 5.5f + Vector3.Up * 0.8f);
            spot.Y = (GroundAt(spot) ?? body.Y - 0.8f) + 1.65f;
            float room = spot.DistanceTo(body);
            bool sees = Sees(space, spot, body);
            if (sees && room > 3f) { best = spot; break; }
            if ((sees ? room + 100f : room) > bestRoom) { bestRoom = sees ? room + 100f : room; best = spot; }
        }
        var look = (body - best) with { Y = 0 };
        float yaw = look.LengthSquared() > 1e-4f ? Mathf.Atan2(-look.X, -look.Z) : Rotation.Y;
        _vrEye.GetParent<Node3D>().GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), best);
        _vrSinceCut = 0f;
        _vrBlind = 0f;
    }

    /// <summary>The eye never moves on its own; it cuts (behind a blink) when the body flies too far or out of sight.</summary>
    private void UpdateVrCrashView(float dt)
    {
        if (_vrEye == null || _ragdoll == null) return;
        // a menu or the spectator took the view: give it back to them and stop
        if (GetViewport().GetCamera3D() != _vrEye && !_vrEye.Current) return;
        _vrSinceCut += dt;
        _vrCheck += dt;
        if (_vrCheck < 0.1f) return;
        var body = _ragdoll.Centre;
        var eye = _vrEye.GlobalPosition;
        _vrBlind = Sees(GetWorld3D().DirectSpaceState, eye, body) ? 0f : _vrBlind + _vrCheck;
        _vrCheck = 0f;
        if (_vrSinceCut < 1.2f || (eye.DistanceTo(body) < 14f && _vrBlind < 0.5f)) return;
        // beside it again, from the side we were on, then round the other ways
        var from = (eye - body) with { Y = 0 };
        var back = from.LengthSquared() > 1e-4f ? from.Normalized() : -GlobalTransform.Basis.Z;
        var round = new Vector3(-back.Z, 0, back.X);
        PlaceVrEye(body, new[] { back, round, -round, -back });
        XR.XrSession.Rig?.Blink();
    }

    /// <summary>The body is still: back into the player's own eyes, behind a blink.</summary>
    private void EndVrCrashView()
    {
        if (_vrEye == null) return;
        _vrEye.GetParent().QueueFree();
        _vrEye = null;
        // first person in VR: the eyes follow the body's heading, which now points along it
        _viewYaw = Rotation.Y;
        _lookYaw = 0f;
        _pitch = 0f;
        if (_camera != null) _camera.Current = true;
        XR.XrSession.Rig?.Blink();
    }
}
