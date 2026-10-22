using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// What a unit of the cosmetic traffic is (#1060, the modes of #1058): the cars and trains there were, and the walkers (#1033),
/// riders (#1056) and trams (#1057) that plug into the same <c>Traffic</c>. Each has its own lane graph and density.
/// </summary>
public enum MoverKind : byte { Car = 0, Train = 1, Walker = 2, Rider = 3, Tram = 4 }

/// <summary>Plain helpers on <see cref="MoverKind"/> (no Godot: unit-tested).</summary>
public static class MoverKinds
{
    /// <summary>The kinds that plug into the traffic as modes of their own (#1058), in the order they are stepped.</summary>
    public static readonly MoverKind[] Modes = [MoverKind.Walker, MoverKind.Rider, MoverKind.Tram];

    /// <summary>
    /// Who a junction's conflict points (#1046, <c>PathYield.Mover</c>) name for movers of this kind: a car gives way at a
    /// point to the movers of that kind only. Trains have none (no junction path crosses a railway).
    /// </summary>
    public static ConflictMover? Conflict(MoverKind kind) => kind switch
    {
        MoverKind.Car => ConflictMover.Car,
        MoverKind.Walker => ConflictMover.Walker,
        MoverKind.Rider => ConflictMover.Bike,
        MoverKind.Tram => ConflictMover.Tram,
        _ => null,
    };

    /// <summary>The kind <c>--trafficcheck --mover</c> names (walker, rider, tram; also car, train), null for none.</summary>
    public static MoverKind? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "car" or "cars" => MoverKind.Car,
        "train" or "trains" => MoverKind.Train,
        "walker" or "walkers" or "ped" or "pedestrian" or "pedestrians" => MoverKind.Walker,
        "rider" or "riders" or "bike" or "bikes" => MoverKind.Rider,
        "tram" or "trams" => MoverKind.Tram,
        _ => null,
    };
}
