# Session perf log

- **Session perf log** (`Core/PerfRecorder`, **F4** start/stop, `--perflog [seconds]` from boot,
  Settings -> "Open folder"): writes `user://perf_logs/<timestamp>/` with `frames.csv` (per frame:
  frame/GPU/render-CPU ms, draws, prims, memory, GC counts, loader queues, commits, camera LV95 +
  speed, mode), `builds.csv` (per tile: latency + worker ms of every stage), `commits.csv`,
  `events.log` (hitches tagged commit/gc/gpu/other, teleports, mode and settings changes) and
  `summary.txt` ending in a diagnosis. **Use the viewport's measured GPU time, not
  `Performance.TimeProcess`, to tell GPU- from CPU-bound**: TimeProcess absorbs the wait for the
  renderer and read 50 ms on a frame the GPU spent 35 ms of. A frame's delta pays for the
  PREVIOUS frame's commits, so the recorder runs last (`ProcessPriority`) and shifts its commit/GC
  context by one frame. The per-build stage array also times `blend+tail` (road-blended collision +
  visual re-mesh), which `BuildTimeReport` never counted and which measured ~33% of worker time.
