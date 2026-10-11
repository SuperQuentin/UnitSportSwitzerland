# Movie studio (`src/Movie/`)

The replay buffer, the movie studio's timeline and puppets (#638), and later its cameras and performance capture (#637).

Index only: one line per note in `docs/notes/movie/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/movie`.

## Architecture

- `movie-studio` — replay buffer (what is recorded, the 30 Hz rings), grabs onto lanes, the studio over the live world, clip edits, `.usmovie` files, controls
- `movie-sound` — songs (import, Godot decode, beat detection), game sound from a Master-bus capture, `AudioDeck` playback, ghost beats and kept markers, snapping, file v2 (#656)
- `movie-camera` — camera track (smooth/linear/cut keys, lens, aim at an actor, v3), studio view orbit/free/track, timeline zoom, cut all and the blade (#669)
- `movie-shots` — shots (#776): rigs World/Lead/Chase/Mount and their frames, the follow spring as a pure function of time, shake, beat links on the movie's beats, sequences, transitions, file v6
- `movie-shots-screen` — the Shots screen (#777): mode tabs, library rows with stills and a hover picture (`ShotThumbs`), the inspector (rig, follows, offsets, lens, shake, beat links), Frame from view (G), N
- `movie-sequences` — the Sequences screen (#778): sequence tabs and their menu, shots dragged onto the Program row, the transition menu and how transitions are drawn, crossfade/wipe as two pictures, `AutoDirector`
- `movie-world` — the recorded world (#775): `ReplayWatch` one-line joins, groups and parts written only when they move, `WorldRing`/`WorldTrack`, the World row, `WorldGhosts`, the live world hidden, looks saved in `user://movies/looks/`
- `movie-puppets` — `FootPlayer.Puppet`: no synchronizers, no interpolator, hiding with ProcessMode, `RidingWith` remapped to puppets

## Commands

- `commands` — `--moviecheck --world flat`, `--moviestudio <s> [t] [--moviesong f] [--moviekeys]` for screenshots, `MovieTests`, `MovieAudioTests`, `MovieCameraTests`
