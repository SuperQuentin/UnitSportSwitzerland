using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// The free space a venue offers an imported building (#980), in metres: across (u) and along (v) the
/// pitch inside the seating front, and up to the ceiling (infinite under open sky).
/// </summary>
public sealed record VenueRoom(float Width, float Length, float Height);

/// <summary>An imported building that fits its venue: its box in our frame as read, and whether it goes in turned a quarter.</summary>
public sealed record VenueFit(Aabb Box, bool Turned)
{
    /// <summary>
    /// Where the whole file goes so its building stands centred on the pitch, turned if it must, its
    /// lowest point at <paramref name="floor"/> (over our pitch's boards, not in them).
    /// </summary>
    public Transform3D Placement(float floor)
    {
        var centre = Box.Position + Box.Size / 2;
        var basis = Turned ? new Basis(Vector3.Up, Mathf.Pi / 2) : Basis.Identity;
        var toOrigin = new Transform3D(Basis.Identity, new Vector3(-centre.X, -Box.Position.Y, -centre.Z));
        return new Transform3D(basis, new Vector3(0, floor, 0)) * toOrigin;
    }
}
