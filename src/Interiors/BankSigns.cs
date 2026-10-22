using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A "BANK" sign over the door of every bank (#213, <see cref="BuildingFootprint.IsBank"/>), so
/// you can find where to deposit your cash from the street, and one naming the shop over every
/// shop's (#273, <see cref="DoorSpot.Shop"/>): "GROCERY" on green, "PHARMACY" on green, "GARAGE"… Client only. Driven by
/// <see cref="ChunkManager.TileFurnished"/>, whose doors already say which buildings are banks;
/// the signs are parented to the tile's own node, so they unload with it.
/// </summary>
public partial class BankSigns : Node
{
    private const string NodeName = "BankSigns";

    private readonly ChunkManager _chunks;
    private StandardMaterial3D _plate = null!;
    private readonly Dictionary<Loot.ShopType, StandardMaterial3D> _shopPlates = new();

    /// <summary>The bank's plate colour and the colour of its letters.</summary>
    private static readonly Color BankPlate = new(0.10f, 0.20f, 0.36f), BankInk = new(0.98f, 0.84f, 0.38f);

    /// <summary>A shop's plate colour and the colour of its letters.</summary>
    private static (Color Plate, Color Text) ShopColors(Loot.ShopType t) => t switch
    {
        Loot.ShopType.Grocery => (new Color(0.12f, 0.36f, 0.16f), new Color(0.98f, 0.92f, 0.60f)),
        Loot.ShopType.Pharmacy => (new Color(0.10f, 0.50f, 0.22f), Colors.White),
        Loot.ShopType.Kiosk => (new Color(0.86f, 0.70f, 0.12f), new Color(0.12f, 0.10f, 0.08f)),
        Loot.ShopType.Hardware => (new Color(0.80f, 0.36f, 0.08f), Colors.White),
        Loot.ShopType.Sport => (new Color(0.10f, 0.30f, 0.62f), Colors.White),
        Loot.ShopType.Boutique => (new Color(0.30f, 0.12f, 0.34f), new Color(0.98f, 0.80f, 0.90f)),
        Loot.ShopType.Electronics => (new Color(0.08f, 0.36f, 0.42f), new Color(0.70f, 0.98f, 1.00f)),
        Loot.ShopType.GunShop => (new Color(0.24f, 0.16f, 0.10f), new Color(0.92f, 0.82f, 0.62f)),
        // #501: the yellow-on-blue the store is known by
        Loot.ShopType.Ikea => (Terrain.BuildingMeshBuilder.IkeaBlue, new Color(0.98f, 0.80f, 0.08f)),
        Loot.ShopType.FarmCoop => (new Color(0.30f, 0.46f, 0.14f), new Color(0.98f, 0.92f, 0.55f)),   // farm co-op (#494)
        _ => (new Color(0.28f, 0.30f, 0.32f), new Color(0.98f, 0.84f, 0.20f)),   // a garage
    };

    private StandardMaterial3D Plate(Loot.ShopType t)
    {
        if (_shopPlates.TryGetValue(t, out var m)) return m;
        // the plate lit in the lit styles, flat in PS1; its lettering stays unshaded (#1069)
        return _shopPlates[t] = Styles.StyleKit.Solid(new StandardMaterial3D { AlbedoColor = ShopColors(t).Plate });
    }

    public BankSigns(ChunkManager chunks)
    {
        Name = "BankSigns";
        _chunks = chunks;
    }

    public BankSigns() : this(null!) { }

    /// <summary>
    /// A sign is drawn to here (#553): a 0.55 m plate is a few pixels at this distance and its
    /// lettering long unreadable. Every commercial building out to the building ring had one, three
    /// draws each (the plate and two transparent labels), with no range: thousands over a city.
    /// </summary>
    private const float DrawnM = 200f;

    public override void _Ready()
    {
        _plate = Styles.StyleKit.Solid(new StandardMaterial3D { AlbedoColor = BankPlate });
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    public override void _ExitTree()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    /// <summary>The tile shed its buildings (#553): their signs go with them.</summary>
    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        if (!doors.Any(d => (d.Bank || d.Shop != Loot.ShopType.None) && d.Width > 0)) return;
        var root = new Node3D { Name = NodeName };
        foreach (var d in doors)
        {
            if (!(d.Bank || d.Shop != Loot.ShopType.None) || d.Width <= 0) continue;
            string text = d.Bank ? "BANK" : Loot.ShopTables.Name(d.Shop).ToUpperInvariant();
            var ink = d.Bank ? BankInk : ShopColors(d.Shop).Text;
            var outward = d.Outward with { Y = 0 };
            if (outward.LengthSquared() < 1e-4f) continue;
            outward = outward.Normalized();
            // +Z of the sign faces the street
            var basis = new Basis(Vector3.Up, Mathf.Atan2(outward.X, outward.Z));
            var at = d.Position + outward * 0.12f + Vector3.Up * (Math.Max(2.1f, d.Height) + 0.45f);
            var sign = Sign(text, ink, d.Bank ? _plate : Plate(d.Shop), d.Width);
            sign.Transform = new Transform3D(basis, at);
            root.AddChild(sign);
        }
        node.AddChild(root);
    }

    /// <summary>A sign's plate over a door <paramref name="doorWidth"/> wide, and its lettering on both faces; +Z faces the street.</summary>
    private static Node3D Sign(string text, Color ink, StandardMaterial3D plate, float doorWidth)
    {
        var sign = new Node3D();
        float w = Math.Max(Math.Max(1.6f, text.Length * 0.25f + 0.3f), doorWidth + 0.6f);
        sign.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(w, 0.55f, 0.08f) },
            MaterialOverride = plate,
            VisibilityRangeEnd = DrawnM,
        });
        // lettering on both faces: whichever way the facade's outward runs, one reads from the street
        foreach (float side in new[] { 1f, -1f })
            sign.AddChild(new Label3D
            {
                Text = text,
                FontSize = 96,
                PixelSize = 0.0035f,
                OutlineSize = 0,
                Modulate = ink,
                Shaded = false,
                DoubleSided = false,
                RenderPriority = 1,
                VisibilityRangeEnd = DrawnM,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(new Basis(Vector3.Up, side > 0 ? 0 : Mathf.Pi), new Vector3(0, 0, 0.06f * side)),
            });
        return sign;
    }

    /// <summary>The bank's sign and every shop's, in the model viewer (#1069), standing on its ground.</summary>
    [Core.Showcase("Interiors", "Shop sign")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseSigns()
    {
        static Node3D Raised(Node3D sign)
        {
            sign.Position = new Vector3(0, 0.3f, 0);
            var root = new Node3D();
            root.AddChild(sign);
            return root;
        }
        yield return ("Bank", () => Raised(Sign("BANK", BankInk, Styles.StyleKit.Solid(new StandardMaterial3D { AlbedoColor = BankPlate }), 1.2f)));
        foreach (var t in Enum.GetValues<Loot.ShopType>())
        {
            if (t == Loot.ShopType.None) continue;
            var (plate, ink) = ShopColors(t);
            yield return (t.ToString(), () => Raised(Sign(Loot.ShopTables.Name(t).ToUpperInvariant(), ink,
                Styles.StyleKit.Solid(new StandardMaterial3D { AlbedoColor = plate }), 1.2f)));
        }
    }
}
