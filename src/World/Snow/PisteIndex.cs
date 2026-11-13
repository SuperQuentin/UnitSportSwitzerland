using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// Which ski slope lies under a point (#887): the tiles' <c>.pst</c> files (an earth leaf's slopes through its
/// stand-in, #1735), loaded on demand from the chunk source and kept while asked for. Used by the server (whose headless terrain loads no
/// cover) to count real skiers' wear, and by any peer for the ground under a body.
/// </summary>
public sealed class PisteIndex
{
    private readonly IWorldGround _terrain;
    private readonly ConcurrentDictionary<TileId, PisteTile?> _tiles = new();
    private readonly HashSet<TileId> _loading = new();
    private readonly Dictionary<TileId, ulong> _lastAsked = new();
    private ulong _frame;
    /// <summary>
    /// Two loads at a time, and a load nobody asks for any more skipped once its turn comes (#1762): an earth leaf's
    /// slopes generate the whole leaf, and a camera flying over (an airliner at the time warp) asked for every leaf it
    /// passed, each on its own pool thread, which starved every other worker (the globe, the near leaves) for an hour.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(2);
    private readonly ConcurrentDictionary<TileId, byte> _cancelled = new();

    public PisteIndex(IWorldGround chunks) => _terrain = chunks;

    /// <summary>The slope under a world point, or null (none, or its tile is still loading). Main thread.</summary>
    public PisteRecord? At(Vector3 world, out float mask)
    {
        mask = 0;
        if (_terrain.Origin is not { } origin) return null;
        double e, n;
        PisteTile? tile;
        if (_terrain.TileAt(world) is { IsLeaf: true } leaf)
        {
            // an earth leaf (#1735): its stand-in's slopes, the point named in the stand-in's frame
            var id = _terrain.ContentName(leaf);
            (e, n, _) = origin.ToGlobal(world).InTileEn(id);
            tile = Tile(id);
        }
        else
        {
            (e, n) = origin.ToLv95(world);
            tile = Tile(TileId.FromLv95(e, n));
        }
        if (tile == null) return null;
        int i = tile.IndexAt(e, n);
        if (i < 0 || tile.Slot[i] == 0) return null;
        mask = PisteFormat.MaskOf(tile.Aux[i]);
        return tile.Records[tile.Slot[i] - 1];
    }

    /// <summary>A tile's slopes if loaded; starts the load otherwise. Main thread.</summary>
    public PisteTile? Tile(TileId id)
    {
        _lastAsked[id] = _frame;
        if (_tiles.TryGetValue(id, out var tile)) return tile;
        if (_terrain.Content is { } source && _loading.Add(id))
        {
            _cancelled.TryRemove(id, out _);
            Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_cancelled.TryRemove(id, out _)) return;
                    PisteTile? t = null;
                    try { t = await source.LoadPistesAsync(id).ConfigureAwait(false); }
                    catch (System.Exception ex) { GD.PushWarning($"[snow] pistes {id}: {ex.Message}"); }
                    _tiles[id] = t;
                }
                finally { _gate.Release(); }
            });
        }
        return null;
    }

    /// <summary>True once a tile's load has finished; <paramref name="tile"/> is null when it has no slopes. Starts the load. Main thread.</summary>
    public bool Loaded(TileId id, out PisteTile? tile)
    {
        tile = Tile(id);
        return tile != null || _tiles.ContainsKey(id);
    }

    /// <summary>Drops tiles nobody asked for in the last <paramref name="frames"/> calls. Main thread.</summary>
    public void Trim(ulong frames)
    {
        _frame++;
        if (_frame % 64 != 0) return;
        var stale = new List<TileId>();
        foreach (var (id, at) in _lastAsked)
            if (_frame - at > frames) stale.Add(id);
        foreach (var id in stale)
        {
            _lastAsked.Remove(id);
            if (_tiles.TryRemove(id, out _)) _loading.Remove(id);
            // still waiting for its turn: skipped, and asked again if wanted
            else if (_loading.Remove(id)) _cancelled[id] = 0;
        }
    }
}
