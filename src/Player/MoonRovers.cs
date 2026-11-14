using System.Linq;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// The Moon rovers (#1943): two entries of the car catalog (<see cref="CarCatalog"/>, append-only) with their own bodies
/// (<see cref="MoonRoverMeshBuilder"/>): the open two-seater and the pressurised cabin. They drive with the car's physics
/// on the Moon's gravity (<see cref="Rideable.Gravity"/> follows the anchor's body).
/// </summary>
public static class MoonRovers
{
    public static CarSpec Open => _open ??= CarCatalog.All.First(c => c.Body.Shape == BodyShape.RoverOpen);
    public static CarSpec Cabin => _cabin ??= CarCatalog.All.First(c => c.Body.Shape == BodyShape.RoverCabin);
    private static CarSpec? _open, _cabin;

    /// <summary>Whether a ride is a Moon rover.</summary>
    public static bool Is(RideKind kind) => CarCatalog.For(kind) is { } s && s.Body.Shape.IsRover();
}
