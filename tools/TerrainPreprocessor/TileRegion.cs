using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The tiles a source read is for: their bounding box, which is what the R-tree is asked, and the
/// tiles themselves, which decide what is kept. A map is rarely a rectangle (Geneva and the Valais
/// are one 94 x 55 km box for 826 tiles), so the box alone reads every feature in between: 214,670
/// road lines there, of which 100,543 touch a built tile (#678).
/// </summary>
public sealed class TileRegion
{
    /// <summary>
    /// One tile around the wanted ones. For lines, which look at their neighbours (a road ramps up
    /// to the bridge it joins, a carriageway takes its direction from its partner): a line that
    /// touches no wanted tile can still change one that does.
    /// </summary>
    public const double RingM = ChunkFormat.TileSizeM;

    private readonly HashSet<TileId> _tiles;
    private readonly double _marginM;

    public double MinE { get; }
    public double MinN { get; }
    public double MaxE { get; }
    public double MaxN { get; }
    public int Count => _tiles.Count;

    /// <param name="marginM">How far from a wanted tile a feature is still kept, inside the box.</param>
    public TileRegion(IEnumerable<TileId> tiles, double marginM = 0)
    {
        _tiles = tiles.ToHashSet();
        if (_tiles.Count == 0) throw new ArgumentException("A tile region needs at least one tile.", nameof(tiles));
        _marginM = marginM;
        MinE = _tiles.Min(t => t.MinE);
        MaxE = _tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        MinN = _tiles.Min(t => t.MinN);
        MaxN = _tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;
    }

    /// <summary>
    /// True when the box comes within the margin of a wanted tile, edges included, as the bounding
    /// box test of the R-tree query includes them. Reads only, so it can be asked from several threads.
    /// </summary>
    public bool Touches(double minX, double maxX, double minY, double maxY)
    {
        const double size = ChunkFormat.TileSizeM;
        // a box that ends exactly on a lattice line touches the tile on the other side of it
        int e0 = (int)Math.Ceiling((minX - _marginM) / size) - 1, e1 = (int)Math.Floor((maxX + _marginM) / size);
        int n0 = (int)Math.Ceiling((minY - _marginM) / size) - 1, n1 = (int)Math.Floor((maxY + _marginM) / size);

        // a lake or a canton-wide polygon spans more tiles than the region holds: ask the tiles instead
        if ((long)(e1 - e0 + 1) * (n1 - n0 + 1) > _tiles.Count)
        {
            foreach (var t in _tiles)
                if (t.E >= e0 && t.E <= e1 && t.N >= n0 && t.N <= n1) return true;
            return false;
        }
        for (int e = e0; e <= e1; e++)
            for (int n = n0; n <= n1; n++)
                if (_tiles.Contains(new TileId(e, n))) return true;
        return false;
    }

    public bool Touches(double x, double y) => Touches(x, x, y, y);
}
