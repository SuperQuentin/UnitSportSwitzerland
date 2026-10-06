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

    /// <summary>
    /// How far out from the entrance wall the totem stands, and how far along the facade from the
    /// door. It sits on the store's own forecourt, beside the entrance.
    ///
    /// <para>
    /// It used to stand 30 m straight out from the door, which put it <b>in the middle of the
    /// road</b>: a door is placed facing the nearest street (<c>BuildingFootprint.Compute</c>), so
    /// following its outward far enough always arrives at one. The right place is the lot entrance
    /// by the road, but the road tile is not available at <see cref="ChunkManager.TileFurnished"/>,
    /// and widening that for a cosmetic prop is not worth it — the forecourt is the store's own
    /// ground, so it is always clear of the carriageway.
    /// </para>
    /// </summary>
    private const float StandOff = 9f, AlongFacade = 15f;
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
        if (_chunks == null) return;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    public override void _ExitTree()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    /// <summary>The tile shed its buildings (#553): the pylon stands with its store.</summary>
    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

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

            // DoorSpot.Position is in the tile node's frame (BankSigns parents its plates straight
            // onto it), but TryGetHeight takes a world point: the two differ by the node's offset
            // from the floating origin, so querying with the local one sampled the ground hundreds
            // of metres away and the miss silently dropped the pylon.
            // beside the entrance, not in front of it: straight out is the way in
            var along = new Vector3(outward.Z, 0, -outward.X);
            var at = d.Position + outward * StandOff + along * AlongFacade;
            var world = node.ToGlobal(at);
            if (!_chunks.TryGetHeight(world, out float ground)) continue;
            at = node.ToLocal(world with { Y = ground });

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
