# GPX replay, cinema and video export (`src/Gpx/`)

Ghost racing, road matching, the cinema director, lens, zoom bubble, sightline cut and the video exporter.

Index only: one line per note in `docs/notes/gpx/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/gpx`.

## Architecture

- `camera-sightline-cut` — Camera sightline cut: (`ChunkManager.SetSightlineCut`, driven per frame by `PlaybackCamera.UpdateSightlineCut`):...
- `zoom-bubble` — Zoom bubble: (`ZoomBubble`, driven from `PlaybackCamera.Step`): whenever the ACTIVE camera is far enough from the...
- `gpx-ghost-racing` — GPX ghost racing: (`src/Gpx/`): `GpxParser` -> `GpxTrack` (LV95 via `SwissProjection`, cumulative time + distance)....
- `absolute-racing` — Absolute Racing: (`CameraMode.Racing`, after Cinema in the C cycle; `--racingmode`; #22, #30): the Absolute Cinema...

## Commands

- `commands` — Commands: --bubble, --cinemamode, --cinemastats, --forceshot, --gpx, --lens, --path, --snap, --speed

## Gotchas

- `visibility-test-cuts-away-defeats` — A visibility test that CUTS AWAY defeats a dissolve that was built to avoid cutting away
- `camera-placed-relative-raw-terrain` — A camera placed relative to raw TERRAIN can end up under the ROAD the runner is actually on
- `two-systems-computing-how-high` — Two systems computing "how high is this road" independently will not agree to the centimetre, and a lift sized for...
- `gpx-recording-s-activity-comes` — A GPX recording's activity comes from the file, not an assumption
- `state-derived-from-course-re` — State derived from the course must be re-derived on EVERY path that changes it
- `cinema-shot-length-was-never` — Cinema shot LENGTH was never the reason it cut too fast at high speed
- `video-exporter-fed-camera-track` — The video exporter fed the camera TRACK seconds where it wanted SCREEN seconds
- `facing-look-ahead-bounded-distance` — A facing look-ahead must be bounded by DISTANCE, not just time
- `raw-gpx-motion-looks-like` — Raw GPX motion looks like a boat: Three separate causes, all handled: positions are smoothed at parse over a...
- `gps-speed-averaged-over-window` — GPS speed must be averaged over a window, never one segment
- `hand-built-basis-checked-handedness` — A hand-built basis must be checked for HANDEDNESS, not just direction
- `xmlreader-readelementcontentasstring-already-advances-reader` — `XmlReader.ReadElementContentAsString()` already advances the reader
- `get-image-root-viewport-from` — `get_image()` on the root viewport from `_Process` returns whatever the render thread last left there
- `map-matched-track-needs-displacement` — A map-matched track needs its DISPLACEMENT rate-limited, not its position
- `roadnetwork-snap-endpoints-rejoin-tile` — `RoadNetwork` must snap endpoints to rejoin tile-clipped roads
- `road-file-road-file` — A `.road` file is not a road file: It also carries cable cars, rivers, avalanche barriers and dry-stone walls....
