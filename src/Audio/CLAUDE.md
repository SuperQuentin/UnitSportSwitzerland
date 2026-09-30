# Audio (`src/Audio/`)

All audio is synthesised at startup; there are no audio files.

Index only: one line per note in `docs/notes/audio/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/audio`.

## Architecture

- `audio` — Audio: (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus that `SfxBus.Ensure()`...
