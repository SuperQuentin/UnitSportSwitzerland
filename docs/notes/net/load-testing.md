# Load testing: swarm bots, server stats, remote smoothness

- `tools/loadtest.sh <players> [label] [seconds]` (env `CHUNKS`, `PORT`, `SEED`, `AT`): dedicated
  server with `--serverstats`, bots in `--swarm` processes of ≤ 16 (each bot its own
  `SceneMultiplayer` on `/root/BotK`, a minimal mirror of the client tree, kinematic driving in
  `Swarm.Drive` — the one place to adapt when replication changes), one real observer client with
  `--netsmooth`. Every process has its own `--cache`. Output in `test_output/loadtest/<label>/`.
- Swarm cost: ~190 MB per process + ~3 MB and ~1.5 % of a core per bot. A full client is ~2.2 GB:
  on a 15 GB machine several agents running clients at once get OOM-killed — check `free -g`,
  and a run with a process killed (exit 137) is invalid.
- `--serverstats`: frame (wall clock) and busy ms p50/p99/max, working set, heap, GC counts and
  pauses, ENet bytes/packets per second total and per peer, rtt/loss, kernel UDP receive drops
  (Linux only).
- **busy is per frame** (#221): a Stopwatch from the first hook of the iteration (`physics_frame`, or
  the multiplayer poll, which `ServerStats` takes over: `SceneTree.MultiplayerPoll = false`) to the
  end of `ServerStats._Process` (`ProcessPriority = int.MaxValue`). Godot's `TimeProcess` monitors
  are the **max over the last second**: one 100 ms frame read as "busy 100 ms" for ~60 frames.
  Every frame over 50 ms is printed, `[stats] slow frame t=... frame=... busy=... ms, jobs: ...`,
  with the periodic jobs that ran in it (`ServerStats.Ran(name, Stopwatch.GetTimestamp())` around
  a job) and whether a GC ran; the summary has `slow_frames_over_50ms` and `job_max_ms <job>`.
- **`GD.Print` blocks on Windows**: ~6 ms a line, at times 30-120 ms (headless console wrapper,
  stdout redirected). The per-player status line every 5 s made a 70-460 ms frame every 5 s at 16
  players; it is now opt-in (`--player-status`). Keep periodic server prints to one line; the
  `[stats]` line and the summary file are written on a worker.
- **#221 results, 16 players, 120 s, Windows** (steady state 20-150 s, medians of the 5 s windows):

  | | before | after |
  |---|---|---|
  | slow frames (> 50 ms) | 31 (one every 5 s: player status 66-459 ms) | 3-4 |
  | frame p99 / max | 18.2 / 475 ms | 18.2 / 103 ms |
  | busy p50 / p99 / max | 1.6 / 3.4 / 460 ms | 1.8 / 3.5 / 43 ms |

  The 5 s `VehicleManager` housekeeping takes ≤ 1.4 ms (no vehicles in a swarm run): left as is.
  The MCP `game_helper` logger is only attached with the editor debugger (it was never drained
  otherwise). Soak 4 players × 10 min: working set 309 MB flat from t=120 s to 600 s.
- Windows (Git Bash): `$!` is the console wrapper's msys pid; `loadtest.sh` reads swarm CPU and
  memory from the Godot child process through PowerShell, and free memory from `MemFree`.
- **Results, 32 players (issue #37)**:

  | | before | after |
  |---|---|---|
  | UDP drops at the server socket | 647,712 | 0 |
  | bots disconnected | 15 / 31 | 0 |
  | packets in / s (peak) | 24,019 | ~2,300 |
  | net in / out | 2,074 / 1,952 KB/s | ~330 / ~227 KB/s |
  | observer: freeze frames / longest | 1,253 / 198 ms | 0 / 0 |
  | observer: step p99 / (v·dt) | 5.79 | 1.09 (plane, 55 m/s, 1.5 km) |

  Soak, 32 players × 30 min: working set 360 → 374 MB over the last 20 min (+3.9 %), heap flat
  ~150 MB, GC pause max 5.6 ms, steady-state frame max 17–28 ms, 0 drops, 0 disconnects.
- **After the server relay** (owners send once; server rebroadcasts near 30 Hz / far 6 Hz): 32
  players incl. the grouped race start — packets in 877/s peak (was ~5,000 with owner relaying to
  audiences, 24,019 originally), 0 UDP drops, net in 106 KB/s, out 539 KB/s; observer 0 freezes.
- The one frame spike left (~110 ms) is 31 bots joining in the same second at startup: a join costs
  < 0.6 ms on the server, the first one 30 ms (JIT of the cold paths).
- **Relay was O(N²) before**: packets in tracked N·(N−1)·60 because every client sent every frame to
  every peer through the server; the Linux default 208 KB socket buffer overflows from ~20 players.
