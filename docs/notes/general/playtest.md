# Playtest suite: scenarios a person judges, with Claude live in the game (#751)

Some things only a person playing can judge: how a crash looks, how a bus turns, whether a dance
loops cleanly. The playtest suite runs such **scenarios** one after another in **one** game, lets the
player talk to a **Claude Code session** that changes the running game, and records each **verdict**
in a committed ledger that knows when the verdict has gone stale.

## Run it

```
tools/playtest.sh [course]       # builds, starts the game on fixture:<course> (flat) with --playtest
claude                           # in the same checkout; /mcp lists unitsport-playtest; then /playtest
tools/playtest.sh pending        # what is due: never played, or the covered code changed since
```

- Debug builds only: `src/Playtest/**` and the `PLAYTEST` symbol exist only in the `Debug`
  configuration (the csproj), so exports never carry the server. It starts only with
  `--playtest`, offline, on `127.0.0.1:7801` (`--playtest-port N` otherwise; `.mcp.json` names 7801).
- **Panel**: F10, a pad's L3 + R3 together, VR the same through XrPad (no typing in VR yet). The
  panel holds the scenario list (✔ validated, ✖ failed, ↻ stale, ● pending), the instructions and
  checklist, Validate / Fail / Reset, and the chat with Claude. Closed, a card top right keeps the
  checklist and Claude's last line. Fail takes the text in the box as the reason.
- Scenarios needing another fixture course relaunch the game on it when picked (same panel, same chat).

## What Claude can do (MCP tools, `PlaytestDirector.Tools`)

`wait_for_player` (long-poll: the player's messages and verdicts), `say`, `status`,
`start_scenario` / `reset_scenario`, `set_param` (scenario knobs), `list_tunables` / `set_tunable`
(`[Tunable]` statics, `Core/TunableAttribute`), `node_get` / `node_set` (any Godot property of any
node; `player`, `scenario:N`), `run_command` (chat commands), `screenshot`, `log_tail`, and
`restart(rebuild)`: saves the scenario and chat to `user://playtest_resume.json`, quits, and
`tools/playtest.sh relaunch` rebuilds (`test_output/playtest/build.log`) and starts the game again
with `--playtest-resume`, which reopens both. Running C# sent over the wire (Roslyn, Harmony
hot-patching) is not built: it needs the user's explicit go-ahead, being remote code execution.

## Write a scenario

A static method returning `PlaytestScenario`, tagged `[PlaytestScenario("id", "Title", "Category")]`
(found by reflection, as `[Showcase]`; `src/Playtest/Scenarios/`):

- `Setup` builds it with `PlaytestContext`: `Drive(kind, at, yaw)`, `Place(kind, at, yaw, kmh)`,
  `PutPlayer`, `Own(node)`, `At(ahead, right)` (relative to the stage: where the session started),
  `Course(x, y)` (fixture course coordinates), `Param`. Everything placed is removed by the next
  scenario; a vehicle the player drove off in goes too.
- `Covers`: repo-relative globs of the code the verdict is about (`!` excludes; `.uid` and
  `CLAUDE.md` never count). Name what the player judges, not everything on the way: covering
  `FootPlayer.cs` makes the verdict stale on every player change.
- `Course` when it needs one (`junction`, `hairpin`...), `Params` for knobs Claude may turn.
- A feature PR whose look or feel no check can judge adds one (category `PR #N`).

## Ledger

`tests/playtests/<id>.json`, one file per scenario (no merge conflicts between people): verdict,
who (`git config user.name`), when, commit, the covered files' hash (SHA-256, CRLF folded) and every
earlier note. Status: no file = pending; hash differs = stale; else the verdict. Commit the files
with the work they judged.

## Checks

`--playtestcheck` (quick, no world): ids, every cover glob matches a file, MCP over real HTTP
(Origin/Host refusal), ledger round-trip. `--playtest --playtest-smoke --world fixture --chunks
fixture:flat` sets every flat-course scenario up in turn. Tier 0: `PlaytestTests` (protocol,
transport, globs, staleness). The smoke run found `FootPlayer.DeckPhysics` reading a freed bus's
deck (fixed: skip a deck whose host is gone until the next refresh).
