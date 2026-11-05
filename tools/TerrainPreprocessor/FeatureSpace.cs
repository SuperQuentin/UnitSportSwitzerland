using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Where a feature stage works (#1507, G2 B3; docs/plans/global-tiling-g2.md, "interface first"): the stage keeps
/// its maths, and its plane, units, ground and tile loop come through this. Two instances:
/// <see cref="Lv95FeatureSpace"/> (the plane is LV95 itself, the tiles are kilometre tiles, the ground their chunk
/// grids: the LV95 build, byte for byte) and <see cref="FrameFeatureSpace"/> (one work frame's plane, its core
/// leaves, the leaf mesh, each leaf's anchor frame). Plane points are metres east and north, heights above sea
/// level.
/// </summary>
public interface IFeatureSpace<TTile> where TTile : notnull
{
    /// <summary>The tiles this run writes.</summary>
    IReadOnlyList<TTile> Tiles { get; }

    /// <summary>The ground on the plane, heights above sea level.</summary>
    IGround Ground { get; }

    /// <summary>The box a source is asked for, in its own coordinates (its first coordinate first).</summary>
    (double MinA, double MinB, double MaxA, double MaxB) SourceBox(ISourceProjection source);

    /// <summary>What a source read keeps, in the source's coordinates.</summary>
    ISourceRegion SourceRegion(ISourceProjection source);

    /// <summary>A source point and its height above the source's sea level, on the plane.</summary>
    (double X, double Y, double H) FromSource(ISourceProjection source, double a, double b, double h);

    /// <summary>A latitude and longitude with no height of its own (taken on the ground where that matters), on the plane.</summary>
    (double X, double Y) FromLatLon(double latDeg, double lonDeg);

    /// <summary>The tile that owns a feature centred on a plane point, when it is one this run writes.</summary>
    bool TryOwner(double x, double y, out TTile tile);

    /// <summary>A plane point and its height above sea level in a tile's own frame: x east, y up, z south.</summary>
    (float X, float Y, float Z) ToLocal(TTile tile, double x, double y, double h);
}

/// <summary>
/// The LV95 build's space: plane = LV95 (E, N), heights LHN95, a feature owned by the kilometre tile holding its
/// centre, tile-local (E − MinE, H, MaxN − N). Latitudes and longitudes enter through <see cref="GeoInput"/> (at the
/// built tiles' ground, #1526). Deleted with the LV95 build (D).
/// </summary>
public sealed class Lv95FeatureSpace : IFeatureSpace<TileId>
{
    private readonly HashSet<TileId> _set;
    private readonly GeoInput _geo;

    public Lv95FeatureSpace(IReadOnlyCollection<TileId> tiles, IGround ground, GeoInput? geo = null)
    {
        Tiles = tiles.ToList();
        _set = tiles.ToHashSet();
        Ground = ground;
        _geo = geo ?? GeoInput.NoGround;
    }

    public IReadOnlyList<TileId> Tiles { get; }

    public IGround Ground { get; }

    public (double MinA, double MinB, double MaxA, double MaxB) SourceBox(ISourceProjection source)
    {
        Lv95Only(source);
        return (Tiles.Min(t => t.MinE), Tiles.Min(t => t.MinN),
            Tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM, Tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM);
    }

    public ISourceRegion SourceRegion(ISourceProjection source)
    {
        Lv95Only(source);
        return new TileRegion(Tiles);
    }

    public (double X, double Y, double H) FromSource(ISourceProjection source, double a, double b, double h)
    {
        Lv95Only(source);
        return (a, b, h);
    }

    public (double X, double Y) FromLatLon(double latDeg, double lonDeg) => _geo.ToLv95(latDeg, lonDeg);

    public bool TryOwner(double x, double y, out TileId tile)
    {
        tile = TileId.FromLv95(x, y);
        return _set.Contains(tile);
    }

    public (float X, float Y, float Z) ToLocal(TileId tile, double x, double y, double h) =>
        ((float)(x - tile.MinE), (float)h, (float)(tile.MaxN - y));

    private static void Lv95Only(ISourceProjection source)
    {
        if (source is not Lv95Source) throw new NotSupportedException($"the LV95 build reads {source.Name} sources through GeoInput");
    }
}

/// <summary>
/// A work frame's space (#1507): plane = the frame's <see cref="WorkPlane"/>, heights above its sea level, a
/// feature owned by the core leaf under its centre (the leaf of the point's geodetic normal), the leaf's anchor
/// frame as its local frame. Sources are read through the frame's query boxes and <see cref="LeafRegion"/>s
/// pushed out by <see cref="MarginM"/>. Points go plane → ECEF → anchor rigorously (no bilinear transform), so a
/// feature that hangs over its leaf's edge is placed as exactly as one inside it.
/// </summary>
public sealed class FrameFeatureSpace : IFeatureSpace<CubeKey>
{
    private readonly FramePlan _plan;
    private readonly HashSet<CubeKey> _core;
    private readonly int _level;

    public FrameFeatureSpace(FramePlan plan, IGround ground, double marginM)
    {
        _plan = plan;
        _core = plan.Core.ToHashSet();
        _level = plan.Core[0].Level;
        Ground = ground;
        MarginM = marginM;
    }

    public double MarginM { get; }

    public IReadOnlyList<CubeKey> Tiles => _plan.Core;

    public IGround Ground { get; }

    public (double MinA, double MinB, double MaxA, double MaxB) SourceBox(ISourceProjection source) => _plan.QueryBox(source, MarginM);

    public ISourceRegion SourceRegion(ISourceProjection source) => _plan.Region(source, MarginM);

    public (double X, double Y, double H) FromSource(ISourceProjection source, double a, double b, double h) =>
        _plan.Frame.FromSource(source, a, b, h);

    public (double X, double Y) FromLatLon(double latDeg, double lonDeg) => _plan.Frame.Plane.ToPlane(latDeg, lonDeg);

    public bool TryOwner(double x, double y, out CubeKey tile)
    {
        var (f, u, v) = CubeSphere.FromNormal(_plan.Frame.Plane.NormalAt(x, y));
        tile = CubeKey.FromFace(f, u, v, _level);
        return _core.Contains(tile);
    }

    public (float X, float Y, float Z) ToLocal(CubeKey tile, double x, double y, double h)
    {
        var (lx, ly, lz) = tile.Anchor.ToLocal(_plan.Frame.ToEcef(x, y, h));
        return ((float)lx, (float)ly, (float)lz);
    }
}
