# Server frame metrics: busy is per frame, slow frames name their jobs (#221, PR #233)

## Rule
- Judge server cost with the `--serverstats` **busy** column (per frame) and the `[stats] slow frame`
  lines. Never use Godot's `Performance.Monitor.TimeProcess` / `TimePhysicsProcess` for it: those
  are the **maximum over the last second**, so one 100 ms frame reads as "busy 100 ms" for about
  60 frames.
- Wrap every new periodic main-thread job on the server (anything on a 0.5 s / 1 s / 5 s timer) as
  `long t0 = Stopwatch.GetTimestamp(); ...job...; Net.ServerStats.Ran("<job name>", t0);`.
  Without `--serverstats` it does nothing; with it, the job is named in slow-frame lines and in the
  summary's `job_max_ms <job>`. It allocates nothing.
- Keep `ServerStats` at `ProcessPriority = int.MaxValue`, and do not turn
  `SceneTree.MultiplayerPoll` back on while it runs. ServerStats polls the multiplayer API itself
  from `ProcessFrame`, at the point where the tree would have polled. That way the poll (ENet
  receive, replication, every incoming RPC) counts as busy time.

## Why
Before #221, the 16-player busy p99 of 140 ms was a 1 s maximum repeated for every frame of that
second, and the 3.9 ms "median" was a median of per-second maxima. Measured per frame, 16 players
give busy p50 1.6 ms and p99 3.4 ms. The slow-frame lines found the 5 s spike at once:
`jobs: player status 66-459 ms`.

## Same logic, preserved
- `frame`, the wall-clock time between frames, still measures the same thing. It is now stamped
  after the 5 s report, so a slow report shows up in its own frame.
- busy runs from the first hook of the main-loop iteration (the `physics_frame` signal, or the poll)
  to the end of `ServerStats._Process`. Deferred calls, timers and tweens that run after
  `_process` are not counted.
- A frame with a long `frame` and a short `busy` lost its time outside the server's work: the OS,
  a GC on another thread, or a shared machine. `(a GC ran)` means a collection finished in that frame.

## Migrating old code / open branches
- `busy_ms_*` values in a `server_summary.txt` from before #221 are per-second maxima. They cannot be
  compared with the new ones: re-measure "before" with this metric (cherry-pick the measurement commit).
- Run `grep -rn "Performance.Monitor.Time" src` over server code and replace each hit with a
  Stopwatch plus `ServerStats.Ran`.
- #188 (Battle Royale), #219 (Interiors) and #169 (bus walking) are open PRs that add server code
  and touch `ServerWorld.cs`. Wrap any periodic jobs they add in `ServerStats.Ran`.
  `ServerStats.cs` itself is not edited by any other PR.

## How to check
Run `tools/loadtest.sh 16` (env `GODOT`, `CHUNKS`). In steady state (t = 20–150 s) there should be
only a handful of slow frames, from joins and leaves, and busy p99 should be about 3–4 ms at
16 players on Windows.
