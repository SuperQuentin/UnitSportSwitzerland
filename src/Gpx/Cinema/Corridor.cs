using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// The map along a track, loaded once so the director can survey the whole route before the run
/// starts.
///
/// <para>
/// It cannot use <see cref="ChunkManager"/> for this. The manager only holds what the LOD rings
/// have streamed around the runner, which at the moment the mode is entered is the first few
/// hundred metres — so asking it for the height of a summit ten kilometres ahead returns nothing.
/// This reads the tiles directly through <see cref="IChunkSource"/>, exactly as
/// <see cref="RoadNetwork"/> already does for road matching, and gets the same shipped → cache →
/// server tiering for free.
/// </para>
/// </summary>
public sealed class Corridor
{
    private readonly Dictionary<TileId, ChunkGrid> _grids = new();
    private readonly Dictionary<TileId, BuildingTile?> _buildings = new();
    private readonly Dictionary<TileId, RoadTile?> _roads = new();

    /// <summary>The track's plane, which <see cref="HeightAt"/> is asked on (#1514).</summary>
    private MapProjection _plane = null!;

    /// <summary>Whether the tiles are an earth world's leaves, by their stand-ins (#1752), rather than LV95 tiles.</summary>
    private bool _leaves;

    public int TileCount => _grids.Count;
    public int BuildingCount { get; private set; }

    /// <summary>
    /// Loads every tile within <paramref name="pad"/> metres of the track's bounding box.
    ///
    /// <para>
    /// The box rather than a swept corridor: a GPX route is rarely axis-aligned, so the box costs
    /// some tiles that the run never approaches, but it is one rectangle instead of a polygon
    /// query and the tiles are a megabyte each. A 16 km ride pulls in a few dozen.
    /// </para>
    /// </summary>
    /// <param name="known">
    /// Tiles the manifest says exist. Asking for one that does not is not free: over
    /// <see cref="NetworkChunkSource"/> a missing asset is retried for 7.3 seconds before it
    /// gives up, which is correct for gameplay streaming and ruinous for a survey — measured at
    /// 117 seconds to gather 12 tiles before this filter existed.
    /// </param>
    public static Task<Corridor> LoadAsync(GpxTrack track, IChunkSource source,
        IReadOnlySet<TileId>? known = null, double pad = 400, CancellationToken ct = default) =>
        LoadAsync(track, source, known == null ? null : known.Contains, pad, ct);

    /// <summary><see cref="LoadAsync(GpxTrack, IChunkSource, IReadOnlySet{TileId}?, double, CancellationToken)"/> with the
    /// tiles that exist as a test: the manifest's and the generated fill's (#1752).</summary>
    public static async Task<Corridor> LoadAsync(GpxTrack track, IChunkSource source,
        Func<TileId, bool>? known, double pad = 400, CancellationToken ct = default)
    {
        // an earth world's tiles are its leaves, asked by their stand-ins (#1752)
        bool leaves = source is Terrain.Leaves.StandInChunkSource;
        var corridor = new Corridor { _plane = track.Plane, _leaves = leaves };

        var wanted = new List<TileId>();
        if (leaves) wanted = track.StandInsAlong(pad);
        else
        {
            // the LV95 tiles under the track's plane box (#1514)
            var (minE, minN, maxE, maxN) = track.Lv95Bounds(pad);
            var from = TileId.FromLv95(minE, minN);
            var to = TileId.FromLv95(maxE, maxN);
            for (int e = from.E; e <= to.E; e++)
                for (int n = from.N; n <= to.N; n++)
                {
                    var id = new TileId(e, n);
                    if (known == null || known(id)) wanted.Add(id);
                }
        }

        // In parallel, because these are three independent file reads per tile and the survey is
        // a wait the player sits through before the mode starts. Serially it was one chain at a
        // time: a hundred tiles meant three hundred round trips end to end, and over the network
        // source that is three hundred latencies.
        var gate = new object();
        await Parallel.ForEachAsync(wanted,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (id, token) =>
            {
                ChunkGrid? grid = null;
                try { grid = await source.LoadChunkAsync(id, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }

                // no terrain means no map here, and nothing else about the tile is usable
                if (grid == null) return;

                BuildingTile? built = null;
                try { built = await source.LoadBuildingsAsync(id, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }

                RoadTile? roads = null;
                try { roads = await source.LoadRoadsAsync(id, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }

                lock (gate)
                {
                    corridor._grids[id] = grid;
                    corridor._buildings[id] = built;
                    corridor._roads[id] = roads;
                    if (built != null) corridor.BuildingCount += built.Buildings.Count;
                }
            }).ConfigureAwait(false);

        return corridor;
    }

    /// <summary>
    /// Terrain height at a point of the track's plane (#1514), or null outside the loaded tiles.
    ///
    /// <para>
    /// Every boundary vertex belongs to two tiles and every corner to four, and
    /// <see cref="TileId.FromLv95"/> can only name one of them — so a position exactly on a seam
    /// asks the wrong tile if that one happens to be missing. Trying the sharing tiles is the same
    /// fix the preprocessor's sampler uses.
    /// </para>
    /// </summary>
    public double? HeightAt(double x, double y)
    {
        if (_leaves) return LeafHeightAt(x, y);

        // the tiles are LV95 (until G2 D): the plane point's LV95 to ~10 cm, a twentieth of a grid cell
        var (e, n) = _plane.Lv95Of(x, y);
        var id = TileId.FromLv95(e, n);
        if (_grids.TryGetValue(id, out var grid)) return grid.SampleHeight(e, n);

        foreach (var neighbour in Sharing(e, n))
            if (_grids.TryGetValue(neighbour, out var other)) return other.SampleHeight(e, n);

        return null;
    }

    /// <summary>
    /// <see cref="HeightAt"/> on the leaves (#1752): the grid of the leaf under the point, read in its stand-in's names,
    /// and that ground's height above sea level (what the track's elevations are). A leaf's stand-in grid reaches past
    /// its edges with the nearest heights, so a point on a seam needs no neighbour.
    /// </summary>
    private double? LeafHeightAt(double x, double y)
    {
        var place = _plane.At(x, y, 0);
        var id = Terrain.Leaves.LeafStandIn.Id(CubeKey.FromEcef(place.Ecef, Terrain.Leaves.LeafFrames.LeafLevel));
        if (!_grids.TryGetValue(id, out var grid)) return null;
        var (le, ln, _) = place.InTileEn(id);
        double h = grid.SampleHeight(le, ln);
        if (double.IsNaN(h)) return null;
        return Core.GlobalPos.OfTileEn(id, le, ln, h).Alt;
    }

    private static IEnumerable<TileId> Sharing(double e, double n)
    {
        // on the tile's west or south edge, in its own frame (x east, z south; #1443)
        var id = TileId.FromLv95(e, n);
        var (x, z) = id.Local(e, n);
        bool onWest = Math.Abs(x) < 0.001;
        bool onSouth = Math.Abs(ChunkFormat.TileSizeM - z) < 0.001;

        if (onWest) yield return id.Neighbour(-1, 0);
        if (onSouth) yield return id.Neighbour(0, 1);
        if (onWest && onSouth) yield return id.Neighbour(-1, 1);

    }

    public BuildingTile? Buildings(TileId id) => _buildings.GetValueOrDefault(id);
    public RoadTile? Roads(TileId id) => _roads.GetValueOrDefault(id);

    public IEnumerable<TileId> Tiles => _grids.Keys;
}
