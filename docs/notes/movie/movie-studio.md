# Movie studio: replay buffer, clips, timeline (#638)

Plan and later milestones (cameras, lenses, VR, webcam mocap): `docs/plans/movie-studio.md`, tracking #637.

- **Replay buffer** (`ReplayRecorder`, a child of `ClientWorld`):
  - Always records the last `GameSettings.ReplayMinutes` (default 5, 0 = off; Settings > Gameplay) of every
    `FootPlayer` in `PlayerSnapshot`: the local player, remote players and race NPCs. Puppets and server
    proxies are skipped.
  - Runs at 30 Hz on `GameClock.Now` from `_PhysicsProcess`. One fixed `ReplayRing` per player instance:
    nothing is allocated while recording, about 1.4 MB a minute per player.
- **What is recorded:** exactly the replicated visual state (`ActorIo.Read`):
  - Continuous: `Global` (LV95 doubles), `WorldVelocity`, `NetYaw`, `Anim`, `DeckPos`/`DeckYaw`, and `NetPose`
    (up to 17 floats: body, train, VR hands).
  - Change-only: the `FootPlayer.OnChangeProperties`, kept as events.
  - The recorder reads C# properties, not `Get(name)`, which boxes Variants and copies strings.
  - `ActorIo.Names` must match `FootPlayer.OnChangeProperties`. `--moviecheck` fails on drift. A new replicated
    property goes in both, and in `ActorIo.Read`/`Write`.
- **Grab** (`F5`, Pause > Save clip, or opening Pause > Movie studio):
  - `ReplayRecorder.Grab` slices every ring after its last grab into the session's `MovieProject`, one clip per
    player on that player's lane (lane key = node name).
  - `MovieProject.PlaceGrab` keeps one session's grabs at their real spacing on the timeline. The first grab after
    loading or restarting goes after everything else.
- **Studio** (`MovieStudio`, a `Ui/Screen`): opens over the live world.
  - The local player gets `ProcessMode.Disabled` and is hidden, the recorder is paused, and a `StudioCamera`
    becomes current and is a terrain anchor.
  - A `MovieStage` (under `ClientWorld`) poses one puppet per lane each frame from `ActiveClip(lane, t)`. Of
    overlapping clips, the latest start wins.
  - Esc / Close restores everything (`_ExitTree`, so leaving the world with the studio open is safe too).
- **Edits** (`MovieProject`, pure, unit tested in `MovieTests`): split at the playhead, trim either edge (never past
  what was recorded, never under `MinLength` 0.2 s), move, duplicate, delete. `Compact` before saving drops unused
  tracks and lanes, and renumbers.
- **Files:** `user://movies/<name>.usmovie` (`MovieFile`, versioned little-endian binary). Change-only properties
  are stored by name, so a file from a build with more or fewer of them still loads.
- **Controls** (studio only, no input-map actions, so they never collide with gameplay):
  - Keyboard: Space / J / K / L, ← → (Shift: 1 s), Home / End, S, Del, Ctrl+D, Tab; right-drag orbits, the wheel
    zooms, Ctrl+wheel zooms the timeline.
  - Pad and VR (`XrPad` is a pad on device 7): Y play, X split, LT / RT shuttle, right stick orbit, LB / RB zoom,
    D-pad on the focused timeline steps a frame. Read through the input map (`TriggerLeft/Right`, `Look*`,
    `MapZoomIn/Out`), never `GetJoyAxis(0, …)`, which misses VR.
- **Not yet:**
  - Puppets of players inside a building are hidden when you are outside it, like remote players
    (`SameSpaceAsLocal`).
  - The live player's HUD canvas layers stay up under the studio.
  - Lanes are named "You" / "Player N" / "Racer N", not the player's chosen name.
