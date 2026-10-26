using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Free climbing on desktop and pad (#1225, <c>docs/notes/player/climbing.md</c>): pushing the stick
/// into a climbable face (<see cref="Climbable"/>) grabs it, in the air at once, on the ground after
/// <see cref="ClimbGroundPush"/> s of pushing when the face rises past the mantle's reach. The stick
/// climbs along the face, Jump climb-jumps, Back + Jump kicks off, Crouch lets go; at a lip the mantle
/// pulls the body over. Spends <see cref="Stamina"/>; empty, the hands slide and let go.
///
/// <para>
/// One chest ray a physics step holds the body <see cref="ClimbStandOff"/> off the face, moved with
/// <c>MoveAndSlide</c> so a roof edge stops it. Everyone sees it through the replicated pose
/// (<see cref="PoseWallClimb"/>), drawn by the skinned figure's bones (no mesh build). An NPC or a bot
/// never enters by pushing: it needs <see cref="FootActions"/>' <c>Climb</c>.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>On foot, free climbing a face: <see cref="Anim"/> = (speed along the face, limb phase 0..1, climb-jump 0..1, 0).</summary>
    public const int PoseWallClimb = 8;

    /// <summary>Static bodies in this group are never climbed (glass towers, stairs, fences, placed objects).</summary>
    public const string NoClimbGroup = "no_climb";

    /// <summary>The climbing ray's height over the feet, and its reach toward the face.</summary>
    private const float ClimbChest = 1.25f, FaceReach = 0.9f;
    /// <summary>The body's centre this far off the face.</summary>
    private const float ClimbStandOff = BodyRadius + 0.05f;
    private const float ClimbUp = 1.2f, ClimbSide = 1.4f, ClimbDown = 1.6f;
    /// <summary>A climb-jump: this far along the face in this long.</summary>
    private const float ClimbJumpDist = 1.9f, ClimbJumpTime = 0.4f;
    /// <summary>Kicking off a face: out and up (m/s).</summary>
    private const float KickOut = 4f, KickUp = 3f;
    /// <summary>Pushing into a face on the ground this long grabs it.</summary>
    private const float ClimbGroundPush = 0.25f;
    /// <summary>Empty: the hands slide this long before the body falls.</summary>
    private const float ClimbSlipTime = 0.3f;
    /// <summary>Pushing into a face counts from this much of the stick toward it.</summary>
    private const float ClimbPushDot = 0.6f;

    private bool _climbing;
    private Vector3 _climbNormal = Vector3.Back;
    private float _climbPush;
    private float _climbPhase, _climbMove, _climbJump;
    private float _climbJumpLeft;
    private Vector3 _climbJumpDir;
    private float _climbSlip;
    private bool _climbLoose;
    private float _climbDrawPhase, _climbSeenPhase = float.NaN;
    private readonly RayQuery _climbRay = new();
    private static bool _climbHintSeen;

    /// <summary>Free climbing a face (on every peer: a remote copy reads it from the replicated pose).</summary>
    public bool IsClimbing => !Net.NetLink.Dropped(this) && IsMultiplayerAuthority()
        ? _climbing
        : PoseKind == PoseWallClimb && RideKindId == (int)RideKind.OnFoot;

    /// <summary>Seconds since this climb began (owner only).</summary>
    public float ClimbTime { get; private set; }

    /// <summary>The face being climbed (owner only): its normal, out toward the climber.</summary>
    public Vector3 ClimbNormal => _climbNormal;

    /// <summary>On crumbling rock (owner only): climbing costs more.</summary>
    public bool ClimbLoose => _climbLoose;

    /// <summary>The first climb of the session, its first 5 s: the controls hint (<c>PlayerFeel</c>).</summary>
    public bool ClimbHintDue => _climbing && !_climbHintSeen && ClimbTime < 5f;

    /// <summary>Grabbed a face (owner only).</summary>
    public event Action? ClimbStarted;
    /// <summary>A climb-jump (owner only).</summary>
    public event Action? ClimbJumped;
    /// <summary>Out of stamina on a face: the grunt, the hands slide (owner only).</summary>
    public event Action? ClimbSlipped;

    /// <summary>Why the last climb let go or could not start (checks), or null.</summary>
    public string? ClimbRefused { get; private set; }

    /// <summary>
    /// The ray's verdict on what is in front: a climbable face's point and normal, whether it crumbles.
    /// <paramref name="why"/> says what was there otherwise (null: nothing hit).
    /// </summary>
    private bool ProbeFace(Vector3 from, Vector3 toward, float reach, out Vector3 point, out Vector3 normal, out bool loose, out string? why)
    {
        point = normal = Vector3.Zero;
        loose = false;
        why = null;
        var hit = _climbRay.Cast(GetWorld3D().DirectSpaceState, from, from + toward * reach, CollisionMask, SelfExclude);
        if (hit.Count == 0) return false;
        point = hit["position"].AsVector3();
        normal = hit["normal"].AsVector3();
        var body = ClassifyClimb(hit["collider"].AsGodotObject());
        Terrain.Format.CoverClass? cover = null;
        // the cover is the owner's to read (a server-run bot has none: it counts as rock)
        if (body == ClimbBody.Terrain && Terrain != null && Terrain.TryGetCover(point, out var c)) cover = c;
        loose = Climbable.Loose(cover);
        if (Climbable.Face(body, normal.Y, cover)) return true;
        why = !Climbable.Body(body) ? $"not climbable ({body})"
            : !Climbable.Steep(normal.Y) ? (normal.Y > Climbable.SteepNormalY ? "not steep enough" : "overhang")
            : $"cover {cover}";
        return false;
    }

    /// <summary>What kind of thing a climbing ray hit.</summary>
    private static ClimbBody ClassifyClimb(GodotObject? collider)
    {
        if (collider is not CollisionObject3D body) return ClimbBody.NoClimb;
        if (body.IsInGroup(NoClimbGroup)) return ClimbBody.NoClimb;
        if (body is CharacterBody3D) return ClimbBody.Player;
        if (body is RigidBody3D or AnimatableBody3D) return ClimbBody.Moving;
        if (body is not StaticBody3D) return ClimbBody.NoClimb;
        var parent = body.GetParent();
        if (parent is World.TreeColliders || parent?.GetParent() is World.TreeColliders) return ClimbBody.Tree;
        // the terrain's own collision: a chunk's unnamed body (its buildings and roads have names)
        if (parent is Terrain.ChunkNode && body.Name != "BuildingBody" && body.Name != "RoadBody") return ClimbBody.Terrain;
        return ClimbBody.Static;
    }

    /// <summary>
    /// The walk's turn: pushing into a climbable face grabs it (in the air at once, on the ground after
    /// <see cref="ClimbGroundPush"/> s and only where the face rises past the mantle's reach). True when
    /// the climb began. An NPC or a bot asks with <paramref name="botClimb"/> instead of pushing.
    /// </summary>
    private bool TryBeginClimb(float dt, Vector3 direction, bool onFloor, bool botClimb)
    {
        bool pushing = direction != Vector3.Zero;
        if (!pushing || (Npc && !botClimb) || !ClimbingAllowed || _sliding || Downed || KnockedOut || _stunTimer > 0f || Indoors)
        {
            _climbPush = 0f;
            return false;
        }
        var chest = GlobalPosition + Vector3.Up * ClimbChest;
        if (!ProbeFace(chest, direction, FaceReach + 0.35f, out var point, out var normal, out bool loose, out string? why))
        {
            if (why != null) ClimbRefused = why;
            _climbPush = 0f;
            return false;
        }
        var flat = new Vector3(normal.X, 0, normal.Z);
        if (flat.LengthSquared() < 1e-4f || direction.Dot(-flat.Normalized()) < ClimbPushDot) { _climbPush = 0f; return false; }
        if (onFloor && !botClimb)
        {
            _climbPush += dt;
            if (_climbPush < ClimbGroundPush) return false;
            // a garden wall still wants Jump to mantle: the face must rise past the mantle's reach
            var high = GlobalPosition + Vector3.Up * (MantleMax + 0.2f);
            if (!ProbeFace(high, direction, 3f, out _, out _, out _, out _)) { _climbPush = 0f; return false; }
        }
        if (_stamina.Winded) { ClimbRefused = "winded"; return false; }
        BeginClimb(normal, loose);
        return true;
    }

    private void BeginClimb(Vector3 normal, bool loose)
    {
        if (_sliding) { _sliding = false; SetBodyHeight(StandHeight); }
        _climbing = true;
        _climbNormal = normal;
        _climbLoose = loose;
        _climbPush = 0f;
        _climbJumpLeft = 0f;
        _climbSlip = 0f;
        _climbJump = 0f;
        _fallSpeed = 0f;
        _wallJumps = 0;
        ClimbTime = 0f;
        ClimbRefused = null;
        DanceId = 0;
        FloorSnapLength = 0f;
        Velocity = Vector3.Zero;
        ClimbStarted?.Invoke();
    }

    /// <summary>Off the face (let go, kicked off, topped out, a ride): the walk takes over.</summary>
    private void EndClimb()
    {
        if (!_climbing) return;
        _climbing = false;
        if (ClimbTime > 0.5f) _climbHintSeen = true;
        if (_ride == null) FloorSnapLength = 0.5f;
        _climbJumpLeft = 0f;
        _climbJump = 0f;
        PoseKind = PoseStride;
        BodyPose = Transform3D.Identity;
        Anim = default;
    }

    /// <summary>
    /// The climb's turn in the on-foot physics, after the mantle and before the swim: true while
    /// climbing (the walk must not run).
    /// </summary>
    private bool ClimbPhysics(float dt, bool onFloor)
    {
        if (!_climbing) return false;
        if (!ClimbingAllowed || Indoors || KnockedOut || Downed)
        {
            EndClimb();
            return false;
        }
        ClimbTime += dt;

        bool keys = !Npc;
        var acts = keys ? default : FootActions?.Invoke() ?? default;
        var stick = keys ? PlayerInput.Move : Vector2.Zero;
        bool jumpDown = keys ? PlayerInput.Held(PlayerInput.Jump) : acts.Jump;
        if (keys && JumpTaken) jumpDown = false;
        bool jumpPressed = jumpDown && !_jumpHeld;
        _jumpHeld = jumpDown;
        bool crouchDown = keys ? PlayerInput.Held(PlayerInput.CrouchSlide) : acts.Crouch;
        bool crouchPressed = crouchDown && !_crouchHeld;
        _crouchHeld = crouchDown;
        if (_stunTimer > 0f) { _stunTimer -= dt; stick = Vector2.Zero; jumpPressed = false; }

        // the face's axes: up along it, right across it (seen from the climber)
        var n = _climbNormal;
        var up = (Vector3.Up - n * n.Y).Normalized();
        var right = up.Cross(n).Normalized();
        // the stick on the face: forward climbs, back goes down, sideways traverses
        float sUp = -stick.Y, sRight = stick.X;
        if (WalkControls?.Invoke() is { } walk)
        {
            // a scripted wish in the world: into the face climbs, away goes down
            var flatN = new Vector3(n.X, 0, n.Z).Normalized();
            sUp = Mathf.Clamp(-walk.Wish.Dot(flatN), -1f, 1f);
            sRight = Mathf.Clamp(walk.Wish.Dot(right), -1f, 1f);
        }
        if (Mathf.Abs(sUp) < 0.1f) sUp = 0f;
        if (Mathf.Abs(sRight) < 0.1f) sRight = 0f;
        float share = Mathf.Min(new Vector2(sUp, sRight).Length(), 1f);

        // --- let go, kick off -----------------------------------------------------------------
        if (crouchPressed)
        {
            ClimbRefused = "let go";
            EndClimb();
            Velocity = n * 0.6f;
            return true;
        }
        if (jumpPressed && sUp < -0.5f)
        {
            // back + jump: off the face, free, then an ordinary fall (the base jump rule opens the wingsuit)
            ClimbRefused = "kicked off";
            EndClimb();
            var outward = new Vector3(n.X, 0, n.Z).Normalized();
            Velocity = outward * KickOut + Vector3.Up * KickUp;
            _groundCoyote = 0f;
            _wallCoyote = 0f;
            _jumpBuffer = 0f;
            Jumped?.Invoke();
            return true;
        }

        // --- empty: the hands slide, then a fall ------------------------------------------------
        if (_climbSlip > 0f)
        {
            _climbSlip -= dt;
            Velocity = -up * 1.2f + n * 0.3f;
            MoveAndSlide();
            if (_climbSlip <= 0f)
            {
                ClimbRefused = "empty";
                EndClimb();
                Velocity = n * 0.6f + Vector3.Down;
            }
            return true;
        }

        // --- the face under the hands ------------------------------------------------------------
        var chest = GlobalPosition + Vector3.Up * ClimbChest;
        bool face = ProbeFace(chest, -n, FaceReach, out var point, out var hitN, out bool loose, out string? why);
        if (!face)
        {
            if (why == null)
            {
                // nothing at the chest: a lip. The mantle pulls the body over, or it holds below it
                var flatN = new Vector3(n.X, 0, n.Z);
                if (sUp > 0f && flatN.LengthSquared() > 1e-4f && TryBeginMantle(-flatN.Normalized(), 1.5f))
                {
                    ClimbRefused = "topped out";
                    EndClimb();
                    _mantling = true;   // EndClimb keeps the mantle TryBeginMantle started
                    return true;
                }
                // hips still on the face: hold (a cornice too high), traverse or let go
                var hips = GlobalPosition + Vector3.Up * 0.6f;
                if (!ProbeFace(hips, -n, FaceReach + 0.3f, out point, out hitN, out loose, out _))
                {
                    ClimbRefused = "nothing to hold";
                    EndClimb();
                    return true;
                }
                sUp = Mathf.Min(sUp, 0f);
                share = Mathf.Min(new Vector2(sUp, sRight).Length(), 1f);
                // the body is held from the hips' point, as if the chest's were there
                point += Vector3.Up * (ClimbChest - 0.6f);
            }
            else if (hitN.Y > Climbable.SteepNormalY)
            {
                // the face lies back into ground you can stand on: walk on
                EndClimb();
                return false;
            }
            else
            {
                // an overhang, ice, a tree, a vehicle: let go
                ClimbRefused = why;
                EndClimb();
                Velocity = n * 0.6f;
                return true;
            }
        }
        // follow the face's turn, eased so the height grid's 1 m facets do not jerk the body about
        _climbNormal = n = n.Slerp(hitN.Normalized(), 1f - Mathf.Exp(-12f * dt)).Normalized();
        up = (Vector3.Up - n * n.Y).Normalized();
        right = up.Cross(n).Normalized();
        _climbLoose = loose;

        // --- the climb-jump -----------------------------------------------------------------------
        if (jumpPressed && _climbJumpLeft <= 0f && _stamina.Pay(StaminaKind.ClimbJump, _climbLoose))
        {
            var along = up * Mathf.Max(sUp, 0f) + right * sRight;
            _climbJumpDir = along.LengthSquared() > 0.01f ? along.Normalized() : up;
            _climbJumpLeft = ClimbJumpTime;
            ClimbJumped?.Invoke();
        }

        // --- moving on the face --------------------------------------------------------------------
        Vector3 wish;
        bool jumping = _climbJumpLeft > 0f;
        if (jumping)
        {
            _climbJumpLeft -= dt;
            wish = _climbJumpDir * (ClimbJumpDist / ClimbJumpTime);
        }
        else wish = up * (sUp * (sUp > 0f ? ClimbUp : ClimbDown)) + right * (sRight * ClimbSide);
        _climbJump = Mathf.MoveToward(_climbJump, jumping ? 1f : 0f, dt / 0.15f);

        // the cost: per second times the stick's share, at least holding on's (a climb-jump paid already)
        float hold = Stamina.Rate(StaminaKind.ClimbHold);
        float rate = Mathf.Max(Stamina.Rate(StaminaKind.ClimbMove) * (jumping ? 0f : share), hold);
        if (!_stamina.Spend(StaminaKind.ClimbHold, dt, rate / hold, _climbLoose) || _stamina.Winded)
        {
            _climbSlip = ClimbSlipTime;
            _climbJumpLeft = 0f;
            ClimbSlipped?.Invoke();
        }

        // held ClimbStandOff off the face: a spring along the normal
        float off = (GlobalPosition + Vector3.Up * ClimbChest - point).Dot(n);
        var keep = n * Mathf.Clamp((ClimbStandOff - off) * 10f, -3f, 3f);
        Velocity = wish + keep;
        var before = GlobalPosition;
        MoveAndSlide();
        var moved = (GlobalPosition - before) / Mathf.Max(dt, 1e-4f);
        _climbMove = (moved - n * moved.Dot(n)).Length();
        _fallSpeed = 0f;

        // down onto ground you can stand on: walking again
        if (sUp < 0f && IsOnFloor() && !TooSteep(out _))
        {
            EndClimb();
            return true;
        }

        // the body faces the face (third person turns the node; the figure is turned by BodyPose either way)
        if (_thirdPerson) FaceTravel(dt, -new Vector3(n.X, 0, n.Z).Normalized());
        UpdateCameraFeel(dt, false, false);
        return true;
    }

    /// <summary>
    /// The owner's climbing pose (<see cref="PublishFootPose"/>): the limb phase from the distance
    /// climbed, the climb-jump's stretch, and the figure leant into the face.
    /// </summary>
    private void PublishClimbPose(float dt)
    {
        DanceId = 0;
        _airTime = 0f;
        _climbPhase = HumanMeshBuilder.AdvanceWallClimb(_climbPhase, _climbMove, dt);
        PoseKind = PoseWallClimb;
        Anim = new Vector4(_climbMove, _climbPhase, _climbJump, 0f);
        BodyPose = ClimbBodyPose(_climbNormal, Rotation.Y);
    }

    /// <summary>
    /// The figure turned to face the wall whatever the node's yaw (first person keeps the node on the
    /// view), and leant about the chest so it lies along the face: a 60° slab and a vertical wall read
    /// differently. Pure in the replicated numbers, so every peer draws the same.
    /// </summary>
    internal static Transform3D ClimbBodyPose(Vector3 n, float nodeYaw)
    {
        var flat = new Vector3(n.X, 0, n.Z);
        float faceYaw = flat.LengthSquared() > 1e-4f ? Mathf.Atan2(flat.X, flat.Z) : nodeYaw;
        float lean = -Mathf.Asin(Mathf.Clamp(n.Y, -0.5f, 0.85f));
        var pivot = new Vector3(0, ClimbChest, 0);
        return new Transform3D(new Basis(Vector3.Up, faceYaw - nodeYaw), Vector3.Zero)
               * new Transform3D(Basis.Identity, pivot)
               * new Transform3D(new Basis(Vector3.Right, lean), Vector3.Zero)
               * new Transform3D(Basis.Identity, -pivot);
    }

    /// <summary>
    /// Draws the climbing figure from the published pose, owner and remote alike (from
    /// <see cref="ApplyFootPose"/>). A remote copy advances the limbs between updates.
    /// </summary>
    private void ApplyClimbFigure()
    {
        if (_walker == null) return;
        float dt = (float)GetProcessDeltaTime();
        if (IsMultiplayerAuthority()) _climbDrawPhase = _climbPhase;
        else if (Anim.Y != _climbSeenPhase) _climbDrawPhase = _climbSeenPhase = Anim.Y;
        else _climbDrawPhase = HumanMeshBuilder.AdvanceWallClimb(_climbDrawPhase, Anim.X, dt);
        _poseWait += dt;
        var key = new FootPoseKey(_walker, PoseWallClimb, Mathf.Round(Anim.Z * 20f), Mathf.Round(_climbDrawPhase * 64f) / 64f,
            ItemArmPose.None, 0f, null, Hat, _walkPalette, HumanMeshBuilder.SmoothFigures);
        if (key != _poseKey && !HoldRemoteFigure())
        {
            _poseKey = key;
            _mountsKey = key;
            _poseWait = 0f;
            _walker.Show(new FigureLook(_walkPalette, Hat: Hat));
            HumanMeshBuilder.PoseWallClimb(_walker, _climbDrawPhase, Anim.Z);
            _poseMounts = HumanMeshBuilder.MountsForWallClimb(_climbDrawPhase, Anim.Z);
        }
        _walker.Transform = BodyPose * FlinchPose(dt);
        // holstered: nothing in the hands while climbing
        HandLocal = null;
        PlaceBack(_poseMounts);
    }
}
