using Godot;

namespace UnitSport.Player;

/// <summary>What the player is currently travelling as. Replicated, so it must stay stable.</summary>
public enum RideKind
{
    OnFoot = 0,
    RoadBike = 1,
    Skis = 2,
    // the flying things (Flight.cs); appended, never reordered, because this travels as an int
    Wingsuit = 3,
    Parachute = 4,
    Paraglider = 5,
    Helicopter = 6,
    Plane = 7,
    // 8..63 are cars: CarCatalog.All[kind - CarCatalog.First]. The catalog is append-only.
    // The next non-car mount is 64.
}

/// <summary>Controls as the vehicle sees them, already stripped of key bindings.</summary>
/// <param name="Throttle">0..1 — pedalling, poling, whatever propels this thing.</param>
/// <param name="Brake">0..1.</param>
/// <param name="Steer">-1 left .. +1 right.</param>
/// <param name="Effort">Shift: sprint on a bike, tuck on skis.</param>
/// <param name="Handbrake">Space on a car (<see cref="Rideable.CanHop"/> false): locks the rear wheels.</param>
public readonly record struct RideInput(float Throttle, float Brake, float Steer, bool Effort, bool Handbrake = false);

/// <summary>
/// The ground under the vehicle.
/// </summary>
/// <param name="OnFloor">False in the air; nothing but gravity applies.</param>
/// <param name="Grade">
/// Rise per horizontal metre <i>along the direction of travel</i> — positive uphill. Not the
/// slope of the terrain: a traverse across a 40% face is flat to a bicycle, and modelling it
/// any other way would have a road that contours a hillside costing power to ride along.
/// </param>
public readonly record struct RideGround(bool OnFloor, float Grade);

/// <summary>
/// The vehicle's own state between frames. Speed is a scalar along <see cref="Yaw"/> rather than
/// a velocity vector, because that is what a bike and a pair of skis actually have: they go
/// where they point. Strafing is a thing people do, not a thing vehicles do.
/// </summary>
public struct RideMotion
{
    public float Speed;
    public float Yaw;

    /// <summary>Roll into the turn, radians. Derived from speed and yaw rate, never authored.</summary>
    public float Lean;

    /// <summary>
    /// The rider's commanded bank, radians — the state steering actually moves. The input sets
    /// where it is heading, it eases there, and the turn follows from it. <see cref="Lean"/> is
    /// what is drawn, recomputed from the yaw rate this produced.
    /// </summary>
    public float Bank;

    /// <summary>Yaw rate from the last step, rad/s. Read by the chase camera to trail the turn.</summary>
    public float YawRate;

    /// <summary>
    /// Direction of travel minus <see cref="Yaw"/>, radians, same sign as yaw (+ = travelling to
    /// the left of where the nose points). Zero for everything that goes where it points; a
    /// drifting car is the one thing that does not. π is reversing.
    /// </summary>
    public float Slip;
}

/// <summary>
/// Something the player can travel on instead of their own legs.
///
/// <para>
/// The point of the abstraction is that a vehicle is a table of numbers plus a mesh: everything
/// that touches the player body, the network, the camera and the UI is written once in
/// <see cref="FootPlayer"/> and <see cref="RideUi"/>, so adding a new one is a class and a line
/// in <see cref="All"/>.
/// </para>
///
/// <para>
/// Both implementations share one physical model — mass, a resistive force, gravity along the
/// slope — because that is what makes them feel like they belong in the same world. What differs
/// is where the propulsion comes from and how willingly the thing changes direction.
/// </para>
/// </summary>
public abstract class Rideable
{
    public const float Gravity = 9.81f;

    /// <summary>
    /// The arcade tuning layer is active (Settings → Movement: Game). Each vehicle keeps its
    /// real-world numbers for Sim and swaps a handful of them here — more power and grip, less
    /// scrub — without a second model: the same equations, just a fitter, braver rider.
    /// </summary>
    public static bool Arcade => Core.GameSettings.Current.RideProfile == Core.RideProfile.Game;

    public abstract RideKind Kind { get; }

    /// <summary>Name in the picker.</summary>
    public abstract string Label { get; }

    /// <summary>One line under it, saying what the controls do.</summary>
    public abstract string Blurb { get; }

    /// <summary>Eye height while riding, used when the camera is in first person.</summary>
    public virtual float EyeHeight => 1.42f;

    /// <summary>
    /// Where the camera sits in first person, in the visual's frame (before lean). Defaults to
    /// straight above the origin; a bent-over rider's eyes are well forward of that.
    /// </summary>
    public virtual Vector3 FirstPersonEye => new(0, EyeHeight, 0);

    /// <summary>Chase camera offset behind and above the rider. Zero distance means first person.</summary>
    public virtual float ChaseDistance => 3.6f;
    public virtual float ChaseHeight => 1.45f;
    /// <summary>Chase camera tilt, radians, negative looks down. A car's roof hides the road from a level camera behind it.</summary>
    public virtual float ChasePitch => 0f;
    /// <summary>How far the chase camera swings toward the direction of travel in a slide, 0..1.</summary>
    public virtual float ChaseFollowsTravel => 0f;

    /// <summary>FOV at rest, and the speed at which it has widened to <see cref="MaxFov"/>.</summary>
    public virtual float BaseFov => 70f;
    public virtual float MaxFov => 96f;
    public virtual float FovSpeed => 18f;

    /// <summary>Below this the rider is treated as stopped — safe to dismount, no lean.</summary>
    public virtual float DismountSpeed => 2.5f;

    /// <summary>The mesh, parented under the player body. Built facing +Z, origin on the ground.</summary>
    public abstract Node3D BuildVisual(int riderIndex);

    // ---- vehicles vs equipment ------------------------------------------------------------
    /// <summary>
    /// A vehicle is a thing in the world: left where you get off it, falling and crashing on its
    /// own (<c>Vehicles.VehicleBody</c>). Equipment — skis, wingsuit, canopies — is worn, and
    /// taking it off simply ends it.
    /// </summary>
    public virtual bool IsVehicle => false;

    /// <summary>Has an engine to switch on and off, and burns when it crashes.</summary>
    public virtual bool HasEngine => false;

    public virtual float MaxHealth => 100f;

    /// <summary>Space hops (bike, skis). False on a car, where Space is the handbrake.</summary>
    public virtual bool CanHop => true;

    /// <summary>The mesh as it stands with nobody on it (a bike without its rider).</summary>
    public virtual Node3D BuildParkedVisual(int riderIndex) => BuildVisual(riderIndex);

    /// <summary>The player's body while riding: radius and height of the capsule.</summary>
    public virtual float BodyRadius => 0.32f;
    public virtual float BodyHeight => 1.78f;

    /// <summary>
    /// Collision box of the vehicle standing empty in the world: centre and size, node space.
    /// Measured from the parked mesh by default, so it cannot drift from what is drawn — the
    /// bike's hand-typed box was 1.1 m tall and centred 0.55 m up while the bike stands 1.0 m.
    /// </summary>
    public virtual (Vector3 Centre, Vector3 Size) ParkedBox => Measured(Kind, BuildParkedVisual);

    private static readonly System.Collections.Generic.Dictionary<RideKind, (Vector3, Vector3)> _measured = new();

    /// <summary>
    /// The bounds of a visual this mount builds, once per kind (a throwaway build, freed at once).
    /// <paramref name="skip"/> names parts left out, such as a rotor disc nothing rests on.
    /// </summary>
    protected static (Vector3 Centre, Vector3 Size) Measured(RideKind kind, System.Func<int, Node3D> build, params string[] skip)
    {
        if (_measured.TryGetValue(kind, out var known)) return known;
        var visual = build(1);
        var box = Avatar.MeshBounds.Of(visual, skip);
        visual.Free();
        // nothing drawn (should not happen, meshes build headless too): the old generic box
        var result = box.Size.LengthSquared() > 1e-4f ? (box.GetCenter(), box.Size) : (new Vector3(0, 0.8f, 0), new Vector3(0.6f, 1.6f, 1.6f));
        return _measured[kind] = result;
    }

    /// <summary>Advances speed, heading and lean by one physics step.</summary>
    public abstract void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion);

    /// <summary>Per-frame visual update — spinning cranks, and so on. Called on the render thread.</summary>
    public virtual void Animate(Node3D visual, in RideMotion motion, float dt) { }

    // ---- what other players see ---------------------------------------------------------
    // The visual's whole transform (lean, tricks, a craft's attitude) is replicated by FootPlayer
    // as BodyPose. These two carry the moving PARTS: whatever drives them beyond that transform,
    // packed into four floats the owner writes and every remote copy reads back. Cars: slip,
    // steer angle, wheel spin, rpm.

    /// <summary>On the rider's own peer, after <see cref="Animate"/>: the state remote copies need to animate the parts.</summary>
    public virtual Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) => default;

    /// <summary>On every other peer, each frame: animates the parts from what <see cref="WritePose"/> sent.</summary>
    public virtual void AnimateRemote(Node3D visual, Vector4 pose, float dt) { }

    /// <summary>
    /// Gravity's component along the direction of travel, m/s². Negative when climbing.
    ///
    /// <para>
    /// The grade arrives as a tangent (rise over run) so it stays finite on a wall; the sine is
    /// what actually accelerates you, and on a 20% ramp the two already differ by 2%.
    /// </para>
    /// </summary>
    protected static float SlopeAccel(float grade) =>
        -Gravity * grade / Mathf.Sqrt(1f + grade * grade);

    /// <summary>
    /// Turns yaw rate into a lean angle: tan φ = v·ω / g, the standard bicycle balance.
    ///
    /// <para>
    /// Derived rather than authored so the lean can never disagree with the turn. A fixed lean
    /// per steering input looks wrong the moment the speed changes — leaning hard into a corner
    /// taken at walking pace is the giveaway.
    /// </para>
    /// </summary>
    protected static float LeanFor(float speed, float yawRate, float maxLean) =>
        Mathf.Clamp(Mathf.Atan(speed * yawRate / Gravity), -maxLean, maxLean);

    /// <summary>
    /// Steers by leaning, the way a bike and a carving ski actually turn: the input sets a
    /// target bank, the bank eases toward it at <paramref name="bankResponse"/> (1/s), and the
    /// yaw rate is whatever that bank sustains at this speed, <c>ω = g·tanφ / v</c>.
    ///
    /// <para>
    /// This replaced setting the yaw rate straight from the input, which is what made steering
    /// feel stiff: the turn was at full rate the frame a key went down and gone the frame it came
    /// up, so every correction was a jerk. With the bank in between, a tap is a gentle drift, a
    /// hold rolls in over a fifth of a second and settles into a steady arc, and letting go rolls
    /// back out — the same input, but it now reads as carving. A keyboard gets the analog feel a
    /// stick has for free.
    /// </para>
    ///
    /// <para>
    /// Below <paramref name="slowSpeed"/> the physics would demand an unbounded rate, so the
    /// speed is floored there and the result capped: at walking pace you steer the bars directly.
    /// </para>
    /// </summary>
    /// <returns>The yaw rate applied, rad/s.</returns>
    protected static float SteerByLean(ref RideMotion motion, float steer, float speed,
        float maxLean, float maxYawRate, float bankResponse, float slowSpeed, float dt)
    {
        // +yaw is left in Godot, +steer is right; the bank takes the yaw's sign
        float target = -steer * maxLean;

        // rolling back out of a lean, or across into the other one, is quicker than rolling in:
        // a rider pushing the bike upright is working with it, not against it
        bool unwinding = Mathf.Abs(target) < Mathf.Abs(motion.Bank) || target * motion.Bank < 0;
        float rate = unwinding ? bankResponse * 1.6f : bankResponse;
        motion.Bank += (target - motion.Bank) * (1f - Mathf.Exp(-rate * dt));

        float yawRate = Gravity * Mathf.Tan(motion.Bank) / Mathf.Max(speed, slowSpeed);
        yawRate = Mathf.Clamp(yawRate, -maxYawRate, maxYawRate);

        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Lean = LeanFor(speed, yawRate, maxLean);
        return yawRate;
    }

    /// <summary>
    /// Every mountable thing, in picker order — prototypes, used for the menu's labels.
    /// Add one here and to <see cref="Create"/> and it appears everywhere.
    /// </summary>
    /// <remarks>
    /// Cars are not here either: there are dozens, and the picker lists <see cref="CarCatalog.All"/>
    /// on a page of its own.
    /// The wingsuit and parachute are not here: nobody straps into a wingsuit on flat ground.
    /// They are a base jump — Jump while falling from height — see <c>FootPlayer</c>.
    /// </remarks>
    public static readonly Rideable[] All =
        { new Bicycle(), new Skis(), new Canopy(paraglider: true), new Helicopter(), new Plane() };

    /// <summary>
    /// A fresh instance for one rider.
    ///
    /// <para>
    /// Not the prototype: a vehicle carries live state — <see cref="Bicycle.RiderWatts"/> is
    /// about to be fed by a real trainer — and sharing one instance between two players on a
    /// server would have them pedalling each other's legs.
    /// </para>
    /// </summary>
    public static Rideable? Create(RideKind kind) => kind switch
    {
        RideKind.RoadBike => new Bicycle(),
        RideKind.Skis => new Skis(),
        RideKind.Wingsuit => new Wingsuit(),
        RideKind.Parachute => new Canopy(paraglider: false),
        RideKind.Paraglider => new Canopy(paraglider: true),
        RideKind.Helicopter => new Helicopter(),
        RideKind.Plane => new Plane(),
        _ when CarCatalog.For(kind) is { } car => new Car(car),
        _ => null,
    };
}
