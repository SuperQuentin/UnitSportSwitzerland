# Playtest suite: scenarios a person judges, in batches or with Claude live (#751, #844)

Some things only a person playing can judge: how a crash looks, how a bus turns, whether a dance
loops cleanly. The playtest suite runs such **scenarios** one after another in **one** game and
records each **verdict** in a committed ledger that knows when the verdict has gone stale. Two ways
to use it:

- **Batch** (default, #844): the player goes through many scenarios (or free play) alone, noting
  what is wrong with F12; the whole session is recorded (`playtest-recorder`); Claude reads the
  bundle afterwards, reproduces and fixes offline; the player then retests only what is due.
- **Live** (#751): the player talks to a Claude Code session that changes the running game
  (one-off tuning: a knob, a tunable, a node property).

## Run it

```
tools/playtest.sh [batch] [course]   # builds, plays fixture:<course> (flat) with --playtest --playtest-batch --playtest-record
tools/playtest.sh retest [course]    # the same with only the scenarios due (failed, stale, never played)
tools/playtest.sh live [course]      # the live loop: then `claude` in this checkout, /mcp lists unitsport-playtest, /playtest
tools/playtest.sh pending            # what is due: never played, or the covered code changed since
tools/playtest.sh last | report [dir] | topview [dir] ...   # the newest session; its report again; its top view
```

### The batch loop

1. **Play**: Validate or Fail each scenario (Fail's text, or the next line, is a note); the next
   one due starts by itself. F12 notes anything, anywhere ("What's wrong?", stamped with the time,
   the place, a screenshot and a top view). Free play records without a scenario. **End session**
   (panel) writes `test_output/playtest/sessions/<time>/report.md`.
2. **Fix**: in Claude Code, "read the report and fix what I noted" (`/playtest`, the skill's batch
   section): it reads the report, the notes' pictures, `--topview` windows, `events.jsonl`, writes a
   headless repro when it can, fixes, and lists what to retest.
3. **Retest**: `tools/playtest.sh retest`. A fix changes the covered files' hash, so its scenario
   turns stale and is due again; a FAILED one stays due until validated.

- **One PR's scenarios, on real terrain** (#1292): `--playtest-only <id prefix>[,...]` keeps only the scenarios
  whose id starts with one of them; with `--at E,N` and `--chunks <dir>` the run loads through the loading
  screen like any play launch: `<godot> --path . -- --chunks D:/.../terrain_chunks --at 2499794.3,1117466.5
  --playtest --playtest-batch --playtest-record --playtest-only pr1327`.
- Debug builds only: `src/Playtest/**` and the `PLAYTEST` symbol exist only in the `Debug`
  configuration (the csproj), so exports never carry the server. It starts only with
  `--playtest`, offline, on `127.0.0.1:7801` (`--playtest-port N` otherwise; `.mcp.json` names 7801).
- **Panel**: F10, a pad's L3 + R3 together, VR the same through XrPad (no typing in VR yet). The
  panel holds the scenario list (✔ validated, ✖ failed, ↻ stale, ● pending), the instructions and
  checklist, Validate / Fail / Reset, and the chat with Claude. Closed, a card top right keeps the
  checklist and Claude's last line. Fail takes the text in the box as the reason. Recorded, a row of
  Note / Top view / Free play / End session (a pad's way to note and see the top view); in batch
  mode the chat is hidden and the box takes notes.
- **Keys** (recorded): F12 note, Shift+F12 top view, Ctrl+F12 stop and scrub it (`topview`).
- Scenarios needing another fixture course relaunch the game on it when picked (same panel, same chat).

## What Claude can do (MCP tools, `PlaytestDirector.Tools`)

`session_report`, `topview` and `note` when the session is recorded (#844);
`wait_for_player` (long-poll: the player's messages and verdicts), `say`, `status`,
`start_scenario` / `reset_scenario`, `set_param` (scenario knobs), `list_tunables` / `set_tunable`
(`[Tunable]` statics, `Core/TunableAttribute`), `node_get` / `node_set` (any Godot property of any
node; `player`, `scenario:N`), `run_command` (chat commands), `screenshot`, `log_tail`, and
`restart(rebuild)`: saves the scenario and chat to `user://playtest_resume.json`, quits, and
`tools/playtest.sh relaunch` rebuilds (`test_output/playtest/build.log`) and starts the game again
with `--playtest-resume`, which reopens both (and `--playtest-record` the same bundle). Running C# sent over the wire (Roslyn, Harmony
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
