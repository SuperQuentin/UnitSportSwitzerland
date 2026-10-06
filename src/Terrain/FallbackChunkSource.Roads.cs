using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain;

/// <summary>
/// Generated roads through RoadGen's network stage (#559): the junctions, markings, yields, signs,
/// bike paths, turn lanes and traffic lights a real region gets from the preprocessor, planned
/// here per tile as the tile is served (notes: <c>terrain/generated-roads-roadgen</c>).
///
/// <para>
/// A tile is rewritten with a one-tile halo for context, the same stage the preprocessor runs on
/// whole regions; every generated segment carries its line's key, so the result is byte-identical
/// to the same tile cut from one big block (<c>tools/GeneratedRoadsSpike</c>) and neighbours meet.
/// Only generated tiles bring raw roads: a real tile's roads are already rewritten and cannot be
/// fed back, so a generated road simply ends at a real tile's edge, as it always did. Real tiles
/// still bring their ground and buildings.
/// </para>
///
/// <para>
/// Threading: the stage is synchronous and its walls and density caches are not thread-safe, so
/// one tile is rewritten at a time behind an async gate (waiters hold no thread). The inputs are
/// fetched before the gate; buildings the density field reaches for beyond them are loaded
/// through <see cref="Neighbours"/> synchronously, on the worker the stage runs on.
/// </para>
/// </summary>
public sealed partial class FallbackChunkSource
{
    /// <summary>Off: generated tiles keep the bare segments (a comparison, or a slow machine).</summary>
    public static bool RewriteRoads { get; set; } = true;

    /// <summary>One tile of halo: enough for every junction on the tile's edge (and checked so).</summary>
    private static readonly TileRewriter.Options Stage = new(BlockSize: 1, Halo: 1);

    /// <summary>
    /// The ground the stage drapes walls and kerbs on. The generated height is bilinear between
    /// 5 m points, so a stride-5 grid holds it exactly at a 25th of a full grid's cost and size.
    /// </summary>
    private const int StageGroundStride = 5;

    /// <summary>
    /// Tiles the shared walls/density may hold before they start over (~1.75 MB a tile). Both are
    /// pure functions of position and walls, so starting over changes no output.
    /// </summary>
    private const int StageSharedTiles = 64;

    /// <summary>Raw roads and stride-5 grids kept per tile: every tile is a neighbour of eight others.</summary>
    private const int StageInputCacheSize = 48;

    private readonly SemaphoreSlim _stageGate = new(1, 1);
    private Facades? _facades;
    private UrbanField? _field;
    private readonly HashSet<TileId> _facadeTiles = new();
    /// <summary>The walls fetched for the tile being rewritten, read by the shared pair's source.</summary>
    private Dictionary<TileId, BuildingTile?> _stageWalls = new();
    private IChunkSource? _stageSource;

    private readonly object _stageInputGate = new();
    private readonly Dictionary<(TileId, long), StageInput> _stageInputs = new();
    private readonly LinkedList<(TileId, long)> _stageInputOrder = new();

    /// <summary>
    /// A generated tile as the stage reads it: its raw keyed roads, kept encoded because the stage
    /// writes into the segments it is given (<c>RoadHeights</c> lowers them in town) and every
    /// tile is read by nine rewrites, and its ground.
    /// </summary>
    private sealed record StageInput(byte[]? Roads, RawRoads.Key?[]? Keys, ChunkGrid Ground)
    {
        /// <summary>A fresh copy of the raw roads, null for none.</summary>
        public RoadTile? FreshRoads() => Roads is null ? null : RoadCodec.Decode(new MemoryStream(Roads));
    }

    /// <summary>
    /// A generated tile's finished roads: its raw segments and its neighbours' through the stage.
    /// Null when the tile has no roads. Falls back to the raw segments if the stage throws, so a
    /// planner bug costs the markings, never the road.
    /// </summary>
    private async Task<RoadTile?> BuildRoadsAsync(TileId id, CancellationToken ct)
    {
        var snap = _snapshot;
        var own = await StageInputAsync(id, snap, ct).ConfigureAwait(false);
        if (own.Roads is null || !RewriteRoads) return own.FreshRoads();

        // the halo: generated neighbours bring roads and ground, real ones their ground
        var roads = new Dictionary<TileId, (RoadTile?, RawRoads.Key?[]?)>();
        var grids = new Dictionary<TileId, ChunkGrid?>();
        var source = Neighbours ?? _inner;
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var t = new TileId(id.E + de, id.N + dn);
                if (snap.Covers(t))
                {
                    var input = t == id ? own : await StageInputAsync(t, snap, ct).ConfigureAwait(false);
                    roads[t] = (input.FreshRoads(), input.Keys);
                    grids[t] = input.Ground;
                }
                else if (snap.Real.Contains(t))
                    grids[t] = await source.LoadChunkAsync(t, ct).ConfigureAwait(false);
            }
        // the walls the density field reads round the halo, ahead of the gate
        var walls = new Dictionary<TileId, BuildingTile?>();
        for (int de = -2; de <= 2; de++)
            for (int dn = -2; dn <= 2; dn++)
            {
                var t = new TileId(id.E + de, id.N + dn);
                walls[t] = await source.LoadBuildingsAsync(t, ct).ConfigureAwait(false);
            }

        await _stageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Rewrite(id, own, roads, grids, walls, source), ct).ConfigureAwait(false);
        }
        finally
        {
            _stageGate.Release();
        }
    }

    private RoadTile? Rewrite(TileId id, StageInput own, Dictionary<TileId, (RoadTile?, RawRoads.Key?[]?)> roads,
        Dictionary<TileId, ChunkGrid?> grids, Dictionary<TileId, BuildingTile?> walls, IChunkSource source)
    {
        try
        {
            _stageWalls = walls;
            _stageSource = source;
            if (_facades is null || _facadeTiles.Count > StageSharedTiles)
            {
                _facadeTiles.Clear();
                _facades = new Facades(t =>
                {
                    _facadeTiles.Add(t);
                    return StageWalls(t);
                });
                _field = new UrbanField(_facades);
            }
            var built = TileRewriter.Rewrite([id], Stage, new TileRewriter.Inputs
            {
                Roads = t => roads.TryGetValue(t, out var r) ? r : (null, null),
                Grid = t => grids.GetValueOrDefault(t),
                Buildings = StageWalls,
                Facades = _facades,
                Field = _field,
            });
            if (built.Count == 0) return own.FreshRoads();
            // through the codec, so the game reads exactly what a preprocessed tile would give it
            using var ms = new MemoryStream();
            RoadCodec.Encode(built[0], ms);
            ms.Position = 0;
            return RoadCodec.Decode(ms);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the pair may hold a half-built tile: start over
            _facades = null;
            _field = null;
            Log?.Invoke($"[fallback] road stage failed on {id}, raw roads kept: {e}");
            return own.FreshRoads();
        }
    }

    /// <summary>
    /// A tile's buildings for the stage: fetched ahead of the gate, else loaded here, synchronously
    /// on the stage's worker (the field's margin can reach past the fetched ring).
    /// </summary>
    private BuildingTile? StageWalls(TileId t) =>
        _stageWalls.TryGetValue(t, out var b) ? b
            : (_stageSource ?? _inner).LoadBuildingsAsync(t).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>A generated tile's raw keyed roads and stride-5 ground, cached per real-set version.</summary>
    private async Task<StageInput> StageInputAsync(TileId id, Snapshot snap, CancellationToken ct)
    {
        var key = (id, snap.Version);
        lock (_stageInputGate)
            if (_stageInputs.TryGetValue(key, out var cached)) return cached;

        var blend = await BlendFor(id, full: true, ct).ConfigureAwait(false);
        var input = await Task.Run(() =>
        {
            var (roads, keys) = World.BuildRoadsKeyed(id, blend);
            var rawKeys = roads is null ? null
                // to the centimetre, as the preprocessor's .keys files hold them
                : keys.Select(k => (RawRoads.Key?)new RawRoads.Key(k.Line, 0,
                    Math.Round(k.FromM, 2, MidpointRounding.AwayFromZero))).ToArray();
            byte[]? encoded = null;
            if (roads is not null)
            {
                using var ms = new MemoryStream();
                RoadCodec.Encode(roads, ms);
                encoded = ms.ToArray();
            }
            return new StageInput(encoded, rawKeys, World.BuildGrid(id, StageGroundStride, blend));
        }, ct).ConfigureAwait(false);

        lock (_stageInputGate)
        {
            if (_stageInputs.TryAdd(key, input))
            {
                _stageInputOrder.AddLast(key);
                while (_stageInputOrder.Count > StageInputCacheSize)
                {
                    _stageInputs.Remove(_stageInputOrder.First!.Value);
                    _stageInputOrder.RemoveFirst();
                }
            }
            return _stageInputs[key];
        }
    }
}
