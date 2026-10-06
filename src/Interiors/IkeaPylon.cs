using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The pylon totem at an IKEA (#501): the tall blue slab with the yellow name on it that stands out
/// by the road, which is how you actually spot one from a motorway. Client only, and driven by
/// <see cref="ChunkManager.TileFurnished"/> exactly as <see cref="BankSigns"/> is — the doors it
/// hands over already say which building is a store (<see cref="DoorSpot.Shop"/>), and the pylon is
/// parented to the tile's own node, so it unloads with it.
///
/// <para>
/// It stands <see cref="StandOff"/> out from the store's entrance along the way that door faces,
/// which is the way the car park and the road are: a store's door is placed toward the street
/// (<c>BuildingFootprint</c>), so following it outward lands in the lot rather than in the building.
/// It is dropped onto the ground there, and skipped if the ground is not loaded — the next time the
/// tile is furnished it tries again.
/// </para>
///
/// <para>
/// Deliberately not a road prop: #499 owns the car park's own bays, signs, trolley shelters and
/// barriers in the <c>.road</c> format. A brand totem at nine known places does not belong in a
/// general road format, so it lives here and touches none of that.
/// </para>
/// </summary>
public partial class IkeaPylon : Node
{
    private const string NodeName = "IkeaPylon";

    /// <summary>How far out from the entrance the totem stands, in metres.</summary>
    private const float StandOff = 30f;
    /// <summary>Height of the mast, and the slab's size: it has to read from the motorway.</summary>
    private const float MastHeight = 14f, SlabWidth = 4.4f, SlabHeight = 3.0f;

    private readonly ChunkManager _chunks;
    private StandardMaterial3D _blue = null!;
    private StandardMaterial3D _grey = null!;

    public IkeaPylon(ChunkManager chunks)
    {
        Name = NodeName;
        _chunks = chunks;
    }

    public IkeaPylon() : this(null!) { }

    public override void _Ready()
    {
        _blue = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = BuildingMeshBuilder.IkeaBlue,
        };
        _grey = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.56f, 0.58f) };
        if (_chunks != null) _chunks.TileFurnished += OnFurnished;
    }

    public override void _ExitTree()
    {
        if (_chunks != null) _chunks.TileFurnished -= OnFurnished;
    }

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        var root = new Node3D { Name = NodeName };
        foreach (var d in doors)
        {
            if (d.Shop != Loot.ShopType.Ikea || d.Width <= 0) continue;
            var outward = d.Outward with { Y = 0 };
            if (outward.LengthSquared() < 1e-4f) continue;
            outward = outward.Normalized();

            var at = d.Position + outward * StandOff;
            if (!_chunks.TryGetHeight(at, out float ground)) continue;
            at.Y = ground;

            // the slab's faces look back along the door's outward, so it is readable from the road
            var basis = new Basis(Vector3.Up, Mathf.Atan2(outward.X, outward.Z));
            var pylon = new Node3D { Transform = new Transform3D(basis, at) };

            // the mast
            pylon.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.6f, MastHeight, 0.6f) },
                Position = new Vector3(0, MastHeight / 2, 0),
                MaterialOverride = _grey,
            });
            // the slab on top
            pylon.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(SlabWidth, SlabHeight, 0.5f) },
                Position = new Vector3(0, MastHeight + SlabHeight / 2, 0),
                MaterialOverride = _blue,
            });
            // the name on both faces, so it reads whichever side you come from
            foreach (float side in new[] { 1f, -1f })
                pylon.AddChild(new Label3D
                {
                    Text = "IKEA",
                    FontSize = 128,
                    PixelSize = 0.016f,
                    OutlineSize = 0,
                    Modulate = new Color(0.98f, 0.80f, 0.08f),
                    Shaded = false,
                    DoubleSided = false,
                    RenderPriority = 1,
                    Transform = new Transform3D(
                        new Basis(Vector3.Up, side > 0 ? 0 : Mathf.Pi),
                        new Vector3(0, MastHeight + SlabHeight / 2, 0.26f * side)),
                });
            root.AddChild(pylon);
        }
        if (root.GetChildCount() == 0) { root.QueueFree(); return; }
        node.AddChild(root);
    }
}
