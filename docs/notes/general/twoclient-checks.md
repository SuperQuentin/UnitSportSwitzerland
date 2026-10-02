# Two-client checks: `tools/lib/twoclient.sh`

## Rule

- A `tools/<x>check.sh` that starts a dedicated server and clients sources
  `. "$(dirname "$0")/lib/twoclient.sh" <x>` first thing, and starts every Godot through it:
  `tc_server <timeout> <wait_listening> <log> --server ...`, `tc_client <timeout> <log> [--windowed] ...`
  (add `&` for a background one), `tc_godot` for anything else, `tc_stop` to end a server,
  `tc_ok <n> <logs>` to count `RESULT: ok` lines.
- Never `timeout godot ...`, `kill $SERVER`, `kill %1`, `pkill`, or a hard-coded `godot`: on Git Bash
  a kill stops only the `timeout` wrapper and the server keeps the port until its timeout; the
  winget `godot` link hangs. The lib kills by PID tree (`guard.sh`) and finds the console exe.
- Ports stay per script and passed in (`PORT=...`); keep them distinct from the other checks.
- Clients are headless unless the script says `--windowed` (a speaker, drawn parked vehicles,
  screenshots) or `WINDOWED=1` is set.
- `user://` (loot, bank, placed, birds, CDs) is a fresh `test_output/userdata_<x>` per run (Windows
  `APPDATA`, Linux `XDG_DATA_HOME`): a check never touches real saves. `USERDATA=<dir>` keeps one
  across runs, `USERDATA=real` uses the real one.

## Why

#221 investigation 3, cluster 20: 18 scripts each re-implemented "start server, wait for
`server listening`, start A and B, grep RESULT" with different kill code (four of them leaked the
server on Git Bash), five ignored `GODOT`, and the loot/bank/placed/birds scripts wrote the real
user data. Before/after runs: see PR (each script fails or passes the same way as on main).

## Same logic, preserved

- Each script keeps its flags, ports, timeouts (now `guard_run` timeouts), log names, RESULT text and
  exit code; windowed clients stay windowed.
- `sleep 12` / `sleep 6` before the clients became "wait for `server listening`" (at most 120 s).
- The lib takes the heavy-run lock (`guard_lock`) unless `GUARD_LOCK_HELD` is set (as under
  `tools/test.sh`), waits for 4 GB free RAM, and the `guard_watch $$` watchdog stays.
- Trap: a check that needs state from an earlier run (an emptied loot epoch, a cracked safe) now
  starts clean every time; pass `USERDATA=<dir>` to reproduce an old state.

## Migrating old code / open branches

- A branch that adds or edits a `tools/*check.sh`: replace the `guard.sh` source line, `cd`,
  `OUT=`, `mkdir`, `CH=()/CHUNKARG=()` and `stop()` boilerplate by the source line above; replace
  `timeout N "$GODOT" --headless --path . -- --server ... > log 2>&1 &` + the wait loop by
  `tc_server N <wait> log --server ...`, each client by `tc_client`, `kill`/`stop $SERVER` by `tc_stop`.
- Not converted yet: `tools/racecheck.sh` (four autopilot clients, own cleanup) and
  `tools/buildnetcheck.sh` (#281, open). `tools/test.sh` keeps its own Godot finder (#269 edits it).

## How to check

`bash tools/<x>check.sh` (with `CHUNKS=` for the real-map ones) and read its RESULT line; after it,
no Godot may hold the script's UDP port (`netstat -ano -p UDP | grep :<port>`).
