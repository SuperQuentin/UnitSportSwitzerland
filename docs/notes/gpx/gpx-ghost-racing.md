# GPX ghost racing

- **GPX ghost racing** (`src/Gpx/`): `GpxParser` -> `GpxTrack` (LV95 via `SwissProjection`,
  cumulative time + distance). `RacePlayback` owns ONE clock; each `Runner` samples its own
  track at that shared time, so several GPX files start together and race as ghosts —
  alignment is by *elapsed* time, not wall-clock date, so runs recorded months apart still
  compare. `TrackRibbon` draws the focused runner's course draped on terrain,
  `PlaybackCamera` has chase / first-person / cinematic / free, `PlaybackHud` gives the
  timeline scrubber, speed multiplier, and a leaderboard with gaps in metres and seconds.
  Each runner is a **streaming anchor**, so terrain loads around the race not the camera.
  Elevation is draped (GPS ele kept only as a drift statistic — measured ~1 m on a real
  track, a good check that projection and heightfield agree). Each ghost's legs run the shared
  gait at its own measured speed, on the **replay** clock — at 4x playback the legs turn over
  four times as fast, or the runner skates. The gait raises and drops the hips itself, which is
  what the old hand-written head bob was standing in for. **The avatar matches the recording**:
  `GpxTrack.Kind` (from the GPX `<type>` element) puts a real pedalling `Cyclist` — the player's
  own bike rig, not a second one — on a ride recorded as cycling, tinted to the runner's
  leaderboard colour rather than the rig's own rider-index palette; anything else still runs.
  Cadence is driven from the sampled speed by the same curve the player's own bike uses.
  **Snap to roads** (`TrackMatcher`/`RoadNetwork`, HUD button or **R**): map-matches a recording
  onto the mapped network so a ghost runs *on* the road rather than 5 m beside it. Hidden Markov
  model in the style of Newson & Krumm — emission from GPS-to-road distance (σ 8 m), transition
  from |route distance − GPS distance| over a bounded Dijkstra, Viterbi over the whole track.
  Pointwise nearest-road snapping is what this replaces: the nearest road is very often the wrong
  one, and the runner then flickers between a carriageway and the cycle path beside it. Each
  runner keeps **both** variants (`Runner.Track`/`Snapped`, `Active`), so the toggle is a fair
  comparison and not a reload; matching runs off the main thread (93 ms for 16 km, tiles
  included) and is applied back on it. Measured on a real 16 km ride: 98% of fixes near a road,
  mean move 2.9 m, p95 10.6 m, length +0.2%.
  Keys: **G** add track(s), **Space** play/pause, **C** camera, **F** follow next runner,
  **Video export** (`VideoExporter`, HUD button): renders the whole run to an mp4 with the camera
  and speed as set, **not in real time**. It drives the clock in exact 1/fps steps and hands that
  same step to the runners and the camera instead of `delta`, so a frame that took eight seconds
  is indistinguishable from one that took eight milliseconds. Before each frame it waits for
  `ChunkManager.SettledNear(camera, 6)` — what the frame can see, not the whole world — and terrain
  that has not arrived is a hole that cannot be fixed afterwards. It also sets
  `ChunkManager.OfflineMode` and starts an `ExportPrefetcher` that reads the entire route into the
  tile cache in route order, so no frame is ever the first to ask for a tile.
  Frames are **streamed raw into ffmpeg's stdin** (`-f rawvideo`, via `System.Diagnostics.Process`
  — Godot's `OS.CreateProcess` has no stdin pipe), so nothing is PNG-compressed on the main thread
  and the encoder works while the game renders the next frame; `frame_00000.png` is still written
  as a known-good reference still, which is how a flipped or colour-swapped pipe would be caught.
  Without ffmpeg on PATH it falls back to the PNG sequence plus `encode.bat`.
  `--export <dir>[,fps][,speed][,warmup]` does the same headlessly.
  **R** snap to roads,
  **Lens** button cycles a simulated optic (`LensLayer`, `shaders/lens.gdshader`): a full-screen
  `CanvasLayer` at layer 5 - under the HUD's 10, so the controls are never bent - doing barrel
  distortion, chromatic aberration and a vignette, paired with a per-profile **FOV bias** applied
  in `ShotContext.Place`. The bias is not decoration: distortion warps the picture that was drawn
  and cannot widen it, so curvature without extra field of view reads as a warped photo rather
  than a wide lens. It tops out around 150 degrees of true FOV; a real >=180 fisheye needs the
  scene rendered to a cube, which is 3-5x the draws and was not worth it for a look.
  **Path** slider fades the course ribbon out (`TrackRibbon.Opacity`, `ps1_path.gdshader` gained
  `blend_mix` + an `alpha` uniform). At 0 the node is hidden outright and stops rebuilding. The
  opacity lives on `GpxSession`, not the ribbon, because `RefreshRibbon` destroys and rebuilds the
  ribbon on every snap toggle and focus change.
  **Pace** button (Cinema only) scales `Director.Pacing`, i.e. every shot's min and max duration.
  **Shot** dropdown (Cinema only, `Director.SetForced`/`PlaybackCamera.ForcedCinemaShot`) pins the
  director to one named shot picked by hand — "Auto" gives the choice back. `Begin` is still
  tested every time the pin (re)starts, so it never opens on a bad vantage, but once running it
  is held regardless of `StillGood`, the event timeline, or Pacing, none of which mean anything
  once a human has taken over. `--forceshot <name>` is the headless equivalent, for screenshotting
  or exporting one shot on its own rather than hoping the director cuts to it in time.
  **H** show/hide UI (the toggle button lives outside the hidden panels, or hiding the UI
  would remove the only way back).
  `--gpx <path>` may be repeated to start a race from the command line.
