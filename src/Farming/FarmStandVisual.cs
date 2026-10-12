using Godot;
using UnitSport.Avatar;
using UnitSport.Items;

namespace UnitSport.Farming;

/// <summary>
/// How a farm stand looks (#494): a wooden table under a small gabled roof, six crates on it, a
/// red honesty box on a post and a "Hofladen" board. A crate shows a heap in its item's colour,
/// scaled to how full it is, so everyone sees what is for sale (<see cref="FarmStands.Changed"/>).
/// Low-poly <see cref="MeshScratch"/> boxes, origin on the ground, front toward +Z.
/// </summary>
public static class FarmStandVisual
{
    public const string HeapName = "Heap";
    private static ArrayMesh? _stand, _heap;

    /// <summary>The fixed parts: table, legs, roof, crates, box, board.</summary>
    public static ArrayMesh StandMesh()
    {
        if (_stand != null) return _stand;
        var s = new MeshScratch();
        var wood = new Color(0.62f, 0.43f, 0.24f);
        var dark = new Color(0.42f, 0.28f, 0.15f);
        var roof = new Color(0.55f, 0.18f, 0.14f);
        s.Box(new Vector3(0, 0.82f, 0), new Vector3(1.9f, 0.06f, 0.8f), wood);   // the table
        foreach (float x in new[] { -0.88f, 0.88f })
        {
            s.Box(new Vector3(x, 0.41f, 0.34f), new Vector3(0.07f, 0.82f, 0.07f), dark);
            s.Box(new Vector3(x, 1.15f, -0.34f), new Vector3(0.07f, 2.3f, 0.07f), dark);   // back posts carry the roof
        }
        s.Box(new Vector3(0, 0.30f, 0), new Vector3(1.8f, 0.04f, 0.7f), dark);   // shelf
        // the roof: two boards meeting over the back posts, sloping forward
        s.Box(new Vector3(0, 2.32f, -0.05f), new Vector3(2.2f, 0.05f, 1.15f), roof, new Basis(Vector3.Right, 0.22f));
        s.Box(new Vector3(0, 2.18f, 0.50f), new Vector3(2.2f, 0.04f, 0.10f), dark);
        // six crates
        for (int i = 0; i < FarmStandRules.Crates; i++)
        {
            var c = CrateCentre(i);
            s.Box(c + new Vector3(0, -0.07f, 0), new Vector3(0.52f, 0.02f, 0.30f), dark);
            s.Box(c + new Vector3(0, 0, 0.15f), new Vector3(0.52f, 0.14f, 0.02f), wood);
            s.Box(c + new Vector3(0, 0, -0.15f), new Vector3(0.52f, 0.14f, 0.02f), wood);
            s.Box(c + new Vector3(0.26f, 0, 0), new Vector3(0.02f, 0.14f, 0.30f), wood);
            s.Box(c + new Vector3(-0.26f, 0, 0), new Vector3(0.02f, 0.14f, 0.30f), wood);
        }
        // the honesty box on its post, by the right front leg
        s.Box(new Vector3(1.15f, 0.55f, 0.3f), new Vector3(0.06f, 1.1f, 0.06f), dark);
        s.Box(new Vector3(1.15f, 1.18f, 0.3f), new Vector3(0.26f, 0.22f, 0.20f), new Color(0.78f, 0.12f, 0.10f));
        s.Box(new Vector3(1.15f, 1.30f, 0.3f), new Vector3(0.12f, 0.012f, 0.03f), new Color(0.1f, 0.1f, 0.1f));   // the slot
        // the board under the roof's edge
        s.Box(new Vector3(0, 1.95f, 0.47f), new Vector3(1.2f, 0.26f, 0.03f), new Color(0.93f, 0.90f, 0.80f));
        return _stand = s.Build();
    }

    private static ArrayMesh HeapMesh()
    {
        if (_heap != null) return _heap;
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.5f, 0), new Vector3(1f, 1f, 1f), Colors.White);
        return _heap = s.Build();
    }

    /// <summary>Where crate <paramref name="i"/> stands on the table (3 in a row front and back).</summary>
    public static Vector3 CrateCentre(int i) => new(-0.6f + 0.6f * (i % 3), 0.93f, i < 3 ? 0.18f : -0.18f);

    /// <summary>The factory of <see cref="PlacedKind.FarmStand"/>.</summary>
    public static Node3D Visual(PlacedObject o) => new FarmStandNode(o.Id);

    /// <summary>The heaps' shared material: vertex colour times the instance's albedo.</summary>
    internal static StandardMaterial3D HeapMaterial(Color tint) => new()
    {
        AlbedoColor = tint,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
    };

    internal static ArrayMesh Heap => HeapMesh();
}

/// <summary>A placed farm stand: the mesh, a solid box, the sign, and one heap per crate redrawn when the stand changes.</summary>
public partial class FarmStandNode : StaticBody3D
{
    private readonly long _id;
    private readonly MeshInstance3D?[] _heaps = new MeshInstance3D?[FarmStandRules.Crates];
    private readonly ItemId[] _shown = new ItemId[FarmStandRules.Crates];

    public FarmStandNode(long id) => _id = id;
    public FarmStandNode() : this(0) { }

    /// <summary>Probes: how many crates show a heap.</summary>
    public int HeapsShown { get; private set; }

    public override void _Ready()
    {
        AddChild(new MeshInstance3D { Mesh = FarmStandVisual.StandMesh(), MaterialOverride = ItemDefs.Material });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.9f, 1.0f, 0.8f) }, Position = new Vector3(0, 0.5f, 0) });
        AddChild(new Label3D
        {
            Text = "HOFLADEN · self-service",
            FontSize = 48,
            PixelSize = 0.0042f,
            Modulate = new Color(0.25f, 0.45f, 0.15f),
            OutlineSize = 0,
            Position = new Vector3(0, 1.95f, 0.49f),
        });
        if (FarmStands.Instance is { } stands) stands.Changed += OnChanged;
        Redraw();
    }

    public override void _ExitTree()
    {
        if (FarmStands.Instance is { } stands) stands.Changed -= OnChanged;
    }

    private void OnChanged(long id)
    {
        if (id == _id) Redraw();
    }

    private void Redraw()
    {
        StandState? s = null;
        FarmStands.Instance?.All.TryGetValue(_id, out s);
        int shown = 0;
        for (int i = 0; i < FarmStandRules.Crates; i++)
        {
            var slot = s != null && i < s.Slots.Count && s.Slots[i].Count > 0 ? s.Slots[i] : null;
            if (slot == null)
            {
                if (_heaps[i] != null) _heaps[i]!.Visible = false;
                continue;
            }
            shown++;
            var heap = _heaps[i];
            if (heap == null)
            {
                heap = _heaps[i] = new MeshInstance3D { Name = $"{FarmStandVisual.HeapName}{i}", Mesh = FarmStandVisual.Heap };
                AddChild(heap);
            }
            if (_shown[i] != slot.Item || heap.MaterialOverride == null)
            {
                _shown[i] = slot.Item;
                heap.MaterialOverride = FarmStandVisual.HeapMaterial(ItemDefs.Get(slot.Item)?.Tint ?? Colors.Wheat);
            }
            float fill = Mathf.Clamp(slot.Count / (float)FarmStandRules.PerCrate, 0.1f, 1f);
            heap.Scale = new Vector3(0.48f, 0.08f + 0.16f * fill, 0.26f);   // heaped over the rim when full
            heap.Position = FarmStandVisual.CrateCentre(i) + new Vector3(0, -0.06f, 0);
            heap.Visible = true;
        }
        HeapsShown = shown;
    }
}
