# No blocking work in periodic server jobs: prints, files, /proc (#221, PR #233)

## Rule
- **No per-player `GD.Print` in a periodic server job.** `GD.Print` blocks on Windows stdout,
  usually for about 6 ms a line and sometimes for 30–360 ms. Print events (a join, a leave, a
  command), not state on a timer. A diagnostic that lists every player goes behind a flag, as
  `ServerWorld` does with `--player-status`.
- **File writes from a timer go on a worker.** Build the string on the main thread, then write it in
  `Task.Run` or a `ContinueWith` chain (see `ServerStats.WriteSummary`), and wait for that task in
  `_ExitTree`. From the worker, never call a Godot API that touches the scene tree (`GD.Print` is safe).
- **Gate platform probes.** `/proc/...` reads run only `if (OperatingSystem.IsLinux())`. Throwing
  and catching an exception every window is not free.
- A loop over players × vehicles that runs every frame should not allocate (no LINQ
  `Select`/`Any`/`ToList` per tick). Every 5 s it is cheap, so measure it with `ServerStats.Ran`
  before rewriting it.

## Why
16 players, 120 s, Windows (`tools/loadtest.sh 16`), steady state t = 20–150 s:

| | before | after |
|---|---|---|
| frames > 50 ms | 31 (one every 5 s: player status 66–459 ms) | 3–4 (joins and leaves) |
| frame p99 / max | 18.2 / 475 ms | 18.2 / 103 ms |
| busy p50 / p99 / max | 1.6 / 3.4 / 460 ms | 1.8 / 3.5 / 43 ms |
| `job_max_ms stats report` | 53 ms | 9.7 ms |

The 5 s `VehicleManager` housekeeping measured ≤ 1.4 ms, so it was left as it is.

## Same logic, preserved
- The `[server] player <id> at <pos>, ground ...` lines still exist with `--player-status`; the
  `player-overlap` check reads them. There was never a `/where` command.
- The `[stats]` line and `server_summary.txt` are still written every 5 s, in order. The last write
  finishes before the process exits (`_ExitTree` waits up to 1 s).
- `udp_rcvbuf_drops` is still counted on Linux. On other systems it reads 0; the old -1 was never a
  valid count there either.

## Migrating old code / open branches
- Run `grep -n "GD.Print" src/Core/ServerWorld.cs src/Net src/World` and check every print inside
  `_Process`, `_PhysicsProcess` or a timer. One line per event is fine; one line per player per
  tick is not.
- When rebasing a branch that edits `ServerWorld._Process`, keep the gate
  `if (_players == null || !PlayerStatus) return;`. Anything the branch adds after that gate runs
  only with `--player-status`, so move it above the gate.
- Open PRs that touch `ServerWorld.cs`: #169, #188, #197 and #219. #188 (Battle Royale) and #219
  (banks) add many prints; check that none of them prints per player on a timer.

## How to check
Run `tools/loadtest.sh 16`. In `server.log`, `grep "slow frame"` between t = 20 and t = 150 should
show no periodic pattern (every 5 s or every 1 s), and every `job_max_ms` line should stay in
single-digit ms.
