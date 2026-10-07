# Trailer (`src/Trailer/`)

The game's trailers (#706), staged and filmed in the game: the story "Meet You at the Top"
(`docs/trailer/script.md`, `StoryScript.cs`, the default) and the showcase (`docs/trailer/storyboard.md`,
`TrailerScript.cs`); `tools/trailer.sh` previews, frames and renders them (`FILM=story|showcase`).

Index only: one line per note in `docs/notes/trailer/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/trailer`.

- `director` — two films (`--trailer-film`), `--trailer` shots in order (stage, place, pre-roll, roll), actors as NPC `FootPlayer`s (autopilot, follow, scripted ride/flight/walk, boats boarded on the shore, characters, out of a door, aboard a machine), keyed camera (world/actor/road/direction points, cubic, lens, damping), 1920x1080 at any window size, frame-exact MP4s under `--fixed-fps`, every render a version folder (`v1`, `v2`, ... in the main checkout), chat, supers and the photo on top, the cut checked at start, `--trailer-stills`, `--trailer-scout` overhead stills, `--trailer-log` and route dumps for framing, the song, staging gotchas
