using Godot;
using UnitSport.Build;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Core.Collision;

/// <summary>
/// One thing a player can run or drive into. <see cref="Build"/> makes it standing at a world
/// transform (a vehicle places itself from its state, so it needs the origin); <see cref="Drawn"/>
/// makes what it looks like when the spawned node draws nothing (a parked vehicle headless): the
/// check aims at what is drawn and judges by what collides.
/// </summary>
public sealed record CollisionTarget(string Category, string Name, Func<WorldOrigin, Transform3D, Node3D> Build,
    Func<Node3D>? Drawn = null)
{
    public string Key => $"{Category}/{Name}";
}

/// <summary>
/// Every collidable thing, enumerated from the registries that make them (#699,
/// <c>docs/notes/general/collision-matrix.md</c>): every ride <see cref="Rideable.Create"/> knows,
/// parked as a <see cref="VehicleBody"/>; every <see cref="PlacedKind"/>; every allowed build piece;
/// a pallet; every door leaf. A new car, gadget or piece is in it by itself. Shared by
/// <see cref="CollisionMatrixProbe"/> (<c>--collidecheck</c>) and <see cref="CollisionSandbox"/>
/// (<c>--collidesandbox</c>).
/// </summary>
public static class CollisionTargets
{
    public static IEnumerable<CollisionTarget> All()
    {
        for (int k = 0; k <= byte.MaxValue; k++)
        {
            var kind = (RideKind)k;
            if (Rideable.Create(kind) is not { IsVehicle: true } ride) continue;
            string category = ride switch
            {
                Car => "Cars",
                Motorbike => "Motorbikes",
                Truck => "Trucks",
                Boat => "Boats",
                Airliner or Helicopter or Player.Plane => "Aircraft",
                Bicycle => "Bicycles",
                _ => "Machines",
            };
            int rider = k;
            yield return new(category, $"{ride.Label} [{k}]", (origin, at) => VehicleBody.Create(
                new VehicleState(kind, origin.ToGlobal(at.Origin), at.Basis.GetEuler().Y, Vector3.Zero, ride.MaxHealth,
                    EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0, Name: $"veh_collide_{rider}_{Mathf.Abs(at.Origin.X):F0}_{Mathf.Abs(at.Origin.Z):F0}"),
                null, origin),
                () => Rideable.Create(kind)!.BuildParkedVisual(rider));
        }

        foreach (var kind in Enum.GetValues<PlacedKind>())
        {
            if (kind == PlacedKind.None) continue;
            var placed = new PlacedObject(0, kind, "", 0, 0, 0, Quaternion.Identity, kind switch
            {
                PlacedKind.RopeLadder => "4",
                _ => "",
            });
            yield return new("Placed", kind.ToString(), (_, at) => Placed(PlacedObjects.VisualOf(placed) ?? new Node3D(), at));
        }

        foreach (var piece in Enum.GetValues<PieceKind>())
            foreach (var material in Enum.GetValues<BuildMaterial>())
                if (BuildGrid.Allowed(piece, material))
                    yield return new("Build", $"{piece} {material}", (_, at) => Piece(piece, material, at));

        yield return new("Props", "Pallet", (_, at) => Placed(PalletNode.Create("collide", 1, new StandardMaterial3D()), at));

        var doorMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.4f, 0.25f) };
        yield return new("Doors", "House door", (_, at) => Interiors.DoorLeaf.Create("collide", at, 1f, 2.1f, Terrain.Format.BuildingKind.House, doorMaterial));
        yield return new("Doors", "Barn pair", (_, at) => Interiors.DoorLeaf.CreateOutward("collide", at, 3.2f, 3.2f, Terrain.Format.BuildingKind.Agricultural, doorMaterial));
        yield return new("Doors", "Garage roll-up", (_, at) => Interiors.DoorLeaf.CreateRollUp("collide", at, 2.6f, 2.3f, doorMaterial));
        yield return new("Doors", "Roll-up from inside", (_, at) => Interiors.DoorLeaf.CreateShutter("collide", at, 2.6f, 2.3f, Interiors.DoorHang.RollUp, Terrain.Format.BuildingKind.House, doorMaterial));
        yield return new("Doors", "Barn pair from inside", (_, at) => Interiors.DoorLeaf.CreateShutter("collide", at, 3.2f, 3.2f, Interiors.DoorHang.OutwardPair, Terrain.Format.BuildingKind.Agricultural, doorMaterial));
    }

    /// <summary>
    /// The known holes (#699): a target (its name up to the ride number), a mover, the lanes that
    /// go wrong, and why. A run listed may go wrong (a ghost or an invisible wall; some only in some
    /// batches), a run not listed must not, and a line none of whose runs goes wrong any more fails
    /// too: the gap is fixed, take it out. Measured by <c>--collidecheck</c>; the lanes are
    /// <see cref="CollisionMatrixProbe"/>'s.
    /// </summary>
    private static readonly (string Target, string Mover, string[] Lanes, string Why)[] Gaps =
    {
        // DoorLeaf.CreateOutward / CreateRollUp: shut, the facade's building collision behind stops you
        ("Doors/Barn pair", "*", new[] { "*" }, "a facade leaf is never solid, the building's wall is"),
        ("Doors/Garage roll-up", "*", new[] { "*" }, "a facade leaf is never solid, the building's wall is"),
        // parked under the VehicleManager, as in the game
        ("Aircraft/Airbus A320", "car", new[] { "side", "side-", "end" }, "a car goes through the parked fuselage"),
        ("Aircraft/Airbus A320", "walk", new[] { "end" }, "a walker goes through the parked nose"),
        ("Aircraft/Military cargo plane", "car", new[] { "side", "end", "end-" }, "a car goes through the parked fuselage"),
        ("Aircraft/Military cargo plane", "walk", new[] { "side", "end", "end-" }, "a walker goes through the parked fuselage"),
        ("Aircraft/Antonov AN-124", "car", new[] { "side", "end", "end-" }, "a car goes through the parked fuselage"),
        ("Aircraft/Antonov AN-124", "walk", new[] { "side", "end" }, "a walker goes through the parked fuselage"),
        // Excavator.ParkedBox: the tracks and the house, not the boom, stick and bucket
        ("Machines/Excavator", "*", new[] { "side", "side-", "side+", "end" }, "the parked box leaves out the boom, stick and bucket"),
        // WheelLoader / Telehandler / MiniExcavator ParkedBox: one end is left out
        ("Machines/Wheel loader", "*", new[] { "side-" }, "the parked box leaves out the arms and bucket"),
        ("Machines/Wheel loader (forks)", "*", new[] { "side-" }, "the parked box leaves out the arms and forks"),
        ("Machines/Mini excavator", "*", new[] { "side-" }, "the parked box leaves out the boom and bucket"),
        ("Machines/Telehandler", "*", new[] { "side-" }, "the parked box leaves out the boom"),
        // Rideable.ParkedBox, measured without the rotor: one box from the ground up, under the tail boom too
        ("Aircraft/Helicopter", "car", new[] { "side+" }, "the parked box is solid under the tail boom"),
        // CampfireNode's cylinder is wider and taller than the logs drawn
        ("Placed/Campfire", "walk", new[] { "side", "end" }, "its collider stands round the low logs"),
    };

    /// <summary>
    /// The gap line (its index, and why) that lets <paramref name="target"/> go wrong for this mover
    /// on this lane; null when none does.
    /// </summary>
    public static (int Line, string Why)? Gap(CollisionTarget target, string mover, string lane)
    {
        for (int i = 0; i < Gaps.Length; i++)
        {
            var g = Gaps[i];
            if ((target.Key == g.Target || target.Key.StartsWith(g.Target + " [")) && (g.Mover == "*" || g.Mover == mover)
                && (g.Lanes[0] == "*" || Array.IndexOf(g.Lanes, lane) >= 0))
                return (i, g.Why);
        }
        return null;
    }

    /// <summary>A gap line as written, for the report.</summary>
    public static string GapLine(int line) => $"{Gaps[line].Target} {Gaps[line].Mover} {string.Join("/", Gaps[line].Lanes)}";

    /// <summary>
    /// Adds the target to <paramref name="parent"/>, standing at <paramref name="at"/>. A vehicle
    /// goes under the <see cref="VehicleManager"/> as the game parks it (a walker builds a walkable
    /// one's deck only from there), and places itself from its state; a facade leaf is top level
    /// (its transform is the world one).
    /// </summary>
    public static Node3D Spawn(CollisionTarget target, Node3D parent, WorldOrigin origin, Transform3D at)
    {
        var node = target.Build(origin, at);
        (node is VehicleBody && VehicleManager.Instance is { } vehicles ? vehicles : parent).AddChild(node);
        if (node is not VehicleBody) node.GlobalTransform = at;
        return node;
    }

    /// <summary>
    /// Every triangle drawn at or under <paramref name="root"/>, in <paramref name="root"/>'s own
    /// space, three vertices each (as <see cref="Avatar.MeshBounds.Of"/> walks it).
    /// </summary>
    public static List<Vector3> DrawnTriangles(Node3D root)
    {
        var tris = new List<Vector3>();
        Walk(root, Transform3D.Identity, tris);
        return tris;

        static void Walk(Node3D n, Transform3D toRoot, List<Vector3> tris)
        {
            if (n is MeshInstance3D { Mesh: { } mesh, Visible: true })
                foreach (var v in mesh.GetFaces()) tris.Add(toRoot * v);
            foreach (var child in n.GetChildren())
                if (child is Node3D c) Walk(c, toRoot * c.Transform, tris);
        }
    }

    /// <summary>Whether any of <paramref name="tris"/> (three vertices each) reaches into <paramref name="box"/>.</summary>
    public static bool Crosses(IReadOnlyList<Vector3> tris, Aabb box)
    {
        var poly = new List<Vector3>(9);
        var next = new List<Vector3>(9);
        var lo = box.Position;
        var hi = box.End;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            var a = tris[t];
            var b = tris[t + 1];
            var c = tris[t + 2];
            if (Mathf.Max(a.X, Mathf.Max(b.X, c.X)) < lo.X || Mathf.Min(a.X, Mathf.Min(b.X, c.X)) > hi.X
                || Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)) < lo.Y || Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)) > hi.Y
                || Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) < lo.Z || Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) > hi.Z)
                continue;
            // clipped by the box's six planes (Sutherland-Hodgman): anything left is inside
            poly.Clear();
            poly.Add(a); poly.Add(b); poly.Add(c);
            for (int axis = 0; axis < 3 && poly.Count > 0; axis++)
            {
                Clip(poly, next, axis, lo[axis], +1);
                Clip(next, poly, axis, hi[axis], -1);
            }
            if (poly.Count > 0) return true;
        }
        return false;

        // keeps the part of the polygon where sign * (p[axis] - at) >= 0
        static void Clip(List<Vector3> from, List<Vector3> to, int axis, float at, int sign)
        {
            to.Clear();
            for (int i = 0; i < from.Count; i++)
            {
                var p = from[i];
                var q = from[(i + 1) % from.Count];
                float dp = sign * (p[axis] - at), dq = sign * (q[axis] - at);
                if (dp >= 0) to.Add(p);
                if (dp >= 0 != dq >= 0) to.Add(p + (q - p) * (dp / (dp - dq)));
            }
        }
    }

    /// <summary>Whether <paramref name="collider"/> is <paramref name="target"/> or a body under it.</summary>
    public static bool Owns(Node target, GodotObject? collider) =>
        collider is Node n && (n == target || target.IsAncestorOf(n));

    private static Node3D Placed(Node3D node, Transform3D at)
    {
        node.Transform = at;
        return node;
    }

    /// <summary>A build piece as <see cref="StructureVisuals"/> draws it, without the structure around it.</summary>
    private static Node3D Piece(PieceKind kind, BuildMaterial material, Transform3D at)
    {
        var body = new StaticBody3D { Name = $"{kind}_{material}", Transform = at };
        body.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = StructureMeshes.Mesh(kind, material), MaterialOverride = ItemDefs.Material });
        foreach (var (shape, shapeAt) in StructureMeshes.Colliders(kind, material))
            body.AddChild(new CollisionShape3D { Shape = shape, Transform = shapeAt });
        return body;
    }
}
