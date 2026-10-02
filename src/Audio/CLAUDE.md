# Audio (`src/Audio/`)

All audio is synthesised at startup; the only audio files are the CDs players burn (`Cd/`), plus live web radio relayed by the server (`Live/`).

Index only: one line per note in `docs/notes/audio/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/audio`.

## Architecture

- `audio` — Audio: (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus that `SfxBus.Ensure()`...
- `cd-beat` — CDs (`Audio/Cd/`): burnt on the server from a YouTube link (yt-dlp + ffmpeg, worker thread), C# beat/style analyser, `--beatcheck`, streamed as `AssetKind.Cd`; personal CDs (negative ids, burnt on the client, `user://cds/personal`)
- `web-radio` — Car radio (`Audio/Live/`, #179): 14 live stations, U / P, `FootPlayer.CarRadio` + `VehicleState.Radio` (a CD instead: `CarCd`, #211), the server taps each stream with ffmpeg and relays µ-law on the shared clock so every client plays the same sample; `tools/webradiocheck.sh`
- `perf-engine-synth-idle` — `EngineSynth` stops its player once silent for 0.2 s and plays again when the level rises; push samples through the reused `_push` span, never a new array per frame
