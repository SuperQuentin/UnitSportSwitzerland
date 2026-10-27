using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The rewards of the world as one peer computes them (#1227): <see cref="RewardPlacer"/> run on a tile's coarse
/// terrain, POIs and roads, cached; the server and every client hold one and compute the same spots. The server
/// places a tile only when a claim names it and keeps the last 64; a client places the tiles round its player.
/// Under <c>--rewardspots fixture|&lt;file&gt;</c> the spots are hand-placed instead (the checks), and a fixture
/// course without it places nothing.
/// </summary>
public sealed class RewardMap
{
    private readonly Func<IChunkSource?> _source;
    private readonly Func<PoiCatalogue> _catalogue;
    private readonly List<RewardSpot>? _fixed;
    private readonly float _fixedGround;
    private readonly int _keep;
    private readonly Dictionary<TileId, Entry> _tiles = new();
    private long _clock;

    private sealed class Entry
    {
        public RewardSpot[]? Spots;
        public ChunkGrid? Grid;
        public Task<RewardSpot[]>? Loading;
        public long Used;
        public readonly Dictionary<string, SealLayout> Layouts = new(StringComparer.Ordinal);
    }

    /// <summary>Spots placed by the placer this run (stats).</summary>
    public int Placed { get; private set; }

    public bool Enabled { get; }

    public RewardMap(Func<IChunkSource?> source, Func<PoiCatalogue> catalogue, int keepTiles, (double E, double N) start)
    {
        _source = source;
        _catalogue = catalogue;
        _keep = keepTiles;
        string? arg = CmdArgs.Value("--rewardspots");
        _fixedGround = (float)PoiCatalogue.FixtureGround;
        if (arg == "fixture")
        {
            _fixed = RewardSpots.Fixture(start.E, start.N, _fixedGround, catalogue());
            // a photo spot for the checks: the fixture peak framed from the start
            PhotoSpots.Extra.Clear();
            if (catalogue().OfKind(PoiKind.Peak) is { Count: > 0 } peaks)
                PhotoSpots.Extra.Add(new PhotoSpot("ph:fixture", "The Fixture Peak from the start", start.E, start.N, PhotoSpots.StandRadius,
                    peaks[0].E, peaks[0].N, peaks[0].Elevation + 4, peaks[0].Name, peaks[0].Id));
        }
        else if (arg != null)
        {
            try { _fixed = RewardSpots.Parse(File.ReadAllLines(ProjectSettings.GlobalizePath(arg)), start.E, start.N, _fixedGround); }
            catch (Exception e) { GD.PushWarning($"[rewards] --rewardspots {arg}: {e.Message}"); _fixed = new(); }
        }
        // a fixture course has no real ground for the placer: hand-placed spots, or nothing
        Enabled = _fixed != null || Systems.FixtureCourse == null;
        if (_fixed != null) GD.Print($"[rewards] {_fixed.Count} hand-placed spots ({arg})");
    }

    /// <summary>The hand-placed spots, when the run has them (checks).</summary>
    public IReadOnlyList<RewardSpot>? Fixed => _fixed;

    /// <summary>A tile's spots if they are known now, else null (and they are being placed).</summary>
    public RewardSpot[]? TryTile(TileId id)
    {
        if (!Enabled) return Array.Empty<RewardSpot>();
        if (_fixed != null) return FixedIn(id);
        if (_tiles.TryGetValue(id, out var e) && e.Spots != null)
        {
            e.Used = ++_clock;
            return e.Spots;
        }
        _ = TileAsync(id);
        return null;
    }

    /// <summary>A tile's spots, placed on the thread pool the first time.</summary>
    public Task<RewardSpot[]> TileAsync(TileId id)
    {
        if (!Enabled) return Task.FromResult(Array.Empty<RewardSpot>());
        if (_fixed != null) return Task.FromResult(FixedIn(id));
        if (_tiles.TryGetValue(id, out var e))
        {
            e.Used = ++_clock;
            if (e.Spots != null) return Task.FromResult(e.Spots);
            if (e.Loading != null) return e.Loading;
        }
        else _tiles[id] = e = new Entry { Used = ++_clock };
        var entry = e;
        entry.Loading = Load(id, entry);
        Trim();
        return entry.Loading;
    }

    private async Task<RewardSpot[]> Load(TileId id, Entry entry)
    {
        var source = _source();
        var cat = _catalogue();
        // the POIs on main: the catalogue is replaced whole there; the lakes whose box may cross this tile too
        var pois = cat.InTile(id).ToArray();
        var lakes = new List<Poi>();
        cat.Near(id.MinE + 500, id.MinN + 500, 900, lakes, PoiKind.Lake);
        RewardSpot[] spots = Array.Empty<RewardSpot>();
        ChunkGrid? grid = null;
        try
        {
            if (source != null)
            {
                grid = await source.LoadCoarseChunkAsync(id);
                if (grid == null && await source.LoadChunkAsync(id) is { } full) grid = full.Decimate(ChunkFormat.CoarseStride);
                RoadTile? roads = null;
                try { roads = await source.LoadRoadsAsync(id); }
                catch (Exception) { /* no roads there */ }
                var g = grid;
                spots = await Task.Run(() => RewardPlacer.Place(id, g, pois, lakes, roads, p => Cairns.Of(cat, p.Canton).Contains(p.Id)));
            }
        }
        catch (Exception ex) { GD.PushWarning($"[rewards] placing {id}: {ex.Message}"); }
        entry.Grid = grid;
        entry.Spots = spots;
        entry.Loading = null;
        Placed += spots.Length;
        return spots;
    }

    private RewardSpot[] FixedIn(TileId id)
    {
        var list = new List<RewardSpot>();
        foreach (var s in _fixed!)
            if (s.Tile == (id.E, id.N)) list.Add(s);
        return list.ToArray();
    }

    private void Trim()
    {
        if (_keep <= 0 || _tiles.Count <= _keep) return;
        var oldest = _tiles.Where(kv => kv.Value.Loading == null).OrderBy(kv => kv.Value.Used).Take(_tiles.Count - _keep).Select(kv => kv.Key).ToList();
        foreach (var k in oldest) _tiles.Remove(k);
    }

    /// <summary>Drops a tile's spots (a client's tile went out of range).</summary>
    public void Forget(TileId id) => _tiles.Remove(id);

    /// <summary>The spot an id names, from its own tile's placement; null when that tile has no such spot (a forged id).</summary>
    public async Task<RewardSpot?> FindAsync(string id)
    {
        var cat = _catalogue();
        if (!RewardRules.TryTile(id, p => cat.Resolve(p) is { } poi ? (poi.E, poi.N) : null, out var t)) return null;
        string spotId = RewardRules.SpotIdOf(id);
        foreach (var s in await TileAsync(new TileId(t.E, t.N)))
            if (s.Id == spotId) return s;
        return null;
    }

    /// <summary>The coarse height at a point of a placed tile (the spots' and the layouts' ground); the fixture's flat ground for hand-placed spots.</summary>
    public float Ground(TileId id, double e, double n)
    {
        if (_fixed != null) return _fixedGround;
        return _tiles.TryGetValue(id, out var en) && en.Grid != null ? (float)en.Grid.SampleHeight(e, n) : 0;
    }

    /// <summary>The coarse grid of a tile if this map has it (the photo judge's line of sight).</summary>
    public ChunkGrid? GridOf(TileId id) => _tiles.TryGetValue(id, out var e) ? e.Grid : null;

    /// <summary>A sealed chest's layout, once its tile is placed (<see cref="LayoutAsync"/>); cached with the tile.</summary>
    public SealLayout LayoutOf(RewardSpot chest)
    {
        if (chest.Seal == SealKind.None) return SealLayout.None;
        var id = new TileId(chest.Tile.E, chest.Tile.N);
        if (!_tiles.TryGetValue(id, out var e))
        {
            // a layout stands on its tile's ground: never computed before the tile is (the server may have dropped it)
            if (_fixed == null) return SealLayout.None;
            _tiles[id] = e = new Entry { Spots = FixedIn(id), Used = ++_clock };
        }
        else if (_fixed == null && e.Spots == null) return SealLayout.None;
        if (e.Layouts.TryGetValue(chest.Id, out var l)) return l;
        l = Seals.Layout(chest, (x, y) => Ground(id, x, y), (x, y) => SlopeAt(id, x, y));
        e.Layouts[chest.Id] = l;
        return l;
    }

    /// <summary>A sealed chest's layout, its tile placed first.</summary>
    public async Task<SealLayout> LayoutAsync(RewardSpot chest)
    {
        await TileAsync(new TileId(chest.Tile.E, chest.Tile.N));
        return LayoutOf(chest);
    }

    private float SlopeAt(TileId id, double e, double n)
    {
        if (_fixed != null || !_tiles.TryGetValue(id, out var en) || en.Grid is not { } g) return 0;
        double dx = (g.SampleHeight(e + 5, n) - g.SampleHeight(e - 5, n)) / 10, dy = (g.SampleHeight(e, n + 5) - g.SampleHeight(e, n - 5)) / 10;
        return (float)(Math.Atan(Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI);
    }

    /// <summary>Loads the coarse grids of the tiles a sight line crosses (the server's photo judge), then answers heights from them.</summary>
    public async Task<Func<double, double, float?>> SightGround(double e0, double n0, double e1, double n1)
    {
        var source = _source();
        var grids = new Dictionary<TileId, ChunkGrid?>();
        double len = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
        int steps = Math.Max(1, (int)(len / PhotoSpots.SightStep));
        for (int i = 0; i <= steps; i++)
        {
            var t = TileId.FromLv95(e0 + (e1 - e0) * i / steps, n0 + (n1 - n0) * i / steps);
            if (grids.ContainsKey(t)) continue;
            ChunkGrid? g = GridOf(t);
            if (g == null && source != null)
            {
                try { g = await source.LoadCoarseChunkAsync(t); }
                catch (Exception) { g = null; }
            }
            grids[t] = g;
        }
        return (e, n) => grids.TryGetValue(TileId.FromLv95(e, n), out var g) && g != null ? (float)g.SampleHeight(e, n) : null;
    }
}
