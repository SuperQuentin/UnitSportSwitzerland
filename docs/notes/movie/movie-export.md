# Exporting the movie to a video (#786)

- **Export** (the editor's top bar) opens a menu: the size (1280×720, 1920×1080 by default, 2560×1440, 3840×2160)
  and the frame rate (24, 30 by default, 60), then *Render the sequence*. It renders the **active sequence**, from its in to its out (or the movie's end), to
  `user://movies/export/<movie> <sequence> <date time>/run.mp4`, with `sound.wav` beside it.
- It is the GPX replay's exporter (`Gpx/VideoExporter`): frame-exact steps, each frame waiting for the terrain round
  the camera (`ChunkManager.SettledNear`), raw frames piped to ffmpeg (x264, CRF 18). It films anything behind
  `Gpx.IExportSource` (clock, camera, sound): `RaceExportSource` for a replay (as before), `MovieExportSource` for
  the movie.
- **What is filmed:** the player's own viewport (`IExportSource.Frames`), set to the chosen size whatever the window's;
  the fade and the wipe/crossfade picture are drawn inside it (a `CanvasLayer` in the viewport), so they are in the
  video exactly as the player shows them. The editor shows only the player meanwhile (`EnterExportView`). Gizmos hide;
  the perf overlay hides itself (it runs while the game is paused, for that).
- **Sound:** every sound clip over the sequence (music, game sound) decoded (`AudioDecodeJob`) and mixed
  (`Soundtrack.Mix`, pure: placed by start/in/out, resampled linearly, scaled down only if the sum would clip) into
  one mono WAV at the mix rate; ffmpeg muxes it as AAC, `-shortest`.
- `--movieexport <s>` (with `--moviestudio`): the last s seconds exported at 30 fps (a chase on the first actor if the
  movie has no shot), the frames counted with ffprobe, the sound stream checked; RESULT printed. Writes into the real
  `user://movies/export/`: delete it after.
- Not yet: exporting several sequences chained.
