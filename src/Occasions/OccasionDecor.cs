using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions;

/// <summary>
/// Puts the running occasions' props on every tile that has its buildings loaded, and takes them
/// off again. Client only.
///
/// <para>
/// Driven by <see cref="ChunkManager.TileFurnished"/>, which fires beside
/// <see cref="DoorIndex.SetTile"/>: the doors a jack-o'-lantern stands at are exactly the doors
/// the player can walk up to. Props are MultiMeshes <b>parented to the tile's own node</b>, so
/// they unload with it and cost nothing to forget. The tile's road file is read too (from the
/// cache, where the tile build just put it), so nothing is planted in a carriageway.
/// </para>
///
/// <para>
/// Everything is rebuilt from scratch whenever the running set changes — a toggle in the settings,
/// a server's <c>/occasion stop</c> — or the town list arrives. Placement is a pure function of the
/// tile (see <see cref="OccasionHash"/>), so a rebuild puts every prop back exactly where it was.
/// </para>
/// </summary>
public partial class OccasionDecor : Node
{
    public static OccasionDecor? Instance { get; private set; }

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly IChunkSource _source;
    private readonly Dictionary<TileId, Tile> _tiles = new();
    private ShaderMaterial _candle = null!, _steady = null!;
    private int _epoch;
    private volatile bool _closing;

    /// <summary><c>--decorlog</c>: print what each tile got and where, for aiming a <c>--shot</c> at it.</summary>
    private static readonly bool LogPlacement = CmdArgs.Has("--decorlog");

    /// <summary>Hunt claims, supplied by <see cref="OccasionHunt"/>: a claimed spot is not drawn.</summary>
    public Func<string, bool> IsClaimed { get; set; } = _ => false;

    private sealed class Tile
    {
        public required ChunkNode Node;
        public required DoorSpot[] Doors;
        public RoadTile? Roads;
        public bool RoadsReady;
        public Node3D? Decor;
        public List<HuntSpot> Spots = new();
        public List<Rect2> Patches = new();
    }

    public OccasionDecor(ChunkManager chunks, WorldOrigin origin, IChunkSource source)
    {
        Name = "OccasionDecor";
        _chunks = chunks;
        _origin = origin;
        _source = source;
    }

    public OccasionDecor() : this(null!, null!, null!) { }

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        _candle = MakeMaterial(candle: true);
        _steady = MakeMaterial(candle: false);
        GameSettings.Changed += ApplyFog;

        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnloaded += OnUnloaded;
        _chunks.TileUnfurnished += OnUnfurnished;
        _chunks.TerrainReplaced += OnReplaced;
        OccasionManager.Changed += RedecorateAll;
        OccasionTowns.Changed += RedecorateAll;
    }

    public override void _ExitTree()
    {
        _closing = true;
        GameSettings.Changed -= ApplyFog;
        OccasionManager.Changed -= RedecorateAll;
        OccasionTowns.Changed -= RedecorateAll;
        if (_chunks != null)
        {
            _chunks.TileFurnished -= OnFurnished;
            _chunks.TileUnloaded -= OnUnloaded;
            _chunks.TileUnfurnished -= OnUnfurnished;
            _chunks.TerrainReplaced -= OnReplaced;
        }
        if (Instance == this) Instance = null;
    }

    public override void _Process(double delta) => OccasionTowns.Poll();

    /// <summary>
    /// Every <see cref="PropKind"/> declared anywhere, under its own material, in the model
    /// viewer (--models): a new occasion's props show by themselves.
    /// </summary>
    [Showcase("Occasions", "Prop")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseProps() =>
        typeof(PropKind).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                         | System.Reflection.BindingFlags.Static))
            .Where(f => f.FieldType == typeof(PropKind))
            .Select(f => (PropKind)f.GetValue(null)!)
            .Select(kind => (kind.Name, (Func<Node3D>)(() =>
                new MeshInstance3D { Mesh = kind.Mesh, MaterialOverride = MakeMaterial(kind.Candle) })));

    private static ShaderMaterial MakeMaterial(bool candle)
    {
        var m = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        m.SetShaderParameter("flicker", candle ? 1f : 0f);
        FogUniforms.Apply(m);
        return m;
    }

    private void ApplyFog()
    {
        FogUniforms.Apply(_candle);
        FogUniforms.Apply(_steady);
    }

    // ---- tile lifecycle ------------------------------------------------------------------------

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        // a tile rebuilt at another stride commits its buildings again: same doors, same roads
        if (_tiles.TryGetValue(id, out var existing) && existing.Node == node && existing.RoadsReady)
        {
            existing.Doors = doors;
            Decorate(id, existing);
            return;
        }

        var tile = new Tile { Node = node, Doors = doors };
        _tiles[id] = tile;
        int epoch = _epoch;
        _source.LoadRoadsAsync(id).ContinueWith(task =>
        {
            if (_closing) return;
            var roads = task.IsCompletedSuccessfully ? task.Result : null;
            Callable.From(() =>
            {
                if (_closing || epoch != _epoch || !_tiles.TryGetValue(id, out var current) || current != tile) return;
                tile.Roads = roads;
                tile.RoadsReady = true;
                Decorate(id, tile);
            }).CallDeferred();
        });
    }

    private void OnUnloaded(TileId id) => _tiles.Remove(id);   // the props went with the tile's node

    /// <summary>The tile shed its buildings (#553): the decorations at their doors go, back with the next furnishing.</summary>
    private void OnUnfurnished(TileId id, ChunkNode node)
    {
        if (!_tiles.Remove(id, out var tile)) return;
        if (tile.Decor != null && IsInstanceValid(tile.Decor)) tile.Decor.QueueFree();
    }

    private void OnReplaced(Func<TileId, bool>? affected)
    {
        // a rebase: everything goes, and anything still waiting on a tile's roads is stale
        if (affected == null)
        {
            _epoch++;
            _tiles.Clear();
            return;
        }
        // real tiles merged into the generated fill: only the tiles whose ground changed were
        // unloaded (TileUnloaded has dropped them already); a decoration still waiting for one
        // finds its entry gone and gives up, the rest carry on
        foreach (var id in _tiles.Keys.Where(affected).ToList()) _tiles.Remove(id);
    }

    private void RedecorateAll()
    {
        foreach (var (id, tile) in _tiles.ToList())
            if (tile.RoadsReady) Decorate(id, tile);
    }

    /// <summary>Re-dresses the tile under a world point — after a hunt claim, so the prop goes.</summary>
    public void Refresh(Vector3 world)
    {
        var id = _origin.TileAt(world);
        if (_tiles.TryGetValue(id, out var tile) && tile.RoadsReady) Decorate(id, tile);
    }

    // ---- dressing ------------------------------------------------------------------------------

    private void Decorate(TileId id, Tile tile)
    {
        if (tile.Decor != null && IsInstanceValid(tile.Decor)) tile.Decor.QueueFree();
        tile.Decor = null;
        tile.Spots = new();
        tile.Patches = new();
        if (!IsInstanceValid(tile.Node))
        {
            _tiles.Remove(id);
            return;
        }

        var running = OccasionManager.Instance?.Active
            .Where(a => a.Has(OccasionFacets.Decorations) || a.Has(OccasionFacets.Hunt)).ToList();
        if (running is not { Count: > 0 }) return;

        var tileOrigin = _origin.ToWorld(id.MinE, id.MaxN, 0);
        var ctx = new TileContext
        {
            Id = id,
            Origin = tileOrigin,
            World = _origin,
            Doors = tile.Doors,
            Roads = tile.Roads,
            HeightAtWorld = p => _chunks.TryGetSurface(p, out float h) ? h : null,
            CoverAtWorld = p => _chunks.TryGetCover(p, out var c) ? c : null,
            Towns = OccasionTowns.Near(id.MinE + 500, id.MaxN - 500, 3000).ToList(),
        };
        var into = new DecorBuilder { IsClaimed = IsClaimed };
        foreach (var a in running)
        {
            if (a.Has(OccasionFacets.Decorations)) a.Content.Decorate(ctx, into);
            if (a.Has(OccasionFacets.Hunt)) a.Content.PlaceHunt(ctx, into);
        }

        tile.Spots = into.Spots;
        tile.Patches = into.Patches;
        if (LogPlacement)
        {
            var first = into.Instances.Values.SelectMany(l => l).Select(l => l.Xf).FirstOrDefault();
            GD.Print($"[decor] {id}: {string.Join(", ", into.Instances.Select(k => $"{k.Value.Count} {k.Key.Name}"))}"
                + $", {into.Patches.Count} patches, {into.Spots.Count} hunt spots; first at {first.Origin + tileOrigin}"
                + $" facing {(-first.Basis.Z).Normalized()}"
                + (into.Patches.Count > 0 ? $"; patch at {tileOrigin + new Vector3(into.Patches[0].GetCenter().X, 0, into.Patches[0].GetCenter().Y)}" : ""));
            foreach (var s in into.Spots)
                GD.Print($"[decor]   hunt {s.Key} at {tileOrigin + s.Local}");
        }
        if (into.Instances.Count == 0 && into.Meshes.Count == 0) return;

        var root = new Node3D { Name = "OccasionDecor" };
        foreach (var (kind, list) in into.Instances)
        {
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = kind.Mesh,
                InstanceCount = list.Count,
            };
            for (int i = 0; i < list.Count; i++)
            {
                multi.SetInstanceTransform(i, list[i].Xf);
                multi.SetInstanceCustomData(i, new Color(list[i].Seed, list[i].Glow, 0, 0));
            }
            root.AddChild(new MultiMeshInstance3D
            {
                Name = kind.Name,
                Multimesh = multi,
                MaterialOverride = kind.Candle ? _candle : _steady,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        foreach (var mesh in into.Meshes)
            root.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = _steady,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

        tile.Node.AddChild(root);
        tile.Decor = root;
    }

    // ---- queries -------------------------------------------------------------------------------

    /// <summary>Unclaimed hunt spots within <paramref name="reach"/> of a world point, nearest first.</summary>
    public IEnumerable<(HuntSpot Spot, Vector3 World)> SpotsNear(Vector3 world, float reach)
    {
        var id = _origin.TileAt(world);
        var found = new List<(HuntSpot, Vector3, float)>();
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                if (!_tiles.TryGetValue(new TileId(id.E + de, id.N + dn), out var tile) || !IsInstanceValid(tile.Node)) continue;
                var o = tile.Node.GlobalPosition;
                foreach (var s in tile.Spots)
                {
                    var w = o + s.Local;
                    float d = w.DistanceTo(world);
                    if (d <= reach) found.Add((s, w, d));
                }
            }
        return found.OrderBy(f => f.Item3).Select(f => (f.Item1, f.Item2));
    }

    /// <summary>Every hunt spot currently drawn, for <c>--huntcheck</c>.</summary>
    public IEnumerable<(HuntSpot Spot, Vector3 World)> AllSpots() =>
        _tiles.Values.Where(t => IsInstanceValid(t.Node))
            .SelectMany(t => t.Spots.Select(s => (s, t.Node.GlobalPosition + s.Local)));

    /// <summary>True inside one of the pumpkin patches currently drawn.</summary>
    public bool InPatch(Vector3 world)
    {
        var id = _origin.TileAt(world);
        if (!_tiles.TryGetValue(id, out var tile) || !IsInstanceValid(tile.Node)) return false;
        var local = world - tile.Node.GlobalPosition;
        var p = new Vector2(local.X, local.Z);
        foreach (var r in tile.Patches)
            if (r.HasPoint(p)) return true;
        return false;
    }
}
