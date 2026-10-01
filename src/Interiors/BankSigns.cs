using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A "BANK" sign over the door of every bank (#213, <see cref="BuildingFootprint.IsBank"/>), so
/// you can find where to deposit your cash from the street. Client only. Driven by
/// <see cref="ChunkManager.TileFurnished"/>, whose doors already say which buildings are banks;
/// the signs are parented to the tile's own node, so they unload with it.
/// </summary>
public partial class BankSigns : Node
{
    private const string NodeName = "BankSigns";

    private readonly ChunkManager _chunks;
    private StandardMaterial3D _plate = null!;

    public BankSigns(ChunkManager chunks)
    {
        Name = "BankSigns";
        _chunks = chunks;
    }

    public BankSigns() : this(null!) { }

    public override void _Ready()
    {
        _plate = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.10f, 0.20f, 0.36f),
        };
        _chunks.TileFurnished += OnFurnished;
    }

    public override void _ExitTree()
    {
        if (_chunks != null) _chunks.TileFurnished -= OnFurnished;
    }

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        if (!doors.Any(d => d.Bank && d.Width > 0)) return;
        var root = new Node3D { Name = NodeName };
        foreach (var d in doors)
        {
            if (!d.Bank || d.Width <= 0) continue;
            var outward = d.Outward with { Y = 0 };
            if (outward.LengthSquared() < 1e-4f) continue;
            outward = outward.Normalized();
            // +Z of the sign faces the street
            var basis = new Basis(Vector3.Up, Mathf.Atan2(outward.X, outward.Z));
            var at = d.Position + outward * 0.12f + Vector3.Up * (Math.Max(2.1f, d.Height) + 0.45f);
            var sign = new Node3D { Transform = new Transform3D(basis, at) };
            float w = Math.Max(1.6f, d.Width + 0.6f);
            sign.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(w, 0.55f, 0.08f) },
                MaterialOverride = _plate,
            });
            // lettering on both faces: whichever way the facade's outward runs, one reads from the street
            foreach (float side in new[] { 1f, -1f })
                sign.AddChild(new Label3D
                {
                    Text = "BANK",
                    FontSize = 96,
                    PixelSize = 0.0035f,
                    OutlineSize = 0,
                    Modulate = new Color(0.98f, 0.84f, 0.38f),
                    Shaded = false,
                    DoubleSided = false,
                    RenderPriority = 1,
                    Transform = new Transform3D(new Basis(Vector3.Up, side > 0 ? 0 : Mathf.Pi), new Vector3(0, 0, 0.06f * side)),
                });
            root.AddChild(sign);
        }
        node.AddChild(root);
    }
}
