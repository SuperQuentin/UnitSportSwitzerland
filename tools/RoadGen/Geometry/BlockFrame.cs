namespace UnitSport.Tools.RoadGen.Geometry;

using UnitSport.Terrain.Format;

/// <summary>
/// The units a block of the network stage runs on (#1660, "junctions the same wherever the tile edges are"): the real
/// tiles (<see cref="Real"/>: LV95 km tiles, leaves) answer which tile holds a point, but every tile's local frame is one
/// frame centred on the block, so records of any tile and whole lines across many lie in one frame; and whole lines are
/// kept under one tile name, <see cref="Lines"/>, that has no edges: nothing planned along a line sees a tile cut. Tiles come
/// back only when the block's records are written (<c>TileCut</c>), in each real tile's own frame.
/// </summary>
public sealed class BlockFrame : IUnits
{
    /// <summary>The name every whole line goes under within a block: a tile with no edges, inside everywhere.</summary>
    public static readonly TileId Lines = new(int.MinValue, int.MinValue);

    /// <summary>The name the band's whole lines go under (the streets of junctions reaching into the block, planned, never written).</summary>
    public static readonly TileId Band = new(int.MinValue, int.MinValue + 1);

    /// <summary>A name of whole lines: no edges.</summary>
    public static bool IsWhole(TileId id) => id == Lines || id == Band;

    private readonly double _west, _north;

    public IUnits Real { get; }

    /// <param name="centre">The frame's centre on the plane (snapped to 2 m: the km caches' lattices stay where they are).</param>
    public BlockFrame(IUnits real, double centreX, double centreY)
    {
        Real = real;
        double cx = 2 * Math.Round(centreX / 2), cy = 2 * Math.Round(centreY / 2);
        _west = cx - real.TileSizeM / 2;
        _north = cy + real.TileSizeM / 2;
    }

    /// <summary>The frame centred on a block: the middle of its tiles' squares.</summary>
    public static BlockFrame Over(IUnits real, IReadOnlyCollection<TileId> block)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var id in block)
        {
            double w = real.West(id), n = real.North(id);
            (x0, x1, y0, y1) = (Math.Min(x0, w), Math.Max(x1, w + real.TileSizeM), Math.Min(y0, n - real.TileSizeM), Math.Max(y1, n));
        }
        return block.Count == 0 ? new BlockFrame(real, 0, 0) : new BlockFrame(real, (x0 + x1) / 2, (y0 + y1) / 2);
    }

    public double TileSizeM => Real.TileSizeM;
    public TileId TileAt(double x, double y) => Real.TileAt(x, y);
    public double West(TileId id) => _west;
    public double South(TileId id) => _north - Real.TileSizeM;
    public double North(TileId id) => _north;

    public Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction) => IsWhole(id) ? direction : Real.DirectionToTile(id, at, direction);
    public double AngleToTile(TileId id, Vec2 at, double angle) => IsWhole(id) ? angle : Real.AngleToTile(id, at, angle);

    /// <summary>A point of this frame in a real tile's own frame.</summary>
    private (double X, double Z) InReal(TileId id, double x, double z) => (_west + x - Real.West(id), Real.North(id) - (_north - z));

    /// <summary>A whole line has no edge; a real tile's edge is its own.</summary>
    public bool OnEdge(TileId id, double x, double z, double eps)
    {
        if (IsWhole(id)) return false;
        var (rx, rz) = InReal(id, x, z);
        return Real.OnEdge(id, rx, rz, eps);
    }

    public bool Inside(TileId id, double x, double z)
    {
        if (IsWhole(id)) return true;
        var (rx, rz) = InReal(id, x, z);
        return Real.Inside(id, rx, rz);
    }

    /// <summary>Within a block every line is whole: no point is on a cut.</summary>
    public bool OnAnyEdge(double x, double y, double eps) => false;

    public IEnumerable<TileId> Around(TileId id, int ring) => IsWhole(id) ? [] : Real.Around(id, ring);
}
