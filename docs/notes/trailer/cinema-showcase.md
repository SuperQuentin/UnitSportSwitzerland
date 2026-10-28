# The cinema showcase film (`tools/cinema-showcase.py`, #1306)

Not the trailer director: a film of the cinemas (#1238, #1279, #1298) and the other venues at night, made from
shot-queue takes, because the director has no camera inside a real building.

- **Run**: `python tools/cinema-showcase.py film [take ...]` films (all, or the takes named), `cut` cuts, `all` both.
  Takes in `test_output/showcase/takes/`, the film `test_output/showcase/cinema.mp4` (1920x1080, 30 fps). Re-filming a
  few takes keeps the others. ~15 min for every take, a minute to cut.
- **A launch per place**: Geneva (origin on the Grutli's door, 2499892.6, 1117461.7: the cinema, a pizzeria, Flanagan's
  pub, Billard Bluff) and Gland (BowlinGland, origin 2510000, 1140000), `--time 21`, Cartoon, `--detail high` (the
  projector's light and beam), `--fixed-fps 30 --clip-hq` (every frame, `general/feature-clips`).
- **A take**: its setup lines (an `i` line through the building's door, `l` stills that place the camera and wait),
  then `/clip start <take> <s> 0` (held until the take before is written), its `before` chat lines (after the clip has
  started: a hall's lights fade while filmed, not while the queue waits), then its glides. A glide of 0.04 s is a cut
  inside a take. `aim(eye, at)` turns a look-at into pitch and yaw (Godot: -Z forward, pitch up positive).
- **The cut**: each take from 0.4 s (the perf overlay's first frame) to 0.05 s short of its moves (the clip runs a
  little longer: the next take's first pose), 0.5 s crossfades, a title and an end card, lower-third captions (Pillow,
  `SairaCondensed`: Krita's ffmpeg has no text filter), `crusaders.xm` (`--trackerwav`, 92 s) faded in and out. libx264
  CRF 16 where the ffmpeg has it (winget's full build), else openh264 16 Mb/s.
- **Films on the screens** need the video stack (#1298) on main: `/cinema play <link>` in the hall the camera stands in.
  A hall decodes its film only while the camera is in it (the cat slides show for a few seconds): the films takes wait
  there 12-24 s first. Open films: Sintel and its trailer, Big Buck Bunny (Blender, CC BY).
- **Places are the plans of Oct 28 2026** (CinemaRules 14, interiors v38): a planner change moves rooms, and the camera
  lands in a wall. Re-read the plans (`user://interiors/<tile>/<n>.json`: rooms by `RoomType`, pieces by `FurnitureType`).
- **Gotchas found filming**: dining venues in mixed buildings are reached through the building's stair hall, so the
  takes fly straight into the guest rooms (#1292); the cinema's own toilet lobby is too tight to film, the take is
  Billard Bluff's; the beam is faint by design (#1279), seen best from the front rows looking up at the porthole;
  the ffmpeg the game films through is the one on its PATH (the script puts its own first).
