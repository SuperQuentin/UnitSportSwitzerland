using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Occasions;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>What stands on a venue's terrace.</summary>
public enum TerraceKind { BistroSet, BeerBench, Parasol, Awning, Planter, MenuBoard }

/// <summary>One piece of a terrace, tile-local (Y the floor of the door), its +Z turned to <see cref="Yaw"/>; <see cref="Span"/> for an awning's width.</summary>
public readonly record struct TerraceItem(TerraceKind Kind, Vector3 At, float Yaw, int Colour, float Span = 1f);

/// <summary>
/// Terraces in front of restaurants, cafés and bars (#1243, epic #1232): bistro tables under parasols or
/// an awning, beer benches in front of a beer garden, planters at the ends, a menu board by the door.
/// Numbers in <c>docs/notes/terrain/venue-dining.md</c>, the rules in <c>docs/notes/terrain/terraces.md</c>.
/// Client only, as <see cref="VenueSigns"/>: on <see cref="ChunkManager.TileFurnished"/>, once the tile's
/// roads are loaded (as <see cref="OccasionDecor"/> waits for them), parented to the tile's node. Solid
/// on the trees' layer (<see cref="World.TreeColliders.Layer"/>): walkers and cars stop, the chase camera
/// does not. A pure function of the tile, so every client lays the same.
/// </summary>
public partial class VenueTerraces : Node
{
    private const string NodeName = "VenueTerraces";

    /// <summary>The pavement kept free between a terrace and the carriageway [S: Basel, 1.5 m at least].</summary>
    public const float Passage = 1.5f;
    /// <summary>
    /// How far a set's ground may lie from the door's sill (a door often stands a step or two up), and
    /// how level its own ground must be (a slope or a kerb under it is no place for a table).
    /// </summary>
    private const float MaxRise = 0.6f, MaxTilt = 0.15f;

    /// <summary><c>--terracelog</c>: why each terrace is or is not there.</summary>
    private static readonly bool Log = CmdArgs.Has("--terracelog");

    private readonly ChunkManager _chunks;
    private readonly IChunkSource _source;
    private ShaderMaterial _material = null!;
    private int _epoch;

    public VenueTerraces(ChunkManager chunks, IChunkSource source)
    {
        Name = NodeName;
        _chunks = chunks;
        _source = source;
    }

    public VenueTerraces() : this(null!, null!) { }

    // ---- what goes where (pure) ------------------------------------------------------------------

    /// <summary>
    /// Whether a venue's name is on an awning over its front rather than on a board (#1250): every café,
    /// and the restaurants whose dice say so; with its terrace's tables under it, or alone over the door.
    /// </summary>
    public static bool HasAwning(VenueRecord v) =>
        v.Type == VenueType.Cafe || v.Type == VenueType.Restaurant && (uint)(Fnv.Unit(v.Osm + "|awning") * 1000) % 2 == 0;

    /// <summary>
    /// Whether a venue has a terrace: as OSM says when it says (<c>outdoor_seating</c>), else by odds
    /// [E]: 0.55 restaurants, cafés and bars, 0.5 pubs, 0.35 fast food, 0.9 beer gardens, never a club.
    /// </summary>
    public static bool Wants(VenueRecord v)
    {
        if ((v.Flags & VenueFlags.NoOutdoor) != 0 || v.Type == VenueType.Nightclub || !Venues.IsDining(v.Type)) return false;
        if ((v.Flags & VenueFlags.Outdoor) != 0) return true;
        double p = v.Type switch
        {
            VenueType.Biergarten => 0.9,
            VenueType.FastFood => 0.35,
            VenueType.Pub => 0.5,
            _ => 0.55,
        };
        return Fnv.Unit(v.Osm + "|terrace") < p;
    }

    /// <summary>
    /// The terrace in front of door <paramref name="d"/>: sets on both sides of the way in (1.6 m each, a
    /// beer garden's benches 2.2 m in two rows), each only where its outer edge keeps
    /// <see cref="Passage"/> from the carriageway and its ground lies within <see cref="MaxRise"/> of the
    /// door's; parasols through the tables or an awning over the front, planters at the ends, the menu
    /// board by the door. Each stands on its own ground. Empty when no set finds room.
    /// </summary>
    /// <param name="carriageway">Plan distance from a tile-local point to the nearest carriageway's edge.</param>
    /// <param name="ground">The ground under a tile-local point, tile-local Y; null where unknown.</param>
    public static List<TerraceItem> Plan(DoorSpot d, VenueRecord v, Func<Vector3, float> carriageway, Func<Vector3, float?> ground)
    {
        var items = new List<TerraceItem>();
        var outward = d.Outward with { Y = 0 };
        bool awning = HasAwning(v);
        if (d.Width <= 0 || outward.LengthSquared() < 1e-4f || !Wants(v) && !awning) return items;
        outward = outward.Normalized();
        var along = new Vector3(outward.Z, 0, -outward.X);
        float yaw = Mathf.Atan2(outward.X, outward.Z), floor = d.Position.Y;
        bool garden = v.Type == VenueType.Biergarten;
        var kind = garden ? TerraceKind.BeerBench : TerraceKind.BistroSet;
        // a set's extent along the facade and out from it, and the rows' distances out (to their centres)
        float setAlong = garden ? 2.2f : 1.6f, setOut = garden ? 1.5f : 0.8f;
        float[] rows = garden ? new[] { 1.1f, 2.9f } : new[] { 0.75f };
        int perSide = garden ? 3 : 2;
        int colour = (int)(Fnv.Unit(v.Osm + "|colour") * TerraceProps.Colours.Length);

        // the ground a piece stands on there, or null: too near the carriageway, on another level, not level
        float? Fits(Vector3 c, float halfAlong, float halfOut)
        {
            if (ground(c) is not { } gc || Math.Abs(gc - floor) > MaxRise) return null;
            foreach (float sa in new[] { -halfAlong, halfAlong })
                foreach (float so in new[] { -halfOut, halfOut })
                {
                    var corner = c + along * sa + outward * so;
                    if (so > 0 && carriageway(corner) < Passage) return null;
                    if (ground(corner) is not { } g || Math.Abs(g - gc) > MaxTilt) return null;
                }
            return gc;
        }

        var sets = new List<(Vector3 At, float S)>();
        if (Wants(v))
            foreach (float row in rows)
            foreach (int side in new[] { -1, 1 })
                for (int k = 0; k < perSide; k++)
                {
                    float s = side * (d.Width / 2 + 0.9f + setAlong / 2 + k * (setAlong + 0.4f));
                    var c = d.Position + outward * row + along * s;
                    if (Fits(c, setAlong / 2, setOut / 2) is not { } y) break;
                    sets.Add((c with { Y = y }, s));
                }
        if (sets.Count == 0)
        {
            // no tables: the awning alone over the door and its windows, the name on it (#1250)
            if (awning)
            {
                // over a glazed front (#1292): as wide as the front
                float span = VenueFront.Has(d) ? d.Span : Math.Max(d.Width + 1.6f, 3.2f);
                if (d.Run > 0) span = Math.Min(span, d.Run - 0.4f);
                if (span >= d.Width + 0.4f) items.Add(new TerraceItem(TerraceKind.Awning, d.Position with { Y = floor + TerraceProps.AwningLift(d) }, yaw, colour, span));
            }
            return items;
        }
        foreach (var (at, _) in sets) items.Add(new TerraceItem(kind, at, yaw, colour));

        // shade: an awning across the front of a café or restaurant, else a parasol through each table
        float lo = sets.Min(x => x.S) - setAlong / 2, hi = sets.Max(x => x.S) + setAlong / 2;
        if (awning)
            items.Add(new TerraceItem(TerraceKind.Awning, (d.Position + along * ((lo + hi) / 2)) with { Y = floor + TerraceProps.AwningLift(d) }, yaw, colour, hi - lo));
        else if (!garden)
            foreach (var (at, _) in sets) items.Add(new TerraceItem(TerraceKind.Parasol, at, yaw, colour));

        // planters closing the row at each end, where they fit
        foreach (float s in new[] { lo - 0.6f, hi + 0.6f })
        {
            var c = d.Position + outward * rows[0] + along * s;
            if (Fits(c, 0.25f, 0.5f) is { } y) items.Add(new TerraceItem(TerraceKind.Planter, c with { Y = y }, yaw + Mathf.Pi / 2, colour));
        }
        // the menu board beside the way in, facing the street
        var board = d.Position + outward * 0.5f + along * (d.Width / 2 + 0.45f);
        if (Fits(board, 0.3f, 0.3f) is { } by) items.Add(new TerraceItem(TerraceKind.MenuBoard, board with { Y = by }, yaw, colour));
        return items;
    }

    /// <summary>Plan distance from a tile-local point to the nearest carriageway's edge (not a footpath, a square or a watercourse).</summary>
    public static float Carriageway(RoadTile? roads, Vector3 local, float giveUpBeyond = 30f)
    {
        if (roads == null) return float.MaxValue;
        float best = giveUpBeyond;
        var p = new Vector2(local.X, local.Z);
        foreach (var s in roads.Segments)
        {
            if (s.Class is not (RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major or RoadClass.Road
                or RoadClass.Minor or RoadClass.Lane or RoadClass.Track or RoadClass.Link or RoadClass.Railway)) continue;
            float half = s.Width * 0.5f;
            for (int i = 0; i + 1 < s.PointCount; i++)
            {
                var a = new Vector2(s.Points[i * 3], s.Points[i * 3 + 2]);
                var b = new Vector2(s.Points[i * 3 + 3], s.Points[i * 3 + 5]);
                var ab = b - a;
                float t = ab.LengthSquared() < 1e-6f ? 0 : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
                best = Math.Min(best, (a + ab * t).DistanceTo(p) - half);
            }
        }
        return best;
    }

    // ---- in the world ----------------------------------------------------------------------------

    public override void _Ready()
    {
        _material = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        _material.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(_material);
        GameSettings.Changed += ApplyFog;
        if (_chunks == null) return;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    public override void _ExitTree()
    {
        _epoch++;
        GameSettings.Changed -= ApplyFog;
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private void ApplyFog() => FogUniforms.Apply(_material);

    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        if (!doors.Any(d => d.Venue > 0 && Wants(Venues.At(d.Venue - 1)))) return;
        int epoch = _epoch;
        // the roads first: a terrace keeps off the carriageway (as OccasionDecor waits for them)
        _source.LoadRoadsAsync(id).ContinueWith(task =>
        {
            var roads = task.IsCompletedSuccessfully ? task.Result : null;
            Callable.From(() =>
            {
                if (epoch != _epoch || !IsInstanceValid(node) || !node.IsInsideTree()) return;
                Build(node, doors, roads);
            }).CallDeferred();
        });
    }

    /// <summary>The venue's name painted along an awning's valance (#1250): dark on a pale awning, the awning's colour on a strong one.</summary>
    private static Label3D Valance(string name, TerraceItem it, Basis basis)
    {
        var c = TerraceProps.Colours[it.Colour % TerraceProps.Colours.Length];
        bool pale = c.R + c.G + c.B > 2.2f;
        float letters = TerraceProps.ValanceH * 0.62f, em = letters / 0.7f;
        string text = NeonSign.Lettering(name);
        float textW = Math.Max(1, text.Length) * em * 0.55f;
        em *= Math.Min(1f, (it.Span - 0.4f) / textW);
        const int fontSize = 64;
        return new Label3D
        {
            Name = "Valance",
            Text = text,
            Font = NeonSign.SignFont(),
            FontSize = fontSize,
            PixelSize = em / fontSize,
            Modulate = pale ? new Color(0.10f, 0.14f, 0.30f) : new Color(c.R, c.G, c.B),
            OutlineSize = 0,
            Shaded = true,
            DoubleSided = false,
            VisibilityRangeEnd = NeonSign.DrawnM,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Transform = new Transform3D(basis, it.At + basis * TerraceProps.ValanceFront),
        };
    }

    private void Build(ChunkNode node, DoorSpot[] doors, RoadTile? roads)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        float? Ground(Vector3 local)
        {
            var world = node.ToGlobal(local);
            return _chunks.TryGetHeight(world, out float h) ? node.ToLocal(world with { Y = h }).Y : null;
        }
        Node3D? root = null;
        StaticBody3D? body = null;
        var guests = new List<Patrons.PatronSpot>();
        foreach (var d in doors)
        {
            if (d.Venue <= 0) continue;
            var v = Venues.At(d.Venue - 1);
            var items = Plan(d, v, p => Carriageway(roads, p), Ground);
            if (Log && Wants(v))
            {
                var o = d.Outward with { Y = 0 };
                var probe = d.Position + o.Normalized() * 0.75f;
                GD.Print($"[terrace] {Venues.NameOf(v)}: {items.Count} pieces; roads {(roads != null)}, carriageway {Carriageway(roads, probe):F1} m, "
                    + $"floor {d.Position.Y:F2}, ground out front {Ground(probe)?.ToString("F2") ?? "?"}, out front at {node.ToGlobal(probe)}");
            }
            if (items.Count == 0) continue;
            root ??= new Node3D { Name = NodeName };
            body ??= new StaticBody3D { Name = "Solid", CollisionLayer = World.TreeColliders.Layer, CollisionMask = 0 };
            var venue = Patrons.VenuePatrons.VenueOf(VenueMusic.MoodFor(v.Type, v.Cuisine));
            foreach (var it in items)
            {
                if (venue is { } pv) Patrons.PatronSpots.OfTerrace(it, pv, guests.Count, guests);
                var basis = new Basis(Vector3.Up, it.Yaw);
                // MeshScratch hands its meshes back turned half round, (-x, y, -z), as a figure faces:
                // turned back here, so a piece's +Z is the street as authored (its collision box needs no turn)
                var look = new Basis(Vector3.Up, it.Yaw + Mathf.Pi);
                var mesh = new MeshInstance3D
                {
                    Mesh = TerraceProps.Mesh(it.Kind, it.Colour).Mesh,
                    MaterialOverride = _material,
                    Transform = new Transform3D(it.Kind == TerraceKind.Awning ? look * Basis.FromScale(new Vector3(it.Span, 1, 1)) : look, it.At),
                };
                root.AddChild(mesh);
                if (it.Kind == TerraceKind.Awning) root.AddChild(Valance(Venues.NameOf(v), it, basis));
                if (TerraceProps.Solid(it.Kind) is { } box)
                    body.AddChild(new CollisionShape3D
                    {
                        Shape = new BoxShape3D { Size = box.Size },
                        Transform = new Transform3D(basis, it.At + basis * box.Centre),
                    });
            }
        }
        if (root == null) return;
        root.AddChild(body!);
        // its guests (#1244): in when it is cold or snowing, and from 23:00
        if (Patrons.VenuePatrons.ForTerrace(guests, Patrons.VenuePatrons.TerraceWeather) is { } patrons) root.AddChild(patrons);
        if (Log) GD.Print($"[terrace] {guests.Count} guest places, out now {Patrons.VenuePatrons.TerraceWeather(Net.ClockSync.ServerNow):F2}");
        node.AddChild(root);
    }
}

/// <summary>
/// The terrace's pieces, each a <see cref="PropKind"/> (so the model viewer shows it by itself): authored
/// with the facade at −Z and the street at +Z, standing on y = 0.
/// </summary>
public static class TerraceProps
{
    /// <summary>One-colour parasols and awnings [S: Basel, one colour each]: cream, red, green, navy, white.</summary>
    public static readonly Color[] Colours =
    {
        PropColors.Matte(0.92f, 0.86f, 0.70f), PropColors.Matte(0.72f, 0.12f, 0.12f), PropColors.Matte(0.14f, 0.40f, 0.22f),
        PropColors.Matte(0.12f, 0.18f, 0.40f), PropColors.Matte(0.95f, 0.95f, 0.93f),
    };

    private static readonly Color Dark = PropColors.Matte(0.12f, 0.12f, 0.13f), Rattan = PropColors.Matte(0.58f, 0.42f, 0.26f),
        Marble = PropColors.Matte(0.90f, 0.90f, 0.88f), Wood = PropColors.Matte(0.56f, 0.40f, 0.24f),
        Stone = PropColors.Matte(0.62f, 0.62f, 0.60f), Leaf = PropColors.Matte(0.24f, 0.46f, 0.20f);

    private const float Sink = 0.012f;

    public static readonly PropKind BistroSet = new("Terrace bistro set", () =>
    {
        var s = new MeshScratch();
        // a round-ish marble top on its pedestal, a rattan chair each side facing it
        s.Box(new Vector3(0, 0.015f, 0), new Vector3(0.45f, 0.03f, 0.45f), Dark);
        s.Box(new Vector3(0, 0.375f, 0), new Vector3(0.06f, 0.69f + Sink, 0.06f), Dark);
        s.Box(new Vector3(0, 0.735f, 0), new Vector3(0.6f, 0.03f, 0.6f), Marble);
        foreach (int side in new[] { -1, 1 })
        {
            float cx = side * 0.55f;
            s.Box(new Vector3(cx, 0.44f, 0), new Vector3(0.42f, 0.05f, 0.42f), Rattan);
            foreach (float lx in new[] { -0.17f, 0.17f })
                foreach (float lz in new[] { -0.17f, 0.17f })
                    s.Box(new Vector3(cx + lx, 0.21f + Sink / 2, lz), new Vector3(0.035f, 0.42f + Sink, 0.035f), Dark);
            s.Box(new Vector3(cx + side * 0.185f, 0.66f, 0), new Vector3(0.04f, 0.42f, 0.39f), Rattan);
        }
        return s.Build();
    }, candle: false);

    public static readonly PropKind BeerBench = new("Terrace beer bench", () =>
    {
        var s = new MeshScratch();
        // the long table and its two benches, on folding legs, as in every Gartenwirtschaft
        s.Box(new Vector3(0, 0.74f, 0), new Vector3(2.2f, 0.035f, 0.6f), Wood);
        foreach (float lx in new[] { -0.9f, 0.9f }) s.Box(new Vector3(lx, 0.36f + Sink / 2, 0), new Vector3(0.05f, 0.72f + Sink, 0.5f), Dark);
        foreach (int side in new[] { -1, 1 })
        {
            float z = side * 0.6f;
            s.Box(new Vector3(0, 0.45f, z), new Vector3(2.2f, 0.035f, 0.26f), Wood);
            foreach (float lx in new[] { -0.9f, 0.9f }) s.Box(new Vector3(lx, 0.2225f, z), new Vector3(0.05f, 0.445f, 0.2f), Dark);
        }
        return s.Build();
    }, candle: false);

    public static readonly PropKind[] Parasols = Colours.Select((c, i) => new PropKind($"Terrace parasol {i}", () =>
    {
        var s = new MeshScratch();
        // a pole through the table, a square canopy, its peak
        s.Box(new Vector3(0, 1.2f, 0), new Vector3(0.04f, 2.4f, 0.04f), PropColors.Matte(0.85f, 0.85f, 0.85f));
        s.Box(new Vector3(0, 2.25f, 0), new Vector3(2.4f, 0.06f, 2.4f), c);
        s.Box(new Vector3(0, 2.33f, 0), new Vector3(1.2f, 0.1f + Sink, 1.2f), c);
        s.Box(new Vector3(0, 2.16f, 1.2f - 0.012f), new Vector3(2.38f, 0.14f, 0.024f), c);
        return s.Build();
    }, candle: false)).ToArray();

    /// <summary>How far an awning is lifted so its roller box clears a glazed front's fascia (#1292), m; 0 without a front.</summary>
    public static float AwningLift(DoorSpot d) =>
        VenueFront.Has(d) ? Math.Max(0f, d.FrontTop + VenueFront.FasciaH + 0.06f - (AwningBoxY - 0.05f)) : 0f;

    /// <summary>The middle of an awning's roller box against the facade, m.</summary>
    private const float AwningBoxY = 2.75f;

    /// <summary>An awning's valance: its middle's height, its height, and the point 1.2 cm before its face the name is painted at (#1250).</summary>
    public const float ValanceY = 2.12f, ValanceH = 0.30f;
    public static readonly Vector3 ValanceFront = new(0, ValanceY, 2.2f + 0.015f + 0.012f);

    public static readonly PropKind[] Awnings = Colours.Select((c, i) => new PropKind($"Terrace awning {i}", () =>
    {
        var s = new MeshScratch();
        // 1 m wide (stretched to the terrace), from the facade out 2.2 m and down 0.5 m, a valance at its edge
        var tilt = new Basis(Vector3.Right, Mathf.DegToRad(13));
        s.Box(new Vector3(0, 2.5f, 1.1f), new Vector3(1f, 0.04f, 2.26f), c, tilt);
        s.Box(new Vector3(0, ValanceY, 2.2f), new Vector3(0.97f, ValanceH, 0.03f), PropColors.Matte(0.95f, 0.95f, 0.93f));
        s.Box(new Vector3(0, AwningBoxY, 0.04f), new Vector3(1.03f, 0.1f, 0.08f), Dark);
        return s.Build();
    }, candle: false)).ToArray();

    public static readonly PropKind Planter = new("Terrace planter", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.22f, 0), new Vector3(1.0f, 0.44f, 0.4f), Stone);
        s.Box(new Vector3(0, 0.44f + 0.2f - Sink / 2, 0), new Vector3(0.9f, 0.4f + Sink, 0.3f), Leaf);
        return s.Build();
    }, candle: false);

    public static readonly PropKind MenuBoard = new("Terrace menu board", () =>
    {
        var s = new MeshScratch();
        // a chalk board on its stand, the day's menu in white lines
        s.Box(new Vector3(0, 0.55f, 0), new Vector3(0.6f, 1.1f, 0.06f), Dark);
        for (int k = 0; k < 5; k++)
            s.Box(new Vector3(-0.05f + (k % 2) * 0.05f, 0.75f + k * 0.07f, 0.03f), new Vector3(0.36f - (k % 2) * 0.1f, 0.02f, 0.024f),
                PropColors.Matte(0.92f, 0.92f, 0.90f));
        s.Box(new Vector3(0, 0.45f, -0.25f), new Vector3(0.5f, 0.9f, 0.04f), Wood, new Basis(Vector3.Right, Mathf.DegToRad(-15)));
        return s.Build();
    }, candle: false);

    public static PropKind Mesh(TerraceKind kind, int colour) => kind switch
    {
        TerraceKind.BistroSet => BistroSet,
        TerraceKind.BeerBench => BeerBench,
        TerraceKind.Parasol => Parasols[colour % Parasols.Length],
        TerraceKind.Awning => Awnings[colour % Awnings.Length],
        TerraceKind.Planter => Planter,
        _ => MenuBoard,
    };

    /// <summary>The box a piece collides as (centre, size), or null for what hangs overhead.</summary>
    public static (Vector3 Centre, Vector3 Size)? Solid(TerraceKind kind) => kind switch
    {
        TerraceKind.BistroSet => (new Vector3(0, 0.45f, 0), new Vector3(1.5f, 0.9f, 0.6f)),
        TerraceKind.BeerBench => (new Vector3(0, 0.38f, 0), new Vector3(2.2f, 0.76f, 1.45f)),
        TerraceKind.Planter => (new Vector3(0, 0.42f, 0), new Vector3(1.0f, 0.84f, 0.4f)),
        TerraceKind.MenuBoard => (new Vector3(0, 0.55f, -0.1f), new Vector3(0.6f, 1.1f, 0.3f)),
        _ => null,
    };
}
