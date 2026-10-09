using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Interiors;

/// <summary>
/// The cars standing in an underground car park's bays as vehicles a player can get into and drive
/// out through the garage door (#558, PR 3), by the pattern of <see cref="HallForklifts"/> (#630):
/// the plan only holds a <see cref="FurnitureType.Car"/> piece in a <see cref="RoomType.CarPark"/>
/// room, and this is what makes it a <see cref="VehicleSlot"/> a vehicle can be woken from.
/// <b>Nothing about it is ever sent</b>: the slot is a pure function of the plan (and of
/// <see cref="HallCarRule"/>'s hash), so the server works it out from its own layout when a peer in
/// that building asks, and a client cannot conjure a car where it likes.
///
/// <para>
/// A bay car is left out of the merged mesh (<see cref="InteriorMeshBuilder.IsCarvedOut"/>) and drawn by
/// <see cref="ParkedCars"/> instead, which is solid and wakes on being aimed at.
/// </para>
/// </summary>
public static class HallCars
{
    public static string OwnerOf(string building) => HallCarRule.OwnerOf(building);

    /// <summary>The building a bay car's owner belongs to, or null if it is not one.</summary>
    public static string? BuildingOf(string owner) => HallCarRule.BuildingOf(owner);

    /// <summary>A car standing on the floor of a car park's bay: a vehicle asleep, rather than a prop.</summary>
    public static bool IsBayCar(InteriorLayout l, FurniturePlan p) =>
        p.Type == FurnitureType.Car && p.Lift == 0f && l.RoomOf(p)?.Type == RoomType.CarPark;

    /// <summary>
    /// Where the car's origin stands and which way it faces, in the interior's own frame: the
    /// piece's centre on the floor, turned so the piece's front (+Z at turn 0, the bonnet's way) is
    /// the car's nose (-Z in a rig's node frame).
    /// </summary>
    public static Transform3D LocalFrame(InteriorLayout l, FurniturePlan f) =>
        new(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Basis(Vector3.Up, Mathf.Pi),
            new Vector3(f.X, l.FloorY(f.Floor), f.Z));

    /// <summary>
    /// The slot of the bay car at furniture index <paramref name="index"/>, or null if there is
    /// none there. LV95 and altitude, so it means the same place after an origin rebase and on every
    /// peer, 3 km down under its building. The kind is an ordinary road car picked by a stable hash
    /// (<see cref="HallCarRule.Pick"/>), as a street car park's are.
    /// </summary>
    public static VehicleSlot? SlotOf(InteriorLayout l, int index, WorldOrigin origin)
    {
        if (index < 0 || index >= l.Furniture.Count || !IsBayCar(l, l.Furniture[index])) return null;
        var f = l.Furniture[index];
        var world = InteriorManager.PlacementFor(l, origin) * LocalFrame(l, f);
        var at = origin.ToGlobal(world.Origin);
        var kinds = DormantVehicles.ParkedKinds;
        int pick = HallCarRule.Pick(l.Key, index, kinds.Length);
        if (pick < 0) return null;
        return new VehicleSlot(HallCarRule.OwnerOf(l.Key), index, at.E, at.N, at.Alt,
            HallCarRule.NoseYaw(f.Turns, l.Yaw), kinds[pick], (byte)((uint)Fnv.Hash($"{l.Key}|paint|{index}") & 7), false);
    }
}
