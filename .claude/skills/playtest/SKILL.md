---
name: playtest
description: Work from a human's playtest of UnitSportSwitzerland. Batch (default) - read a recorded session's report.md (test_output/playtest/sessions/), its notes' screenshots and top views, reproduce each finding headless, fix, and say what to retest. Live - listen through the unitsport-playtest MCP server and change the running game. Use when the user runs /playtest, says they playtested or are playtesting, points at a session or report.md, or asks you to listen to the game.
---

# Playtest: fix what the player noted

Read `docs/notes/general/playtest.md` once per session; the bundle is
`docs/notes/general/playtest-recorder.md`, the pictures `docs/notes/general/topview.md`.

## Batch (the default)

The player ran `tools/playtest.sh` (or `retest`), played scenarios, noted what was wrong (F12,
Fail) and pressed End session. Claude was not in the loop: everything is in the bundle.

1. **Find the session**: the one they name, else `tools/playtest.sh last`. If it has no
   `report.md` (the game crashed or was closed), `tools/playtest.sh report <dir>`.
2. **Read `report.md`**: scenarios and verdicts, then each note with the events and errors around
   it. Make a list of findings: one per note or FAILED verdict, plus any error the report groups
   that no note explains.
3. **For each finding**, look before guessing:
   - `Read` the note's `notes/NNN.png` (what they saw) and `notes/NNN_top.png` (±10 s from above).
   - Another window or zoom: `tools/playtest.sh topview <dir> --from A --to B [--at T] [--around E,N --radius m] --out test_output/x.png`, then `Read` it. A `server/` beside it adds the server's view (dashed red, `Δ` offsets): the replication and authority bugs.
   - `events.jsonl` ±10 s (`grep` by type or `[tag]`: `ride`, `teleport`, `prop`, `log`), and `perf/summary.txt` for hitches.
   - The trace in code when you need numbers: `TraceReader(dir).Window(t0, t1)` (`src/Playtest/Recording/Trace.cs`).
4. **Reproduce headless** when you can, the cheapest way (`docs/notes/general/testing.md`): a
   probe or check printing `RESULT`, a Tier 0 test, or the scenario itself in
   `--playtest --playtest-smoke`. Run it failing first.
5. **Fix and check**: the repo workflow (issue, branch, worktree, PR) for code; `tools/test.sh quick`
   (and `net` for replicated state). A finding you cannot reproduce or fix: say what you saw and
   what you would need (a longer note, a second session around it).
6. **Hand back**: per finding, fixed / not reproduced / needs the player, and the scenarios a fix
   made stale. Ask them to run `tools/playtest.sh retest` (it starts only what is due). Commit the
   ledger files (`tests/playtests/`) with the work they judged.

## Live (one-off tuning)

The game runs with `tools/playtest.sh live` and `/mcp` lists `unitsport-playtest`.

1. `status`; `say` a one-line hello naming the scenario, or suggest `start_scenario next`.
2. `wait_for_player` (timeout 50: the MCP client drops a call past about a minute). On "nothing
   yet", call it again; never leave the player without one pending (the panel shows "Claude is
   working…").
3. Act, briefly `say` what changed, back to 2. Cheapest first: `set_param` + `reset_scenario`;
   `node_get`/`node_set`; `list_tunables`/`set_tunable` (a `const` to tune becomes a `[Tunable] static`,
   then `restart`); code, then `restart` (rebuild=true; on BUILD FAILED read `test_output/playtest/build.log`).
   Look with `screenshot`, `log_tail`, and in a recorded session `topview` and `session_report`.
4. `VALIDATED`: offer the next due. `FAILED <id>: why`: reproduce, fix, `restart`, ask to retry.

Keep answers short: they are reading them while playing.
