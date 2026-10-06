using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Interiors;

/// <summary>
/// The forklifts standing in a warehouse's or a works' hall (#630), which a player can get into and
/// drive like any other (#583). The plan only holds a <see cref="FurnitureType.Forklift"/> piece;
/// this is what makes it a <see cref="VehicleSlot"/> a vehicle can be woken from, as a yard's
/// dormant vehicles are (#499), and <b>nothing about it is ever sent</b>: the slot is a pure
/// function of the plan, so the server works it out itself from the layout when a client asks to
/// wake one, and a client cannot conjure a vehicle where it likes.
///
/// <para>
/// A parked hall forklift is left out of the merged mesh (<see cref="InteriorMeshBuilder.IsParkedForklift"/>)
/// and drawn by <see cref="ParkedForklift"/> instead, which is solid and wakes on being aimed at.
/// </para>
/// </summary>
public static class HallForklifts
{
    /// <summary>
    /// The piece's size: the machine itself. 1.15 m of hull and its step, and from the counterweight
    /// to the tines' tips 3.75 m, under the 2.18 m overhead guard. It was a 1.3 x 2.1 m box before
    /// it could be driven, which a real forklift would have stuck out of by a metre either end.
    /// </summary>
    public const float Width = 1.4f, Length = 3.8f, Height = 2.2f;

    /// <summary>
    /// How far the machine's own origin (mid-wheelbase) is behind the piece's centre, m: the
    /// counterweight's back face is at -1.5 m and the tines' tips at +2.25 m, so the middle of that
    /// is +0.375 m ahead of the origin.
    /// </summary>
    public const float OriginBack = 0.375f;

    /// <summary>The owner name of every hall slot: its building, then this, so it never meets a yard's (<c>E_N_Index</c>).</summary>
    public const string OwnerSuffix = "_h";

    public static string OwnerOf(string building) => building + OwnerSuffix;

    /// <summary>The building a hall owner belongs to, or null if it is not one.</summary>
    public static string? BuildingOf(string owner) =>
        owner.EndsWith(OwnerSuffix, StringComparison.Ordinal) ? owner[..^OwnerSuffix.Length] : null;

    /// <summary>A hall's forklift as it stands: parked on the floor, rather than hung on a lift or a ramp.</summary>
    public static bool IsParked(FurniturePlan p) => p.Type == FurnitureType.Forklift && p.Lift == 0f;

    /// <summary>
    /// Where the machine's origin stands and which way it faces, in the interior's own frame: the
    /// piece's centre brought back to mid-wheelbase, turned so its front (+Z, the forks' way, at
    /// turn 0) is the machine's nose (-Z in a rig's node frame).
    /// </summary>
    public static Transform3D LocalFrame(InteriorLayout l, FurniturePlan f)
    {
        var turn = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2);
        var at = new Vector3(f.X, l.FloorY(f.Floor), f.Z) + turn * new Vector3(0, 0, -OriginBack);
        return new Transform3D(turn * new Basis(Vector3.Up, Mathf.Pi), at);
    }

    /// <summary>
    /// The slot of the hall forklift at furniture index <paramref name="index"/>, or null if there
    /// is none there. LV95 and altitude, so it means the same place after an origin rebase and on
    /// every peer, 3 km down under its building.
    /// </summary>
    public static VehicleSlot? SlotOf(InteriorLayout l, int index, WorldOrigin origin)
    {
        if (index < 0 || index >= l.Furniture.Count || !IsParked(l.Furniture[index])) return null;
        var world = InteriorManager.PlacementFor(l, origin) * LocalFrame(l, l.Furniture[index]);
        var at = origin.ToGlobal(world.Origin);
        // a vehicle's yaw: its nose (-Z) is (-sin, -cos) in world x, z
        var back = world.Basis.Z;
        float yaw = Mathf.Atan2(back.X, back.Z);
        return new VehicleSlot(OwnerOf(l.Key), index, at.E, at.N, at.Alt, DormantSlots.Wrap(yaw),
            (int)RideKind.Forklift, 0, false);
    }
}
