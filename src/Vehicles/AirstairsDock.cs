using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// Where airstairs dock (#417), worked out from the aircraft's walkable deck alone, so any type with
/// one docks the same way (the AN-124's crew door and its kneeling, #419, the freighter's, #420): a
/// door's sill is the deck's <see cref="DeckPart.DoorStep"/> box of that door, out through the skin
/// (<see cref="A320Deck"/>). Its outer edge, its top and the way out of the cabin are all the stairs
/// need: they face it, the bridge plate's lip <see cref="AirstairsLayout.SillOverlap"/> inside the
/// edge, the platform <see cref="AirstairsLayout.DockAbove"/> over the sill.
/// See <c>docs/notes/vehicles/airstairs.md</c>.
/// </summary>
public static class AirstairsDock
{
    /// <summary>A door's sill, world space: the middle of its outer edge at its top, and the horizontal way out.</summary>
    public readonly record struct Sill(Node3D Host, int Door, Vector3 Edge, Vector3 Out);

    /// <summary>A door's sill in the deck's own frame (node space): edge point at its top, and the level way out; null when the deck has none.</summary>
    public static (Vector3 Edge, Vector3 Out)? LocalSill(VehicleDeck deck, int door)
    {
        var middle = deck.Aboard.GetCenter();
        var half = deck.Aboard.Size * 0.5f;
        foreach (var box in deck.Boxes)
        {
            if (box.Part != DeckPart.DoorStep || box.Door != door) continue;
            var top = box.Centre + box.Basis.Y * (box.Size.Y * 0.5f);
            // out of the cabin, measured against its size: a door well forward in a long fuselage is
            // still out through its side (the cabin's short axis), not out through its nose
            var off = (top - middle) with { Y = 0 };
            var away = new Vector3(off.X / Mathf.Max(half.X, 0.1f), 0, off.Z / Mathf.Max(half.Z, 0.1f));
            // the box's level axis that points most out of the cabin
            var ax = box.Basis.X with { Y = 0 };
            var az = box.Basis.Z with { Y = 0 };
            bool alongX = Mathf.Abs(ax.Normalized().Dot(away)) >= Mathf.Abs(az.Normalized().Dot(away));
            var axis = (alongX ? ax : az).Normalized();
            float depth = (alongX ? box.Size.X : box.Size.Z) * 0.5f;
            if (axis.Dot(away) < 0f) axis = -axis;
            return (top + axis * depth, axis);
        }
        return null;
    }

    /// <summary>The node whose transform is section <paramref name="k"/>'s frame, as drawn (a parked one's posed visual, or its body headless).</summary>
    public static Node3D? FrameOf(Node3D host, int k) => host switch
    {
        VehicleBody v => k == 0 ? v.Visual ?? v : v.Visual?.GetNodeOrNull<Node3D>($"Section{k}"),
        FootPlayer p => k == 0 ? p.Visual : null,
        _ => null,
    };

    /// <summary>What can be docked to: a walkable aircraft (an airliner's cabin), parked or with somebody at its controls.</summary>
    private static Rideable? DockableRide(Node3D host) => host switch
    {
        VehicleBody { Wrecked: false, Posed: true } v when v.Ride is Flyer { Walkable: true } => v.Ride,
        FootPlayer p when p.RideModel is Flyer { Walkable: true } jet && !p.RidingAlong => jet,
        _ => null,
    };

    /// <summary>Every door sill within <paramref name="reach"/> m (level) of <paramref name="point"/>, into <paramref name="into"/> (cleared first).</summary>
    public static void SillsNear(SceneTree tree, Vector3 point, float reach, List<Sill> into)
    {
        into.Clear();
        if (VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody v) Collect(v, point, reach, into);
        foreach (var s in PlayerSnapshot.Of(tree))
            if (s.Ride != RideKind.OnFoot) Collect(s.Player, point, reach, into);
    }

    private static void Collect(Node3D host, Vector3 point, float reach, List<Sill> into)
    {
        if (DockableRide(host) is not { } ride) return;
        // a whole airliner is 40 m: only one whose middle is near enough to have a door in reach
        if ((host.GlobalPosition - point).Length() > reach + 40f) return;
        foreach (var deck in ride.Decks)
        {
            if (FrameOf(host, deck.Section) is not { } frame || !frame.IsInsideTree()) continue;
            var xf = frame.GlobalTransform.Orthonormalized();
            for (int door = 0; door < 8; door++)
            {
                if (LocalSill(deck, door) is not { } local) continue;
                // a door at the ground (a bus's step) is not for stairs
                if (local.Edge.Y < AirstairsLayout.MinHeight - 0.5f) continue;
                var edge = xf * local.Edge;
                var outward = (xf.Basis * local.Out) with { Y = 0 };
                if (outward.LengthSquared() < 1e-4f) continue;
                if (new Vector2(edge.X - point.X, edge.Z - point.Z).Length() > reach) continue;
                into.Add(new Sill(host, door, edge, outward.Normalized()));
            }
        }
    }

    /// <summary>
    /// Where the stairs stand docked at <paramref name="sill"/>: their origin (on the ground at
    /// <paramref name="groundY"/>), heading (a node's <c>Rotation.Y</c>) and platform height.
    /// </summary>
    public static (Vector3 Origin, float Yaw, float Height) Pose(in Sill sill, float groundY) =>
        Pose(sill.Edge, sill.Out, groundY);

    public static (Vector3 Origin, float Yaw, float Height) Pose(Vector3 edge, Vector3 outward, float groundY)
    {
        // the truck faces the door: its nose (−Z) along −out, so its back (+Z) along out
        float yaw = Mathf.Atan2(outward.X, outward.Z);
        var lip = edge - outward * AirstairsLayout.SillOverlap;
        var origin = lip + outward * AirstairsLayout.LipZ;
        return (origin with { Y = groundY }, yaw, edge.Y - groundY + AirstairsLayout.DockAbove);
    }

    /// <summary>The platform's lip in the world, for stairs standing at <paramref name="frame"/>.</summary>
    public static Vector3 Lip(Transform3D frame) => frame * new Vector3(0, 0, -AirstairsLayout.LipZ);

    /// <summary>
    /// The sill the stairs at <paramref name="frame"/> are docked at, if any: its edge within 0.5 m
    /// (level) of where the lip would put it, facing within 10°.
    /// </summary>
    public static Sill? DockedSill(Transform3D frame, List<Sill> near)
    {
        var lip = Lip(frame);
        var back = frame.Basis.Z with { Y = 0 };
        back = back.Normalized();
        Sill? best = null;
        float bestD = 0.5f;
        foreach (var s in near)
        {
            var want = s.Edge - s.Out * AirstairsLayout.SillOverlap;
            float d = new Vector2(want.X - lip.X, want.Z - lip.Z).Length();
            if (d < bestD && back.Dot(s.Out) > 0.985f) { best = s; bestD = d; }
        }
        return best;
    }

    /// <summary>
    /// The door the driver is bringing the stairs to: the nearest sill whose docked spot is within
    /// <see cref="Reach"/> (level) of the lip, ahead of it, the truck turned within 40° of square
    /// to it, and a sill in the platform's range.
    /// </summary>
    public static Sill? Approach(Transform3D frame, List<Sill> near)
    {
        var lip = Lip(frame);
        var back = (frame.Basis.Z with { Y = 0 }).Normalized();
        Sill? best = null;
        float bestD = Reach;
        foreach (var s in near)
        {
            var want = s.Edge - s.Out * AirstairsLayout.SillOverlap;
            float d = new Vector2(want.X - lip.X, want.Z - lip.Z).Length();
            float h = s.Edge.Y - frame.Origin.Y + AirstairsLayout.DockAbove;
            if (d >= bestD || back.Dot(s.Out) < 0.76f || h < AirstairsLayout.MinHeight - 0.05f || h > AirstairsLayout.MaxHeight + 0.05f) continue;
            best = s;
            bestD = d;
        }
        return best;
    }

    /// <summary>How near the lip must come to the docked spot for the stairs to line up by themselves, m.</summary>
    public const float Reach = 3.5f;

    // ---- placing stairs at a parked aircraft (#422's stands use this) --------------------------

    /// <summary>
    /// Airstairs standing docked at door <paramref name="door"/> of the aircraft <paramref name="plane"/>
    /// describes (parked, level, on its gear at its position), or null when it has no such door.
    /// </summary>
    public static VehicleState? DockedTo(VehicleState plane, int door)
    {
        if (plane.CreateRide() is not Flyer { Walkable: true } jet) return null;
        foreach (var deck in jet.Decks)
        {
            if (deck.Section != 0 || LocalSill(deck, door) is not { } local) continue;
            var turn = new Basis(Vector3.Up, plane.Yaw);
            var (origin, yaw, height) = Pose(turn * local.Edge, turn * local.Out, 0f);
            var stairs = new Airstairs { Height = AirstairsLayout.Clamp(height) };
            return new VehicleState(RideKind.Airstairs, plane.Position + origin, yaw, Vector3.Zero, stairs.MaxHealth,
                false, false, 0f, VehicleState.Now, Flags: stairs.PackFlags());
        }
        return null;
    }

    /// <summary>The A320's boarding doors, L1 and L2 (front and rear left).</summary>
    public static readonly int[] A320Doors = { 0, 2 };

    /// <summary>
    /// Places airstairs docked at <paramref name="doors"/> of a parked aircraft (two at an A320's
    /// L1 and L2 by default) through <see cref="VehicleManager.Place"/>: the server's, or offline
    /// this client's. Names are <paramref name="name"/> + the door. Returns how many were placed.
    /// </summary>
    public static int PlaceAt(VehicleManager vehicles, VehicleState plane, string name, params int[] doors)
    {
        if (doors.Length == 0) doors = A320Doors;
        int placed = 0;
        foreach (int door in doors)
            if (DockedTo(plane, door) is { } stairs && vehicles.Place(stairs, $"{name}_{door}") != null) placed++;
        return placed;
    }
}
