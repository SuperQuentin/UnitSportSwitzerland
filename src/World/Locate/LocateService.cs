using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;
using UnitSport.Vehicles;

namespace UnitSport.World.Locate;

/// <summary>
/// <c>/locate</c> on the machine that owns the world (#1277): the dedicated server, or the client offline.
/// It gathers every list the world already holds into a <see cref="LocateEngine"/> (towns, the survey's
/// points of interest, OSM venues, castles, stadiums, IKEA, landings, airports), builds the
/// <see cref="BuildingIndex"/> at boot on workers (<see cref="BuildingIndexBuilder"/>, cached on disk), and
/// asks the vehicles for the steamer at each search. A parking entrance is checked on the real footprint
/// (<see cref="BuildingFootprint.Compute"/>, tile, roads and ground read for the few nearest candidates), so
/// the coordinates given are the garage door's own. Notes: <c>docs/notes/world/locate.md</c>.
/// </summary>
public partial class LocateService : Node
{
    public const string NodeName = "Locate";
    /// <summary>Answers per search.</summary>
    public const int MaxHits = 3;

    public static LocateService? Instance { get; private set; }

    private readonly IChunkSource _source;
    private readonly TerrainManifest _manifest;
    private readonly Func<PlaceIndex?> _places;
    private readonly WorldOrigin _origin;
    private readonly CancellationTokenSource _cancel = new();

    private LocateEngine? _engine;
    private PoiCatalogue? _enginePois;
    private Task<LocateEngine>? _engineBuild;
    private BuildingIndex? _buildings;
    private TileQueue? _queue;
    private readonly Func<IEnumerable<GlobalPos>> _players;
    private double _playersIn;
    /// <summary>Runs from the first tile read, for the time left in <see cref="Pending"/>.</summary>
    private readonly System.Diagnostics.Stopwatch _indexClock = new();
    private bool _indexing;

    /// <summary>Raised on the main thread once the building index is ready (checks wait on it).</summary>
    public event Action? Indexed;

    /// <summary>The building index, once built or read (null before; empty with no tiles).</summary>
    public BuildingIndex? Buildings => _buildings;

    /// <summary>
    /// Under <c>--frame earth</c>, a leaf's stand-in as the world builds it (<c>EarthWorld.StandOf</c>; null on flat). A parking
    /// entrance is then judged on the leaf's own building, not the LV95 tile's (#1772): on leaves every building's rolls
    /// (shops under flats, the garage) are seeded from its Earth identity (#1658), so the LV95 tile's answer pointed at
    /// garage doors the leaves never build.
    /// </summary>
    public Func<Terrain.Format.CubeKey, Terrain.Leaves.LeafStand?>? Leaves { get; init; }

    /// <param name="players">Where the players are, for the order the index is built in (round them first).</param>
    public LocateService(IChunkSource source, TerrainManifest manifest, Func<PlaceIndex?> places, WorldOrigin origin,
        Func<IEnumerable<GlobalPos>>? players = null)
    {
        _players = players ?? (() => []);
        Name = NodeName;
        _source = source;
        _manifest = manifest;
        _places = places;
        _origin = origin;
    }

    public LocateService() : this(null!, new TerrainManifest(), () => null, new WorldOrigin(0, 0)) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        _cancel.Cancel();
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Offline the client builds the index on the first <c>/locate</c>, not at boot: a client that goes on to
    /// join a server never needs its own.
    /// </summary>
    public bool OnDemand { get; init; }

    private bool _started;

    public override void _Ready()
    {
        if (!OnDemand) EnsureIndexing();
    }

    /// <summary>Starts the index build (or the cache read) once.</summary>
    public void EnsureIndexing()
    {
        if (_started) return;
        _started = true;
        _ = IndexAsync();
    }

    private async Task IndexAsync()
    {
        if (_manifest.Tiles.Count == 0) { _buildings = BuildingIndex.Empty; return; }
        _indexing = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? key = BuildingIndexBuilder.CacheKey(_manifest, earthSeeds: Leaves != null);
        try
        {
            var index = key == null ? null : await Task.Run(() => BuildingIndexBuilder.ReadCache(key));
            bool cached = index != null;
            if (index == null)
            {
                // a quarter of the cores: the server keeps running its world while this goes on
                int workers = Math.Max(1, System.Environment.ProcessorCount / 4);
                var tiles = _manifest.Tiles.Select(t => t.Id).ToList();
                // filled in place, round the players first: /locate answers from it while it grows
                var growing = BuildingIndex.Growing(tiles);
                _queue = new TileQueue(tiles, _origin.E, _origin.N);
                FeedPlayers();
                _buildings = growing;
                if (_engine != null) _engine.Buildings = growing;
                _indexClock.Restart();
                await BuildingIndexBuilder.BuildInto(_source, growing, _queue, workers, _cancel.Token, earthSeeds: Leaves != null);
                index = growing;
                if (key != null) await Task.Run(() => BuildingIndexBuilder.WriteCache(key, index));
            }
            _buildings = index;
            if (_engine != null) _engine.Buildings = index;
            GD.Print($"[locate] building index: {index.Count} buildings from {_manifest.Tiles.Count} tiles in {started.ElapsedMilliseconds} ms"
                + (cached ? " (cached)" : ""));
            Indexed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            GD.PushError($"[locate] building index failed: {e}");
            _buildings = BuildingIndex.Empty;
        }
        finally { _indexing = false; }
    }

    private string? Pending() => _buildings is { Complete: false } growing
        ? LocateEngine.Indexing(growing.Progress.Done, growing.Progress.Total, _indexClock.Elapsed.TotalSeconds)
        : _buildings != null ? null
        : _indexing ? "Reading the building index…"
        : "The building index is not ready.";

    /// <summary>While the index fills: the players' positions, once a second, for the order it is read in.</summary>
    public override void _Process(double delta)
    {
        if (_queue is not { Left: > 0 }) return;
        _playersIn -= delta;
        if (_playersIn > 0) return;
        _playersIn = 1;
        FeedPlayers();
    }

    private void FeedPlayers() => _queue?.SetPlayers(_players().Select(p => (p.Lv95.E, p.Lv95.N)).ToList());

    /// <summary>The engine over the lists as they are now; rebuilt when the POI catalogue arrives or changes.</summary>
    private async Task<LocateEngine> EngineAsync()
    {
        var pois = PoiCatalogueHost.Current;
        if (_engine != null && _enginePois == pois) return _engine;
        if (_engineBuild == null || _enginePois != pois)
        {
            _enginePois = pois;
            var places = _places();
            _engineBuild = Task.Run(() => new LocateEngine(Points(places, pois)));
        }
        var engine = await _engineBuild;
        engine.Buildings = _buildings;
        engine.BuildingsPending = Pending;
        engine.Live = LivePoints;
        return _engine = engine;
    }

    /// <summary>Every named thing of every list, once (a worker: the venue list is read here the first time).</summary>
    public static List<LocatePoint> Points(PlaceIndex? places, PoiCatalogue pois)
    {
        var all = new List<LocatePoint>();
        if (places != null)
            foreach (var p in places.Places)
                all.Add(new LocatePoint(p.Kind switch { PlaceKind.Summit => LocateTarget.Peak, PlaceKind.Pass => LocateTarget.Pass, _ => LocateTarget.Town },
                    p.Name, p.E, p.N, p.Kind == PlaceKind.Town ? p.Canton : $"{p.Elevation} m"));
        foreach (var p in pois.All)
        {
            var target = p.Kind switch
            {
                PoiKind.Peak => LocateTarget.Peak,
                PoiKind.Pass => LocateTarget.Pass,
                PoiKind.SummitCross => LocateTarget.SummitCross,
                PoiKind.Waterfall => LocateTarget.Waterfall,
                PoiKind.Lake => LocateTarget.Lake,
                PoiKind.LookoutTower => LocateTarget.Tower,
                PoiKind.Church => LocateTarget.Church,
                PoiKind.Chapel => LocateTarget.Chapel,
                PoiKind.Castle => LocateTarget.Castle,
                PoiKind.Bridge => LocateTarget.Bridge,
                PoiKind.Dam => LocateTarget.Dam,
                PoiKind.RailStation => LocateTarget.Station,
                PoiKind.BusStop => LocateTarget.BusStop,
                PoiKind.ShipLanding => LocateTarget.Harbour,
                PoiKind.CableCarStation => LocateTarget.CableCar,
                PoiKind.Hut => LocateTarget.Hut,
                _ => LocateTarget.None,
            };
            if (target == LocateTarget.None) continue;
            string detail = p.Kind is PoiKind.Peak or PoiKind.Pass or PoiKind.Hut or PoiKind.SummitCross
                ? $"{p.Elevation:0} m, {p.Canton}" : p.Canton;
            // a lake's point is the middle of its outline: in the water, which is where a boat goes
            all.Add(new LocatePoint(target, p.Name, p.E, p.N, detail));
        }
        foreach (var v in Venues.List)
        {
            var target = v.Type switch
            {
                VenueType.Cinema => LocateTarget.Cinema,
                VenueType.Bowling => LocateTarget.Bowling,
                VenueType.Arcade => LocateTarget.Arcade,
                VenueType.Restaurant => LocateTarget.Restaurant,
                VenueType.Cafe => LocateTarget.Cafe,
                VenueType.Bar => LocateTarget.Bar,
                VenueType.Pub => LocateTarget.Pub,
                VenueType.Nightclub => LocateTarget.Nightclub,
                VenueType.Biergarten => LocateTarget.BeerGarden,
                VenueType.FastFood => LocateTarget.FastFood,
                _ => LocateTarget.None,
            };
            if (target == LocateTarget.None) continue;
            string name = v.Name.Length > 0 ? v.Name : LocateVocabulary.Label(target) is var l ? char.ToUpperInvariant(l[0]) + l[1..] : "";
            string detail = v.Type switch
            {
                VenueType.Cinema when v.Count > 0 => v.Count == 1 ? "1 screen" : $"{v.Count} screens",
                VenueType.Bowling when v.Count > 0 => $"{v.Count} lanes",
                _ => v.Cuisine.Replace(';', ' ').Replace('_', ' '),
            };
            all.Add(new LocatePoint(target, name, v.E, v.N, detail));
        }
        foreach (var (name, canton, kind, points) in Castles.List)
            all.Add(new LocatePoint(LocateTarget.Castle, name, points[0], points[1], $"{kind.ToString().ToLowerInvariant()}, {canton}".TrimEnd(',', ' ')));
        foreach (var s in StadiumCatalog.All)
            all.Add(new LocatePoint(s.Kind switch { VenueKind.Arena => LocateTarget.Arena, VenueKind.OpenAir => LocateTarget.OpenAir, _ => LocateTarget.Stadium },
                s.Name, s.E, s.N, s.Town));
        foreach (var ikea in Landmarks.Ikea)
            all.Add(new LocatePoint(LocateTarget.Ikea, "IKEA " + ikea.Name, ikea.E, ikea.N));
        foreach (var l in Landings.All)
            all.Add(new LocatePoint(LocateTarget.Harbour, l.Name, l.E, l.N, l.Berth is { Fits: true } ? "pier" : ""));
        if (AirportStands.Instance?.Index is { } airports)
            foreach (var a in airports.Airports)
                all.Add(new LocatePoint(LocateTarget.Airport, a.Name, a.E, a.N, a.Code));
        return all;
    }

    /// <summary>The steamer wherever it is now (main thread: reads the vehicles).</summary>
    private IEnumerable<LocatePoint> LivePoints()
    {
        if (VehicleManager.Instance is not { } vehicles) yield break;
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody { Kind: Player.RideKind.Steamer, Wrecked: false } v)
            {
                var (e, n) = _origin.Frame.ToLv95(v.GlobalPosition);
                yield return new LocatePoint(LocateTarget.Steamer, "Paddle steamer", e, n, Landings.Nearest(e, n) is { } at && Math.Abs(at.E - e) + Math.Abs(at.N - n) < 300 ? $"at {at.Name}" : "on the lake");
            }
    }

    /// <summary>
    /// The answer to <c>/locate &lt;rest&gt;</c> asked from <paramref name="at"/>: the lines to show, and whether it is a
    /// refusal. Awaits on the main thread (the engine and the garage checks run on workers).
    /// </summary>
    public async Task<(List<string> Lines, bool Failed)> Answer(string rest, GlobalPos? at)
    {
        EnsureIndexing();
        if (rest.Trim().Length == 0) return ([LocateVocabulary.Usage, "Things: " + string.Join(", ", LocateVocabulary.Nouns)], true);
        if (LocateVocabulary.Parse(rest, out string error) is not { } q) return ([error], true);

        double e, n;
        string from = "you";
        if (q.Town != null)
        {
            if (_places()?.Search(q.Town, 1) is not [var town])
                return ([$"No town matching '{q.Town}'."], true);
            (e, n, from) = (town.E, town.N, town.Name);
        }
        else if (at is { } here) (e, n) = (here.Lv95.E, here.Lv95.N);
        else return (["Where are you? Get into the world first, or ask '/locate <thing> in <town>'."], true);
        // still indexing: what is round the asker next
        if (q.OnBuildings || q.Target == LocateTarget.Church) _queue?.Prefer(e, n);

        var engine = await EngineAsync();
        var result = engine.Find(q, e, n, MaxHits);
        if (q.Has(LocateModifier.Parking) && result.Refusal == null)
        {
            var doors = await GaragesAsync(result.Hits, MaxHits);
            // the doors are judged against what is indexed, as the engine judges any other answer
            result = _buildings is { Complete: false } growing
                ? LocateEngine.Partial(doors, growing.CoveredRadius(e, n), MaxHits, Pending())
                : new LocateResult(doors);
        }
        return (LocateEngine.Lines(q, result, e, n, from), result.Refusal != null);
    }

    /// <summary>
    /// The candidates whose real footprint has an underground garage door, nearest first, at most
    /// <paramref name="max"/>, each moved onto its door. Reads the tiles of the candidates only.
    /// </summary>
    private async Task<List<LocateHit>> GaragesAsync(List<LocateHit> candidates, int max)
    {
        // a tile's candidates are worked out together, the first time one of them comes up, so its roads are
        // indexed once; the tiles of candidates past the first few that pass are never read
        var doors = new Dictionary<(int E, int N, int Index), (double E, double N)?>();
        var read = new HashSet<TileId>();
        var kept = new List<LocateHit>();
        foreach (var hit in candidates)
        {
            if (hit.Building is not { } b) continue;
            var id = new TileId(b.TileE, b.TileN);
            if (read.Add(id))
            {
                var tile = await _source.LoadBuildingsAsync(id, _cancel.Token);
                if (tile == null) continue;
                var mine = candidates.Where(h => h.Building is { } o && o.TileE == b.TileE && o.TileN == b.TileN && o.Index < tile.Buildings.Count)
                    .Select(h => h.Building!.Value.Index).ToList();
                if (Leaves is { } leaves)
                {
                    var onLeaves = await Task.Run(() => GaragesOnLeaves(tile, mine, leaves));
                    for (int i = 0; i < mine.Count; i++) doors[(b.TileE, b.TileN, mine[i])] = onLeaves[i];
                    continue;
                }
                var roads = await _source.LoadRoadsAsync(id, _cancel.Token);
                var grid = await _source.LoadChunkAsync(id, _cancel.Token);
                var fps = await Task.Run(() => BuildingFootprint.Compute(tile, mine, roads, grid));
                for (int i = 0; i < mine.Count; i++)
                {
                    // the door is tile-local (X east, Z south from the tile's north-west corner)
                    if (fps[i]?.Extra.FirstOrDefault(d => d.Ramp != GarageRule.RampKind.None) is { Width: > 0 } g)
                    {
                        var (ge, gn) = id.Global(g.Position.X, g.Position.Z);
                        doors[(b.TileE, b.TileN, mine[i])] = (ge, gn);
                    }
                    else doors[(b.TileE, b.TileN, mine[i])] = null;
                }
            }
            if (!doors.TryGetValue((b.TileE, b.TileN, b.Index), out var found) || found is not var (de, dn)) continue;
            kept.Add(hit with { E = de, N = dn, Detail = hit.Detail.Length > 0 ? hit.Detail + ", garage door" : "garage door" });
            if (kept.Count >= max) break;
        }
        return kept;
    }

    /// <summary>
    /// Under earth (#1772): each of an LV95 tile's buildings found on the leaf under its footprint centre by its Earth identity
    /// (<see cref="BuildingId"/>, the same from both builds but a few on a unit's edge, so the nearest within two units), and
    /// that leaf's garage door as the world built it, in LV95 notation; null where the leaf's building has none.
    /// </summary>
    internal static (double E, double N)?[] GaragesOnLeaves(BuildingTile tile, IReadOnlyList<int> indices,
        Func<Terrain.Format.CubeKey, Terrain.Leaves.LeafStand?> leaves)
    {
        var result = new (double E, double N)?[indices.Count];
        var ids = BuildingId.Assign(tile.Buildings, bb => BuildingId.OnLv95Tile(tile.Id, bb));
        for (int i = 0; i < indices.Count; i++)
        {
            if (ids[indices[i]] is not { } want) continue;
            var leaf = Terrain.Leaves.LeafFrames.LeafAt(want.LatDeg, want.LonDeg);
            if (leaves(leaf) is not { Buildings: { } built, Doors: { } leafDoors } stand) continue;
            int best = -1, bestD = 3;
            for (int j = 0; j < built.Buildings.Count; j++)
            {
                // the stand-in key hashes as its building's Earth identity (LeafStandIn.Buildings registered it)
                if (Fnv.StandIn($"{stand.Id.E}_{stand.Id.N}_{j}") is not { } name || !BuildingId.TryParse(name, out var have)) continue;
                int d = Math.Abs(have.LatE5 - want.LatE5) + Math.Abs(have.LonE5 - want.LonE5) + (have.Dup == want.Dup ? 0 : 1);
                if (d < bestD) (best, bestD) = (j, d);
            }
            if (best < 0) continue;
            foreach (var d in leafDoors)
                if (d.Index == best && d.Ramp != GarageRule.RampKind.None && d.Width > 0)
                {
                    // the door is in the stand-in tile's frame: its place, in LV95 notation as every answer is
                    var (e, n, _) = SwissGeodesy.FromEcef(GlobalPos.OfTile(stand.Id, d.Position.X, d.Position.Y, d.Position.Z).Ecef);
                    result[i] = (e, n);
                    break;
                }
        }
        return result;
    }

    /// <summary>For a server-run bot (#837): the nearest things a query names, from a point; empty while it cannot answer.</summary>
    public async Task<List<LocateHit>> FindAsync(string query, double e, double n, int max = MaxHits)
    {
        if (LocateVocabulary.Parse(query, out _) is not { } q) return [];
        EnsureIndexing();
        var engine = await EngineAsync();
        var result = engine.Find(q, e, n, max);
        return q.Has(LocateModifier.Parking) ? await GaragesAsync(result.Hits, max) : result.Hits;
    }
}
