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
  128-colour palette per clip (`palettegen stats_mode=diff`, bayer dither 5): 0.2-0.6 MB/s of the PS1 look
  (640 px at 15 fps was twice that, 1.1 MB/s inside the freighter's hold); keep release GIFs under ~10 s. Plus
  `<name>.mp4` (960 px, 30 fps, x264 crf 20) when the ffmpeg has libx264 (the chocolatey one does; the release's
  LGPL `bin/ffmpeg` does not, so an export writes the GIF only). Drag the MP4 or the GIF into a PR description.
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
