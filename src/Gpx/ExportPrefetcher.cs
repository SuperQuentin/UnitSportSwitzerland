using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>
/// Reads the whole route into the tile cache while the export renders, so no frame is ever the
/// first to ask for a tile.
///
/// <para>
/// An export renders in chronological order, which is the worst possible order for streaming: it
/// visits every tile along the route exactly once and waits for each ring to fill as it arrives.
/// But nothing about the route is a surprise — the track is known in full before frame one. This
/// walks it from the start and pulls each tile through <see cref="CachingChunkSource"/>, so by the
/// time the LOD rings ask, the answer is in memory and the worker only has to mesh it.
/// </para>
///
/// <para>
/// It reads in <b>route order</b> and never blocks the render, so it does not need to be told
/// where the clock is: pure IO with no meshing outruns a renderer that is also waiting on the GPU,
/// and if it ever falls behind, the rings simply fetch that tile themselves as they always did.
/// </para>
/// </summary>
public sealed class ExportPrefetcher
{
    /// <summary>
    /// Loads in flight at once. Deliberately modest: this is a background convenience and must not
    /// take disk bandwidth away from the tile the next frame is actually waiting for.
    /// </summary>
    private const int Parallelism = 4;

    /// <summary>Metres between the track samples whose tiles are collected.</summary>
    private const double SampleSpacingM = 250;

    private readonly IChunkSource _source;
    private readonly LodPolicy _lod;

    /// <summary>Tiles in the order the render needs them, each with the closest ring it reaches.</summary>
    private readonly List<(TileId Id, int Ring)> _route;

    private CancellationTokenSource? _cancel;
    private int _done;

    public int Prefetched => Volatile.Read(ref _done);
    public int Total => _route.Count;

    private ExportPrefetcher(IChunkSource source, LodPolicy lod, List<(TileId, int)> route)
    {
        _source = source;
        _lod = lod;
        _route = route;
    }

    /// <summary>
    /// Plans the tile order for a track: every tile the LOD rings will want, first needed first.
    ///
    /// <para>
    /// Ordering is by position along the route rather than by distance from it, because that is
    /// the order the render will need them in. Within one position the near rings come first,
    /// since those are the tiles that carry roads and buildings and therefore the ones a frame
    /// actually stalls on.
    /// </para>
    /// </summary>
    public static ExportPrefetcher Create(GpxTrack track, ChunkManager chunks)
    {
        var lod = chunks.Lod;
        var closest = new Dictionary<TileId, int>();
        var order = new List<TileId>();

        double nextSample = 0;
        foreach (var p in track.Points)
        {
            if (p.Distance < nextSample) continue;
            nextSample = p.Distance + SampleSpacingM;

            var centre = TileId.FromLv95(p.E, p.N);
            for (int ring = 0; ring <= lod.MaxDist; ring++)
                for (int de = -ring; de <= ring; de++)
                    for (int dn = -ring; dn <= ring; dn++)
                    {
                        // only the new perimeter, so each tile is first seen at its own ring
                        if (Math.Max(Math.Abs(de), Math.Abs(dn)) != ring) continue;

                        var id = new TileId(centre.E + de, centre.N + dn);
                        if (!chunks.AvailableTiles.Contains(id)) continue;

                        // A tile on the horizon of one part of the route can be underfoot on
                        // another, and it is the closest approach that decides what has to be
                        // read. Keeping only the first sighting would prefetch the 5 KB companion
                        // for ground the runner later stands on, and the frame would stall there.
                        if (closest.TryGetValue(id, out int best))
                        {
                            if (ring < best) closest[id] = ring;
                            continue;
                        }

                        closest[id] = ring;
                        order.Add(id);
                    }
        }

        var route = order.Select(id => (id, closest[id])).ToList();
        return new ExportPrefetcher(chunks.Source!, lod, route);
    }

    /// <summary>Starts warming the cache. Safe to call twice; the second call replaces the first.</summary>
    public void Start()
    {
        Stop();
        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(_route,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                    async (entry, token) =>
                    {
                        await WarmAsync(entry.Id, entry.Ring, token).ConfigureAwait(false);
                        Interlocked.Increment(ref _done);
                    }).ConfigureAwait(false);

                GD.Print($"[export] prefetched {_done} tiles along the route "
                    + $"in {clock.Elapsed.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { GD.PushWarning($"[export] prefetch stopped: {e.Message}"); }
        }, ct);
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _cancel = null;
    }

    /// <summary>
    /// Pulls exactly what the rings would pull for this tile, and nothing more.
    ///
    /// <para>
    /// The radii come from the live <see cref="LodPolicy"/> rather than being repeated here, so
    /// prefetching a tile the rings will not want — or missing one they will — cannot happen by
    /// the two drifting apart.
    /// </para>
    /// </summary>
    private async Task WarmAsync(TileId id, int ring, CancellationToken ct)
    {
        try
        {
            // Cover and holes are a few kilobytes and every ring needs them.
            await _source.LoadCoverAsync(id, ct).ConfigureAwait(false);
            await _source.LoadHolesAsync(id, ct).ConfigureAwait(false);

            // Beyond the road ring the ground is drawn one vertex in ten or twenty and nothing is
            // built on top of it, so reading the full grid here would hand back the whole saving
            // the coarse tile exists for: 490 KB apiece, for 280 of the 361 an anchor wants.
            if (ring > _lod.RoadMaxDist)
            {
                await _source.LoadCoarseChunkAsync(id, ct).ConfigureAwait(false);
                return;
            }

            await _source.LoadChunkAsync(id, ct).ConfigureAwait(false);
            await _source.LoadRoadsAsync(id, ct).ConfigureAwait(false);

            if (ring <= _lod.BuildingMaxDist)
            {
                await _source.LoadTreesAsync(id, ct).ConfigureAwait(false);
                await _source.LoadBuildingsAsync(id, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* a tile that will not load is the rings own problem to report */ }
    }
}
