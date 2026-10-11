# The recorded world (#775)

- **What is recorded:** every `MeshInstance3D` under a node `ReplayWatch` knows: a container joins with
  `Movie.ReplayWatch.Container(this)` (each child is one object), a lone object with `ReplayWatch.Object(this)`.
  Joined so far: `Traffic`, `BirdLife`, `VehicleManager`, `DroppedItems`, `CombatManager`, `BrCrates`,
  `StructureVisuals`, `OccasionCreatures`. A new world system that draws moving things joins with one line.
  - Players and anything under a `FootPlayer` are skipped: they come back as puppets (`movie-studio`).
- **How** (`WorldRecorder`, ticked by `ReplayRecorder` at 30 Hz): an object is a group whose LV95 pose is kept; each
  mesh is a part whose pose is kept in the group's frame. A pose is written only when it moved (1 mm, ~0.05°),
  so a parked car costs one frame. The tree is walked twice a second for new and gone meshes.
  - Looks (mesh + materials, `WorldLook`) are shared by index: a thousand frames of a car hold one mesh.
  - `WorldRing`: the events of the last `ReplayMinutes`; older ones fold into the starting state, like
    `ReplayRing`'s change-only properties. `Slice` on a grab gives a `WorldTrack`.
- **Playback:** a clip on the timeline's World row (`Clip.World`, `MovieProject.Worlds`). `WorldGhosts` under the
  `MovieStage` draws visual-only copies (no physics, no network), group nodes top level and placed from LV95 every
  frame. Between two frames one tick apart a pose is interpolated; across a longer gap the object rested and holds.
- **The studio hides the live world** (`ReplayWatch.HideLive` in `Freeze`, `ShowLive` in `Thaw`).
- **Files:** `.usmovie` v5 holds the world tracks; each look is saved once as a tiny packed scene in
  `user://movies/looks/<guid>.scn` (`WorldLookStore`), shared by every project. Loading resolves them.
- **Record / Stop** (#775): Shift+F5, or Pause > Record / Stop recording (pad, VR). `ReplayRecorder` feeds a
  second `Recording` (players, world, sound) beside the buffer, up to `SessionMinutes` (30; it then stops and
  saves by itself). Stopping saves it as its own movie, "Recording <date time>", which the studio's Open lists.
  A red "● REC m:ss" badge shows while it runs. It is held in memory, preallocated when it starts (about 86 MB of
  sound plus 8 MB per player for 30 min); streaming to disk is a follow-up.
- **Not yet:** time of day and weather, doors (many and static: to measure first),
  MultiMesh-drawn things (the dormant fleet stays live), particles and lights.
