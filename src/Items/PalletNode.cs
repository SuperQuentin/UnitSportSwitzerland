using Godot;
using UnitSport.Interiors;

namespace UnitSport.Items;

/// <summary>
/// A pallet a forklift can lift (#583), drawn: a hall's own floor pallet (a child of its
/// <c>InteriorNode</c>, carved out of the merged mesh by <c>InteriorManager.AddPallets</c>) or one
/// somebody set down (a child of <see cref="PalletService"/>). Its origin is the pallet's centre on
/// its underside, its X axis along its runners. One box body, so it stops a walker and a machine;
/// hidden and passed through once it has been taken.
///
/// <para>
/// Every one in the tree is in <see cref="All"/>, which is what a driver's forks are tested against
/// (<see cref="PalletService.Tend"/>): a hall holds 6-20 of them, so a plain walk is cheaper than
/// any index would be.
/// </para>
/// </summary>
public partial class PalletNode : Node3D
{
    /// <summary>Every pallet in the tree, by id.</summary>
    public static readonly Dictionary<string, PalletNode> All = new();

    /// <summary>Its id (<see cref="Pallets.HallId"/>, <see cref="Pallets.LooseId"/>).</summary>
    public string Id { get; private set; } = "";
    public byte Load { get; private set; }
    /// <summary>A hall pallet that has been forked away: hidden, and solid no more.</summary>
    public bool Taken { get; private set; }

    private CollisionShape3D? _shape;

    /// <summary>A pallet's mesh per load byte: at most 256, built on first use.</summary>
    private static readonly Dictionary<byte, ArrayMesh> Meshes = new();

    private static ArrayMesh MeshOf(byte load)
    {
        if (Meshes.TryGetValue(load, out var mesh)) return mesh;
        var data = InteriorMeshBuilder.PalletPiece(load);
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Meshes[load] = mesh;
        return mesh;
    }

    /// <summary>
    /// A pallet that can be forked: <paramref name="material"/> is the hall's own lit one for a
    /// hall pallet, the figures' (lit by the sun, as the forklift is) for one set down.
    /// </summary>
    public static PalletNode Create(string id, byte load, Material material)
    {
        var node = new PalletNode { Name = "Pallet_" + id.Replace(':', '_').Replace('#', 'L'), Id = id, Load = load };
        node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = MeshOf(load), MaterialOverride = material });
        var body = new StaticBody3D { Name = "Body" };
        float depth = Pallets.Depth(load);
        node._shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(Pallets.Length, Pallets.Height, depth) },
            Position = new Vector3(0, Pallets.Height * 0.5f, 0),
        };
        body.AddChild(node._shape);
        node.AddChild(body);
        return node;
    }

    /// <summary>
    /// The pallet as it rides on the forks (<c>ForkliftMast.Carry</c>): no body, its runners along
    /// the tines or <paramref name="across"/> them as it was picked up, and sitting
    /// <see cref="Pallets.Seat"/> below their top face, so it left the floor at exactly the height
    /// it was lifted from.
    /// </summary>
    public static Node3D Carried(byte load, bool across = false)
    {
        var node = new Node3D();
        node.AddChild(new MeshInstance3D
        {
            Name = "Mesh", Mesh = MeshOf(load), MaterialOverride = Avatar.HumanMeshBuilder.FigureMaterial(),
            // runners (+X) along the carriage's forward (-Z, the rig is drawn turned), or across it
            Transform = new Transform3D(new Basis(Vector3.Up, CarriedYaw(across)), new Vector3(0, -Pallets.Seat, 0)),
        });
        return node;
    }

    /// <summary>
    /// A pallet's yaw against the forklift carrying it: runners along the tines a quarter turn
    /// (its +X onto the machine's forward, -Z), across them none. Set down, its yaw is the
    /// machine's plus this.
    /// </summary>
    public static float CarriedYaw(bool across) => across ? 0f : Mathf.Pi * 0.5f;

    /// <summary>Hides a taken hall pallet (and lets things through it), or puts it back.</summary>
    public void SetTaken(bool taken)
    {
        if (taken == Taken) return;
        Taken = taken;
        Visible = !taken;
        _shape?.SetDeferred(CollisionShape3D.PropertyName.Disabled, taken);
    }

    /// <summary>
    /// Every pallet the game draws, in the model viewer (--models): each goods on each deck, as it
    /// rides on the forks, and a forklift carrying one raised.
    /// </summary>
    [Core.Showcase("Pallets")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcasePallets()
    {
        foreach (var (goods, roll) in new[] { (PalletGoods.Cartons, 0.1f), (PalletGoods.Drums, 0.6f), (PalletGoods.Sacks, 0.8f), (PalletGoods.Wrapped, 0.95f) })
            foreach (float depth in new[] { Pallets.NarrowDepth, Pallets.SquareDepth })
            {
                byte load = Pallets.LoadOf(roll, depth);
                yield return ($"{goods}, {depth:F1} m deck", () => Carried(load));
            }
        yield return ("On the forks", () =>
            Avatar.ForkliftMeshBuilder.CreateRig(1.4f, null, Pallets.Carried(Pallets.LoadOf(0.6f, Pallets.NarrowDepth))));
    }

    public override void _EnterTree()
    {
        All[Id] = this;
        SetTaken(PalletService.Instance?.IsTaken(Id) == true);
    }

    public override void _ExitTree()
    {
        if (All.TryGetValue(Id, out var me) && me == this) All.Remove(Id);
    }
}
