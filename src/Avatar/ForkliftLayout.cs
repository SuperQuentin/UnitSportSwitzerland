using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The counterbalance forklift (#583): a short truck with a cast counterweight at the back, a mast
/// over the front axle and two forks on a carriage that rides up it. One set of numbers for the
/// drawn model, its hull boxes and the rectangle the forks sweep — which is what decides whether a
/// pallet is on them. AUTHORED frame like every avatar mesh: +Z forward (the forks' way), +X the
/// left side, origin on the ground in the middle of the wheelbase. Metres, after a 2.5 t
/// electric three-wheeler of the common European class (Linde H25 / Toyota Tonero: 1.15 m wide,
/// 2.6 m to the fork face, 3.3 m lift).
///
/// <para>
/// Godot maths only, in a file of its own, so the unit tests link it with the pallet rule
/// (<c>Items/Pallets</c>) that asks it where the forks are.
/// </para>
/// </summary>
public static class ForkliftLayout
{
    /// <summary>The body: the counterweight's back face, the mast's plane, half width.</summary>
    public const float Rear = -1.5f, MastZ = 1.1f, HalfWidth = 0.575f;
    /// <summary>The bonnet over the battery, the footplate the driver's feet rest on.</summary>
    public const float BonnetTop = 0.95f, Floor = 0.42f;
    /// <summary>Front axle (driven, under the mast) and rear axle (steered), and their half tracks.</summary>
    public const float FrontAxle = 0.8f, RearAxle = -0.8f, FrontTrack = 0.42f, RearTrack = 0.2f;
    public const float Wheelbase = FrontAxle - RearAxle;
    /// <summary>Front wheels are big and solid, the steered rear pair small and close together.</summary>
    public const float FrontRadius = 0.28f, FrontWidth = 0.22f, RearRadius = 0.21f, RearWidth = 0.17f;

    /// <summary>The overhead guard: its roof, and the posts' section.</summary>
    public const float GuardTop = 2.18f, Post = 0.055f;
    /// <summary>The outer mast: its channels' height, their half spacing and their section.</summary>
    public const float MastTop = 2.0f, MastX = 0.3f, MastRail = 0.07f;
    /// <summary>
    /// Free lift: the carriage rises this far inside the outer mast before the inner stage starts
    /// to extend with it, which is what lets a 2 m mast put a fork 3.2 m up.
    /// </summary>
    public const float FreeLift = 1.25f;

    /// <summary>The forks: their length ahead of the mast, their half spacing, and their section.</summary>
    public const float TineLength = 1.15f, TineX = 0.28f, TineWidth = 0.1f, TineThick = 0.045f;
    /// <summary>The carriage the tines hang on: its height and the backrest above it.</summary>
    public const float CarriageTop = 0.42f, BackrestTop = 1.1f;

    /// <summary>The forks' travel: on the ground, and at full lift.</summary>
    public const float MinLift = 0.05f, MaxLift = 3.2f;
    /// <summary>How fast the mast runs, m/s, and what the small dial's face reads to.</summary>
    public const float LiftRate = 0.5f, LiftDial = 3.5f;

    /// <summary>Under this the forks are low enough to drive into a pallet; over it they carry one.</summary>
    public const float ForkEntry = 0.35f, SetDown = 0.12f;

    /// <summary>Half the span of the two tines, outside edge to outside edge: the fork rectangle's half width.</summary>
    public const float ForkHalfSpan = TineX + TineWidth * 0.5f;

    /// <summary>Where E gets in: the step on the left, beside the seat.</summary>
    public static readonly Vector3 Step = new(HalfWidth + 0.12f, 0f, -0.3f);

    /// <summary>
    /// Where a pallet's own origin (its centre, on its underside) sits when it is on the forks,
    /// authored, relative to the carriage (whose origin is on the tines' top face at the mast
    /// plane): a pallet's length out from the hull's front face, so it rides against the backrest
    /// and is set down clear of the machine, overhanging the tips a little as a real one does.
    /// </summary>
    public static readonly Vector3 LoadCentre = new(0f, 0f, MastZ + 0.12f + Items.Pallets.Length * 0.5f);

    public static float Clamp(float lift) => Mathf.Clamp(lift, MinLift, MaxLift);

    /// <summary>
    /// How far the inner mast stage has extended with the carriage at <paramref name="lift"/>:
    /// nothing through the free lift, then one for one.
    /// </summary>
    public static float Stage(float lift) => Mathf.Max(0f, Clamp(lift) - FreeLift);

    /// <summary>
    /// The box the two tines sweep at <paramref name="lift"/>, authored: a pallet whose centre is
    /// inside it is on the forks. Z from the carriage face to the tips, X across both tines, Y the
    /// pallet deck's own thickness above the tines.
    /// </summary>
    public static Aabb ForkBox(float lift)
    {
        float y = Clamp(lift);
        return new Aabb(new Vector3(-ForkHalfSpan, y - 0.05f, MastZ),
            new Vector3(ForkHalfSpan * 2f, 0.3f, TineLength));
    }
}
