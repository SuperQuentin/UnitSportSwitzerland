# Feature clips: GIFs of new features, shown in releases (#487)

- **Record by hand** (any run, offline or online, client only): `/clip start <name> [seconds] [delay]` in the
  chat (default 10 s, at most 60, after 2 s to close the chat), `/clip stop` to end early, `/clip` for status.
  The chat answers it locally like `/style`; a server never hears of it. No key, so no pad or VR way to decide
  (a dev tool, not a player action).
- **Record a scripted run**: `--clip name[,delay[,seconds]]` starts filming `delay` s after boot; `--clip-quit`
  exits once the files are written (code 1 if not). Pair it with a probe that plays the feature
  (`--stairscheck`, `--freightercheck car`, `--trafficcheck`...) or a `--shot-queue` (its `/` lines drive the
  chat). One launch: `tools/record-clip.sh <name> <delay> <seconds> [game flags...]`; read the probe's own log
  in `test_output/clips/<name>.log` to time `delay` to the moment worth showing. A probe that quits first still
  leaves whole files (ffmpeg finishes them when the pipe closes), only shorter. Easiest: film the whole probe once
  (`delay` 3, `seconds` 60), look at the MP4, then `tools/record-clip.sh trim <mp4> <start> <seconds> <name>`.
- **Output**: `test_output/clips/<name>.gif` from source (`user://clips` in an export): 560 px wide, 12 fps,
  128-colour palette per clip (`palettegen stats_mode=diff`), bayer dither scale 5 in PS1 and 3 in the lit styles
  (#1070: at 5, Cartoon's sky gradient and the haze round the sun banded into rings; 3 is smooth for ~13% more
  bytes, 256 colours at 5 still banded; `trim` uses 3, `BAYER=5` for a PS1 clip): 0.2-0.6 MB/s of the PS1 look
  (640 px at 15 fps was twice that, 1.1 MB/s inside the freighter's hold); keep release GIFs under ~10 s. Plus
  `<name>.mp4` (960 px, 30 fps, x264 crf 20) when the ffmpeg has libx264 (the chocolatey one does; the release's
  LGPL `bin/ffmpeg` does not, so an export writes the GIF only), else libopenh264 at 6 Mb/s (#1105: Krita's ffmpeg,
  `C:/Program Files/Krita (x64)/bin` put on PATH, where no chocolatey one is installed). Drag the MP4 or the GIF into a PR
  description.
- **`--clip-hq`** (#1306, a showcase film): the frames at the window's size (`--resolution 1920x1080`), x264 CRF 16 slow (or
  openh264 at 24 Mb/s); with `--fixed-fps 30` the game waits for the encoder instead of dropping a frame, so every frame of
  the shot queue's glides is in the film, however slowly it renders. Winget's full ffmpeg (`Gyan.FFmpeg`, in
  `%LOCALAPPDATA%/Microsoft/WinGet/Links`) has libx264 and https; Krita's has neither.
- **`--clip-realtime`** (#1105): a slow frame fills every 1/30 s slot it took (the picture repeated) instead of one, so a
  heavy scene (a busy junction at 10-27 fps) plays in the time it filmed; without it such a clip played up to 3x fast. E.g.
  two cycles of the signal test region's J14: `SIZE=1280x720 tools/record-clip.sh j14-two-cycles 50 250 --clip-realtime
  --chunks <region> --generated off --origin 2917500,1323500 --at 2917500,1323500 --trafficcheck --seconds 600 --shot-queue
  <file with 34,g20,27,-28,50,900,x.png> --nohud --time 13 --style cartoon --traffic 150 --walkers 100 --bikes 50 --trams 6`
  (`--trafficcheck --at` keeps the traffic and its feeders there; the shot queue's line holds the camera).
- **Camera moves**: a `--shot-queue` of glides (`x,g20,z,pitch,yaw,seconds,~ease`, `core/commands`) flies the camera
  while it films; one still shot first settles the tiles. `[clip] t=` and each `[glide] t=` log `RealClock.Now` in ms, so a glide's
  place in the MP4 is its time less the clip's (with `--clip-realtime`). A flythrough of the signal test region, cut from
  five such takes with crossfades and caption PNGs (Krita's ffmpeg has no text filter), fit 2:39 at 960x540 in 7.9 MB
  (libopenh264 395 kb/s).
- **How**: `Core/ClipRecorder` reads the last drawn frame back every 1/30 s, scales it and pipes raw RGB to
  ffmpeg (`BundledTools.Resolve`), which encodes both on its own core; frames are dropped, never queued without
  bound, when ffmpeg lags 2 s behind. Real time, unlike `Gpx/VideoExporter` (fixed steps, waits for tiles):
  what you see is what is filmed, HUD included (`--nohud` hides it only for `--shot`). Nothing is drawn headless.
- **Into a release**: `RELEASE=1 CAPTION="..." tools/record-clip.sh ...` (or copy a GIF by hand) stages it in
  `test_output/release/clips/` of the main checkout; `tools/release.sh` uploads every GIF there as a release
  asset and shows them first, under `### Highlights`, captioned by `<name>.txt` beside it (else the name,
  dashes as spaces), then moves them to `clips/released/vX.Y.Z/`. `--dry-run` prints the notes with them.
  Asset GIFs render inline in the release page (GitHub's image proxy follows the download redirect). Name clips
  without spaces: `gh` renames those assets and the link breaks.
