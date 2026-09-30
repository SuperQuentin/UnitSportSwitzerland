# GPX replay, cinema and video export (`src/Gpx/`)

Ghost racing, road matching, the cinema director, lens, zoom bubble, sightline cut and the video exporter.

## Architecture

- **Camera sightline cut** (`ChunkManager.SetSightlineCut`, driven per frame by
  `PlaybackCamera.UpdateSightlineCut`): when a ray from the camera to the runner's head hits
  something, a corridor along that segment is **dissolved with a Bayer-dither `discard`** in
  `ps1_building`/`ps1_tree`. Dither, not alpha: those shaders are `unshaded` with no blend mode,
  the trees are one MultiMesh sharing a single material so per-instance transparency is not
  available, and a dithered dissolve is already this renderer's visual language. One uniform write
  reaches the whole streamed world however much has loaded since. The radius is **ramped**, never
  switched - a corridor that snaps open reads as geometry popping out of existence - and it settles
  to exactly 0 so the shaders take their disabled branch. **Terrain and roads are deliberately
  excluded**: dissolving ground opens a hole straight through to the sky, which looks far worse
  than the hillside it was hiding, and a camera behind a ridge is already rejected outright by
  `ShotContext.CanSee` before the shot is committed. A road lying flat never occludes anything.
- **Zoom bubble** (`ZoomBubble`, driven from `PlaybackCamera.Step`): whenever the ACTIVE camera
  is far enough from the runner that they are a few pixels (a wide Locked-off tripod, a Free
  camera flown across the valley), a comic speech bubble pops up holding a **live close-up** of the
  runner, its tail pointing at where they are in the main picture. It replaced a red "HERE" arrow,
  which said where the runner was but still left them too small to see. The inset is a 256² 
  `SubViewport` with `OwnWorld3D = false`, so it renders the same streamed world with no extra
  loading (the runner is already an anchor), from a chase camera 4.5 m behind along `Heading`,
  eased and clamped above the ground; its update mode is `Disabled` whenever the bubble is hidden,
  so it costs nothing up close. `CanvasLayer` 6: above `LensLayer` (5) so the barrel distortion
  does not bend it, below the HUD (10). A runner off screen or behind the lens pins the bubble to
  the nearest edge, tail pointing outward. Trigger is pure distance with hysteresis (shows past
  35 m, hides under 25 m). Exported videos include it — the layer draws into the root viewport.
  **HUD Bubble button / `--bubble off`** (`--arrow off` still accepted) turns it off.
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

- **Absolute Racing** (`CameraMode.Racing`, after Cinema in the C cycle; `--racingmode`; #22, #30): the
  Absolute Cinema director (`Director.ForRacing()`) over a shot set of **camera drones**
  (`Cinema/RacingShots.cs`, MF Ghost style). Every shot is a `DroneShot`: a body with a velocity,
  capped at 200 km/h and ~2.5 g, flying at the shot's `Target` with feed-forward of the target's own
  motion; it follows the terrain (3.5 m AGL here and 0.8 s ahead) and climbs to regain line of sight.
  Because it flies straight at its target it takes shortcuts by itself: **apex cut** finds the sharpest
  corner 1.5-6 s ahead (`Runner.CourseAt`, `NextCorner`), starts at the chase position, flies the chord
  to hover 10 m up on the inside and watches the car slide round, then drops in behind. Also parallel
  tracking on the inside of the next bend, top-down, lead reverse (framing the chaser), a swoop from
  high ahead to behind, a wide duel shot framing both cars, and a drone chase as the fallback (last in
  the array). Nothing sits at ground level: the bumper/wheel/trackside rigs it replaced spent most of a
  descent filming grass. `ShotContext.Rival` is the nearest other runner; `Nose`/`Slip` are the body vs
  the travel — for a recorded car the body turns to the recorded yaw (`<us:yaw>`), so a drift reads as a
  drift. `CanSee` ignores tree trunks (layer 2): the sightline cut dissolves them, so a forest must not
  veto a vantage. The zoom bubble is off in this mode: a drone never lets the car get small. A
  recorded race from `--drivecheck --record` exported with
  `--racingmode --path 0 --export ...` is the way to film one.

## Commands

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

## Gotchas

- **A visibility test that CUTS AWAY defeats a dissolve that was built to avoid cutting away.**
  `LockedOff`, `DroneOrbit` and `LowHeroPass` all re-tested `ctx.CanSee` in `StillGood`, so the
  instant anything drifted between the camera and the runner the Director scored the shot
  "broken" and cut to something else — before the sightline-cut shader ever got a frame to
  dissolve it in. The dissolve existed and worked; Absolute Cinema simply never gave it the
  chance, because pre-empting a shot happens the same frame the obstruction appears and a fade
  needs several. `CanSee` still gates `Begin` — a shot never STARTS aimed at a wall — but once
  running these three now trust the dissolve instead of testing sight afresh every frame. This is
  also why "the old Cinematic mode sees through things and Absolute Cinema doesn't" was reported
  as a difference between modes when the shader code was actually identical for both: Cinematic
  never had a competing cut-away trigger to race against, and Cinema's own `StillGood` was
  quietly winning that race every time.
- **A camera placed relative to raw TERRAIN can end up under the ROAD the runner is actually on.**
  `AnkleCam` and `LowHeroPass` computed their ground height from `ctx.Ground(p)` — the bare
  terrain grid — with `ctx.Subject.Y` as a fallback only when the tile hadn't streamed in yet.
  But a runner on a road is not always AT terrain height: a graded cut or a low embankment sits
  measurably above it, which swissALTI3D does not model (see the bridge-approach gotcha below).
  For a camera placed a couple of metres from the runner, the runner's OWN elevation — already
  correct, road-matched or draped, whichever applies — is a far better local reference than the
  bare grid, and using terrain alone put the lowest-angle shots' cameras under the visible road
  surface on exactly the stretches where the two disagreed, which is also where a low angle makes
  the clipping most obvious. `ShotContext.GroundNear` takes `Math.Max(Ground(p), Subject.Y - cap)`
  instead — a cap, not the runner's height outright, so AnkleCam's own by-design offset below the
  runner is not clamped away.
- **Two systems computing "how high is this road" independently will not agree to the
  centimetre, and a lift sized for one will not clear the other.** The GPX ribbon's tread sits
  `TreadLift` above the height `TrackMatcher` interpolated along the `.road` polyline; the
  rendered road tread `RoadMeshBuilder` draws from the SAME polyline adds its own render-time
  offsets on top for reasons that have nothing to do with the recording — `BridgeLift` (0.15 m,
  purely to stop a deck z-fighting the terrain), and a little more at junctions and type-change
  joins where width and height are blended across the seam. None of that is visible to
  `TrackMatcher`, so the base 0.28 m tread lift — sized to clear terrain noise on a DRAPED course
  — was not always enough to clear the render-time offsets on a SNAPPED one, and the ribbon sank
  under the road it was following rather than the ground beneath it. Fixed with a second,
  larger `RoadClearance` margin applied only when `ElevationIsSurface` is true.
- **A GPX recording's activity comes from the file, not an assumption.** `Runner` always built a
  running figure, so a bike ride played back as someone jogging alongside their own bicycle.
  `GpxParser` now reads the standard `<trk><type>` element (Strava, Garmin and most exporters
  write it; matched by substring - "cycling", "biking", "road biking", "1" all count, since
  exporters do not agree on the string) into `GpxTrack.Kind` (`UnitSport.Player.RideKind`, the
  same enum the player's own mount picker uses), carried through `TrackMatcher` so a road-matched
  copy keeps it. `Runner` builds the real `Cyclist` rig - the one E mounts, not a second one - for
  `RideKind.RoadBike`, via `Cyclist.CreateWithTint` rather than `Cyclist.Create(riderIndex)`:
  the existing factory colours from `HumanPalette.ForRider(index)`'s hue formula, which is a
  *different* colour than the fixed six-entry leaderboard palette `Runner.Tint` already uses for
  a human avatar, and a bike ghost whose rider colour disagreed with its own leaderboard row would
  be its own small bug. Cadence is driven from `Runner.Speed` through the same
  `speed * 60 / 6.2` clamp(40,112) formula `Bicycle.cs` drives the player's own legs from - there
  is no wattage for a recording, but there is a speed, and `Cyclist` already freezes the cranks
  below ~0.01 rpm so a finished or paused ghost simply stops pedalling. Camera mounts (helmet POV,
  ankle cam, etc.) come from `HumanMeshBuilder.MountsForPose(HumanPose.Cycling)`, a fixed-pose
  sibling of the gait-sampled `MountsFor` added for this - a cyclist has no gait phase to sample,
  the legs just turn a crank around a fixed torso. A track with no `<type>`, or an unrecognised
  one, still plays as a runner: this is additive, not a reclassification of every existing GPX.
- **State derived from the course must be re-derived on EVERY path that changes it.**
  `RacePlayback.SetSnapToRoads` raised `SnapChanged` only from the end of a matching pass, so the
  two paths that return early - turning the toggle off, and turning it back on when everything is
  already matched - left the ribbon drawn from one variant while the avatar ran the other. It does
  not read as a stale ribbon; it reads as **the body being rotated off the path**, which is how it
  was reported. The event is now raised from a `finally`, and `EnsureCinemaPlan` is subscribed to
  it too, or the director keeps cutting to corners belonging to the other variant. Same class of
  bug in the HUD: the camera button's shot name was recomputed only inside `Refresh()`, which
  nothing called on a cut, so it showed whichever shot was running the last time any control was
  pressed. `_camera.CinemaCuts` is now in the change-detection string - the count, not the name,
  because two consecutive cuts can land on shots of the same name.
- **Cinema shot LENGTH was never the reason it cut too fast at high speed.** `_target` and `_held`
  were already in screen seconds and already unscaled by the clock. The collapse came from the
  other two triggers: `Imminent`'s lead window widens with the clock, so at 32x it spans 51 track
  seconds while the clock advances 32 per screen second - the window is essentially never empty,
  and the same event re-fired a cut on every frame past `MinSeconds`. Fixed by capping the lead
  (`MaxLeadSeconds`) and by letting an event pull exactly **one** cut (`Director._covered`).
  `Imminent` must still be called unconditionally, never behind a `&&` short-circuit: it is what
  walks `_cursor` past events the clock has left behind, so skipping it parks the cursor on the
  covered event for ever. Second cause: `LockedOff` and `DroneOrbit` guard themselves with fixed
  metre distances that a runner eats in about a second at 32x, where `LowHeroPass` already scaled
  by `ClockSpeed`. Measured on a 4 km track, 40 screen seconds: **1x 7 cuts, 8x 8, 32x 12 -> 9**.
- **The video exporter fed the camera TRACK seconds where it wanted SCREEN seconds.**
  `_step` is `clockSpeed / fps`; `ShotContext.Dt` and every easing rate in `PlaybackCamera` are
  screen rates, and `ctx.Follow()` re-applies `ClockSpeed` itself - so the multiplier was counted
  twice and an export paced visibly differently from the preview the player had just set up at the
  same speed. It is `_camera.Step(1.0 / _fps)`; only `_race.StepTo` takes `_step`.
- **A facing look-ahead must be bounded by DISTANCE, not just time.** `HeadingLookahead` is 2.5 s
  either side, which is ~17 m on foot and 60-100 m on a bike. That is fine for outrunning GPS
  jitter on a raw recording and wrong on a road-matched one, which has real corners: a hairpin is
  chorded straight across, and on a switchback the two samples land on opposite legs so the
  difference collapses toward the degenerate guard and the heading **freezes**. Bounded now by
  `MaxHeadingChordM`. The facing slerp also has to be clock-scaled like the position follow next
  to it, or at 8x the body keeps up with the course while its heading lags eight times as far
  behind every corner.
- **Raw GPX motion looks like a boat.** Three separate causes, all handled: positions are
  smoothed at parse over a *distance* window (`GpxParser.SmoothingWindowM`, so dense 1 Hz
  tracks are filtered while sparse ones are untouched); facing comes from a +/-2.5 s
  look-ahead and is slerped (`Runner.HeadingLookahead`); and the rendered position eases
  toward the sample (`Runner.PositionFollow`), which also hides the 2 m heightfield
  stepping underneath. Seeking snaps rather than easing, so scrubbing stays responsive.
- **GPS speed must be averaged over a window, never one segment.** A ~1 Hz recording has
  metres of jitter between consecutive fixes, so differencing a single segment reports a
  walk as a run and never settles. `GpxTrack.SpeedWindow` (6 s either side) makes the
  readout match the avatar's real world speed — verified at 5.8 reported vs 5.6 measured.
- **A hand-built basis must be checked for HANDEDNESS, not just direction.** `right = up × forward`
  and `right = forward × up` differ by a sign, and that sign is the difference between a rotation
  and a **reflection** (determinant −1). Both `PlaybackCamera.Aim` and `Runner.SafeBasis` had the
  operands the wrong way round, so the replay camera rendered the **entire world mirrored** and
  every ghost was mirrored on top of it. It hides extremely well: the −Z column is unaffected, so
  facing still looks right, and terrain is symmetric enough that nothing looks wrong — until you
  follow a route you know and every turn you took comes back the other way. `--shot` never showed
  it, because `ShotRunner` sets `Rotation` as Euler angles instead of building a basis. The rule:
  `right = forward × up`, and if a basis is built by hand, assert `det ≈ +1`.
- **`XmlReader.ReadElementContentAsString()` already advances the reader.** Calling
  `Read()` again after it silently skips the next sibling; that is how every `<time>` after
  an `<ele>` went missing and GPX tracks all fell back to an assumed pace.
- **`get_image()` on the root viewport from `_Process` returns whatever the render thread last
  left there.** `ShotRunner` gets away with it because the scene has been static for seconds by
  the time it grabs. A per-frame exporter does not: measured 75 identical frames of empty sky,
  with `RenderTotalPrimitivesInFrame` reading 0 at the moment of capture while a `--shot` from the
  same camera position drew 5.2 M. Await `RenderingServer.FramePostDraw` first.
- **A map-matched track needs its DISPLACEMENT rate-limited, not its position.** Where the model
  changes road, the projection jumps: the two roads meet at a junction but the switch happens
  wherever the fixes stop being nearer one than the other, which is somewhere else. Measured 29
  steps over 10 m in a single fix across 16 km — one visible sideways twitch every ~550 m. Two
  fixes failed first: switching at the thinning-sample boundary made it *worse* (51), and
  bridging unmatched gaps changed nothing. Slew-limiting the snap offset to 1.2 m per fix took it
  to **0**, and costs only that the track is briefly between two roads at a junction instead of
  exactly on one — which looks like cutting a corner, i.e. like a runner.
- **`RoadNetwork` must snap endpoints to rejoin tile-clipped roads.** `.road` segments are clipped
  at every kilometre boundary, so a road crossing one arrives as two features with coincident
  ends. Without a snap tolerance every tile edge is a dead end, no route crosses one, and the
  transition term then scores every step near a seam as impossible.
- **A `.road` file is not a road file.** It also carries cable cars, rivers, avalanche barriers
  and dry-stone walls. Matching a GPS track onto a wall is not a near miss, and a wall or a
  watercourse is often the closest line to a riverside path — `RoadNetwork.IsTravellable` is the
  filter, and railways are excluded too because they parallel valley roads for kilometres.
