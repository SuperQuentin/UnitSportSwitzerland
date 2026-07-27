using Godot;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// First-person on-foot controller tuned for human scale: WASD (physical keys), mouse
/// look, Shift to run, Space to jump, Ctrl to slide, Space against a wall to wall jump.
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
    [Export] public float WalkSpeed { get; set; } = 1.6f;   // ~5.8 km/h, brisk walk
    [Export] public float RunSpeed { get; set; } = 4.6f;    // ~16.6 km/h, steady run
    [Export] public float JumpVelocity { get; set; } = 4.2f;

    /// <summary>Speed a slide is entered at, if you were not already going faster.</summary>
    [Export] public float SlideSpeed { get; set; } = 7.0f;

    /// <summary>Upward kick of a wall jump. Slightly over a standing jump — it has to clear a lip.</summary>
    [Export] public float WallJumpUp { get; set; } = 4.6f;

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
    private int _wallJumps;
    private Vector3 _lastWallNormal = Vector3.Zero;
    private float _wallCoyote;
    private Vector3 _coyoteNormal = Vector3.Zero;
    private float _jumpBuffer;

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

        // a teleport mid-slide would otherwise land you crouched in the new place, with the
        // capsule still short and no ground contact to end the slide against
        if (_sliding && _body is not null)
        {
            _sliding = false;
            _slideCooldown = 0;
            SetBodyHeight(StandHeight);
        }
    }

    public override void _Ready()
    {
        // authority pushes its transform to everyone else (server relays)
        var replication = new SceneReplicationConfig();
        replication.AddProperty(".:position");
        replication.AddProperty(".:rotation");
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
        AddChild(sync);

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
                Far = 20000f,
                Fov = BaseFov,
            };
            AddChild(_camera);
            _camera.Current = true;
        }
        else
        {
            // remote player: a visible marker, transform comes from the synchronizer
            AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = BodyRadius, Height = StandHeight },
                Position = new Vector3(0, StandHeight * 0.5f, 0),
            });
            SetPhysicsProcess(false);
            SetProcessUnhandledInput(false);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return;

        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.Relative.X * 0.0022f);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.0022f,
                -Mathf.Pi / 2 + 0.01f, Mathf.Pi / 2 - 0.01f);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // drop onto the terrain surface once its height data is available
        if (!_placed)
        {
            if (Terrain == null || !Terrain.TryGetHeight(GlobalPosition, out float g))
                return;
            GlobalPosition = new Vector3(GlobalPosition.X, Mathf.Max(GlobalPosition.Y, g + 1f), GlobalPosition.Z);
            _placed = true;
        }

        float dt = (float)delta;
        var velocity = Velocity;
        bool onFloor = IsOnFloor();

        // Movement reads physical keys directly, so a focused text field has to be checked
        // explicitly or typing in chat walks the player around.
        bool typing = UnitSport.Core.UiFocus.TextEntryActive;

        var input = Vector2.Zero;
        if (!typing)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1;
        }

        bool running = !typing && Input.IsPhysicalKeyPressed(Key.Shift);
        bool crouchHeld = !typing
            && (Input.IsPhysicalKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.C));

        // Wall jumps and slide entries need the *edge*, not the state: the ground jump below
        // polls, so holding Space bunny-hops, and a polled wall jump would rocket you up a
        // cliff one physics frame at a time.
        //
        // Ctrl is edge-triggered for a related reason found by measuring it: a spent slide
        // ends at ~2 m/s, the walk accelerates you back over the entry threshold in about a
        // second, and a held key starts the next one — so holding Ctrl became a permanent
        // 7 m/s crouch-run. A slide is one move; you re-press to take another.
        bool spaceDown = !typing && Input.IsPhysicalKeyPressed(Key.Space);
        bool jumpPressed = spaceDown && !_jumpHeld;
        _jumpHeld = spaceDown;

        bool crouchPressed = crouchHeld && !_crouchHeld;
        _crouchHeld = crouchHeld;

        var direction = (Basis * new Vector3(input.X, 0, input.Y)).Normalized();
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
                var lateral = Basis.X * input.X;
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
                velocity.Y = JumpVelocity;   // horizontal momentum is kept: only Y changes
        }

        // --- gravity and the ordinary jump -------------------------------------------
        if (!onFloor)
        {
            velocity.Y -= Gravity * dt;
            _fallSpeed = Mathf.Max(_fallSpeed, -velocity.Y);

            if (_jumpBuffer > 0 && TryWallJump(ref velocity, direction)) _jumpBuffer = 0;
        }
        else
        {
            _wallJumps = 0;
            _lastWallNormal = Vector3.Zero;
            if (!_sliding && spaceDown)
            {
                velocity.Y = JumpVelocity;
                _jumpBuffer = 0;   // spent here, so it cannot also fire a wall jump on the way up
            }
        }

        // --- ordinary walking / running ----------------------------------------------
        if (!_sliding)
        {
            float speed = running ? RunSpeed : WalkSpeed;

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

        UpdateCameraFeel(dt, running, onFloor);

        // safety net: never end up under the terrain surface
        if (Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float ground)
            && GlobalPosition.Y < ground - 2f)
        {
            _sinceSnapWarning += delta;
            if (_sinceSnapWarning > 2)
            {
                _sinceSnapWarning = 0;
                GD.Print($"[player] {Name} below terrain ({GlobalPosition.Y:F1} < {ground:F1}), snapping up");
            }
            GlobalPosition = new Vector3(GlobalPosition.X, ground + 1f, GlobalPosition.Z);
            Velocity = Vector3.Zero;
        }
    }

    /// <summary>A/D as -1..1, for the slide's camera bank. Zero while a text field has the keyboard.</summary>
    private static float SteerInput()
    {
        if (UnitSport.Core.UiFocus.TextEntryActive) return 0;
        float x = 0;
        if (Input.IsPhysicalKeyPressed(Key.A)) x -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D)) x += 1;
        return x;
    }

    /// <summary>
    /// Drops into a slide, launching at <see cref="SlideSpeed"/> unless you arrived faster —
    /// a slide must never cost you speed you already had, or chaining one off a wall jump
    /// would be a downgrade.
    /// </summary>
    private void BeginSlide(ref Vector3 velocity, float flatSpeed)
    {
        _sliding = true;
        _slideTime = 0;

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

        _camera.Position = new Vector3(bobSide, eye + bobUp + _landingDip, 0);
        _camera.Rotation = new Vector3(_pitch, 0, roll + lean);

        // slight FOV widening while running reads as effort without inducing sickness;
        // a slide pushes it further, because the speed is the whole reward
        float targetFov = running && groundSpeed > WalkSpeed * 1.2f ? RunFov : BaseFov;
        targetFov = Mathf.Lerp(targetFov, SlideFov, _slideBlend);
        _camera.Fov = Mathf.Lerp(_camera.Fov, targetFov, 1f - Mathf.Exp(-5f * dt));
    }
}
