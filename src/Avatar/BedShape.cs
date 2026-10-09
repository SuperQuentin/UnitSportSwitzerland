using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Where a tipping body carries a pallet (#615): a tipper's body, a mini dumper's skip. All in the
/// vehicle's node space (−Z forward), with the body down. <see cref="Floor"/> is the middle of the
/// floor a pallet stands on, <see cref="Hinge"/> what the body tips about (the rig's tipping node),
/// by <see cref="TipAngle"/> as the rig turns it. A pallet slides toward the hinge, the open end,
/// and comes to rest at <see cref="Spill"/> on the ground the vehicle stands on.
///
/// <para>Godot maths only, in a file of its own, so the unit tests link it.</para>
/// </summary>
public readonly record struct BedShape(Vector3 Hinge, Vector3 Floor, float HalfWidth, float HalfLength, float TipAngle, Vector3 Spill)
{
    /// <summary>The floor's middle in the tipping node's own frame: where the rig draws the pallet.</summary>
    public Vector3 FloorOnTip => Floor - Hinge;

    /// <summary>Along the body toward its open end (node space, flat): +Z for a rear tipper, −Z for a front skip.</summary>
    public Vector3 Out => new(0f, 0f, Hinge.Z >= Floor.Z ? 1f : -1f);

    /// <summary>How far a pallet's middle slides to go over the edge: from the floor's middle to the hinge, m.</summary>
    public float Travel => Mathf.Abs(Hinge.Z - Floor.Z);

    /// <summary>
    /// Whether a point (node space) is over the floor: within its rectangle, flat, by
    /// <paramref name="margin"/> m more each way.
    /// </summary>
    public bool Over(Vector3 local, float margin = 0f) =>
        Mathf.Abs(local.X - Floor.X) <= HalfWidth + margin && Mathf.Abs(local.Z - Floor.Z) <= HalfLength + margin;

    /// <summary>
    /// How far along its travel a pallet has slid for a body shown <paramref name="tipped"/> up
    /// (0 down, 1 fully up): it holds until the floor is steep enough, then slides to the edge.
    /// </summary>
    public static float Slid(float tipped) => Mathf.SmoothStep(SlideFrom, SlideTo, tipped);

    /// <summary>The body's share of its tip where a pallet starts to slide, and where it is at the edge.</summary>
    public const float SlideFrom = 0.35f, SlideTo = 0.85f;
}
