using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// Client: the building sites' yard containers round the player (#627), the tiles within reach
/// planned on a worker (<see cref="SitePlans.YardOfTile"/>, from the tile's own files, as the
/// server lists them), kept per tile and forgotten two tiles away, as <c>SiteSounds</c> keeps its
/// sites. Asking it is cheap and allocates nothing once a tile is in: nearly every tile has no site.
/// </summary>
internal sealed class YardIndex
{
    /// <summary>A tile is planned when its edge is this close: a container is searched from 1.3 m.</summary>
    private const double TileReach = 40;

    private readonly Dictionary<TileId, YardStock[]> _tiles = new();
    private readonly Dictionary<TileId, Task<YardStock[]>> _loading = new();
    private readonly List<TileId> _scratch = new();
    private TileId _here;
    private bool _haveHere;

    /// <summary>Plans the tiles round <paramref name="e"/>, <paramref name="n"/> not yet planned, and takes in those that are done.</summary>
    public void Around(IChunkSource source, double e, double n)
    {
        var here = TileId.FromLv95(e, n);
        if (!_haveHere || here != _here)
        {
            _here = here;
            _haveHere = true;
            _scratch.Clear();
            foreach (var id in _tiles.Keys)
                if (Math.Abs(id.E - here.E) > 2 || Math.Abs(id.N - here.N) > 2) _scratch.Add(id);
            foreach (var id in _scratch) _tiles.Remove(id);
        }
        for (int dE = -1; dE <= 1; dE++)
            for (int dN = -1; dN <= 1; dN++)
            {
                var id = new TileId(here.E + dE, here.N + dN);
                if (_tiles.ContainsKey(id) || _loading.ContainsKey(id)) continue;
                double dx = Math.Max(0, Math.Max(id.MinE - e, e - (id.MinE + ChunkFormat.TileSizeM)));
                double dz = Math.Max(0, Math.Max(id.MinN - n, n - (id.MinN + ChunkFormat.TileSizeM)));
                if (dx * dx + dz * dz > TileReach * TileReach) continue;
                _loading[id] = Task.Run(() => Plan(source, id));
            }
        if (_loading.Count == 0) return;
        _scratch.Clear();
        foreach (var (id, task) in _loading)
            if (task.IsCompleted) _scratch.Add(id);
        foreach (var id in _scratch)
        {
            var task = _loading[id];
            _loading.Remove(id);
            _tiles[id] = task.IsCompletedSuccessfully ? task.Result : [];
        }
    }

    private static YardStock[] Plan(IChunkSource source, TileId id)
    {
        try { return SitePlans.YardOfTile(source, id); }
        catch (Exception ex)
        {
            GD.PushWarning($"[loot] yard of tile {id}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// The container a player at <paramref name="e"/>, <paramref name="n"/> (feet at
    /// <paramref name="alt"/>) looking along <paramref name="look"/> faces best
    /// (<see cref="SiteLoot.Score"/>), skipping one <paramref name="skip"/> says is gone.
    /// </summary>
    public bool Nearest(double e, double n, float alt, Vector2 look, Func<YardStock, bool> skip, out YardStock best)
    {
        best = default;
        float bestScore = float.MaxValue;
        if (!_haveHere) return false;
        for (int dE = -1; dE <= 1; dE++)
            for (int dN = -1; dN <= 1; dN++)
            {
                if (!_tiles.TryGetValue(new TileId(_here.E + dE, _here.N + dN), out var list)) continue;
                for (int i = 0; i < list.Length; i++)
                {
                    float s = SiteLoot.Score(list[i], e, n, alt, look);
                    if (s >= bestScore || skip(list[i])) continue;
                    bestScore = s;
                    best = list[i];
                }
            }
        return bestScore < float.MaxValue;
    }

    /// <summary>A container already planned here, by its site's building and its index.</summary>
    public bool TryFind(string building, int index, out YardStock found)
    {
        found = default;
        if (!BuildingKey.TryParse(building, out var key) || !_tiles.TryGetValue(key.Tile, out var list)) return false;
        foreach (var c in list)
            if (c.Index == index && c.Building == building) { found = c; return true; }
        return false;
    }

    /// <summary>Every container planned round the player (for the probes).</summary>
    public IEnumerable<YardStock> All => _tiles.Values.SelectMany(t => t);
}
