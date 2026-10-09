using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The tracked mini dumper (#614): one set of numbers for the drawn model, its hull, the tracks and
/// the skip, after a 1 t machine of the common class (Wacker Neuson DT10, Yanmar C12R: 1.0 m wide on
/// rubber tracks, a 0.5 m³ skip at the front that tips forward, the driver seated behind it, 4.5
/// km/h). AUTHORED frame like every avatar mesh: +Z forward (the skip's way), +X the left side,
/// origin on the ground in the middle of the tracks. Metres and radians.
///
/// <para>Godot maths only, in a file of its own, so the unit tests link it.</para>
/// </summary>
public static class MiniDumperLayout
{
    /// <summary>The tracks: half their length, the half gauge, a shoe's width, their height.</summary>
    public const float TrackHalfLength = 0.92f, HalfGauge = 0.4f, ShoeWidth = 0.24f, TrackHeight = 0.36f;
    /// <summary>The chassis over the tracks: its top, half its width, its nose and its tail.</summary>
    public const float DeckTop = 0.58f, HalfWidth = 0.55f, Nose = 1.3f, Tail = -1.25f;

    /// <summary>The skip: its back and front (authored z), its floor, its top, half its width.</summary>
    public const float SkipBack = -0.05f, SkipFront = 1.32f, SkipFloor = 0.62f, SkipTop = 1.28f, SkipHalf = 0.56f;
    /// <summary>
    /// The skip's hinge, low at its front: it tips forward about it, its back rising, by
    /// <see cref="TipAngle"/> (as the rig's tipping node turns it: negative raises the back).
    /// </summary>
    public static readonly Vector3 Hinge = new(0f, SkipFloor - 0.04f, SkipFront - 0.05f);
    public const float TipAngle = -1.25f;

    /// <summary>The engine hood behind the skip, under the seat; the platform the driver sits on; the roll bar.</summary>
    public const float HoodTop = 0.95f, Floor = 0.62f, RopsTop = 2.1f, RopsZ = -1.05f;

    /// <summary>Top speed on the tracks either way, m/s (4.5 km/h), how fast it gets there, and a full turn on the spot, rad/s.</summary>
    public const float TopSpeed = 1.25f, Accel = 1.2f, TurnRate = 0.9f;

    /// <summary>
    /// The tracks' speeds, m/s, for a travel speed and a turn rate (+ turning left): on the spot,
    /// one runs back as the other runs forward.
    /// </summary>
    public static (float Left, float Right) Tracks(float speed, float yawRate) =>
        (speed - yawRate * HalfGauge, speed + yawRate * HalfGauge);

    /// <summary>
    /// Where the skip carries a pallet (#615), node space: on its flat floor, tipped out ahead of
    /// the lip onto the ground.
    /// </summary>
    public static readonly BedShape Bed = new(
        Hinge: new Vector3(-Hinge.X, Hinge.Y, -Hinge.Z),
        Floor: new Vector3(0f, SkipFloor + 0.06f, -(SkipBack + SkipFront - 0.3f) * 0.5f),
        HalfWidth: SkipHalf - 0.04f, HalfLength: (SkipFront - SkipBack) * 0.5f, TipAngle: TipAngle,
        Spill: new Vector3(0f, 0f, -(Hinge.Z + 1.25f)));

    /// <summary>
    /// What a parked one keeps in <c>VehicleState.Flags</c>: whether its skip is up (two bits, 1
    /// down and 2 up; zero is "never set": down) and the pallet in it (ten bits above, #615).
    /// </summary>
    public static int Pack(bool tipped, int bed = 0) => (tipped ? 2 : 1) | (bed & BedMask) << 2;

    public static bool Unpack(int flags) => (flags & 3) == 2;

    /// <summary>The pallet in the skip from the parked flags: 0, else <c>Pallets.Carried</c>.</summary>
    public static int BedOf(int flags) => (flags >> 2) & BedMask;

    private const int BedMask = 0x3FF;
}
