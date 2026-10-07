# The trailer director (`src/Trailer/`, #706)

- **What it is**: `--trailer all|N|A-B|N,M` stages and films the shots of `TrailerScript` (the
  storyboard `docs/trailer/storyboard.md` as data) one after the other in the real world, with the
  game's own machines. A `ToolRun` row in `ClientWorld` (it places the camera itself: the spectator,
  freed like `ShotRunner`'s).
- **One shot** (`TrailerDirector`): `Next` (style through the chat's `/style`, the sea state, the
  traffic count, the clock held at the shot's hour, `Traffic.Forget()`, the origin moved to the place
  with `OriginShifter.ShiftTo`) → `Stage` (the camera at its first key until `SettledNear` 3 rings has
  held 2 s, 150 s at most) → `Place` (the actors: road, body, mount, launch; 60 s at most) →
  `Preroll` (the actors move for `Shot.Preroll` s, the camera already on its first key) → `Roll`
  (the keys for the shot's length, the captions on top) → the next shot.
- **Time**: shots run from a bar line of the song (`Song.Bar(k)`: 123 BPM, first downbeat 0.186 s);
  a recorded shot has `round(End·fps) − round(Start·fps)` frames, so cuts never drift off the bars.
- **Actors** (`Actor`, a `Cast` each): a `FootPlayer` with `Npc = true` (no camera, anchors its own
  ground and collision) unless it walks (an NPC on foot only stands). `Drive.Road` = the race
  `AutoPilot` on a `RaceRoute` built from the spot toward `Toward` (`RouteBook`, shared by name);
  `Drive.Follow` = pure pursuit at `Speed` (trucks and buses have no autopilot); `Drive.Controls` =
  a `RideInput` script; `Drive.Fly` = `DebugLaunch` at the spot's height, then `FootPlayer.FlyControls`
  (scripted flight, several craft at once); `Drive.Walk` = `WalkControls`. Boats get in on a dry
  `Board` spot, then `PlaceBoat` on the water. `Dance`, `Item` (held), `Doors` (the freighter's
  ramp is bit 3), `Trailer`, `Lights`, `Seed` (looks).
- **Camera** (`ShotCamera`): keys of eye + look, each a world `Spot` (height above the surface, or
  `Spot.Alt`), a point in an actor's travel frame (`Pt.On(actor, right, up, back)`), or for a look a
  compass direction (`Pt.Dir`). A cubic through the keys with Catmull-Rom slopes per second (the
  move keeps its speed through every key), the lens eased per key (full-frame mm, 24 mm film height),
  `Smooth` damping, `Shake` (slow sines, not jitter), never under the surface. `KeysFrom` lets
  several shots carry one move on (the three style shots).
- **Picture**: the root renders at `--trailer-size` (1920x1080) whatever the window
  (`ContentScaleMode.Viewport`), every `CanvasLayer` but the captions hidden (walked once, then
  `NodeAdded`), `Captions` sized from the frame's height, fades as a black veil.
- **Recording** (`FrameRecorder`): with `--fixed-fps 30`, each frame is read after
  `RenderingServer.FramePostDraw` and piped raw RGBA into ffmpeg (x264, CRF 16); time in a recorded
  roll is frames written / fps, never the clock. Without `--fixed-fps` it warns: frames follow the
  wall clock. `tools/trailer.sh render` films the shots and cuts them with the song (`-ss` the first
  shot's start, `-shortest`).
- **Framing a shot**: `tools/trailer.sh stills 5` (first, middle, last frame) with `LOG=1`
  (`--trailer-log`): every actor's LV95, speed and arc twice a second, and each road dumped every
  5 m to `test_output/trailer/routes/shotNN_<road>.csv`, to put a camera where a car will be.
- **The song** is not committed: `tools/trailer.sh` downloads it from incompetech.com (CC BY 4.0,
  credited on the end card, `Song.Credit`).
