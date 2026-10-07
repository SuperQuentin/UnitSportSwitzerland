using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// The pure half of the underground car park's cars as real vehicles (#558, PR 3): how a slot is
/// named and which ordinary car stands in which bay. No Godot, so tier 0 pins it
/// (<c>HallCarTests</c>); <see cref="HallCars"/> is the half that knows the plan and the world.
/// </summary>
public static class HallCarRule
{
    /// <summary>
    /// The owner name of every bay car: its building, then this. Distinct from a hall forklift's
    /// (<c>_h</c>) and from a yard's (<c>E_N_Index</c>), so the three never meet.
    /// </summary>
    public const string OwnerSuffix = "_c";

    public static string OwnerOf(string building) => building + OwnerSuffix;

    /// <summary>The building a bay car's owner belongs to, or null if it is not one.</summary>
    public static string? BuildingOf(string owner) =>
        owner.EndsWith(OwnerSuffix, StringComparison.Ordinal) && owner.Length > OwnerSuffix.Length
            ? owner[..^OwnerSuffix.Length] : null;

    /// <summary>
    /// Which of <paramref name="count"/> ordinary cars stands in the bay of furniture piece
    /// <paramref name="index"/>: a stable hash of the building and the piece, so the server and every
    /// client agree without a byte sent, and one bay changing never shuffles another.
    /// </summary>
    public static int Pick(string building, int index, int count) =>
        count <= 0 ? -1 : (int)((uint)Fnv.Hash($"{building}|bay|{index}") % (uint)count);

    /// <summary>
    /// The yaw of a vehicle (radians about +Y, 0 = -Z) whose nose lies where a plan piece's front
    /// (+Z at turn 0) points: the piece turned <paramref name="turns"/> quarter turns, in an interior
    /// turned <paramref name="interiorYaw"/> in the world. A vehicle's nose is -Z in its node frame
    /// and a yaw turns the node the same way a <c>Basis(Up, yaw)</c> does, so the node must be turned
    /// half a turn past the piece. Wrapped to [0, 2 pi) as every slot's is.
    /// </summary>
    public static float NoseYaw(int turns, float interiorYaw)
    {
        const float tau = MathF.PI * 2;
        float yaw = (interiorYaw + turns * MathF.PI / 2 + MathF.PI) % tau;
        return yaw < 0 ? yaw + tau : yaw;
    }
}
