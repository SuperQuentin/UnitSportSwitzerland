using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Occasions;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The glazed street front of a dining venue or a shop unit with a door of its own (#1292,
/// <see cref="DoorSpot.Unit"/>): 1.1 m bays across its room's width on the street, the door among them, a sill
/// and a top over the ground storey's window grid. The sizes are shared by the footprint (how wide a venue's
/// front is), the planner (the room behind it, its windows at the bays) and the mesh. A sibling of
/// <see cref="HallFront"/>: aluminium mullions and a painted fascia over the glass, a stone plinth under it, the panes
/// in <c>shop_glass.gdshader</c>, the fake room behind them (<c>room_behind</c>) tinted to the venue.
/// </summary>
public static class VenueFront
{
    /// <summary>A bay of glass, m: HallFront's.</summary>
    public const float Bay = 1.1f;

    /// <summary>The glass's sill and top over the pavement, m [owner, #1292]; the top is capped under the storey's ceiling.</summary>
    public const float Sill = 0.4f, Top = 2.6f;

    /// <summary>A front narrower than this (the door and a bay each side) is no front: the door stays a plain door.</summary>
    public const float MinSpan = 3.2f;

    /// <summary>The painted band over the glass, m: where a sign hangs.</summary>
    public const float FasciaH = 0.45f;

    /// <summary>
    /// How many bays a venue's front is when it is not a shop unit's (an office block, a school, a house; owner,
    /// #1292): a café, a kebab or a fast food 5, a restaurant, a pub or a bar 7, a club 9. The door is one of them.
    /// </summary>
    public static int BaysFor(VenueType t) => t switch
    {
        VenueType.Cafe or VenueType.FastFood => 5,
        VenueType.Nightclub => 9,
        _ => 7,
    };

    /// <summary>The span of a venue's front by its kind, its door <paramref name="doorWidth"/> wide.</summary>
    public static float SpanFor(VenueType t, float doorWidth) => doorWidth + (BaysFor(t) - 1) * Bay;

    /// <summary>Whether a door has a front.</summary>
    public static bool Has(DoorSpot d) => d.Unit && d.Span > 0 && d.Width > 0 && d.FrontTop > 0;

    /// <summary>The bays each side of the door: as many 1.1 m bays as the glass beside it holds, at least one.</summary>
    public static int BaysBeside(DoorSpot d) => Math.Max(1, (int)MathF.Round((d.Span / 2 - DoorEdge(d)) / Bay));

    /// <summary>Where the door's frame stops, from its middle, m.</summary>
    public static float DoorEdge(DoorSpot d) => d.Width / 2 + 0.06f;

    /// <summary>
    /// The bays' centres along the wall, from the door's middle (+X along the wall as the door's frame has it), and
    /// their width: the room inside lines its windows up with them (#1292).
    /// </summary>
    public static List<(float Center, float Width)> Bays(DoorSpot d)
    {
        var list = new List<(float, float)>();
        if (!Has(d)) return list;
        int n = BaysBeside(d);
        float edge = DoorEdge(d), w = (d.Span / 2 - edge) / n;
        foreach (int side in new[] { -1, 1 })
            for (int k = 0; k < n; k++)
                list.Add((side * (edge + w * (k + 0.5f)), w));
        return list;
    }

    /// <summary>The glass's tint: the venue's palette, a shop's pale (#1292).</summary>
    public static Color Tint(DoorSpot d)
    {
        if (d.Venue <= 0) return new Color(1.0f, 0.98f, 0.92f);
        return InteriorGenerator.DiningRoomOf(Venues.At(d.Venue - 1)) switch
        {
            RoomType.Gaststube => new Color(1.0f, 0.78f, 0.52f),
            RoomType.Pizzeria => new Color(1.0f, 0.66f, 0.48f),
            RoomType.CafeRoom => new Color(1.0f, 0.92f, 0.78f),
            RoomType.KebabRoom or RoomType.FastFoodRoom => new Color(1.0f, 0.86f, 0.55f),
            RoomType.BarRoom => new Color(0.95f, 0.55f, 0.50f),
            RoomType.PubRoom => new Color(0.90f, 0.70f, 0.48f),
            _ => new Color(0.78f, 0.58f, 1.0f),   // a club: violet
        };
    }

    private static readonly Color Alu = PropColors.Matte(0.30f, 0.31f, 0.33f), Stone = PropColors.Matte(0.40f, 0.39f, 0.37f);

    /// <summary>A fascia's paint: one of a few, by the door.</summary>
    private static Color Fascia(DoorSpot d)
    {
        var paints = new[]
        {
            PropColors.Matte(0.16f, 0.22f, 0.20f), PropColors.Matte(0.40f, 0.10f, 0.10f), PropColors.Matte(0.12f, 0.16f, 0.30f),
            PropColors.Matte(0.85f, 0.82f, 0.74f), PropColors.Matte(0.20f, 0.20f, 0.22f),
        };
        return paints[(int)(Fnv.Unit($"{d.Index}|{d.Slot}|fascia") * paints.Length) % paints.Length];
    }

    /// <summary>
    /// The fronts of a tile's doors, in the tile's frame: one mesh of frames (vertex-coloured, the prop material) and one
    /// of panes (the glass material). Null when no door has a front.
    /// </summary>
    public static Node3D? Build(IEnumerable<DoorSpot> doors, Material prop, Material glass)
    {
        var frames = new MeshScratch();
        var panes = new SurfaceTool();
        panes.Begin(Mesh.PrimitiveType.Triangles);
        int count = 0;
        foreach (var d in doors)
        {
            if (!Has(d) || VenueSigns.Frame(d) is not { } frame) continue;
            Add(d, frame, frames, panes);
            count++;
        }
        if (count == 0) return null;
        var root = new Node3D { Name = "VenueFronts" };
        root.AddChild(new MeshInstance3D
        {
            Name = "Frames",
            Mesh = frames.Build(),
            MaterialOverride = prop,
            // MeshScratch hands its meshes back turned half round: turned back, so its tile frame is the tile's
            Rotation = new Vector3(0, Mathf.Pi, 0),
            VisibilityRangeEnd = 400f,
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "Glass",
            Mesh = panes.Commit(),
            MaterialOverride = glass,
            VisibilityRangeEnd = 400f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return root;
    }

    /// <summary>One door's front into the tile's meshes: its frame's boxes, its panes.</summary>
    private static void Add(DoorSpot d, Transform3D frame, MeshScratch frames, SurfaceTool panes)
    {
        var basis = frame.Basis;
        void Box(float x, float y, float z, float w, float h, float dz, Color c) =>
            frames.Box(frame * new Vector3(x, y, z), new Vector3(w, h, dz), c, basis);

        float half = d.Span / 2, edge = DoorEdge(d), top = d.FrontTop, doorTop = Math.Max(2.1f, d.Height) + 0.06f;
        const float m = 0.07f, md = 0.07f, mz = 0.035f;
        // the plinth under the glass, the fascia over it: both a hand proud of the wall, so the window grid is hidden
        Box(-(half + edge) / 2, Sill / 2, 0.02f, half - edge, Sill, 0.08f, Stone);
        Box((half + edge) / 2, Sill / 2, 0.02f, half - edge, Sill, 0.08f, Stone);
        Box(0, top + FasciaH / 2, 0.03f, d.Span + 2 * m, FasciaH, 0.10f, Fascia(d));
        // the frame: the sill and head rails, the jambs at the ends and round the door, a mullion between each bay, the transom
        Box(0, top + m / 2 - 0.012f, mz, d.Span + m, m, md, Alu);
        // the sill rails run into the jambs, so their ends never share a plane with the plinth's
        foreach (int side in new[] { -1, 1 })
            Box(side * (edge + half) / 2, Sill, mz, half - edge + m, m, md, Alu);
        var bays = Bays(d);
        foreach (var (c, w) in bays)
            foreach (int end in new[] { -1, 1 })
                Box(c + end * w / 2, (Sill + top) / 2, mz + 0.002f * end, m, top - Sill, md, Alu);
        Box(0, doorTop + m / 2, mz + 0.012f, 2 * edge + m, m, md, Alu);

        // the panes, the room behind them as wide as the front and as tall as the glass's storey
        var n = basis * Vector3.Back;
        void Pane(float x0, float x1, float y0, float y1)
        {
            if (x1 - x0 < 0.05f || y1 - y0 < 0.05f) return;
            Vector3 P(float x, float y) => frame * new Vector3(x, y, 0f);
            Vector2 Uv(float x, float y) => new((x + half) / d.Span, y / (top + 0.3f));
            var room = new Vector2(d.Span, top + 0.3f);
            var tint = Tint(d);
            // clockwise seen from the street: Godot's front face
            foreach (var (x, y) in new[] { (x0, y0), (x1, y1), (x1, y0), (x0, y0), (x0, y1), (x1, y1) })
            {
                panes.SetNormal(n);
                panes.SetColor(tint);
                panes.SetUV(Uv(x, y));
                panes.SetUV2(room);
                panes.AddVertex(P(x, y));
            }
        }
        foreach (var (c, w) in bays) Pane(c - w / 2, c + w / 2, Sill, top);
        Pane(-edge, edge, doorTop, top);
    }

    /// <summary>The front in the model viewer: a restaurant's, a club's and a shop unit's, on a wall.</summary>
    [Showcase("Interiors", "Venue front")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseFronts()
    {
        foreach (var (name, kind) in new[] { ("Restaurant", VenueType.Restaurant), ("Club", VenueType.Nightclub), ("Shop", VenueType.None) })
            yield return (name, () =>
            {
                var root = new Node3D();
                int venue = -1;
                if (kind != VenueType.None)
                {
                    Venues.Override = [new VenueRecord("n" + name, kind, 0, 0, VenueFix.Inside, VenueFlags.None, 0, "", name)];
                    venue = 0;
                }
                float span = kind == VenueType.None ? 7.6f : SpanFor(kind, BuildingFootprint.UnitDoorWidth);
                var d = new DoorSpot(0, Vector3.Zero, Vector3.Back, BuildingFootprint.UnitDoorWidth, DoorBudget.ServiceHeight)
                {
                    Unit = true, Span = span, FrontTop = Top, Venue = venue + 1,
                };
                if (Build([d], Styles.StyleKit.Material(Styles.MaterialRole.Prop), GlassMaterial()) is { } front) root.AddChild(front);
                if (kind != VenueType.None)
                {
                    if (VenueSigns.Dress(d, Venues.At(0), Styles.StyleKit.Material(Styles.MaterialRole.Prop)) is { } sign) root.AddChild(sign);
                    Venues.Override = null;
                }
                // the wall behind it
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(span + 4f, 6f, 0.2f) },
                    Position = new Vector3(0, 3f, -0.13f),
                });
                return root;
            });
    }

    private static ShaderMaterial? _glass;

    /// <summary>The panes' material, one for every front: the room behind is in the vertices.</summary>
    public static ShaderMaterial GlassMaterial() => _glass ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/shop_glass.gdshader") };
}

/// <summary>
/// Client only: the glazed fronts of a tile's units and venues (#1292, <see cref="VenueFront"/>), one node per tile on
/// <see cref="ChunkManager.TileFurnished"/> like <see cref="VenueSigns"/>, parented to the tile's node.
/// </summary>
public partial class VenueFronts : Node
{
    private const string NodeName = "VenueFronts";

    private readonly ChunkManager _chunks;
    private ShaderMaterial _prop = null!;

    public VenueFronts(ChunkManager chunks)
    {
        Name = "VenueFronts";
        _chunks = chunks;
    }

    public VenueFronts() : this(null!) { }

    public override void _Ready()
    {
        _prop = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        _prop.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(_prop);
        GameSettings.Changed += ApplyFog;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    private void ApplyFog() => FogUniforms.Apply(_prop);

    public override void _ExitTree()
    {
        GameSettings.Changed -= ApplyFog;
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        if (VenueFront.Build(doors, _prop, VenueFront.GlassMaterial()) is { } fronts) node.AddChild(fronts);
    }
}
