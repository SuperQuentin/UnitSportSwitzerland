using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// What moves inside an apartment block (#557): every elevator's sliding doors on every floor,
/// and a leaf in every flat's front doorway. Built with the interior, driven by
/// <see cref="InteriorManager"/> from the server's state.
/// </summary>
public partial class InteriorNode
{
    /// <summary>One floor's elevator doors: the two panels and what stops you walking into the shaft.</summary>
    private sealed record LiftDoors(Node3D Left, Node3D Right, CollisionShape3D Shut, float Half)
    {
        public float Open = -1f;
    }

    private readonly Dictionary<(int Lift, int Floor), LiftDoors> _liftDoors = new();
    private readonly Dictionary<int, DoorLeaf> _innerLeaves = new();
    private readonly Dictionary<int, float> _innerSwing = new();

    /// <summary>Slides one floor's elevator doors, 0 shut to 1 open; solid unless well open.</summary>
    public void SetLiftDoors(int lift, int floor, float open)
    {
        if (!_liftDoors.TryGetValue((lift, floor), out var d) || Math.Abs(d.Open - open) < 0.001f) return;
        d.Open = open;
        float s = open * open * (3 - 2 * open);
        d.Left.Position = new Vector3(-d.Half / 2 - s * d.Half * 0.95f, 0, 0);
        d.Right.Position = new Vector3(d.Half / 2 + s * d.Half * 0.95f, 0, 0);
        d.Shut.Disabled = open > 0.7f;
    }

    /// <summary>Swings a flat's front door, 0 shut to 1 open.</summary>
    public void SetInnerDoor(int door, float swing)
    {
        if (!_innerLeaves.TryGetValue(door, out var leaf)) return;
        _innerSwing[door] = swing;
        leaf.SetSwing(swing);
    }

    public float InnerSwing(int door) => _innerSwing.GetValueOrDefault(door);

    private static void AddLiftDoors(InteriorNode node, Material material)
    {
        var l = node.Layout;
        for (int i = 0; i < l.Lifts.Count; i++)
        {
            var lift = l.Lifts[i];
            var (ox, oz) = lift.Outward;
            var z = new Vector3(ox, 0, oz);
            var basis = new Basis(Vector3.Up.Cross(z), Vector3.Up, z);
            float half = lift.DoorWidth / 2;
            var panel = InteriorNode.BuildMesh(InteriorMeshBuilder.LiftDoorPanel(half, lift.DoorTop), material);
            for (int f = lift.Bottom; f <= lift.Top && f < l.Floors.Count; f++)
            {
                // in the wall, between the cabin's face and the landing's: slid open, a panel is inside it
                // (a panel mesh is centred on its own origin: each leaf node sits at the middle of its half of the doorway)
                var frame = new Node3D { Name = $"Lift{i}_{f}", Transform = new Transform3D(basis, lift.WallPoint(0, l.FloorY(f), 0)) };
                var left = new Node3D { Name = "Left" };
                left.AddChild(new MeshInstance3D { Mesh = panel });
                var right = new Node3D { Name = "Right" };
                right.AddChild(new MeshInstance3D { Mesh = panel });
                frame.AddChild(left);
                frame.AddChild(right);
                var body = new StaticBody3D { Name = "Shut" };
                var shape = new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(lift.DoorWidth, lift.DoorTop, 0.1f) },
                    Position = new Vector3(0, lift.DoorTop / 2, 0),
                };
                body.AddChild(shape);
                frame.AddChild(body);
                node.AddChild(frame);
                var doors = new LiftDoors(left, right, shape, half);
                node._liftDoors[(i, f)] = doors;
                node.SetLiftDoors(i, f, 0);
            }
        }
    }

    private static void AddInnerDoors(InteriorNode node, Material material)
    {
        var l = node.Layout;
        for (int i = 0; i < l.InnerDoors.Count; i++)
        {
            var d = l.InnerDoors[i];
            var r = l.Floors[d.Floor].Rooms[d.Room];
            float y = l.FloorY(d.Floor);
            // the doorway frame as a street door's (DoorLeaf): origin on the sill at the wall line,
            // Z out of the flat, so the leaf hangs on the hall's side and swings into it
            var (at, z) = d.Side switch
            {
                Side.Front => (new Vector3(d.Center, y, r.Z0), new Vector3(0, 0, -1)),
                Side.Back => (new Vector3(d.Center, y, r.Z1), new Vector3(0, 0, 1)),
                Side.Left => (new Vector3(r.X0, y, d.Center), new Vector3(-1, 0, 0)),
                _ => (new Vector3(r.X1, y, d.Center), new Vector3(1, 0, 0)),
            };
            var doorway = new Transform3D(new Basis(Vector3.Up.Cross(z), Vector3.Up, z), at);
            var leaf = DoorLeaf.Create($"flat{i}", doorway, d.Width, d.Top, Terrain.Format.BuildingKind.Apartment, material);
            node.AddChild(leaf);
            leaf.SetSwing(0);
            node._innerLeaves[i] = leaf;
        }
    }
}
