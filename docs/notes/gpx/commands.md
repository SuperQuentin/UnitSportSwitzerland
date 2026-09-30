# Commands

- Replay verification flags (all alongside `--gpx <track>`): `--snap` road matching, `--cinemamode`
  Absolute Cinema, `--speed <n>` the playback multiplier, `--lens <n>` a lens profile by index,
  `--path <0..100>` course-line opacity, `--forceshot <name>` pins Absolute Cinema to one named
  shot (matches the HUD's override list, e.g. `"Ankle cam"` — quote it, names have spaces),
  `--bubble off` disables the zoom bubble, and `--cinemastats <screenSeconds>` which runs the
  director for that much SCREEN time and prints
  cuts, rejections and seconds-per-shot, then quits. The last two are how the pacing claim is
  actually checked: "a scene is as long at 32x as at 1x" is a number, and eyeballing cannot tell a
  director cutting twice too often from one cutting twenty times too often — and a forced shot
  held for the whole window (1 cut, not a fresh one every few seconds) is how the manual override
  itself is checked, the same way. Example:
  `<godot> --path . -- --gpx seb.gpx --cinemamode --speed 32 --cinemastats 40`
