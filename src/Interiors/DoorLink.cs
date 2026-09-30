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
    /// How far out from the facade the portal quad stands: in front of the door leaf baked into the
    /// facade and its handle (11 cm out), or the handle stays hanging in the open doorway.
    /// </summary>
    public const float OutsideQuadOffset = 0.12f;

    public required string Door { get; init; }
    public required string Plan { get; init; }
    public required TileId Tile { get; init; }
    public required Transform3D Outside { get; init; }
    public required Transform3D Inside { get; init; }
    public float OutsideWidth { get; init; }
    public float OutsideHeight { get; init; }
    public float InsideWidth { get; init; }
    public float InsideHeight { get; init; }

    public Transform3D ToInside => Inside * Outside.AffineInverse();
    public Transform3D ToOutside => Outside * Inside.AffineInverse();

    /// <summary>Half the width a body can pass through: the narrower of the two openings, less a margin.</summary>
    public float HalfPass => Math.Min(OutsideWidth, InsideWidth) / 2 - 0.12f;
    public float PassHeight => Math.Min(OutsideHeight, InsideHeight);

    /// <summary>The server says the door is open.</summary>
    public bool Open { get; set; }
    /// <summary>How far the leaf has swung, 0 shut .. 1 open.</summary>
    public float Swing { get; set; }
    /// <summary>Open wide enough to walk through, and to see through.</summary>
    public bool Passable => Open && Swing > 0.6f;

    /// <summary>The doorway quads on each side, one per portal depth (see <see cref="DoorPortals"/>).</summary>
    public MeshInstance3D?[] OutsideQuads { get; } = new MeshInstance3D?[3];
    public MeshInstance3D?[] InsideQuads { get; } = new MeshInstance3D?[3];
    public DoorLeaf? Leaf { get; set; }

    public static DoorLink Create(InteriorLayout layout, EntrancePlan e, WorldOrigin origin, float? outsideWidth)
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
        return new DoorLink
        {
            Door = e.Door,
            Plan = layout.Key,
            Tile = door.Tile,
            Outside = outside,
            Inside = inside,
            OutsideWidth = outsideWidth ?? e.Width,
            OutsideHeight = BuildingFootprint.DoorHeightFor(kind),
            InsideWidth = width,
            InsideHeight = top,
        };
    }

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
}
