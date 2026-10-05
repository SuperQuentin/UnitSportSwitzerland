using Godot;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// One parked vehicle the local player can act on, and through which door (#261). A car is
/// worked door by door: <see cref="Door"/> is that door's bit, open or shut. A machine without
/// hinged doors (a bike, a plane, a truck's cab) has <see cref="Door"/> 0 and is got onto as a whole.
/// </summary>
public readonly record struct VehicleAim(VehicleBody Vehicle, byte Door)
{
    public bool HasDoor => Door != 0;
    public bool DoorOpen => (Vehicle.DoorsOpen & Door) != 0;

    /// <summary>What E does here, for the prompt bar.</summary>
    public string Action => HasDoor && !DoorOpen ? "Open the door" : $"Get in the {Vehicle.Ride.Label.ToLowerInvariant()}";
}

/// <summary>
/// Which vehicle (and which door of it) the local player is at, and the border round it (#261).
///
/// <para>
/// Getting in used to be "any vehicle within 3.5 m of the player": standing at the boot of one car
/// got you into the next one over. Now it is the door the view points at, from where a hand could
/// reach it; or, with the view elsewhere, the door the player stands at. A car door has to be open
/// to get in through it (E opens it, E again gets in); G works it either way.
/// </para>
/// </summary>
public static class VehicleReach
{
    /// <summary>How far (flat) from the middle of a door a body can stand and work it, m.</summary>
    public const float DoorReach = 1.35f;
    /// <summary>How far from the hull of a vehicle without doors a body can stand and get on, m.</summary>
    public const float HullReach = 1.3f;
    /// <summary>How far from a cab's or bus's door (its <c>EntryPoint</c>) to climb in, m.</summary>
    public const float EntryReach = 1.6f;
    /// <summary>How far past the chest the view ray looks for a vehicle, m.</summary>
    private const float AimRange = 3.2f;

    /// <summary>What the local player is at right now, refreshed every frame by the item controller.</summary>
    public static VehicleAim? Current { get; private set; }

    private static Node3D? _outlined;

    /// <summary>The vehicle and door <paramref name="player"/> can act on, or null.</summary>
    public static VehicleAim? Find(FootPlayer player)
    {
        if (VehicleManager.Instance is not { } vehicles) return null;
        var chest = player.GlobalPosition + Vector3.Up * 1.0f;

        // what the view points at: the door the eye is on, if a hand can get to it
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        var query = PhysicsRayQueryParameters3D.Create(from, from + forward * (AimRange + from.DistanceTo(chest)), uint.MaxValue,
            new Godot.Collections.Array<Rid> { player.GetRid() });
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && VehicleOf(hit["collider"].AsGodotObject() as Node) is { } aimed && vehicles.Enterable(aimed) && FromOutside(aimed)
            && At(player, aimed, hit["position"].AsVector3()) is { } pointed)
            return pointed;

        // a car park's dormant car (#499): aiming at one wakes it, and it is a real vehicle a frame
        // or two later, which the next call finds the ordinary way. Only on the aim ray, never on
        // mere proximity: walking past a full lot must not promote eighty cars.
        if (hit.Count > 0 && DormantBodyOf(hit["collider"].AsGodotObject() as Node) is { } dormant)
        {
            DormantVehicles.Instance?.Wake(dormant.Slot);
            return null;
        }

        // else whatever the player stands at, the nearest first
        VehicleAim? best = null;
        float bestD = float.MaxValue;
        foreach (var node in vehicles.GetChildren())
        {
            if (node is not VehicleBody v || !vehicles.Enterable(v) || !FromOutside(v)) continue;
            float hull = HullDistance(v, v.Ride.ParkedBox, chest);
            if (hull > DoorReach + 0.5f || hull >= bestD) continue;
            if (At(player, v, chest) is { } here) { best = here; bestD = hull; }
        }
        return best;
    }

    /// <summary>The dormant car a collider belongs to, or null (#499).</summary>
    private static DormantBody? DormantBodyOf(Node? node)
    {
        for (var n = node; n != null; n = n.GetParent())
            if (n is DormantBody body) return body;
        return null;
    }

    /// <summary>
    /// Whether E from outside may take this vehicle (#384): always, but a walkable one (a bus, the
    /// steamer) only with <see cref="Core.GameSettings.BoardWalkableFromOutside"/> on; off, it is
    /// walked aboard and driven from inside.
    /// </summary>
    public static bool FromOutside(VehicleBody v) => Core.GameSettings.Current.BoardWalkableFromOutside || !v.Ride.DrivenFromInside;

    /// <summary>The door of <paramref name="v"/> nearest <paramref name="point"/> if the player can reach it, or the whole machine when it has none.</summary>
    private static VehicleAim? At(FootPlayer player, VehicleBody v, Vector3 point)
    {
        var feet = player.GlobalPosition;
        if (v.Rig is { DoorCount: > 0 } rig)
        {
            var (bit, _) = rig.NearestDoor(point);
            if (bit != 0 && Reaches(feet, rig.DoorCentre(bit))) return new VehicleAim(v, bit);
            // pointed past it (the bonnet, the far side): the door the player stands at, if any
            (bit, _) = rig.NearestDoor(feet + Vector3.Up);
            return bit != 0 && Reaches(feet, rig.DoorCentre(bit)) ? new VehicleAim(v, bit) : null;
        }
        var entry = v.Ride.EntryPoint;
        if (entry != Vector3.Zero)
        {
            var door = v.ToGlobal(entry);
            return new Vector2(door.X - feet.X, door.Z - feet.Z).Length() <= EntryReach && Mathf.Abs(door.Y - feet.Y) < 2f
                ? new VehicleAim(v, 0) : null;
        }
        return HullDistance(v, v.Ride.ParkedBox, feet + Vector3.Up * 0.8f) <= HullReach ? new VehicleAim(v, 0) : null;
    }

    private static bool Reaches(Vector3 feet, Vector3 door) =>
        new Vector2(door.X - feet.X, door.Z - feet.Z).Length() <= DoorReach && Mathf.Abs(door.Y - (feet.Y + 1f)) < 1.6f;

    /// <summary>How far a point is outside a box standing in <paramref name="frame"/>'s space (0 inside).</summary>
    public static float HullDistance(Node3D frame, (Vector3 Centre, Vector3 Size) box, Vector3 point)
    {
        var local = frame.ToLocal(point) - box.Centre;
        var q = local.Abs() - box.Size * 0.5f;
        return new Vector3(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0), Mathf.Max(q.Z, 0)).Length();
    }

    private static VehicleBody? VehicleOf(Node? node)
    {
        for (; node != null; node = node.GetParent())
            if (node is VehicleBody v) return v;
        return null;
    }

    /// <summary>Points at <paramref name="aim"/> (null for nothing): the border goes on the door, or on the whole machine.</summary>
    public static void Point(VehicleAim? aim)
    {
        Current = aim;
        Node3D? target = aim is { } a && GodotObject.IsInstanceValid(a.Vehicle)
            ? (a.HasDoor ? a.Vehicle.Rig?.DoorPivot(a.Door) : null) ?? a.Vehicle.Visual
            : null;
        if (target == _outlined && (target == null || GodotObject.IsInstanceValid(target))) return;
        if (_outlined != null && GodotObject.IsInstanceValid(_outlined)) Items.Highlight.Set(_outlined, false);
        _outlined = target;
        // a door flush in the body: its edge is drawn over the body (the stencil border, #401)
        if (target != null) Items.Highlight.Set(target, true);
    }
}
