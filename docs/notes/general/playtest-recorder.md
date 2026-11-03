# Playtest recorder: a whole session on disk, for Claude to read afterwards (#844)

`--playtest-record [dir]` in a Debug build (`src/Playtest/Recording/PlaytestRecorder`), on a client
(offline or online) or a dedicated server. `tools/playtest.sh batch` turns it on with the scenarios.

## The bundle

`test_output/playtest/sessions/<yyyyMMdd_HHmmss>/` (gitignored), a server's under `.../server/`
unless `[dir]` is given (relative to the repo):

| File | What |
|---|---|
| `report.md` | Written at End session (or `tools/playtest.sh report [dir]` after a crash): scenarios and verdicts, each note with its pictures, the events and errors ±10 s around it and the `--topview` line to look again, errors grouped, the perf diagnosis, the movie's name. **Read it first.** |
| `trace.bin` + `trace.idx` | 10 Hz frames (`Trace.cs`): every player (LV95 E/N/alt, heading, velocity, ride kind, seat, held item, whose vehicle, the ride's footprint) and every `ReplayWatch` object within 400 m (across) of a player or the camera (pose, heading, velocity from the last sample, spin). The index is one Int64 a second: a window is read without the rest. |
| `entities.jsonl` | One line per traced thing, when first seen: id, kind (`player`, a vehicle's label, `bird`, the manager's class), node name and path, size (meshes, else collision shapes, measured once). |
| `events.jsonl` | `session` (start, with the ground it ran on for the top view; relaunch; stop), `scenario` (start, verdict, setup failed, free play), `note`, `chat`, `ride`, `prop` (any other `ActorIo` change-only property), `teleport` (> 60 m in a tick), `join`/`leave` (a player in or out of view), `movie`, and **every log line** (`level`, `tag` from `[tag]`, `text`). Grep it. |
| `notes.jsonl`, `notes/NNN.png`, `notes/NNN_top.png` | A note: text, source (key, chat, fail, panel, mcp, check), scenario, where the local player was when the key was pressed, the screenshot (none headless) and the top view of 10 s either side (drawn once those 10 s have passed). |
| `perf/` (`perf-2/`... after a relaunch) | The PerfRecorder's output (`frames.csv`, `summary.txt`...), started into the bundle unless F4 already records. |

The movie studio's recording of the same session is saved as **"Playtest &lt;session&gt;"** in
`user://movies` (the studio opens only from there), named in the report; `--playtest-nomovie`
skips it. Closing the window loses it (its spool is joined on a worker after the game quits): End
session saves it. A server records no movie and no perf.

## Times and relaunches

Every time is **wall-clock seconds since the session began** (`TraceHeader.StartUnixMs`), not game
time. A relaunch (`restart`, another fixture course) passes `--playtest-record <same dir>`: the
trace, events and notes are appended to, so one session spans the relaunches. A server's trace and
a client's line up on the wall clock (`--topview` shifts one onto the other).

## Notes

F12 (`NoteTaker`): the screenshot is the frame before the box draws, and the time is the key
press, not the Enter. `/note <text>` in the chat, the Fail button's text, the panel's Note button
(the box's text, or none: a pad marks the moment), and Claude's MCP `note` do the same.

## What the panel shows (#1383)

Under the panel's Note / Top view / Free play / End session row, and on the closed card, a line
with the recording's state: a red dot and "Recording · 5 notes", "Saving the session…" while End
session closes the files (the movie first), then "Not recording: session ended". Every saved note,
whichever way it came, adds "✔ Note 5 saved" on that line for 4 s (`PlaytestRecorder.Changed`
fires with its id; 0 when the recording stops); a note asked for when nothing records says "Not
recording: the note was not saved" in red. Once ended the panel lists what was saved, the report's
path (`test_output/playtest/sessions/<time>/report.md`), that the game keeps running unrecorded and
that Claude reads the report next; Note and End session go grey (a pad's focus moves to Top view).
The same panel serves the pad (L3+R3) and VR (the XrUi screen, XrPad), so all three see it.

## Cost and size

Reads only, at 10 Hz; one frame copy handed to the worker, which writes and flushes every second.
About 1 MB a minute on the flat course with 35 birds and a car; 400 objects at most a frame. A
sound, light or particle node in a container is not a thing; neither is a car park's instancer.
Not recorded: contacts (vehicles are `CharacterBody3D`, no `body_entered`: read them off the
boxes in the top view), what is outside 400 m. The terrain is not copied: the top view reads it
again from the tiles the session names (`topview`).

## Checks

`--playtest --playtest-record test_output/playtest/recordcheck --playtest-recordcheck --playtest-nomovie --world fixture --chunks fixture:flat`
(quick tier): a 20 s session with a scenario's car and a scripted note, read back, ended through
the director's End session; the panel's line must confirm the note and the end with the report's
path. Windowed it also checks the screenshot and saves `test_output/playtest/recordcheck_overlay.png`
(the live top view) and `recordcheck_{note,ended}{,_card}.png` (the panel open and its card).
`tools/playtestnetcheck.sh` (net tier): a recording server and two recording clients; B must have
drawn A within a few metres of where the server had it (median 0.3 m at 8 m/s on loopback).
Tier 0: `PlaytestTraceTests` (trace, index, relaunch append, cut frame, ring, top view, report).
