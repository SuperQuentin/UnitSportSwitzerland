---
name: playtest
description: Listen to a human playtesting UnitSportSwitzerland and fix what they report, live. Use when the user runs /playtest, says they are playtesting, or asks you to listen to the game. Requires the game running with --playtest (tools/playtest.sh) and the unitsport-playtest MCP server from .mcp.json.
---

# Playtest: listen to the player, change the game live

A person is playing scenarios in the running game (`tools/playtest.sh`, `--playtest`). They see a
panel (F10) with the scenario, its checklist and a chat with you. You reach the game through the
`unitsport-playtest` MCP tools. Read `docs/notes/general/playtest.md` once per session.

## The loop

1. `status`: what is running and what is due. If the game doesn't answer, ask the user to start
   `tools/playtest.sh` (default course `flat`) and check that `/mcp` lists `unitsport-playtest`.
2. `say` a one-line hello naming the current scenario, or suggest `start_scenario next`.
3. `wait_for_player` (timeout 300). It returns the player's lines and events: messages,
   `VALIDATED`/`FAILED <id>: <note>`, `scenario started`. On "nothing yet", call it again.
4. Act on what they said, briefly `say` what you changed, and go back to 3. Never leave the player
   waiting without a `wait_for_player` pending: the panel shows "Claude is working…" until you do.

## Changing the game, cheapest first

- **Scenario knob** (`set_param` then `reset_scenario`): speed, distance, wall size...
- **Node property** (`node_get`, then `node_set` on `player`, `scenario:N` or a path): position,
  velocity, `[Export]` fields. Effective at once.
- **Tunable** (`list_tunables`, `set_tunable`): `[Tunable]` statics. To tune a `const`, turn it into a
  `[Tunable] static` field with the same value (`src/Core/TunableAttribute.cs`), then `restart`.
- **Code**: edit the source in this repo (the worktree the game runs from), then `restart`
  (rebuild=true). The game rebuilds, relaunches and reopens the scenario. Call `wait_for_player`
  until it answers again (20-60 s). If it reports BUILD FAILED, read `test_output/playtest/build.log`,
  fix, and `restart` again.
- Look before and after: `screenshot`, `log_tail` (filter by a tag like `[car]`).

## When the player gives a verdict

- `VALIDATED`: the ledger file `tests/playtests/<id>.json` is written. Offer the next due scenario.
- `FAILED <id>: why`: reproduce (screenshot, logs), propose and make a fix, `restart`, ask them to retry.
- Before finishing: if code changed, the fix ships like any change (repo workflow: branch, tests,
  PR). Commit the ledger files with it: they record what a person checked, against which code.

Keep answers short: they are reading them while playing.
