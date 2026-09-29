using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>Controls as a flying thing sees them.</summary>
/// <param name="Stick">Left stick / WASD: x right, y back (forward is −1), up to length 1.</param>
/// <param name="Up">Climb: Space / A, or the right trigger. 0..1.</param>
/// <param name="Down">Descend: Ctrl / B, or the left trigger. 0..1.</param>
/// <param name="LeverUp">Throttle lever forward: Shift / L3, or the right trigger.</param>
/// <param name="LeverDown">Throttle lever back: Ctrl / B, or the left trigger.</param>
/// <param name="Action">Jump pressed this step (edge): open the canopy, launch, flare.</param>
/// <param name="Effort">Shift / X: the fast setting, whatever that means for the craft.</param>
/// <param name="ViewYaw">Where the camera looks, for craft that turn to follow it.</param>
/// <param name="Engine">The engine is running. Off: a helicopter autorotates, a plane glides.</param>
/// <param name="Piloted">Someone is at the controls. An empty helicopter does not autorotate —
/// that takes a pilot working the collective — it falls.</param>
public readonly record struct FlightInput(Vector2 Stick, float Up, float Down, float LeverUp,
    float LeverDown, bool Action, bool Effort, float ViewYaw, bool Engine = true, bool Piloted = true);

/// <param name="OnFloor">Touching walkable ground.</param>
/// <param name="Clearance">Height above the terrain surface, m (buildings not counted).</param>
public readonly record struct FlightEnv(bool OnFloor, float Clearance);

/// <summary>What a flight step asks the player to do next.</summary>
public enum FlightEvent
{
    None,
    OpenCanopy,
    Landed,
    Crashed,
}

/// <summary>
/// A flying craft's state between steps. Unlike <see cref="RideMotion"/> this is a full 3D
/// velocity and orientation: a ground vehicle goes where it points along the ground, a flying
/// one climbs, dives and banks, and none of that fits in a speed and a heading.
/// </summary>
public struct FlightMotion
{
    public Vector3 Velocity;
    /// <summary>Heading, radians about +Y: what the player body is turned to.</summary>
    public float Yaw;
    /// <summary>The craft's orientation in world space: X right, Y up, −Z nose.</summary>
    public Basis Attitude;
    public float Bank;
    /// <summary>A craft-specific control state: lift coefficient, throttle lever, rotor spool.</summary>
    public float Control;
    public float Spool;
    public float Spin;
    /// <summary>
    /// Speed along the nose, kept as its own state (the plane). Re-deriving it each step as
    /// velocity·nose fed the stall sink back in as airspeed once the nose dropped — measured
    /// 112 to 394 km/h in two seconds of stall recovery.
    /// </summary>
    public float Airspeed;
}

/// <summary>
/// Something the player flies. Shares <see cref="Rideable"/>'s place in the picker, the network
/// and the body, but not its physics: <see cref="Rideable.Step"/> is a speed along a heading,
/// and a craft owns a 3D velocity instead (<see cref="Fly"/>).
///
/// <para>
/// All of these are arcade models built on real shapes — a lift and drag polar for the
/// wingsuit, a glide ratio for the canopies, a coordinated turn for the plane — so the numbers
/// that come out are plausible without anything feeling like homework: a wingsuit glides ~2.8:1
/// at ~140 km/h, a paraglider ~9:1 at ~38 km/h.
/// </para>
/// </summary>
public abstract class Flyer : Rideable
{
    public sealed override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion) { }

    public abstract FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m);

    /// <summary>Speed lost to an obstacle in one step that counts as a crash, m/s.</summary>
    public virtual float CrashSpeed => 14f;

    /// <summary>The look input turns the craft (helicopter) rather than looking around it.</summary>
    public virtual bool LookSteers => false;

    /// <summary>Chase camera distance and height, and the point on the craft it looks at.</summary>
    public virtual float CameraDistance => 7f;
    public virtual float CameraHeight => 2f;
    public virtual float CameraPivot => 1.2f;

    /// <summary>The camera follows the nose (plane, wingsuit), or only the heading (level craft).</summary>
    public virtual bool CameraFollowsPitch => true;

    /// <summary>Rotation centre of the visual, in its own frame; the craft pivots about it.</summary>
    public virtual Vector3 Pivot => new(0, 1f, 0);

    /// <summary>From the mesh's authored pose to "level, nose forward" (the wingsuit lies prone).</summary>
    public virtual Basis Fix => Basis.Identity;

    /// <summary>Everyday and exciting speeds, m/s, for the feel layer.</summary>
    public virtual (float Calm, float Fast) Thrill => (20f, 45f);

    public override float DismountSpeed => 1.5f;

    /// <summary>Starts a flight from whatever the player was doing, keeping their momentum.</summary>
    public virtual void Begin(ref FlightMotion m, Vector3 velocity, float yaw)
    {
        m = new FlightMotion
        {
            Velocity = velocity,
            Yaw = yaw,
            Attitude = new Basis(Vector3.Up, yaw),
        };
    }

    /// <summary>Spinning parts — rotors, propellers. Per rendered frame.</summary>
    public virtual void AnimateFlight(Node3D visual, in FlightMotion m, float dt) { }

    /// <summary>Spool and throttle: what spins the rotor or prop and what the engine sounds like.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(flight.Spool, flight.Control, 0, 0);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt) =>
        AnimateFlight(visual, new FlightMotion { Spool = pose.X, Control = pose.Y }, dt);

    /// <summary>Places the visual at the craft's attitude, relative to the yaw-only body.</summary>
    public void Pose(Node3D visual, float bodyYaw, in FlightMotion m)
    {
        var attitude = m.Attitude == default ? Basis.Identity : m.Attitude;
        var local = new Basis(Vector3.Up, -bodyYaw) * attitude * Fix;
        visual.Transform = new Transform3D(local, Pivot - local * Pivot);
    }

    /// <summary>Forward for the chase camera, before the player's free look is added.</summary>
    public virtual Vector3 CameraForward(in FlightMotion m) =>
        CameraFollowsPitch && m.Attitude != default ? -m.Attitude.Z : Heading(m.Yaw);

    protected static Vector3 Heading(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));

    /// <summary>
    /// A right-handed orientation from a nose direction and an up hint (X right, Y up, −Z nose).
    /// right = forward × up — the other order is a mirror image with a determinant of −1, the
    /// bug that once rendered the whole replay world reversed.
    /// </summary>
    public static Basis Orient(Vector3 forward, Vector3 upHint, Vector3 fallbackUp)
    {
        var f = forward.Normalized();
        var right = f.Cross(upHint);
        if (right.LengthSquared() < 0.0025f) right = f.Cross(fallbackUp);
        if (right.LengthSquared() < 1e-6f) return Basis.Identity;
        right = right.Normalized();
        var up = right.Cross(f).Normalized();
        return new Basis(right, up, -f);
    }

    protected static float Approach(float value, float target, float rate, float dt) =>
        value + (target - value) * (1f - Mathf.Exp(-rate * dt));
}

// ============================================================================================

/// <summary>
/// A wingsuit, deployed by pressing Jump while falling off something high (base jump). A point
/// mass with a lift and drag polar: lift is perpendicular to the airflow and tilted by the bank,
/// drag opposes it, gravity does the rest. That is what makes it feel right with no scripting —
/// a fall pulls out into a glide on its own, a dive trades height for speed, a flare trades it
/// back and stalls if held.
/// </summary>
public sealed class Wingsuit : Flyer
{
    public override RideKind Kind => RideKind.Wingsuit;
    public override string Label => "Wingsuit";
    public override string Blurb => "Jump off a cliff, then Jump again to fly";

    private const float Mass = 85f, Area = 1.5f, AirDensity = 1.1f;
    /// <summary>Zero-lift drag and induced-drag factor: best glide ~2.9 at ~38 m/s.</summary>
    private const float Cd0 = 0.12f, InducedK = 0.25f;
    private const float ClDive = 0.12f, ClTrim = 0.7f, ClFlare = 1.3f;
    private const float MaxBank = 1.0f;

    public override float CrashSpeed => 12f;
    public override float CameraDistance => 5.5f;
    public override float CameraHeight => 1.2f;
    public override float CameraPivot => 1.0f;
    public override Basis Fix => new(Vector3.Right, -Mathf.Pi / 2);   // lie the upright figure prone
    public override (float Calm, float Fast) Thrill => (30f, 55f);
    public override float BaseFov => 75f;
    public override float MaxFov => 105f;
    public override float FovSpeed => 60f;

    public override Node3D BuildVisual(int riderIndex) => new MeshInstance3D
    {
        Name = "Wingsuit",
        Mesh = AircraftMeshBuilder.Wingsuit(HumanPalette.ForRider(riderIndex)),
        MaterialOverride = HumanMeshBuilder.Material(),
    };

    public override void Begin(ref FlightMotion m, Vector3 velocity, float yaw)
    {
        base.Begin(ref m, velocity, yaw);
        m.Control = ClTrim;
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        if (env.OnFloor) return m.Velocity.Length() > CrashSpeed ? FlightEvent.Crashed : FlightEvent.Landed;
        if (input.Action) return FlightEvent.OpenCanopy;

        // stick forward dives, back flares; the lift coefficient eases, so the suit does too
        float cl = input.Stick.Y < 0
            ? Mathf.Lerp(ClTrim, ClDive, -input.Stick.Y)
            : Mathf.Lerp(ClTrim, ClFlare, input.Stick.Y);
        m.Control = Approach(m.Control, cl, 3f, dt);
        m.Bank = Approach(m.Bank, input.Stick.X * MaxBank, 3.5f, dt);

        var v = m.Velocity;
        float speed = v.Length();
        var heading = Heading(m.Yaw);
        var flow = speed > 0.5f ? v / speed : heading;

        // lift is perpendicular to the airflow; with the flow vertical (just jumped), the heading
        // decides which way "up" the suit points — so a fall pulls out forward, not sideways
        var right = flow.Cross(Vector3.Up);
        if (right.LengthSquared() < 0.01f) right = heading.Cross(Vector3.Up);
        right = right.Normalized();
        var lift0 = right.Cross(flow).Normalized();
        var liftDir = lift0 * Mathf.Cos(m.Bank) + right * Mathf.Sin(m.Bank);

        float q = 0.5f * AirDensity * speed * speed * Area;
        float cd = Cd0 + InducedK * m.Control * m.Control;
        var accel = Vector3.Down * Gravity
            + liftDir * (q * m.Control / Mass)
            - flow * (q * cd / Mass);

        // Phugoid damping. A bare lift-and-drag point mass porpoises for ever — measured
        // hands-off: dive to -36 m/s, zoom back up to a 13:1 "glide", repeat — because nothing
        // in it resists a change of flight path. A real flyer's body does exactly that, so the
        // sink is pulled toward the polar's steady glide for the current setting. A deliberate
        // flare still zooms: this only stops the oscillation the pilot did not ask for.
        float horizontal = new Vector2(v.X, v.Z).Length();
        float steadySink = -horizontal * cd / Mathf.Max(m.Control, 0.05f);
        if (horizontal > 15f) accel.Y += (steadySink - v.Y) * 0.6f;
        m.Velocity = v + accel * dt;

        var flat = new Vector2(m.Velocity.X, m.Velocity.Z);
        if (flat.Length() > 3f) m.Yaw = Mathf.Atan2(-flat.X, -flat.Y);

        m.Attitude = Orient(speed > 0.5f ? flow : heading, liftDir, heading);
        return FlightEvent.None;
    }
}

// ============================================================================================

/// <summary>
/// A canopy: the base jumper's parachute, or a paraglider flown from the picker. Modelled by its
/// glide — a trim airspeed and sink, slower and flatter on the brakes, faster and steeper on the
/// speed bar — rather than by airfoil forces, because a canopy's pilot flies exactly those three
/// settings and nothing else. Velocity eases toward the target, which is the opening shock when
/// it is deployed at wingsuit speed and the pendulum feel of a turn.
/// </summary>
public class Canopy : Flyer
{
    private readonly bool _paraglider;

    public Canopy(bool paraglider) => _paraglider = paraglider;

    public override RideKind Kind => _paraglider ? RideKind.Paraglider : RideKind.Parachute;
    public override string Label => _paraglider ? "Paraglider" : "Parachute";
    public override string Blurb => _paraglider
        ? "W run, Jump to launch; stick steers, back brakes, forward speed bar"
        : "Stick steers, back brakes and flares";

    private float TrimSpeed => _paraglider ? 10.5f : 9f;
    private float TrimSink => _paraglider ? 1.15f : 4.2f;
    private float BrakeSpeed => _paraglider ? 6.5f : 5f;
    private float BrakeSink => _paraglider ? 1.4f : 2.6f;
    private float BarSpeed => _paraglider ? 14.5f : 10f;
    private float BarSink => _paraglider ? 1.9f : 4.8f;
    private float TurnRate => _paraglider ? 0.9f : 1.2f;

    public override float CrashSpeed => 9f;
    public override float CameraDistance => _paraglider ? 13f : 10f;
    public override float CameraHeight => _paraglider ? 4f : 3.5f;
    public override float CameraPivot => _paraglider ? 3f : 2.5f;
    public override bool CameraFollowsPitch => false;
    /// <summary>Swings from the wing: the pilot hangs below the pivot like a pendulum.</summary>
    public override Vector3 Pivot => new(0, _paraglider ? 7f : 5f, 0);
    public override (float Calm, float Fast) Thrill => (9f, 16f);
    public override float BaseFov => 70f;
    public override float MaxFov => 82f;
    public override float FovSpeed => 16f;

    public override Node3D BuildVisual(int riderIndex) => new MeshInstance3D
    {
        Name = Label,
        Mesh = AircraftMeshBuilder.Canopy(HumanPalette.ForRider(riderIndex), _paraglider),
        MaterialOverride = HumanMeshBuilder.Material(),
    };

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        var heading = Heading(m.Yaw);

        if (env.OnFloor)
        {
            // A parachute's flight ends at the ground. A paraglider is still worn: run it up to
            // flying speed and press Jump to launch again.
            if (!_paraglider) return FlightEvent.Landed;

            float run = Mathf.Max(0f, -input.Stick.Y) * 5.5f;
            m.Yaw -= input.Stick.X * 1.6f * dt;
            heading = Heading(m.Yaw);
            var ground = new Vector3(m.Velocity.X, 0, m.Velocity.Z).MoveToward(heading * run, 6f * dt);
            m.Velocity = new Vector3(ground.X, Mathf.Min(m.Velocity.Y, 0f) - Gravity * dt, ground.Z);
            if (input.Action && ground.Length() > 3.5f)
                m.Velocity = heading * TrimSpeed * 0.7f + Vector3.Up * 2.5f;   // off the hill
            m.Bank = Approach(m.Bank, 0f, 4f, dt);
            m.Attitude = new Basis(Vector3.Up, m.Yaw);
            return FlightEvent.None;
        }

        float brake = Mathf.Max(0f, input.Stick.Y);
        float bar = Mathf.Max(0f, -input.Stick.Y);
        float turn = input.Stick.X;

        float airspeed = Mathf.Lerp(TrimSpeed, BrakeSpeed, brake) + bar * (BarSpeed - TrimSpeed);
        float sink = Mathf.Lerp(TrimSink, BrakeSink, brake) + bar * (BarSink - TrimSink)
            + Mathf.Abs(turn) * (_paraglider ? 1.3f : 1.8f);   // a turn costs height

        m.Yaw -= turn * TurnRate * dt;
        heading = Heading(m.Yaw);
        var target = heading * airspeed + Vector3.Down * sink;
        m.Velocity = m.Velocity.Lerp(target, 1f - Mathf.Exp(-1.6f * dt));

        // the pilot swings out under the wing in a turn
        m.Bank = Approach(m.Bank, turn * 0.45f, 2.5f, dt);
        m.Attitude = new Basis(Vector3.Up, m.Yaw) * new Basis(Vector3.Back, -m.Bank);
        return FlightEvent.None;
    }
}

// ============================================================================================

/// <summary>
/// An arcade helicopter, flown the way every game does it: the camera sets the heading, the
/// stick moves you along it, Space / RT climbs and Ctrl / LT descends, and letting go holds
/// altitude. The body tilts into its acceleration so it still reads as a helicopter.
/// </summary>
public sealed class Helicopter : Flyer
{
    public override RideKind Kind => RideKind.Helicopter;
    public override string Label => "Helicopter";
    public override string Blurb => "Look to turn, stick to fly, Space/RT up, Ctrl/LT down, Shift fast";

    private const float Cruise = 42f, Dash = 68f, Strafe = 16f, Climb = 9f;
    private const float Accel = 9f, VerticalAccel = 12f, YawRate = 1.8f;

    public override bool LookSteers => true;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override float BodyRadius => 1.0f;
    public override float BodyHeight => 2.6f;
    public override (Vector3 Centre, Vector3 Size) ParkedBox => (new Vector3(0, 1.3f, 2.0f), new Vector3(2.0f, 2.6f, 8.4f));

    /// <summary>
    /// Spool at which the rotor holds the machine up. Below it lift fades into autorotation:
    /// the disc still turns in the airflow and slows the fall, but nothing climbs.
    /// </summary>
    private const float LiftSpool = 0.6f;
    private const float AutorotationSink = 9f;
    public override float CrashSpeed => 11f;
    public override float CameraDistance => 13f;
    public override float CameraHeight => 3.5f;
    public override float CameraPivot => 1.8f;
    public override bool CameraFollowsPitch => false;
    public override Vector3 Pivot => new(0, 1.4f, 0);
    public override (float Calm, float Fast) Thrill => (25f, 60f);
    public override float BaseFov => 72f;
    public override float MaxFov => 90f;
    public override float FovSpeed => 60f;

    public override Node3D BuildVisual(int riderIndex)
    {
        var paint = Color.FromHsv((riderIndex * 0.37f) % 1f, 0.55f, 0.75f);
        var root = new Node3D { Name = "Helicopter" };
        var material = HumanMeshBuilder.Material();
        root.AddChild(new MeshInstance3D { Mesh = AircraftMeshBuilder.Helicopter(paint), MaterialOverride = material });
        root.AddChild(new MeshInstance3D
        {
            Name = "Rotor",
            Mesh = AircraftMeshBuilder.Rotor(),
            MaterialOverride = material,
            Position = AircraftMeshBuilder.Flip(AircraftMeshBuilder.RotorHub),
        });
        return root;
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        float lift = input.Up - input.Down;
        bool parked = env.OnFloor && lift <= 0.05f;
        // Engine off: the rotor winds down. Starting it: about three seconds to flying speed,
        // which is short for a real turbine and long enough to be felt.
        float spoolTarget = !input.Engine ? 0f
            : parked && input.Stick.LengthSquared() < 0.01f ? 0.35f : 1f;
        m.Spool = Mathf.MoveToward(m.Spool, spoolTarget, (input.Engine ? 0.3f : 0.18f) * dt);
        float authority = Mathf.Clamp((m.Spool - LiftSpool) / (1f - LiftSpool), 0f, 1f);
        if (authority <= 0f) parked = env.OnFloor;

        // the nose swings round to where the camera looks, at a helicopter's pace
        if (!parked)
            m.Yaw += Mathf.Clamp(Mathf.Wrap(input.ViewYaw - m.Yaw, -Mathf.Pi, Mathf.Pi), -YawRate * dt, YawRate * dt);

        var fwd = Heading(m.Yaw);
        var right = fwd.Cross(Vector3.Up);
        var flat = new Vector3(m.Velocity.X, 0, m.Velocity.Z);
        var wanted = parked ? Vector3.Zero
            : (fwd * (-input.Stick.Y * (input.Effort ? Dash : Cruise)) + right * (input.Stick.X * Strafe)) * authority;
        var before = flat;
        flat = flat.MoveToward(wanted, Accel * dt);
        var accel = (flat - before) / Mathf.Max(dt, 1e-4f);

        // with the rotor below lifting speed, the collective does nothing and it sinks
        float vyTarget = Mathf.Lerp(-AutorotationSink, lift * Climb, authority);
        float vy = parked ? Mathf.Min(m.Velocity.Y, 0f) - Gravity * dt
            : !input.Piloted && authority <= 0f ? m.Velocity.Y - Gravity * dt * 0.85f
            : Mathf.MoveToward(m.Velocity.Y, vyTarget, VerticalAccel * dt);
        m.Velocity = new Vector3(flat.X, vy, flat.Z);

        // nose down to go forward, bank into a sideslip — the tilt is the thrust vector
        float along = flat.Dot(fwd), across = flat.Dot(right);
        float pitch = Mathf.Clamp(-along / Dash * 0.35f - accel.Dot(fwd) * 0.02f, -0.45f, 0.3f);
        float roll = Mathf.Clamp(across / Strafe * 0.25f + accel.Dot(right) * 0.02f, -0.4f, 0.4f);
        m.Control = Approach(m.Control, pitch, 3f, dt);
        m.Bank = Approach(m.Bank, roll, 3f, dt);
        m.Attitude = new Basis(Vector3.Up, m.Yaw) * new Basis(Vector3.Right, m.Control) * new Basis(Vector3.Back, -m.Bank);
        return FlightEvent.None;
    }

    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        if (visual.GetNodeOrNull<Node3D>("Rotor") is not { } rotor) return;
        rotor.RotateY(m.Spool * 38f * dt);   // ~6 rev/s at full spool
    }
}

// ============================================================================================

/// <summary>
/// An arcade light plane. The throttle is a lever (Shift/RT forward, Ctrl/LT back) because
/// nobody wants to hold a key for a whole flight; the stick flies it — back to climb, sideways to
/// roll — and the heading follows the bank as a coordinated turn, so there is no rudder to learn.
/// Energy is honest: climbing costs speed, diving buys it, and below ~80 km/h it stalls.
/// </summary>
public sealed class Plane : Flyer
{
    public override RideKind Kind => RideKind.Plane;
    public override string Label => "Plane";
    public override string Blurb => "Shift/RT throttle up, Ctrl/LT down; stick pitches and rolls";

    /// <summary>
    /// m/s² at full throttle — well under gravity, as for any light plane. At 11 (the first
    /// version) thrust beat weight and a pull-up became a vertical climb that never ran out of
    /// speed, a fighter jet in a Cessna's body. Now climbing genuinely costs airspeed.
    /// </summary>
    private const float MaxThrust = 4.5f;
    private const float DragK = 0.0014f;          // level top speed ~57 m/s (205 km/h)
    private const float StallSpeed = 22f;
    private const float RotateSpeed = 24f;
    private const float PitchRate = 0.8f, RollRate = 2.2f;

    public override float CrashSpeed => 10f;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override float BodyRadius => 1.1f;
    public override float BodyHeight => 2.4f;
    /// <summary>
    /// The fuselage, not the wingspan. An 11 m box on a mountainside starts inside the slope and
    /// is shoved out of it; the wings may clip a hillside visually, which is a far smaller sin.
    /// </summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox => (new Vector3(0, 1.3f, 0.6f), new Vector3(1.6f, 2.2f, 7.0f));
    public override float CameraDistance => 15f;
    public override float CameraHeight => 3.5f;
    public override float CameraPivot => 2f;
    public override Vector3 Pivot => new(0, 1.5f, 0);
    public override (float Calm, float Fast) Thrill => (40f, 80f);
    public override float BaseFov => 72f;
    public override float MaxFov => 92f;
    public override float FovSpeed => 80f;

    public override Node3D BuildVisual(int riderIndex)
    {
        float hue = (riderIndex * 0.37f) % 1f;
        var root = new Node3D { Name = "Plane" };
        var material = HumanMeshBuilder.Material();
        root.AddChild(new MeshInstance3D
        {
            Mesh = AircraftMeshBuilder.Plane(new Color(0.93f, 0.93f, 0.95f), Color.FromHsv(hue, 0.7f, 0.8f)),
            MaterialOverride = material,
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "Prop",
            Mesh = AircraftMeshBuilder.Propeller(),
            MaterialOverride = material,
            Position = AircraftMeshBuilder.Flip(AircraftMeshBuilder.PropHub),
        });
        return root;
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        if (m.Attitude == default) m.Attitude = new Basis(Vector3.Up, m.Yaw);
        m.Control = Mathf.Clamp(m.Control + (input.LeverUp - input.LeverDown) * 0.6f * dt, 0f, 1f);
        m.Spool = Approach(m.Spool, input.Engine ? 0.15f + 0.85f * m.Control : 0f, input.Engine ? 2f : 0.8f, dt);
        float thrust = input.Engine ? m.Control * MaxThrust : 0f;

        var att = m.Attitude.Orthonormalized();
        var fwd = -att.Z;
        // Airspeed is carried, not re-derived; it is only re-read from the velocity when
        // something outside the model changed that velocity (a collision, a launch).
        var expected = fwd * m.Airspeed + Vector3.Down * m.Spin;
        if ((m.Velocity - expected).LengthSquared() > 9f) m.Airspeed = Mathf.Max(0f, m.Velocity.Dot(fwd));
        float speed = m.Airspeed;

        // energy along the nose: thrust, drag, and gravity's share — climbing costs speed
        speed += (thrust - DragK * speed * speed - Gravity * fwd.Y) * dt;

        if (env.OnFloor)
        {
            // rolling: wheels keep it level, the nosewheel steers, the brakes are the lever's end
            speed -= (0.25f + (input.LeverDown > 0.5f && m.Control < 0.05f ? 5f : 0f)) * dt;
            // an empty plane on the ground is chocked: it does not roll off down the mountain
            if (!input.Piloted && input.LeverDown > 0.5f) speed = Mathf.MoveToward(speed, 0f, 8f * dt);
            speed = Mathf.Max(0f, speed);
            float steer = speed < 30f ? -input.Stick.X * Mathf.Clamp(speed / 4f, 0f, 1f) * 0.8f : 0f;
            m.Yaw += steer * dt;
            // rotate for take-off once fast enough: stick back lifts the nose
            float nose = speed > RotateSpeed ? Mathf.Clamp(input.Stick.Y, 0f, 1f) * 0.2f : 0f;
            att = new Basis(Vector3.Up, m.Yaw) * new Basis(Vector3.Right, nose);
            fwd = -att.Z;
            m.Airspeed = speed;
            m.Spin = nose > 0.05f ? 0f : 0.5f;
            m.Velocity = fwd * speed + Vector3.Down * m.Spin;
            m.Attitude = att;
            m.Bank = 0;
            return FlightEvent.None;
        }

        float authority = Mathf.Clamp(speed / 30f, 0.25f, 1.2f);
        // local rotations: +X lifts the nose; -Z rolls right
        att = att * new Basis(Vector3.Right, input.Stick.Y * PitchRate * authority * dt);
        att = att * new Basis(Vector3.Back, -input.Stick.X * RollRate * authority * dt);

        // hands off the roll and it levels, slowly, the way a stable trainer does
        float bank = -Mathf.Asin(Mathf.Clamp(att.X.Y, -1f, 1f));   // + right wing down
        if (Mathf.Abs(input.Stick.X) < 0.1f)
            att = att * new Basis(Vector3.Back, Mathf.Clamp(bank, -0.6f * dt, 0.6f * dt));

        // and the nose drifts back toward the horizon too, gently, so letting go of the stick in a
        // dive is a recovery rather than a commitment
        if (Mathf.Abs(input.Stick.Y) < 0.1f && speed > StallSpeed)
        {
            float pitchAngle = Mathf.Asin(Mathf.Clamp(-att.Z.Y, -1f, 1f));   // nose up +: the nose is -Z
            att = att * new Basis(Vector3.Right, Mathf.Clamp(-pitchAngle, -0.25f * dt, 0.25f * dt));
        }

        // coordinated turn: the heading follows the bank
        float yawRate = -Gravity * Mathf.Tan(Mathf.Clamp(bank, -1.3f, 1.3f)) / Mathf.Max(speed, 15f);
        att = new Basis(Vector3.Up, yawRate * dt) * att;

        // below stall the wing gives up: it sinks, and the nose drops to fly again
        float stall = Mathf.Clamp((StallSpeed - speed) / 8f, 0f, 1f);
        if (stall > 0) att = att * new Basis(Vector3.Right, -0.9f * stall * dt);
        m.Spin = Mathf.MoveToward(m.Spin, stall * 14f, 10f * dt);   // spin = sink speed here

        att = att.Orthonormalized();
        fwd = -att.Z;
        m.Airspeed = Mathf.Max(speed, 0f);
        m.Velocity = fwd * m.Airspeed + Vector3.Down * m.Spin;
        m.Attitude = att;
        m.Bank = bank;
        var flat = new Vector2(fwd.X, fwd.Z);
        if (flat.Length() > 0.1f) m.Yaw = Mathf.Atan2(-flat.X, -flat.Y);
        return FlightEvent.None;
    }

    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        if (visual.GetNodeOrNull<Node3D>("Prop") is not { } prop) return;
        prop.RotateZ((10f + 70f * m.Spool) * dt);
    }
}
