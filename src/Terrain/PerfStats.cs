namespace UnitSport.Terrain;

/// <summary>
/// What the tile loader is doing right now, for the performance overlay. Latencies are
/// milliseconds over the last 128 builds (NaN before the first); stage times are worker
/// milliseconds per completed build, averaged over the whole session.
/// </summary>
public readonly record struct PerfStats(
    int Loaded, int Desired, int Pending, int InFlight, int ReadyQueue,
    int Cancelled, int Completed, int FullLoads, int CoarseLoads, int HorizonBlocks,
    double GroundP50, double GroundP95, double CompleteP50, double CompleteP95,
    double LastCommitMs, int LastCommits,
    (string Name, double Ms)[] StageAvgMs);
