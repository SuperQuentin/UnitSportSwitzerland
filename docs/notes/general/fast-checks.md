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

## Net checks at `/speed` (`--checkspeed n`, #931)

`--fixed-fps` cannot speed up a server and its clients, but the server-wide `/speed` can: every peer
follows it through `SimClock`. A client probe on `ChatProbe` passes `--checkspeed n`, and `SpeedUp(password)`
logs in with the server's `--admin-password` and sends `/speed n`. `SpeedBack()` goes back to x1 before a
part the scale would distort.

- **What shrinks:** `/speed` sets `Engine.TimeScale` on every peer, so each physics step is `scale / 60` s
  long. There are not more steps. Probe `Seconds()` timers (`CreateTimer`), the server's `_clock += delta`
  timers (fight phases, the BR match through `ClockSync.SimNow`), falls, walks and countdowns all shrink.
- **What does not:** `Until()` deadlines (they count physics frames through `GameClock.Now`, so they only
  get more lenient), bash `sleep`, boot, tile and collision builds on threads, connects, and network
  delay.
- **Where it breaks:**
  - **Hits on a moving remote body.** At x4 a walking bot is 4 times as far along on the server as its copy
    on the client is in the same real-time delay. In `brbotcheck`, 20 knife stabs at 1.6-2 m all missed.
    Go back to x1 for melee.
  - **Bigger steps.** Vehicles, boats, stacking, ragdolls and lifts take 1/15 s steps at x4. Every
    vehicle or physics check below stays at x1.
  - **Clock and sync checks** (`signalnetcheck`, `clocknetcheck`, `speednetcheck`, `radiocheck`,
    `playtestnetcheck`) and screenshot or perf checks (`useanimcheck`, `switchcheck`, `loadtest`) are
    meaningless at another scale.
- **Measured** (Windows, 4.7.2, one after the other, cold tile cache):

| Check | x1 | x4 |
|---|---|---|
| `fightnetcheck` | 36 s | 20-21 s (3 runs ok) |
| `brbotcheck` (x4 until the bots land, then x1 for the knife) | 235 s | 154 s |

- **Survey of the 72 net/full scripts (estimates, Oct 2026).** Most spend their time on boot, tile
  loading, `sleep`s and chat handshakes, which leaves `/speed` little to save.
  - **Worth it, done:** `brbotcheck`, `fightnetcheck`, and `brsquadcheck` (already at x4 before #931; it
    fails at x4 on main as of Oct 20, the mate falling through the world under its canopy, #937).
  - **Maybe, with a physics risk:** only one wait at x4, then x1 again.
    - `rampnetcheck`: the 66 s park wait in `GarageProbe`.
    - `steamernetcheck`: the 30 s full-ahead in `SteamerNetProbe`, but `Sample()` counts frames.
    - `racecheck`: the entry window and the countdown.
    - `palletnetcheck`, `holdnetcheck`, `hallcarnetcheck`, `botcheck`.
  - **Not worth it:** everything else, at ~0-15 s each.
