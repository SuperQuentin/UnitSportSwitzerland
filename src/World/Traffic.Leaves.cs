using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// Traffic on the earth world's leaves (#1636, G2 C6): under <c>--frame earth</c> the lane, rail, walk, ride and tram graphs
/// are built from the roads of the leaves round the view (each named as its stand-in tile, its records left in the leaf's
/// anchor frame: <see cref="TilePlace.OnAnchor"/>), rebuilt once the view is <see cref="RebuildAfter"/> from where they were
/// built, as the flat world's are from LV95 tiles as it enters a new kilometre. Their ends join across leaf edges by Earth
/// places (<see cref="TilePlace.EarthKey"/>), and the graphs turn with the frame's re-anchors like everything else in the
/// world (<see cref="OnOriginShifted"/>).
/// </summary>
public partial class Traffic
{
    /// <summary>
    /// The earth world's leaf roads (<c>EarthLeaves.RoadsOf</c>): set, the graphs come from leaves; null, from the LV95 tiles
    /// of the chunk source.
    /// </summary>
    public Func<CubeKey, RoadTile?>? LeafRoads { get; set; }

    /// <summary>Where the graphs were last built around (the earth world's <see cref="_builtAround"/>).</summary>
    private GlobalPos? _builtLeaf;

    /// <summary>
    /// The view this far from where the graphs were built (m), they are built again: as often as the flat world's, which
    /// rebuilds on entering a new kilometre tile, and ~1.7 km inside the near square's edge, past the cars' 950 m.
    /// </summary>
    private const double RebuildAfter = 800;

    /// <summary>Half the side of the squares the graphs read (m), as the flat world's 5 × 5 and 7 × 7 km of tiles about the view.</summary>
    private const double NearHalf = 2500, WideHalf = 3500;

    /// <summary>How finely the squares are sampled for the leaves they touch (m): well under a leaf's ~610 m side.</summary>
    private const double LeafSampleStep = 150;

    /// <summary>Leaves' roads as stand-in tiles, kept across builds (a leaf is read once while the view stays near).</summary>
    private readonly ConcurrentDictionary<CubeKey, RoadTile> _leafTiles = new();

    private const int LeafTilesKept = 600;

    private void RebuildOnLeaves(Vector3 focus, Func<CubeKey, RoadTile?> roadsOf)
    {
        int epoch = _epoch;
        var at = _origin.ToGlobal(focus);
        var geo = at.Geodetic;
        var here = LeafFrames.LeafAt(geo.LatDeg, geo.LonDeg);
        uint wanted = ModesWanted();
        if (_builtLeaf is { } b && b.HorizontalDistanceTo(at) < RebuildAfter && (wanted & ~_modesBuilt) == 0) return;
        _building = true;
        var origin = _origin.Frame;
        Task.Run(() =>
        {
            List<RoadTile> tiles, near;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var (wideKeys, nearKeys) = LeavesAround(at);
                tiles = new();
                near = new();
                foreach (var key in wideKeys)
                {
                    if (StandIn(key, roadsOf) is not { } t) continue;
                    tiles.Add(t);
                    if (nearKeys.Contains(key)) near.Add(t);
                }
            }
            catch (Exception x)
            {
                GD.PushError($"[traffic] reading the leaves round {here} failed: {x}");
                tiles = near = new();
            }
            GD.Print($"[traffic] leaves round {here}: {tiles.Count} with roads ({near.Count} near) read in "
                + $"{System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
            BuildGraphs(tiles, near, origin, wanted, epoch, $"leaf {here} ({near.Count} leaves near, {tiles.Count} wide)", focus,
                () => _builtLeaf = at);
        });
    }

    /// <summary>
    /// The leaves touching the squares <see cref="WideHalf"/> and <see cref="NearHalf"/> about <paramref name="at"/>, east and north
    /// on the Earth there: every leaf under a sample of a <see cref="LeafSampleStep"/> grid (across cube faces too).
    /// </summary>
    public static (List<CubeKey> Wide, HashSet<CubeKey> Near) LeavesAround(GlobalPos at)
    {
        var wide = new List<CubeKey>();
        var seen = new HashSet<CubeKey>();
        var near = new HashSet<CubeKey>();
        int steps = (int)Math.Ceiling(WideHalf / LeafSampleStep);
        for (int j = -steps; j <= steps; j++)
            for (int i = -steps; i <= steps; i++)
            {
                double east = Math.Clamp(i * LeafSampleStep, -WideHalf, WideHalf), north = Math.Clamp(j * LeafSampleStep, -WideHalf, WideHalf);
                var g = at.Moved(east, north).Geodetic;
                var key = LeafFrames.LeafAt(g.LatDeg, g.LonDeg);
                if (seen.Add(key)) wide.Add(key);
                if (Math.Abs(east) <= NearHalf && Math.Abs(north) <= NearHalf) near.Add(key);
            }
        return (wide, near);
    }

    /// <summary>A leaf's roads as its stand-in tile, kept once read; null where it has none (asked again next build: a fill leaf may come).</summary>
    private RoadTile? StandIn(CubeKey key, Func<CubeKey, RoadTile?> roadsOf)
    {
        if (_leafTiles.TryGetValue(key, out var kept)) return kept;
        if (roadsOf(key) is not { Segments.Count: > 0 } roads) return null;
        var tile = TilePlace.OnAnchor(roads, key);
        if (_leafTiles.Count >= LeafTilesKept) _leafTiles.Clear();
        _leafTiles[key] = tile;
        return tile;
    }
}
