# Batch playtest: record everything, judge many scenarios, fix offline, replay

Issue #844. Status: built on `feat/844-playtest-recorder` (PR #847); the real batch run with a
player (verification 5) is still to do.

**Built differently from the plan below:**
- Times are wall-clock seconds since the session began, not game time: a relaunch appends to the
  same bundle, and a server's trace lines up with a client's on one machine.
- The movie is saved in `user://movies` as "Playtest <session>" (the studio opens only from there)
  and named in the report, not copied into the bundle; closing the window loses it, End session saves it.
- No contact events: vehicles are `CharacterBody3D` (no `body_entered`); the top view's boxes show
  the contacts instead.
- The offline PNG draws its labels with a 5×7 pixel font (ThorVG draws no SVG `<text>` headless).
- Players carry their ride's footprint in the trace (a driver is drawn over the box of what it drives);
  birds are a faint "minor" kind; sounds, lights and particles in a container are not traced.
- Extras: `tools/playtest.sh last|report|topview`, `--playtest-report <dir>`, an MCP `note` tool,
  the panel's Note / Top view buttons (the pad and VR way), `tools/playtestnetcheck.sh`.
- Not built: the terrain or roads under the top view (`--ground`).

## Context

Today's playtest (`src/Playtest`, `/playtest` skill, #751) is a **live loop**: the player plays one
scenario, Claude waits on `wait_for_player`, changes something, restarts, and the player retries.
Each round trip costs minutes, so a session covers very few things.

The new flow works in batches:
1. **Play.** The player goes through many scenarios (or plays freely) and drops typed notes with a
   hotkey whenever something looks wrong. Claude is not in the loop.
2. **Record.** Everything is recorded: players, world objects, events, log and perf, plus a 2D
   top view.
3. **Fix.** Claude reads the session bundle, reproduces each finding with small headless
   tests/probes, and fixes them.
4. **Replay.** The player runs a second playtest on only the failed or changed scenarios.

**The top view.** A 2D top-down render, both live in game and offline from a recording, helps the
player and Claude understand spatial bugs: collisions, paths, replication offsets.

**Reuse.** The movie studio's recorder (`src/Movie`) already records players at 30 Hz and every
watched world object. We reuse it rather than writing a new one.

## What gets built

### 1. `PlaytestRecorder`: a detailed debug recording (new, `src/Playtest/Recording/`)

Debug-only (`PLAYTEST` symbol), like the rest of `src/Playtest`. It starts with the playtest session
and can also be started with `--playtest-record` in any client or server run. One bundle per session
in `test_output/playtest/sessions/<yyyyMMdd_HHmmss>/` (gitignored, local).

| File | Content | Built from |
|---|---|---|
| `trace.bin` + `trace.idx` | 10 Hz frames. Every player: LV95 E/N/alt, velocity, yaw, ride kind, seat, held item, change-only props. Every watched world object: stable id, kind + node name/path, LV95 pose, yaw, velocity, bounding box size. | `PlayerSnapshot.Of` + `ActorIo.Read` for players; `ReplayWatch` containers/objects as the registry. |
| `events.jsonl` | Timestamped semantic events: scenario start/reset/verdict, notes, chat lines, mode changes, ride enter/exit, teleports, contacts, peer join/leave, and every log line tagged `[xxx]`. | `PlaytestDirector` events, `ChatManager`, `ActorIo` change-only diffs, a logger. |
| `perf/` | The full PerfRecorder output. | `PerfRecorder` with its output dir set to the bundle. |
| `notes/NNN.png` + `notes.jsonl` | One entry per note: text, time, LV95 position, current scenario, a screenshot, and a top-view PNG of ±10 s. | Hotkey (below). |
| `session.usmovie` | The movie-studio recording of the same session. | `ReplayRecorder.StartSession/StopSession`. |
| `report.md` | Scenarios with verdicts and notes, notes in time order with their PNGs, perf diagnosis, error/warning count. **What Claude reads first.** | All of the above. |

Players and objects in a compact binary trace with a per-second index; events and notes in JSONL;
2–4 MB/min at 50 objects, flushed every second so a crash keeps the session. The writer runs on a
worker thread (`Task.ContinueWith` chain, as `SessionSpool`).

**Server side.** With `--server --playtest-record`, the dedicated server writes its own trace into
`.../sessions/<ts>/server/`, so the top view can draw **server truth vs what the client saw**.

### 2. Notes hotkey: F12

F12 opens a one-line "What's wrong?" box (nothing freezes); Enter stamps the note with time,
position and scenario, a screenshot taken before the box draws, and the top view of the last 10 s
plus the next 10 s. Also from the Fail button, and `/note <text>` in chat.

### 3. Top view: one renderer, two outputs (`src/Playtest/TopView/`)

- `TopViewScene`: a pure model (Tier 0): tracks with 1 s ticks, oriented boxes, player arrows,
  velocity vectors, note pins, server-vs-client offset lines, scale bar, north arrow.
- Live overlay (Shift+F12): centred on the player, 25–400 m zoom; Ctrl+F12 pauses and scrubs the
  last 60 s from the recorder's ring.
- Offline: `--topview <session-dir> --from T --to T [--around E,N --radius m] --out x.png`, headless:
  SVG → `Image.LoadSvgFromString` → PNG; `--html` writes a page with a time slider.

### 4. Batch mode in the playtest suite

Validate/Fail no longer wait for Claude; Fail asks for a note and moves on. "Free play" and "End
session" in the panel. `tools/playtest.sh batch` (default), `retest` (failed, stale, pending only),
`live`. The ledger is unchanged. MCP: `session_report` and `topview`.

### 5. Skill and notes

The `/playtest` skill rewritten around the batch loop (read `report.md`, look at the notes'
pictures and top views, reproduce headless, fix, list what to retest); notes `playtest`,
`playtest-recorder`, `topview`.

## Verification

1. Tier 0: trace round-trip, index seek, `TopViewScene` for a known track.
2. A 20 s recorded smoke on `fixture:flat`: a bundle with a trace, report.md and a scripted note
   with its two PNGs.
3. `--topview` on it: the car box and the player track where the scenario put them.
4. A dedicated server and a client recording on loopback: both traces have the remote player, and
   the top view draws the server-vs-client offset.
5. A real batch run with the user: 3–5 scenarios with notes, Claude fixes from the bundle, the user
   runs `retest`.

## Open risks

- What each object is: `ReplayWatch` registers containers; a child is labelled by class and node
  name, with better labels where cheap.
- Recorder cost: well under 1 ms/frame (10 Hz, reads only, writes on a worker).
