# Loading must not starve the PC (#1381)

- **Build cap**: on a desktop, outside video export (`OfflineMode`), at most `BuildPolicy.Ceiling(cores)` =
  `Max(2, cores - 2)` tile builds run at once (10 on 12 threads). The saved `MaxConcurrentBuilds` / `--builds`
  is clamped to it, logged once (`[terrain] 32 parallel builds asked, held to 10`). Phones keep their own cap,
  video export keeps its 2 per core. `DesktopBuildCeiling` is declared right after `MobileMaxConcurrentBuilds`
  (static initializer order, `net/terrain-streaming`).
- **Priority**: builds run on `BuildScheduler`, dedicated BelowNormal threads (`Core`-free, `Terrain/BuildPolicy.cs`),
  not on the ThreadPool; video export and phones still use `Task.Run`. An `await` in the build captures the
  scheduler, so the CPU stages after an IO wait return to it. `LocalChunkSource` decodes go through
  `BuildScheduler.Run` (an `AsyncLocal` says "inside a build"): from a build it runs the work inline and returns a
  finished task. Never queue it on the build threads: sync-over-async callers (`FallbackChunkSource.StageWalls`,
  `SitePlans`) block a build thread on it and would deadlock the scheduler.
  **Not covered**: `FallbackChunkSource` (generated fill, its `Task.Run` and `Parallel.For`) and the horizon
  still use the pool at Normal priority; they are bounded by the builds in flight.
- **Frame cap**: `LoadingScreen.Begin` sets `Engine.MaxFps` to min(60, monitor Hz) (only if the user's limit is
  higher or off) and `Finish`/`Close` restore it. Not under `--fixed-fps`.
- **Cache trim**: `NetworkChunkSource.EvictIfOversized` no longer scans the directory on every write: the running
  byte total decides (`CacheTrimPolicy`), the scan runs in a background task, at most once per 30 s.
- **Measured** (offline `--at 2500300,1118450 --perflog 90`, shared machine, noisy): the same work is done, so
  the cores used while loading barely drop (first 30 s: 8.3 / 5.1 cores before, 7.3 / 5.8 / 5.7 after); what
  changes is who wins: the build threads yield to the OS and the main thread.
- `System.GC.ConserveMemory=7` left alone: GC counts and frame times did not differ between runs, no case to change it.
