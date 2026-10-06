# Movie studio (`src/Movie/`)

The replay buffer, the movie studio's timeline and puppets (#638), and later its cameras and performance capture (#637).

Index only: one line per note in `docs/notes/movie/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/movie`.

## Architecture

- `movie-studio` — replay buffer (what is recorded, the 30 Hz rings), grabs onto lanes, the studio over the live world, clip edits, `.usmovie` files, controls
- `movie-sound` — songs (import, Godot decode, beat detection), game sound from a Master-bus capture, `AudioDeck` playback, ghost beats and kept markers, snapping, file v2 (#656)
- `movie-camera` — camera track (smooth/linear/cut keys, lens, aim at an actor, v3), studio view orbit/free/track, timeline zoom, cut all and the blade (#669)
- `movie-puppets` — `FootPlayer.Puppet`: no synchronizers, no interpolator, hiding with ProcessMode, `RidingWith` remapped to puppets

## Commands

- `commands` — `--moviecheck --world flat`, `--moviestudio <s> [t] [--moviesong f] [--moviekeys]` for screenshots, `MovieTests`, `MovieAudioTests`, `MovieCameraTests`
