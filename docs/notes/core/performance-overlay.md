# Performance overlay

- **Performance overlay** (`Core/PerfOverlay`, **F3** cycles Off / FPS / Detailed, saved as
  `GameSettings.PerfOverlay`, also in Settings; `--perf off|fps|full` for one run). Detailed shows
  frame avg/p99/max over 2 s, draws/prims/memory, and the loader via `ChunkManager.GetPerfStats()`:
  queue depths, builds/s, per-stage worker ms per tile, and tile latency over the last 128 builds —
  **ground** (build start -> first surface committed) and **complete** (-> last result committed).
  Both are timed on the main thread, so they include waiting behind the commit budget. Both FPS and Detailed show where
  the camera is (#772): `LV95 E,N  alt m  facing NE 45°` (8-point compass and degrees, 0° north), through
  `WorldOrigin.ToLv95`, so it stays right across origin shifts: what playtest directions are given in. Hidden
  during a video export (`OfflineMode`); shows up in `--shot` captures, which is how to screenshot it.
