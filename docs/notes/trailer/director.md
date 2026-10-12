# The trailer director (`src/Trailer/`, #706)

- **What it is**: `--trailer all|N|A-B|N,M` stages and films the shots of a film one after the
  other in the real world, with the game's own machines. A `ToolRun` row in `ClientWorld` (it places
  the camera itself: the spectator, freed like `ShotRunner`'s).
- **The films** (`--trailer-film`, `FILM=` in `tools/trailer.sh`): `drognens` (`DrognensScript`,
  `docs/trailer/drognens.md`, #717: the 30 s music clip at the barracks) and
  `story` (default, `StoryScript`, the script `docs/trailer/script.md`: four friends and a fondue,
  the features shown only through what they do) and `showcase` (`TrailerScript`, the storyboard
  `docs/trailer/storyboard.md`: one feature a shot). The story reuses the showcase's helpers,
  places and machine constants (`internal`, `using static`).
- **One shot** (`TrailerDirector`): `Next` (style through the chat's `/style`, the sea state, the
  traffic count, the clock held at the shot's hour, `Traffic.Forget()`, the origin moved to the place
  with `OriginShifter.ShiftTo`) → `Stage` (the camera at its first key until
  `ChunkManager.CompleteNear` 3 rings has held 3 s: every tile at the detail it is wanted at, with
  its roads, buildings and collision. `SettledNear`, some mesh on every tile, let shots start on the
  coarse ground still waiting for its fine build. A world only drawn for 45 s is filmed as it is,
  with a warning; 150 s at most) → `Place` (the actors: road, body, mount, launch; 60 s at most; a
  prop item is set with its lowest point on the floor, a path's or a road's collision included) →
  `Preroll` (the actors move for `Shot.Preroll` s, the camera already on its first key) → `Roll`
  (the keys for the shot's length, the captions on top) → the next shot.
- **A song per film** (`Song` is a record, `Shot.Song`; the cut is checked against the film's): `VoxelRevolution` for
  the trailers, `IGotAStick` for the clip (121 BPM, first downbeat 0.322 s, 15 bars to 30.07 s);
  `tools/trailer.sh` fetches the film's own.
- **Film sets** (`Shot.Set`, `SetAt`): a room the director builds (the dormitory, `DormSet`, after
  the army's photo of room 61-405), put 400 m under the sea under the place it stands for and lit by
  its own lamps. Camera points `Pt.Set`, actors `Cast.InSet` (put inside with `EnterInterior`, so
  nothing measures them against the terrain), props `Prop.InSet` (items rest on what is under them:
  the table), `Prop.Seated` (a character sitting, the passengers' `SeatedFigure`), `Cast.Use` (the
  held item used on cue: a drink at the mouth). `MeshScratch.Build` bakes a half turn, so a set's
  meshes are turned back (`Unturn`) to match its lights, collision and points; the roll logs where
  the camera starts in the set's metres.
- **Night**: the game's night leaves olive and dark machines black on black tarmac, a directional
  light barely lifts them (the terrain's shaders keep their night); `Shot.Moon` adds a soft blue one
  anyway. A race was moved to first light instead.
- **Time**: shots run from a bar line of the song (`Song.Bar(k)`: 123 BPM, first downbeat 0.186 s);
  a recorded shot has `round(End·fps) − round(Start·fps)` frames, so cuts never drift off the bars.
- **Actors** (`Actor`, a `Cast` each): a `FootPlayer` with `Npc = true` (no camera, anchors its own
  ground and collision) unless it walks (an NPC on foot only stands). `Drive.Road` = the race
  `AutoPilot` on a `RaceRoute` built from the spot toward `Toward` (`RouteBook`, shared by name);
  `Drive.Follow` = pure pursuit at `Speed` (trucks and buses have no autopilot); `Drive.Controls` =
  a `RideInput` script; `Drive.Fly` = `DebugLaunch` at the spot's height, then `FootPlayer.FlyControls`
  (scripted flight, several craft at once); `Drive.Walk` = `WalkControls`. Boats get in on a dry
  `Board` spot, then `PlaceBoat` on the water. `Dance`, `Item` (held), `Doors` (the freighter's
  ramp is bit 3), `Trailer`, `Lights`, `Seed` (looks). `StopAt` stops a `Follow` driver at an arc
  (3 m/s² of braking).
- **Characters** (`Character`: a name, a chat colour, an `Appearance` and the clothes worn): a
  `Cast.Who` has the same face and clothes in every shot. `FromDoor` puts one on the step of the
  front door nearest its spot, the door opened (`InteriorManager.OpenDoorForCamera`). `Aboard` puts
  one on another actor's machine at `Deck` (its frame) once that one is under way, launched with
  its velocity (the steamer's deck, the freighter's hold).
- **Camera** (`ShotCamera`): keys of eye + look, each a world `Spot` (height above the surface, or
  `Spot.Alt`), a point in an actor's travel frame (`Pt.On(actor, right, up, back)`), a point on a
  shot's road at an arc (`Pt.Road`), the driver's seat (`Pt.Cockpit`: the rig's `EyeFrame` as the
  cockpit camera has it; with `Cast.FirstPerson` the car draws its cockpit; 0.7 m behind the eye
  shows the wheel, dash and hands), or for a look a compass direction (`Pt.Dir`). A cubic through the keys with Catmull-Rom slopes per second (the
  move keeps its speed through every key), the lens eased per key (full-frame mm, 24 mm film height),
  `Smooth` damping, `Shake` (slow sines, not jitter), never under the surface. `KeysFrom` lets
  several shots carry one move on (the three style shots).
- **On top** (`Captions`, the one layer left): `Captions` (low line or middle title), `Chat`
  (the friends' group chat bottom left, names in their colours, typed in; a null `Who` is a
  system line), `Supers` (place and hour, top left), `Photo` (at that second a view is rendered once
  with `PhotoCapture`: the film's camera, or `PhotoFrom`'s key, a photographer's of its own; a
  white flash, then a polaroid settling on screen, the title under it and a low line over it), fades.
- **Picture**: the root renders at `--trailer-size` (1920x1080) whatever the window
  (`ContentScaleMode.Viewport`), every `CanvasLayer` but the captions hidden (walked once, then
  `NodeAdded`), `Captions` sized from the frame's height, fades as a black veil.
- **Recording** (`FrameRecorder`): with `--fixed-fps 30`, each frame is read after
  `RenderingServer.FramePostDraw` and piped raw RGBA into ffmpeg (x264, CRF 16); time in a recorded
  roll is frames written / fps, never the clock. Without `--fixed-fps` it warns: frames follow the
  wall clock. `tools/trailer.sh render` films the shots and cuts them with the song (`-ss` the first
  shot's start from `shotNN.start`, `-shortest`); `render 19,24` films those again and re-cuts
  them with the last version's others, `cut` re-cuts the newest. The 36 showcase shots take
  ~15 min on this machine, the 38 of the story ~25; either film is exactly 3810 frames, 127.000 s.
- **Versions**: every render is a new folder, never written over: `test_output/trailer/<film>/v1`,
  `v2`, ... in the **main checkout** (`TRAILERS=` elsewhere), so a film outlives the worktree it
  was made in. Each holds `trailer.mp4`, `trailer_share.mp4` (CRF 23, to send), `shots/`,
  `render.log` and `version.txt` (when, the commit and whether there were local changes, which
  shots were filmed and from which version the others came). `VERSION=2 tools/trailer.sh cut`
  re-cuts an older one. Stills stay scratch, in the checkout's `test_output/trailer/<film>/stills`.
- **Framing a shot**: `tools/trailer.sh stills 5` (first, middle, last frame) with `LOG=1`
  (`--trailer-log`): every actor's LV95, speed and arc twice a second, and each road dumped every
  5 m to `test_output/trailer/routes/shotNN_<road>.csv`, to put a camera where a car will be.
- **Placing a shot**: `--trailer-scout "E,N,H;E,N,H"` takes a still straight down from H m over each
  spot, north up, 24 mm (so H/1080 m a pixel at 1920x1080): put an LV95 grid over it to read off a
  street's axis, a shore, a castle, a runway end. `--trailer-log` then times the actors.
- **The cut** is checked at start (`CutProblems`): shot numbers ascending, each shot starting on the
  bar the one before ends, the last ending with the song; a problem fails the run's RESULT.
- **The song** is not committed: `tools/trailer.sh` downloads it from incompetech.com (CC BY 4.0,
  credited on the end card, `Song.Credit`).
- **Staging gotchas** (each cost a stills pass):
  - An autopilot put down mid-route must be seeded (`D.Near = IndexAt(arc)`): it searches forward
    from the start and, on a zigzag like the Tremola, locks onto the leg metres away and drives that.
  - The race pilot launches motorbikes at walking pace (wheelie, low profile); bikes use
    `Drive.Follow`, which banks a two-wheeler (`tan φ = v²κ/g`) and slows for bends itself.
  - A pigeon on the ground (and one launched from there) takes off only on a flap (`Up`), not on the stick.
  - A boat is mounted on dry ground (`Board`) and then `PlaceBoat`ed: its spot must be water
    (`WaterField.TryLevelAt`), or it waits forever; the steamer floats at 1.6 m (`Draught`).
  - Skis are gravity only and carving is their brake: face them down the real fall line (try
    several headings with `--trailer-log`), keep the steer small.
  - A road route's direction is the road's, not `Toward`'s: read it off the route dump before
    giving an actor a negative arc.
  - A scripted body cannot swim: it is held at the water's surface, and swimming reads the
    player's own keys, not `WalkControls`.
  - A fixed camera beside a road sees whatever stands there (barns, bridge parapets): put it on the
    road, or high, or check it in a still.
  - Every actor runs under the one local peer, and a figure's look is found by its rider index
    (`Appearance.For`): left alone, every figure wore the same face. An NPC actor is named as a race
    entrant of owner 0 (`npc_0_<n>`, an index of its own) and its look registered there; a walker
    registers its own (one walker a shot keeps its look).
  - An NPC body on foot only falls and stands: it never boards a deck (Léa stood in the lake under
    the steamer). An actor `Aboard` is a walking body, and it and its machine ignore each other's
    bodies: the hull shoving a body put down inside it is a crash, and the steamer and the freighter
    were wrecked by their own passengers.
  - The steamer is real: 3 km/h after 10 s, 90 % of its speed after 71 s. A short pre-roll leaves it
    standing; it reads as at anchor from afar.
  - A walker out of a door on a slope vaults the bank in front of the step: stand it there instead.
  - The director's warnings (an actor left out, placing timed out) go to stderr, not into
    `stills.log`: read the run's whole output.
