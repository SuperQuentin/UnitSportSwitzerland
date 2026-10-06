using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// The specialty buyers in the world (#494): near the camera, each buyer's yard gets a weighbridge
/// office and a tall sign with its name, what it buys and the premium, beside an access road
/// (<see cref="FarmBuyerSite"/>), so a player driving a load there sees where to stop. Client only,
/// drawn from the map's roads and buildings; selling itself still goes by the yard's circle
/// (<see cref="FarmBuyers.At"/>). A plain <see cref="Node"/>: its children follow origin shifts.
/// </summary>
public partial class FarmBuyerYards : Node
{
    /// <summary>Built within this many metres of the camera, freed past <see cref="Drop"/>.</summary>
    private const double Show = 2500, Drop = 3500;

    private WorldOrigin _origin = null!;
    private Func<IChunkSource?> _source = null!;
    private Func<Vector3, float?> _groundAt = null!;
    private readonly Dictionary<string, Node3D> _built = new();
    private readonly HashSet<string> _busy = new();
    private double _wait;

    public static FarmBuyerYards Create(Node world, WorldOrigin origin, Func<IChunkSource?> source, Func<Vector3, float?> groundAt)
    {
        var y = new FarmBuyerYards { Name = "FarmBuyerYards", _origin = origin, _source = source, _groundAt = groundAt };
        world.AddChild(y);
        return y;
    }

    /// <summary>Probes: the yard node of a buyer, once built.</summary>
    public Node3D? YardOf(string key) => _built.GetValueOrDefault(key);

    /// <summary>Probes: the terrain's height under a world point, or null while it loads.</summary>
    public float? GroundAt(Vector3 world) => _groundAt(world);

    /// <summary>Probes: the buyers whose office stands by an access road (the others stand on their point).</summary>
    public HashSet<string> ByRoad { get; } = new();

    public override void _Process(double delta)
    {
        _wait -= delta;
        if (_wait > 0) return;
        _wait = 1.0;
        if (GetViewport().GetCamera3D() is not { } cam) return;
        var (e, n) = _origin.ToLv95(cam.GlobalPosition);
        foreach (var b in FarmBuyers.Everyone)
        {
            double d = Math.Sqrt((b.E - e) * (b.E - e) + (b.N - n) * (b.N - n));
            if (_built.TryGetValue(b.Key, out var yard))
            {
                if (d > Drop) { yard.QueueFree(); _built.Remove(b.Key); }
                else Settle(yard);
            }
            else if (d < Show && _busy.Add(b.Key)) Build(b);
        }
    }

    private async void Build(FarmBuyer b)
    {
        var sites = new List<YardSite>();
        if (_source() is { } source)
        {
            var roads = new List<(TileId, RoadSegment)>();
            var houses = new List<(TileId, Building)>();
            var tiles = new HashSet<TileId>();
            double r = b.Reach + 60;
            foreach (var (de, dn) in new[] { (0.0, 0.0), (-r, -r), (r, -r), (-r, r), (r, r) })
                tiles.Add(TileId.FromLv95(b.E + de, b.N + dn));
            foreach (var t in tiles)
            {
                try
                {
                    if (await source.LoadRoadsAsync(t) is { } rt) roads.AddRange(rt.Segments.Select(s => (t, s)));
                    if (await source.LoadBuildingsAsync(t) is { } bt) houses.AddRange(bt.Buildings.Select(h => (t, h)));
                }
                catch (Exception ex) { GD.PushWarning($"[buyer] {b.Key} tile {t}: {ex.Message}"); }
            }
            sites = await Task.Run(() => FarmBuyerSite.Candidates(b, roads, houses));
        }
        if (!IsInsideTree()) return;
        // no access road in the data (a fixture, a stand-in): on the buyer's point
        bool byRoad = sites.Count > 0;
        if (!byRoad) sites.Add(new YardSite(b.E, b.N, 0f, 0f));
        // wait for the ground, then take the nearest site level enough for the office's plinth (else the levellest)
        for (int i = 0; i < 120 && Floor(sites[0]) == null && IsInsideTree(); i++)
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;
        if (Floor(sites[0]) == null) { _busy.Remove(b.Key); return; }
        var level = sites.Where(s => Floor(s) is { Spread: <= MaxSpread }).Take(1).ToList();
        var site = level.Count > 0 ? level[0] : sites.MinBy(s => Floor(s)?.Spread ?? float.MaxValue);
        if (byRoad) ByRoad.Add(b.Key);
        var yard = new FarmBuyerYard(b) { Name = $"Yard_{b.Key}" };
        AddChild(yard);
        yard.GlobalTransform = new Transform3D(new Basis(Vector3.Up, site.Yaw), _origin.ToWorld(site.E, site.N, Floor(site)!.Value.Top));
        _built[b.Key] = yard;
        _busy.Remove(b.Key);
        GD.Print(FormattableString.Invariant($"[buyer] {b.Key}: office at {site.E:F0}/{site.N:F0}, {Math.Sqrt((site.E - b.E) * (site.E - b.E) + (site.N - b.N) * (site.N - b.N)):F0} m from the point, ground falls {Floor(site)!.Value.Spread:F1} m under it"));
    }

    /// <summary>The most the ground may fall under the office: its plinth reaches this far down.</summary>
    private const float MaxSpread = 1.2f;
    private static readonly Vector3[] Corners = { new(-1.6f, 0, -1.3f), new(1.6f, 0, -1.3f), new(-1.6f, 0, 1.3f), new(1.6f, 0, 1.3f) };

    /// <summary>The ground at the office's corners at a site: the highest (its floor) and how far it falls; null while it loads.</summary>
    private (float Top, float Spread)? Floor(YardSite s) => Floor(new Transform3D(new Basis(Vector3.Up, s.Yaw), _origin.ToWorld(s.E, s.N, 0)));

    private (float Top, float Spread)? Floor(Transform3D at)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var c in Corners)
        {
            if (_groundAt(at * c) is not { } h) return null;
            lo = Math.Min(lo, h);
            hi = Math.Max(hi, h);
        }
        return (hi, hi - lo);
    }

    /// <summary>Puts a built office back on the ground: the terrain under it refines as its finer tiles load.</summary>
    private void Settle(Node3D yard)
    {
        if (Floor(yard.GlobalTransform with { Origin = yard.GlobalPosition with { Y = 0 } }) is not { } f) return;
        if (Math.Abs(f.Top - yard.GlobalPosition.Y) > 0.05f) yard.GlobalPosition = yard.GlobalPosition with { Y = f.Top };
    }
}

/// <summary>One buyer's weighbridge office and sign. Origin on the ground, front (+Z) to the road.</summary>
public partial class FarmBuyerYard : StaticBody3D
{
    private static readonly Dictionary<Color, ArrayMesh> Meshes = new();
    /// <summary>The sign board's centre: right of the office, high enough to see over a lorry.</summary>
    private const float SignX = 4.0f, SignY = 6.3f;
    private readonly FarmBuyer _buyer;

    public FarmBuyerYard(FarmBuyer buyer) => _buyer = buyer;
    public FarmBuyerYard() : this(FarmBuyers.All[0]) { }

    /// <summary>The sign's second line: "Buys sugar beet · +30 %".</summary>
    public static string GoodsLine(FarmBuyer b) =>
        $"Buys {b.GoodsText(FarmSales.NameOfItem)} · +{Math.Round((b.Premium - 1) * 100):0} %";

    public override void _Ready()
    {
        var colour = BrandColour(_buyer);
        // MeshScratch builds facing -Z (a node's forward); turned back so the front faces +Z with the labels
        AddChild(new MeshInstance3D { Mesh = Mesh(colour), MaterialOverride = ItemDefs.Material, Rotation = new Vector3(0, Mathf.Pi, 0) });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3.2f, 2.8f, 2.6f) }, Position = new Vector3(0, 1.4f, 0) });
        foreach (float x in new[] { -2.2f, 2.2f })
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, 8.9f, 0.2f) }, Position = new Vector3(SignX + x, 3.45f, 0.6f) });
        // the sign, both faces: the name big, the place and the goods under it
        foreach (float yaw in new[] { 0f, Mathf.Pi })
        {
            var face = new Node3D { Position = new Vector3(SignX, SignY, 0.6f), Rotation = new Vector3(0, yaw, 0) };
            AddChild(face);
            face.AddChild(Text(_buyer.Name, 84, new Vector3(0, 0.05f, 0.09f), Colors.White, VerticalAlignment.Bottom));
            face.AddChild(Text($"{_buyer.Place}\n{GoodsLine(_buyer)}", 56, new Vector3(0, -0.05f, 0.09f), new Color(1f, 0.95f, 0.8f), VerticalAlignment.Top));
        }
        AddChild(Text("WEIGHBRIDGE · ANNAHME", 40, new Vector3(0, 2.45f, 1.36f), new Color(0.15f, 0.15f, 0.15f), VerticalAlignment.Center));
    }

    /// <summary>A label wrapped to the board's width (4.2 m).</summary>
    private static Label3D Text(string text, int size, Vector3 at, Color colour, VerticalAlignment anchor) => new()
    {
        Width = 4.2f / 0.0045f,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        VerticalAlignment = anchor,
        Text = text,
        FontSize = size,
        PixelSize = 0.0045f,
        Modulate = colour,
        OutlineSize = 0,
        DoubleSided = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        Position = at,
    };

    /// <summary>Sugar factories blue, mills a flour red, oil mills yellow: one colour per trade on the board.</summary>
    private static Color BrandColour(FarmBuyer b) =>
        b.Buys(ItemId.SugarBeet) ? new Color(0.12f, 0.30f, 0.62f)
        : b.Buys(ItemId.Rapeseed) ? new Color(0.72f, 0.58f, 0.08f)
        : new Color(0.62f, 0.16f, 0.12f);

    /// <summary>The office (a box with a flat roof, door and window toward the road) and the sign on two posts to its right.</summary>
    private static ArrayMesh Mesh(Color board)
    {
        if (Meshes.TryGetValue(board, out var cached)) return cached;
        var s = new MeshScratch();
        var wall = new Color(0.86f, 0.84f, 0.78f);
        var trim = new Color(0.35f, 0.36f, 0.38f);
        var glass = new Color(0.25f, 0.35f, 0.42f);
        s.Box(new Vector3(0, 0.55f, 0), new Vector3(3.2f, 4.1f, 2.6f), wall);   // 1.5 m below the floor: the ground falling under it never shows a gap
        s.Box(new Vector3(0, 2.68f, 0.1f), new Vector3(3.6f, 0.16f, 3.0f), trim);   // the roof slab, over the door
        s.Box(new Vector3(-0.8f, 1.0f, 1.31f), new Vector3(0.9f, 2.0f, 0.03f), trim);   // door
        s.Box(new Vector3(0.7f, 1.5f, 1.31f), new Vector3(1.2f, 0.9f, 0.03f), glass);   // the window onto the scale
        s.Box(new Vector3(0, 2.45f, 1.32f), new Vector3(2.6f, 0.28f, 0.02f), new Color(0.95f, 0.78f, 0.15f));   // the reception band
        // the sign: two grey posts, the board in the trade's colour, a white rim
        foreach (float x in new[] { -2.2f, 2.2f })
            s.Box(new Vector3(SignX + x, 3.45f, 0.6f), new Vector3(0.18f, 8.9f, 0.18f), trim);
        s.Box(new Vector3(SignX, SignY, 0.6f), new Vector3(4.6f, 2.3f, 0.14f), Colors.White);
        s.Box(new Vector3(SignX, SignY, 0.6f), new Vector3(4.4f, 2.1f, 0.16f), board);
        return Meshes[board] = s.Build();
    }
}
