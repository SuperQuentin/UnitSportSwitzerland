# Audio (`src/Audio/`)

All audio is synthesised at startup; the only audio files are the CDs players burn (`Cd/`), plus live web radio relayed by the server (`Live/`).

Index only: one line per note in `docs/notes/audio/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/audio`.

## Architecture

- `audio` — Audio: (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus that `SfxBus.Ensure()`...
- `ears` — The listener is `Audio/Ears` at the body's head (`FootPlayer.EarFrame`), never the camera (#375); ask `Ears.Of/FrameOf` for the ear; `Ears.Cabin`/`Shut` for a closed vehicle
- `mixing` — Bus tree (Master limiter; Sfx world + cabin filter; Player own body; Music), level targets, reverb values per space, footstep structure, sources (#375)
- `cd-beat` — CDs (`Audio/Cd/`): burnt on the server from a YouTube link (yt-dlp + ffmpeg, worker thread), C# beat/style analyser, `--beatcheck`, streamed as `AssetKind.Cd`; personal CDs (negative ids, burnt on the client, `user://cds/personal`)
- `hearing` — Music bus + Settings slider (#261), `Hearing`: speakers muffled behind walls (ray), heard across interiors through linked doorways by the leaf's swing, or through the walls
- `web-radio` — Car radio (`Audio/Live/`, #179): 14 live stations, U / P, `FootPlayer.CarRadio` + `VehicleState.Radio` (a CD instead: `CarCd`, #211), the server taps each stream with ffmpeg and relays µ-law on the shared clock so every client plays the same sample; `tools/webradiocheck.sh`
- `perf-surface-grid` — `Surfaces.At(..., this)` for a vehicle; road lookups go through the 32 m `RoadIndex`, never a full tile scan; `--surfacecheck` (#221)
- `sound-player` — `--sounds`: every synthesised sound by category with its waveform, found by reflection (static banks/streams) plus `[SoundShowcase]` sets; `--sounds,check` flags clipped/NaN/silent
- `perf-engine-synth-idle` — `EngineSynth` stops its player once silent for 0.2 s and plays again when the level rises; push samples through the reused `_push` span, never a new array per frame
