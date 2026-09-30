# Audio (`src/Audio/`)

All audio is synthesised at startup; the only audio files are the CDs players burn (`Cd/`).

Index only: one line per note in `docs/notes/audio/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/audio`.

## Architecture

- `audio` — Audio: (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus that `SfxBus.Ensure()`...
- `cd-beat` — CDs (`Audio/Cd/`): burnt on the server from a YouTube link (yt-dlp + ffmpeg, worker thread), C# beat/style analyser, `--beatcheck`, streamed as `AssetKind.Cd`
