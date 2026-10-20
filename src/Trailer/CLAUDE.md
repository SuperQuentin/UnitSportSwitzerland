# Trailer (`src/Trailer/`)

The game's trailers (#706), staged and filmed in the game: the story "Meet You at the Top"
(`docs/trailer/script.md`, `StoryScript.cs`, the default) and the showcase (`docs/trailer/storyboard.md`,
`TrailerScript.cs`), and the Drognens music clip (`docs/trailer/drognens.md`, `DrognensScript.cs`,
`DormSet.cs`), `BubbleDemoScript.cs` (`FILM=bubbles`, a test film of the speech bubbles) and `UfoDemoScript.cs` (`FILM=ufo`, the film kit's demo; `FILM=doors`); `tools/trailer.sh` previews, frames and renders them (`FILM=story|showcase|drognens|bubbles|ufo|doors`).

Index only: one line per note in `docs/notes/trailer/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/trailer`.

- `director` — three films (`--trailer-film`), a song per film, film sets (the dormitory), seated props, moonlight, `--trailer` shots in order (stage, place, pre-roll, roll), actors as NPC `FootPlayer`s (autopilot, follow, scripted ride/flight/walk, boats boarded on the shore, characters, out of a door, aboard a machine), keyed camera (world/actor/road/direction points, cubic, lens, damping), 1920x1080 at any window size, frame-exact MP4s under `--fixed-fps`, every render a version folder (`v1`, `v2`, ... in the main checkout), chat, speech bubbles (`Shot.Bubbles`, #862), supers and the photo on top, subtitle cues per shot and `<film>.<lang>.srt` tracks muxed by the cut (`docs/trailer/<film>.subtitles.json`, `tools/trailer.sh lines`), the cut checked at start, `--trailer-stills`, `--trailer-scout` overhead stills, `--trailer-log` and route dumps for framing, the song, real terrain only (`missing_tiles.txt`, `--trailer-allow-generated`), staging gotchas
- `render-pins` — reproducible renders (#924): the render profile (fresh `user://`, `RENDER` settings, `PINS=0`), the film's date (`FilmDate`, `Core.Calendar`), seeds per shot (`RunSeeds`: birds, traffic, creatures, particles, herds, crash coin, beam bob, ids), the go on a frame, the terrain fingerprint, `version.txt` pins and `shotNN.pins.json`, `--trailer-tracks` and `tools/trailer-drift.py`, the drift measured and what still drifts
- `film-kit` — the UFO films' additions (#882): Cast `Actions`/`Fight`/`Headwear`/`Lowered`/`Radio`/`Swim`/`Ufo`/`DownRamp`, Prop `Placed`/`Piece`/`Vehicle`/`Beamable`, Shot `Occasion`/`Zone`/`ItemCues`/`Screens`/`Craft` (crashed, landed, mothership; real beams), the porting table, `FILM=ufo` demo
