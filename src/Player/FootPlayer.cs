using Godot;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// First-person on-foot controller tuned for human scale: WASD (physical keys), mouse
/// look, Shift to run, Space to jump.
///
/// Speeds are deliberately realistic rather than arcade — at the old 6–14 m/s a player
/// covered a 10 m building's frontage in under a second, which made the whole world feel
/// miniature. Walking at ~1.6 m/s against a 10 m facade is what sells the scale.
/// </summary>
public partial class FootPlayer : CharacterBody3D
{
    [Export] public float WalkSpeed { get; set; } = 1.6f;   // ~5.8 km/h, brisk walk
    [Export] public float RunSpeed { get; set; } = 4.6f;    // ~16.6 km/h, steady run
    [Export] public float JumpVelocity { get; set; } = 4.2f;

    public ChunkManager? Terrain { get; set; }

    private const float Gravity = 9.81f;
    private const float EyeHeight = 1.68f;   // average adult eye level
    private const float BaseFov = 68f;
    private const float RunFov = 76f;

    /// <summary>Ground speed is scaled down on steep ground — this is alpine terrain.</summary>
    private const float MaxClimbSlowdown = 0.45f;

    private Camera3D? _camera;
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

    public Camera3D Camera => _camera!;

    /// <summary>Rises to 1 with each footfall — hook sounds or footstep effects here.</summary>
    public float StepPhase => _bobPhase;

    /// <summary>
    /// Re-runs the drop-onto-the-ground pass. Called after a teleport, where the body has
    /// been put down over terrain that has not streamed in yet.
    /// </summary>
    public void RequestReplacement() => _placed = false;

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

        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.32f, Height = 1.78f },
            Position = new Vector3(0, 0.89f, 0),
        });

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
                Mesh = new CapsuleMesh { Radius = 0.32f, Height = 1.78f },
                Position = new Vector3(0, 0.89f, 0),
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

        if (!onFloor)
        {
            velocity.Y -= Gravity * dt;
            _fallSpeed = Mathf.Max(_fallSpeed, -velocity.Y);
        }
        else if (!typing && Input.IsPhysicalKeyPressed(Key.Space))
        {
            velocity.Y = JumpVelocity;
        }

        var input = Vector2.Zero;
        if (!typing)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1;
        }

        bool running = !typing && Input.IsPhysicalKeyPressed(Key.Shift);
        float speed = running ? RunSpeed : WalkSpeed;
        var direction = (Basis * new Vector3(input.X, 0, input.Y)).Normalized();

        // climbing costs speed: scale by how much of the move is uphill
        if (onFloor && direction != Vector3.Zero)
        {
            var floorNormal = GetFloorNormal();
            float climb = -direction.Dot(new Vector3(floorNormal.X, 0, floorNormal.Z));
            if (climb > 0)
                speed *= Mathf.Lerp(1f, MaxClimbSlowdown, Mathf.Clamp(climb * 1.6f, 0f, 1f));
        }

        // ease into the target velocity rather than snapping, so starts and stops read
        float accel = onFloor ? 12f : 2.5f;
        velocity.X = Mathf.MoveToward(velocity.X, direction.X * speed, accel * dt);
        velocity.Z = Mathf.MoveToward(velocity.Z, direction.Z * speed, accel * dt);

        Velocity = velocity;
        MoveAndSlide();

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

    /// <summary>
    /// Head bob, landing impact and a running FOV kick. All of it is camera-only: the
    /// body never moves, so none of this can push the player through geometry.
    /// </summary>
    private void UpdateCameraFeel(float dt, bool running, bool onFloor)
    {
        if (_camera == null) return;

        float groundSpeed = new Vector2(Velocity.X, Velocity.Z).Length();
        _speedSmoothed = Mathf.Lerp(_speedSmoothed, groundSpeed, 1f - Mathf.Exp(-8f * dt));

        // step cadence scales with speed, so running steps land faster and harder
        if (onFloor && groundSpeed > 0.15f)
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

        _camera.Position = new Vector3(bobSide, EyeHeight + bobUp + _landingDip, 0);
        _camera.Rotation = new Vector3(_pitch, 0, roll);

        // slight FOV widening while running reads as effort without inducing sickness
        float targetFov = running && groundSpeed > WalkSpeed * 1.2f ? RunFov : BaseFov;
        _camera.Fov = Mathf.Lerp(_camera.Fov, targetFov, 1f - Mathf.Exp(-5f * dt));
    }
}
