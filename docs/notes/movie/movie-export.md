# Exporting the movie to a video (#786)

- **Export** (the editor's top bar) renders the **active sequence**, from its in to its out (or the movie's end), to
  `user://movies/export/<movie> <sequence> <date time>/run.mp4`, with `sound.wav` beside it.
- It is the GPX replay's exporter (`Gpx/VideoExporter`): frame-exact steps, each frame waiting for the terrain round
  the camera (`ChunkManager.SettledNear`), raw frames piped to ffmpeg (x264, CRF 18). It films anything behind
  `Gpx.IExportSource` (clock, camera, sound): `RaceExportSource` for a replay (as before), `MovieExportSource` for
  the movie.
- **What is filmed:** the editor hides everything but its player, which fills the window (`EnterExportView`: the
  preview's container reparented over the whole screen), looking through the program. The exporter grabs the window,
  so cuts, transitions, fades, crossfades and wipes are in the video exactly as the player shows them. Gizmos hide;
  the perf overlay hides itself (it runs while the game is paused, for that).
- **Sound:** every sound clip over the sequence (music, game sound) decoded (`AudioDecodeJob`) and mixed
  (`Soundtrack.Mix`, pure: placed by start/in/out, resampled linearly, scaled down only if the sum would clip) into
  one mono WAV at the mix rate; ffmpeg muxes it as AAC, `-shortest`.
- `--movieexport <s>` (with `--moviestudio`): the last s seconds exported at 30 fps (a chase on the first actor if the
  movie has no shot), the frames counted with ffprobe, the sound stream checked; RESULT printed. Writes into the real
  `user://movies/export/`: delete it after.
- Not yet: a resolution or fps choice in the editor (30 fps, the window's size), exporting several sequences chained.
