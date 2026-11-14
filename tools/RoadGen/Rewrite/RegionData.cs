namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The region-wide inputs of the network stage, read once (#1507): the OSM overlay (keyed by TLM line and the
/// distance along it), the OSM nodes (signals, restrictions, crossings), the cantons, the gantries' data and
/// places, the tram stops. The files are the LV95 build's (LV95 positions); <see cref="On"/> hands them to a run
/// on another plane (a work frame's), every position and bearing moved, for <see cref="ITileLoop"/>s outside
/// this assembly to build their <see cref="TileRewriter.Inputs"/>.
/// </summary>
public sealed class RegionData
{
    public OsmOverlayReader? Overlay { get; private init; }
    public TileRewriter.Cantons? Cantons { get; private init; }
    public OsmNodesReader? Nodes { get; private init; }
    public GantryContext? Gantries { get; private init; }
    public List<(string Name, Vec2 At)> TramStops { get; private init; } = [];

    /// <summary>The files <paramref name="options"/> names (its overlay, the nodes beside it, SITG, places, tram stops), logged as the LV95 run logs them.</summary>
    public static RegionData Load(TileRewriter.Options options, Action<string> log)
    {
        var overlay = options.OsmOverlay is { } overlayPath ? OsmOverlayReader.TryLoad(overlayPath) : null;
        var cantons = options.Cantons ?? TileRewriter.Cantons.Find();   // pedestrian heads per canton (#348); a test world's zones (#1038)
        // OSM traffic signals (#347), beside the overlay
        var nodes = options.OsmOverlay is { } nodesBeside
            ? OsmNodesReader.TryLoad(Path.Combine(Path.GetDirectoryName(nodesBeside) ?? ".", OsmNodesReader.FileName)) : null;
        var data = new RegionData
        {
            Overlay = overlay, Cantons = cantons, Nodes = nodes,
            TramStops = options.TramStops is { } stopsPath ? TileRewriter.ReadAllTramStops(stopsPath) : [],
        };
        if (TileRewriter.SignalSites.From(nodes) is { } sites) log($"  OSM traffic signals: {sites.Count:N0} junction signals");
        if (overlay is not null) log($"  OSM overlay: {overlay.RowCount:N0} rows from {options.OsmOverlay}");
        log(cantons is null ? "  cantons: none found, every junction keeps the default heads and poles"
            : $"  cantons (signal heads, plates, poles): {cantons.Source}");
        return data;
    }

    /// <summary>The gantries' data (#1110), loaded after the blocks are known, as the LV95 run does; null when off.</summary>
    public GantryContext? LoadGantries(TileRewriter.Options options, Action<string> log)
    {
        var gantries = options.Gantries ? GantryContext.Load(options.SitgSigns, options.Places) : null;
        if (gantries is not null) gantries.Overlay = Overlay;
        if (gantries?.Sitg is { } sitg) log($"  SITG lane signs (#1110): {sitg.Gantries.Count:N0} gantries from {options.SitgSigns}");
        return gantries;
    }

    /// <summary>
    /// Inputs on <paramref name="units"/>'s plane: the per-tile sources as given, the region's data moved by
    /// <paramref name="fromLv95"/> (an LV95 point onto the plane), <paramref name="bearing"/> (an LV95 bearing at an LV95
    /// point, degrees clockwise from north, as one on the plane) and <paramref name="toLv95"/> (a plane point to LV95, for
    /// the canton lattice). Sets <see cref="TileRewriter"/>'s walkers' tram crossings for the run (one run at a time).
    /// </summary>
    public TileRewriter.Inputs On(IUnits units,
        Func<double, double, (double X, double Y)> fromLv95, Func<double, double, double, double> bearing, Func<double, double, (double E, double N)> toLv95,
        Func<TileId, (RoadTile? Tile, RawRoads.Key?[]? Keys)> roads, Func<IReadOnlyCollection<TileId>, IGround?> ground,
        Func<TileId, BuildingTile?> buildings, Func<TileId, List<RawParking.Lot>>? parking, GantryContext? gantries)
    {
        var nodes = Nodes?.Moved(fromLv95);
        TileRewriter.UncontrolledTramCrossings = TileRewriter.UncontrolledCrossings(nodes);
        return new TileRewriter.Inputs
        {
            Units = units, Roads = roads, Ground = ground, Buildings = buildings, Parking = parking,
            Overlay = Overlay, Cantons = Cantons?.On(toLv95),
            SignalSites = TileRewriter.SignalSites.From(nodes), Restrictions = TileRewriter.Restrictions.From(nodes),
            Crossings = TileRewriter.CrossingNodes.From(nodes),
            Gantries = gantries?.Moved(fromLv95, bearing),
            TramStops = TramStops.Select(s => { var (x, y) = fromLv95(s.At.X, s.At.Y); return (s.Name, new Vec2(x, y)); }).ToList(),
        };
    }
}
