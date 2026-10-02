using UnitSport.Terrain.Format;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// The set of 1 km tiles the user picked. Only tiles that exist in swissALTI3D can be
/// selected, so a rectangle drawn over the border never asks for tiles in France or Italy
/// that have nothing to download.
/// </summary>
public sealed class Selection
{
    private readonly CountryData _country;
    private readonly HashSet<TileId> _tiles = new();

    public Selection(CountryData country) => _country = country;

    public IReadOnlyCollection<TileId> Tiles => _tiles;
    public int Count => _tiles.Count;
    public bool Contains(TileId t) => _tiles.Contains(t);

    /// <summary>Bumped on every change, so a view can cache anything derived from the set.</summary>
    public int Version { get; private set; }

    public bool Add(TileId t)
    {
        if (!_country.Covered(t) || !_tiles.Add(t)) return false;
        Version++;
        return true;
    }

    public bool Remove(TileId t)
    {
        if (!_tiles.Remove(t)) return false;
        Version++;
        return true;
    }

    public void Toggle(TileId t)
    {
        if (!Remove(t)) Add(t);
    }

    public void Clear()
    {
        if (_tiles.Count == 0) return;
        _tiles.Clear();
        Version++;
    }

    /// <summary>Every covered tile in the rectangle spanned by two corner tiles (either order).</summary>
    public void AddRect(TileId a, TileId b, bool remove = false)
    {
        for (int e = Math.Min(a.E, b.E); e <= Math.Max(a.E, b.E); e++)
            for (int n = Math.Min(a.N, b.N); n <= Math.Max(a.N, b.N); n++)
                if (remove) Remove(new TileId(e, n)); else Add(new TileId(e, n));
    }

    /// <summary>A disc of tiles whose centres are within <paramref name="radiusKm"/> of a point (LV95 m).</summary>
    public void AddCircle(double e, double n, double radiusKm, bool remove = false)
    {
        double r = radiusKm * 1000;
        var c = TileId.FromLv95(e, n);
        int span = (int)Math.Ceiling(radiusKm) + 1;
        for (int de = -span; de <= span; de++)
            for (int dn = -span; dn <= span; dn++)
            {
                var t = new TileId(c.E + de, c.N + dn);
                double ce = t.MinE + 500 - e, cn = t.MinN + 500 - n;
                if (ce * ce + cn * cn <= r * r || (de == 0 && dn == 0))
                {
                    if (remove) Remove(t); else Add(t);
                }
            }
    }

    public void AddCanton(Canton canton, bool remove = false)
    {
        foreach (var t in _country.TilesInCanton(canton.Id))
            if (remove) Remove(t); else Add(t);
    }

    public void AddAll(IEnumerable<TileId> tiles)
    {
        foreach (var t in tiles) Add(t);
    }

    /// <summary>LV95 metre bounds of the selection (min E, min N, max E, max N), or null when empty.</summary>
    public (double MinE, double MinN, double MaxE, double MaxN)? Bounds()
    {
        if (_tiles.Count == 0) return null;
        return (_tiles.Min(t => t.E) * 1000.0, _tiles.Min(t => t.N) * 1000.0,
                (_tiles.Max(t => t.E) + 1) * 1000.0, (_tiles.Max(t => t.N) + 1) * 1000.0);
    }

    /// <summary>The selected tile nearest the selection's centre of mass — where to spawn afterwards.</summary>
    public TileId? Centre()
    {
        if (_tiles.Count == 0) return null;
        double ce = _tiles.Average(t => (double)t.E), cn = _tiles.Average(t => (double)t.N);
        return _tiles.MinBy(t => (t.E - ce) * (t.E - ce) + (t.N - cn) * (t.N - cn));
    }
}
