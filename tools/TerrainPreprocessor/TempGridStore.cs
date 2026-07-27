using System.Runtime.InteropServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Persists pass-1 grids as raw little-endian uint16 files and serves them to pass 2
/// through a small LRU cache (pass 2 touches up to 9 tiles per chunk).
/// Existing temp files are reused across runs, so re-running skips the expensive parse.
/// </summary>
public sealed class TempGridStore
{
    private const int MaxCached = 24; // ~192 MB
    private readonly string _dir;
    private readonly Dictionary<TileId, LinkedListNode<(TileId Id, ushort[] Grid)>> _cache = new();
    private readonly LinkedList<(TileId Id, ushort[] Grid)> _lru = new();
    private readonly object _lock = new();

    public TempGridStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public string PathFor(TileId id) => Path.Combine(_dir, $"{id.E}_{id.N}.raw");

    public bool HasValidFile(TileId id)
    {
        var info = new FileInfo(PathFor(id));
        return info.Exists && info.Length == (long)XyzParser.CellsPerSide * XyzParser.CellsPerSide * 2;
    }

    public void Save(TileId id, ushort[] grid)
    {
        using var fs = File.Create(PathFor(id));
        fs.Write(MemoryMarshal.AsBytes(grid.AsSpan()));
    }

    /// <summary>Returns the cached/loaded grid, or null if the tile has no temp file.</summary>
    public ushort[]? Load(TileId id)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Grid;
            }

            if (!HasValidFile(id)) return null;

            var grid = new ushort[XyzParser.CellsPerSide * XyzParser.CellsPerSide];
            using (var fs = File.OpenRead(PathFor(id)))
                fs.ReadExactly(MemoryMarshal.AsBytes(grid.AsSpan()));

            var newNode = _lru.AddFirst((id, grid));
            _cache[id] = newNode;
            while (_cache.Count > MaxCached)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Id);
            }
            return grid;
        }
    }
}
