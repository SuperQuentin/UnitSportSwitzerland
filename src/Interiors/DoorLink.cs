using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// One door whose interior is built on this client, seen as a pair of doorways: the real one on
/// the facade, and the one in the interior's wall 3 km below. Both are frames in world space
/// with their origin on the sill in the middle of the opening, X along the wall, Y up and Z out
/// of the building.
///
/// <para>
/// <b>The two doorways are the same hole.</b> Mapping one frame onto the other
/// (<see cref="ToInside"/>, <see cref="ToOutside"/>) is what the portal camera, a player
/// stepping across the sill and a camera arm reaching through all use. It is a rigid transform:
/// mostly the drop to <see cref="InteriorManager.InteriorBaseY"/>, plus the small turn and shift
/// between the facade the door was found on and the plan box the interior was laid out in.
/// </para>
/// </summary>
public sealed class DoorLink
{
    /// <summary>
    /// How far out from the facade the portal quad stands: just proud of the wall, which has no
    /// hole in it. The closed leaf and handle baked in front of it (6..11 cm) are dropped by the
    /// building shader while the portal shows (<see cref="DoorPortals.OpenDoors"/>), and the frame
    /// round the opening stands in front of it like a real one.
    /// </summary>
    public const float OutsideQuadOffset = 0.02f;
    /// <summary>Where the interior doorway's portal quad stands: at the reveal, just inside the room.</summary>
    public const float InsideQuadOffset = -0.005f;
    /// <summary>
    /// The slab, in doorway-frame Z, a camera lens must not stand in: from the interior quad to the
    /// facade one, widened by <paramref name="margin"/> (how far the near plane's corners reach from
    /// the lens). In it the near plane cuts the portal's mouth and the view goes black or shows the
    /// wall behind. The map keeps Z, so it holds in either frame.
    /// </summary>
    public static float LensSlabMin(float margin) => InsideQuadOffset - margin;
    public static float LensSlabMax(float margin) => OutsideQuadOffset + margin;

    public required string Door { get; init; }
    public required string Plan { get; init; }
    public required TileId Tile { get; init; }
    public required Transform3D Outside { get => _outside; init => _outside = value; }
    public required Transform3D Inside { get => _inside; init => _inside = value; }
    private Transform3D _outside, _inside;

    /// <summary>The origin moved (#185): both doorways are somewhere else in world space.</summary>
    public void Shift(OriginShift shift)
    {
        _outside = shift.Apply(_outside);
        _inside = shift.Apply(_inside);
    }
    public float OutsideWidth { get; init; }
    public float OutsideHeight { get; init; }
    public float InsideWidth { get; init; }
    public float InsideHeight { get; init; }
    /// <summary>The building's kind as dressed: a garage's or a barn's door lets vehicles through.</summary>
    public BuildingKind Kind { get; init; }
    public bool VehicleDoor => BuildingFootprint.VehicleDoor(Kind);

    public Transform3D ToInside => Inside * Outside.AffineInverse();
    public Transform3D ToOutside => Outside * Inside.AffineInverse();

    /// <summary>Half the width a body can pass through: the narrower of the two openings, less a margin.</summary>
    public float HalfPass => Math.Min(OutsideWidth, InsideWidth) / 2 - 0.12f;
    public float PassHeight => Math.Min(OutsideHeight, InsideHeight);

    /// <summary>The server says the door is open.</summary>
    public bool Open { get; set; }
    /// <summary>How far the leaf has swung, 0 shut .. 1 open.</summary>
    public float Swing { get; set; }
    /// <summary>Seconds a swing takes, open or shut (<see cref="SwingSecondsFor"/>).</summary>
    public float SwingSeconds { get; init; } = SwingSecondsFor(1f);
    /// <summary>Open wide enough to walk through, and to see through.</summary>
    public bool Passable => Open && Swing > 0.6f;

    /// <summary>The doorway quads on each side, one per portal depth (see <see cref="DoorPortals"/>).</summary>
    public MeshInstance3D?[] OutsideQuads { get; } = new MeshInstance3D?[3];
    public MeshInstance3D?[] InsideQuads { get; } = new MeshInstance3D?[3];
    public DoorLeaf? Leaf { get; set; }
    /// <summary>A barn's pair as its interior sees it, swung with <see cref="Leaf"/> (<see cref="DoorLeaf.CreateShutter"/>).</summary>
    public DoorLeaf? Shutter { get; set; }

    /// <summary>Both sides' leaves to <paramref name="swing"/>.</summary>
    public void SetLeaves(float swing)
    {
        Leaf?.SetSwing(swing);
        Shutter?.SetSwing(swing);
    }

    public static DoorLink Create(InteriorLayout layout, EntrancePlan e, WorldOrigin origin, float? outsideWidth, float? outsideHeight = null)
    {
        BuildingKey.TryParse(e.Door, out var door);
        var tileOrigin = origin.ToWorld(door.Tile.MinE, door.Tile.MaxN, 0);

        // the door spot stands 3 cm proud of the facade; the plane is the facade itself
        var outward = new Vector3(e.DoorOutX, 0, e.DoorOutZ).Normalized();
        var sill = tileOrigin + new Vector3(e.DoorX, e.DoorY, e.DoorZ) - outward * 0.03f;
        var outside = Frame(sill, outward);

        var placement = InteriorManager.PlacementFor(layout, origin);
        var inward = placement.Basis * new Vector3(e.InX, 0, e.InZ);
        var inside = Frame(placement * new Vector3(e.X, 0, e.Z), -inward.Normalized());

        var kind = layout.DressedKind();
        var (width, top) = layout.OpeningOf(e);
        float outsideW = outsideWidth ?? e.Width;
        return new DoorLink
        {
            Door = e.Door,
            Plan = layout.Key,
            Tile = door.Tile,
            Outside = outside,
            Inside = inside,
            OutsideWidth = outsideW,
            OutsideHeight = outsideHeight ?? BuildingFootprint.DoorHeightFor(kind),
            InsideWidth = width,
            InsideHeight = top,
            Kind = kind,
            // a barn's pair: each leaf is half the opening; a roll-up door takes a second, as a
            // car driving up has to find it open
            SwingSeconds = DoorLeaf.RollsUp(kind) ? DoorLeaf.RollSeconds
                : SwingSecondsFor(DoorLeaf.SwingsOut(kind) ? outsideW / 2 : outsideW),
        };
    }

    /// <summary>
    /// How long a leaf <paramref name="leafWidth"/> metres wide takes to swing: 0.6 s for a house
    /// door, and 0.4 s more per extra metre, so a big leaf moves with its weight (a 10 m barn
    /// pair's 5 m leaves take 2.2 s).
    /// </summary>
    public static float SwingSecondsFor(float leafWidth) => 0.6f + 0.4f * Math.Max(0f, leafWidth - 1f);

    /// <summary>A doorway frame: origin on the sill, Z out of the building, Y up.</summary>
    private static Transform3D Frame(Vector3 origin, Vector3 outward)
    {
        var z = new Vector3(outward.X, 0, outward.Z).Normalized();
        var x = Vector3.Up.Cross(z);
        return new Transform3D(new Basis(x, Vector3.Up, z), origin);
    }

    /// <summary>
    /// Whether a body outside stands in the facade doorway: where it has to be let through the
    /// building's closed shell to reach the sill.
    /// </summary>
    public bool InOutsideDoorway(Vector3 feet, float radius)
    {
        var p = Outside.AffineInverse() * feet;
        return Math.Abs(p.X) < HalfPass + 0.05f && p.Z > -0.3f && p.Z < radius + 0.6f && p.Y > -1.5f && p.Y < 1.5f;
    }

    /// <summary>
    /// As <see cref="InOutsideDoorway(Vector3, float)"/> for a vehicle: a box
    /// <paramref name="halfWidth"/> by <paramref name="halfLength"/> heading along
    /// <paramref name="forward"/>. Before its nose reaches the facade it has to be lined up with
    /// the opening, all of it between the jambs, or it drives into the wall beside them; once it is
    /// into the doorway (let in, or come out through it) only its middle has to stay in the opening.
    /// </summary>
    public bool InOutsideDoorway(Vector3 feet, Vector3 forward, float halfWidth, float halfLength)
    {
        var inverse = Outside.AffineInverse();
        var p = inverse * feet;
        var f = inverse.Basis * forward;
        var flat = new Vector2(f.X, f.Z);
        flat = flat.LengthSquared() > 1e-6f ? flat.Normalized() : new Vector2(0, 1);
        float sin = Math.Abs(flat.X), cos = Math.Abs(flat.Y);
        // the box's reach across the doorway and out of it
        float across = halfWidth * cos + halfLength * sin, deep = halfWidth * sin + halfLength * cos;
        if (p.Y < -1.5f || p.Y > 1.5f || p.Z < -0.3f || p.Z > deep + 0.6f) return false;
        float allowed = p.Z < deep ? HalfPass + 0.05f : HalfPass + 0.1f - across;
        return Math.Abs(p.X) < allowed;
    }
}
