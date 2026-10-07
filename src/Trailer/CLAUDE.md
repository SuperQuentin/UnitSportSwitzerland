# Trailer (`src/Trailer/`)

The game's showcase trailer (#706), staged and filmed in the game: the storyboard is
`docs/trailer/storyboard.md`, the shots are `TrailerScript.cs`, `tools/trailer.sh` previews, frames and renders it.

Index only: one line per note in `docs/notes/trailer/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/trailer`.

- `director` — `--trailer` shots in order (stage, place, pre-roll, roll), actors as NPC `FootPlayer`s (autopilot, follow, scripted ride/flight/walk, boats boarded on the shore), keyed camera (world/actor/direction points, cubic, lens, damping), 1920x1080 at any window size, frame-exact MP4s under `--fixed-fps`, `--trailer-stills`, `--trailer-log` and route dumps for framing, the song
