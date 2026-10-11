# The recorded world (#775)

- **What is recorded:** every `MeshInstance3D`, `Light3D` (not the sun) and `GPUParticles3D` under a node `ReplayWatch` knows: a container joins with
  `Movie.ReplayWatch.Container(this)` (each child is one object), a lone object with `ReplayWatch.Object(this)`.
  Joined so far: `Traffic`, `BirdLife`, `VehicleManager`, `DroppedItems`, `CombatManager`, `BrCrates`,
  `StructureVisuals`, `OccasionCreatures`, and each `DoorLeaf` as a lone object. A new world system that draws moving things joins with one line.
  - **Instancers** (#782) are recorded only when they opt in with `ReplayWatch.Instanced(mmi)`: a lone object of its
    own, its look a copy of its instances (`WorldRecorder.InstancesLookOf`, one `SetInstanceTransform` at a time: the
    headless renderer refuses a whole buffer and keeps no transforms). Call it again whenever the instances change,
    or the recording keeps the old copy. In: the dormant fleet (`DormantVehicles.Instanced`, a wake redraws the tile)
    and the hall cars (`ParkedCars.Show`, a car undrawn in place). Out: anything set every frame (`OccasionCreatures`'
    flocks, `CombatManager`'s tracers), which would be a copy a frame.
  - Players and anything under a `FootPlayer` are skipped: they come back as puppets (`movie-studio`).
- **How** (`WorldRecorder`, ticked by `ReplayRecorder` at 30 Hz): an object is a group whose LV95 pose is kept; each
  mesh is a part whose pose is kept in the group's frame. A pose is written only when it moved (1 mm, ~0.05°),
  so a parked car costs one frame. The tree is walked twice a second for new and gone meshes. Something that
  has not moved for 2 s (`RestAfter`) is looked at only every 4th tick; waking, it writes the pose it rested at a
  tick back so it does not glide from long ago.
  - **One read, many rings:** `ReplayRecorder` owns the one `WorldRecorder`; the buffer's and a session's
    `WorldRing`s are attached to it (`Attach` writes everything alive as it stands, so a late ring starts whole).
  - Cost measured (`TickMicros`, logged on a grab): fixture 124 live, 166 µs a tick; Lausanne with `--traffic 80`
    372 live, 374 µs a tick for buffer and session together.
  - Looks (mesh + materials, `WorldLook`) are shared by index: a thousand frames of a car hold one mesh.
  - A light or emitter's look is a detached copy of the node (`WorldLook.Template`, `Duplicate(0)`), a new one when
    what shows changes (energy, colour, range, emitting); a ghost duplicates it and replaces it on a change.
  - `WorldRing`: the events of the last `ReplayMinutes`; older ones fold into the starting state, like
    `ReplayRing`'s change-only properties. `Slice` on a grab gives a `WorldTrack`.
- **Playback:** a clip on the timeline's World row (`Clip.World`, `MovieProject.Worlds`). `WorldGhosts` under the
  `MovieStage` draws visual-only copies (no physics, no network), group nodes top level and placed from LV95 every
  frame. Between two frames one tick apart a pose is interpolated; across a longer gap the object rested and holds.
- **The studio hides the live world** (`ReplayWatch.HideLive` in `Freeze`, `ShowLive` in `Thaw`).
- **Files:** `.usmovie` v5 holds the world tracks; each look is saved once as a tiny packed scene in
  `user://movies/looks/<guid>.scn` (`WorldLookStore`), shared by every project. Loading resolves them.
- **Record / Stop** (#775): Shift+F5, or Pause > Record / Stop recording (pad, VR). `ReplayRecorder` feeds a
  second `Recording` (players, world, sound) beside the buffer, with no time limit: it is streamed to disk
  (`movie-session-spool`). Stopping saves it as its own movie, "Recording <date time>", which the studio's Open lists,
  once the worker has joined it (a toast says "Saving the recording…" meanwhile). A red "● REC m:ss" badge shows while it runs.
- **The world's state** (#775): one undrawn entity (`Parent = WorldEntity.Environment`, -2) holds the hour in its
  pose's X, the sea state in Y and the snowfall in Z (`OccasionPrecip.Snowfall`), written when they change. While a world clip plays, `MovieEnvironment` holds
  the sky there (`DayNight.HourOverride`, applied at once even paused), sets the sea state and holds the snow (`OccasionPrecip.SnowfallOverride`); leaving the clip or
  the studio gives the game's own back.
- A command-line launch on the real map starts in the fly camera: no player body, so no actor clip (correct).
- **Not yet:** particles replay their emitting state, not their exact puffs. Instancers that do not opt in (scenery
  decor, trees) are not recorded, and the studio shows them live.
