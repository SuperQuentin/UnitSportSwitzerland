# Performance overlay

- **Performance overlay** (`Core/PerfOverlay`, **F3** cycles Off / FPS / Detailed, saved as
  `GameSettings.PerfOverlay`, also in Settings; `--perf off|fps|full` for one run). Detailed shows
  frame avg/p99/max over 2 s, draws/prims/memory, and the loader via `ChunkManager.GetPerfStats()`:
  queue depths, builds/s, per-stage worker ms per tile, and tile latency over the last 128 builds —
  **ground** (build start -> first surface committed) and **complete** (-> last result committed).
  Both are timed on the main thread, so they include waiting behind the commit budget. Hidden
  during a video export (`OfflineMode`); shows up in `--shot` captures, which is how to screenshot it.
