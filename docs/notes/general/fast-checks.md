# Fast checks: the quick tier runs on game time (`--fixed-fps 60`, #461)

`tools/test.sh quick` starts every headless Godot check with `--fixed-fps 60`: Godot drops real-time
sync and advances one frame = one physics tick (1/60 s) as fast as the CPU allows. A check that
drives 3 km or waits a minute moored no longer takes that long on the wall clock. Physics is
unchanged (same 1/60 s step): only the waiting goes away.

## Measured (Windows, 4.7.1 headless, the 55 quick checks of `tools/lib/checkmap.txt`, one after the other)

| | Real time | `--fixed-fps 60` |
|---|---|---|
| all 55 | 2 298 s (38 min) | 514 s (8.6 min) |
| `--steamercheck --chunks fixture:lake` | 326 s | 50 s |
| `--boatcheck speedboat` / `jetski` | 246 / 201 s | 37 / 29 s |
| `--swimcheck` | 230 s | 39 s |
| `--steamercheck walk` | 164 s | 31 s |
| `--drivecheck --chunks fixture:hairpin` | 88 s | 15 s |
| `--flycheck a320 --world flat --airliner sim` | 84 s | 5 s |
| `--exitcheck --world fixture` | 87 s | 13 s |
| `--synccheck --world flat` | 53 s | 5 s |

Same verdicts in both modes except `--steamercheck` (default stage) and `--waterprobe`, which fail on
`main` in real time too, with the same expectations.

## Rules for checks and probes

- **Time a wait or a duration with `Core.GameClock.Now`** (physics ticks in seconds), never
  `Time.GetTicksMsec()`: under `--fixed-fps` the wall clock runs ~5-20x slower than the game, so a
  "stopped in N s" reads wrong and a wall-clock timeout allows far more game time than meant.
- **Wait for threaded work (tile builds, generated terrain) on the wall clock**, never in frames or
  game seconds: the threads do not speed up. `GameClock.Pace` already holds the frames to real time
  while `ClientWorld` loads or `ChunkManager.Settled` is false; a check outside `ClientWorld`
  (`WaterProbe`) bounds its settle in wall time itself. The spawn waits in `CabinCheck`,
  `FreighterCheck`, `HoldCheck`, `AirstairsCheck` are wall-clock bounded (30 s).
- **Simulation timers in gameplay use game time** (`GameClock.Now` or `ClockSync.ServerNow`):
  the emote wheel's tap/hold, the crash seat grace, the marina respawn, the hull-touch walk. Pure
  presentation (UI pulses, audio chimes) may stay on the wall clock.
- `ClockSync.LocalNow` is `GameClock.Now` under `GameClock.Fixed` (one process, nothing to sync
  with), so the waves, build growth, signals and BR timers keep pace with the physics.
- `GameClock.Fixed` reads the process's own argv (`System.Environment.GetCommandLineArgs()`):
  Godot removes the engine args it consumed from `OS.GetCmdlineArgs()`.
- A check that must stay real time: start its map row's check with `@realtime`
  (`src/X quick @realtime --mycheck ...`). `TEST_REALTIME=1 tools/test.sh quick` runs the whole
  tier in real time, to tell a fixed-fps artefact from a real failure.
- **The net and full tiers stay real time**: a server and its clients each run their own clock and
  sync through `ClockSync`; fast-forwarding one process breaks that. Never pass `--fixed-fps` to a
  `tools/*check.sh` process.
- `--fixed-fps` must equal the physics tick rate (60, project default): at 120 every other frame
  would have no physics tick and `GameClock.Pace` would hold the wrong length.
- Render frames: in real time a headless check drew ~2 frames per tick (uncapped), now exactly 1.
  `--synccheck` counts half the frames; its thresholds are unchanged and it passes.
