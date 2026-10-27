using UnitSport.Terrain.Format;

namespace UnitSport.World.Locate;

/// <summary>
/// The order <c>BuildingIndexBuilder</c> reads a region's tiles in (#1277): nearest first to where people are, so
/// <c>/locate</c> can answer round them long before the whole region is in. The points are the players' (their
/// positions every second), and an asker's ahead of everyone (<see cref="Prefer"/>); with none yet, the region's
/// start. Re-sorted only when the points change. Thread safe: workers take tiles, the main thread moves the points.
/// Pure C#, tier 0 (<c>LocateTests</c>).
/// </summary>
public sealed class TileQueue
{
    /// <summary>An asker's tiles count as this much nearer than a player's: what was asked for comes first.</summary>
    public const double AskerWeight = 0.25;

    private readonly object _lock = new();
    private readonly List<TileId> _left;
    private (double E, double N)[] _players = [];
    private readonly List<(double E, double N)> _askers = new();
    private bool _dirty = true;

    public TileQueue(IEnumerable<TileId> tiles, double startE, double startN)
    {
        _left = tiles.ToList();
        _players = [(startE, startN)];
    }

    public int Left { get { lock (_lock) return _left.Count; } }

    /// <summary>Where the players are now (an empty set keeps the last ones).</summary>
    public void SetPlayers(IReadOnlyCollection<(double E, double N)> players)
    {
        if (players.Count == 0) return;
        lock (_lock)
        {
            if (players.SequenceEqual(_players)) return;
            _players = players.ToArray();
            _dirty = true;
        }
    }

    /// <summary>Someone asked from here: their surroundings next.</summary>
    public void Prefer(double e, double n)
    {
        lock (_lock)
        {
            _askers.Add((e, n));
            if (_askers.Count > 8) _askers.RemoveAt(0);
            _dirty = true;
        }
    }

    /// <summary>The next tile to read, nearest to the points; false when all are taken.</summary>
    public bool TryNext(out TileId tile)
    {
        lock (_lock)
        {
            tile = default;
            if (_left.Count == 0) return false;
            if (_dirty)
            {
                // farthest first, so the next is taken off the end
                _left.Sort((a, b) => Rank(b).CompareTo(Rank(a)));
                _dirty = false;
            }
            tile = _left[^1];
            _left.RemoveAt(_left.Count - 1);
            return true;
        }
    }

    private double Rank(TileId t)
    {
        double ce = t.E * 1000.0 + 500, cn = t.N * 1000.0 + 500, best = double.MaxValue;
        foreach (var (e, n) in _players) best = Math.Min(best, (ce - e) * (ce - e) + (cn - n) * (cn - n));
        foreach (var (e, n) in _askers) best = Math.Min(best, AskerWeight * AskerWeight * ((ce - e) * (ce - e) + (cn - n) * (cn - n)));
        return best;
    }
}
