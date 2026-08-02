using Godot;

namespace UnitSport.Player;

/// <summary>What the player is currently travelling as. Replicated, so it must stay stable.</summary>
public enum RideKind
{
    OnFoot = 0,
    RoadBike = 1,
    Skis = 2,
}

/// <summary>Controls as the vehicle sees them, already stripped of key bindings.</summary>
/// <param name="Throttle">0..1 — pedalling, poling, whatever propels this thing.</param>
/// <param name="Brake">0..1.</param>
/// <param name="Steer">-1 left .. +1 right.</param>
/// <param name="Effort">Shift: sprint on a bike, tuck on skis.</param>
public readonly record struct RideInput(float Throttle, float Brake, float Steer, bool Effort);

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

    public abstract RideKind Kind { get; }

    /// <summary>Name in the picker.</summary>
    public abstract string Label { get; }

    /// <summary>One line under it, saying what the controls do.</summary>
    public abstract string Blurb { get; }

    /// <summary>Eye height while riding, used when the camera is in first person.</summary>
    public virtual float EyeHeight => 1.42f;

    /// <summary>Chase camera offset behind and above the rider. Zero distance means first person.</summary>
    public virtual float ChaseDistance => 3.6f;
    public virtual float ChaseHeight => 1.45f;

    /// <summary>FOV at rest, and the speed at which it has widened to <see cref="MaxFov"/>.</summary>
    public virtual float BaseFov => 70f;
    public virtual float MaxFov => 96f;
    public virtual float FovSpeed => 18f;

    /// <summary>Below this the rider is treated as stopped — safe to dismount, no lean.</summary>
    public virtual float DismountSpeed => 2.5f;

    /// <summary>The mesh, parented under the player body. Built facing +Z, origin on the ground.</summary>
    public abstract Node3D BuildVisual(int riderIndex);

    /// <summary>Advances speed, heading and lean by one physics step.</summary>
    public abstract void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion);

    /// <summary>Per-frame visual update — spinning cranks, and so on. Called on the render thread.</summary>
    public virtual void Animate(Node3D visual, in RideMotion motion, float dt) { }

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
    /// Every mountable thing, in picker order — prototypes, used for the menu's labels.
    /// Add one here and to <see cref="Create"/> and it appears everywhere.
    /// </summary>
    public static readonly Rideable[] All = { new Bicycle(), new Skis() };

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
        _ => null,
    };
}
