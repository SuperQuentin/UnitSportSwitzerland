# Core: modes, input, settings, diagnostics (`src/Core/`)

Boot, game modes and menu, the input facade, settings, performance tools, teleport and spawn.

## Architecture

- **Modes** (`Core/MainMenu`, `GameMode`): Explore / GpxReplay / Multiplayer. `ClientWorld`
  owns the switching; **Esc** opens the picker, and it is shown at boot unless a mode was
  named on the command line (`--connect`, `--gpx`) or a verification tool is running
  (`--shot`, `--probe`). Each mode owns the camera while it runs, so `GpxSession.Begin`/`End`
  activate the playback camera + HUD and hand the previous camera back on the way out —
  `SetReturnCamera` matters because Explore may have swapped to the on-foot camera since.
  The menu also owns the mouse: opening releases the pointer, closing recaptures it, which
  is why `SpectatorCamera` no longer handles Esc. `--menu` forces the picker open (and is
  how it gets screenshotted).
- **Input** (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered
  **in code** at boot (`PlayerInput.Install`, called from `ClientWorld._Ready` after
  `GameSettings.Load` so the saved deadzone applies), bound to keyboard (physical keycodes, so
  AZERTY still works), mouse and gamepad. Query through the static facade (`Move`, `LookRate`,
  `Steer`, `Held`, `Strength`, `Rumble`) rather than `Input.IsPhysicalKeyPressed`: it returns
  neutral while `UiFocus.TextEntryActive`, so callers no longer each check for typing. Pad layout:
  left stick move/steer, right stick look (squared response, `StickSensitivity`/`InvertY`),
  A jump, B slide, L3 sprint (latched until the stick is released), RT/LT throttle/brake (analog
  straight into `RideInput`), X tuck/sprint, Y mount picker, R3 camera toggle, Start menu,
  D-pad down fly/foot toggle. Menus call `PlayerInput.FocusFirst` on open so Godot's built-in
  `ui_*` actions drive them with the D-pad, and `MainMenu` holds `UiFocus` while open or the
  stick navigating it would also walk the player. Tab (place search) stays keyboard-only: a pad
  can't type in it. The facade is the seam an OpenXR backend plugs into later.
  Godot's built-in `ui_accept`/`ui_cancel` have **no** face buttons by default (the D-pad moved
  focus but A pressed nothing), so `RegisterActions` adds A/B and the left stick to the `ui_*`
  actions. **GPX replay** has its own pad layout in `GpxSession.HandlePad` (A play/pause, Y
  camera, X snap, RB next runner, LB hide UI, D-pad ←/→ seek 10 s, ↑/↓ speed; right stick looks
  in Free), read in `_Input` rather than `_UnhandledInput` because a HUD button left focused by a
  mouse click would otherwise swallow A and the D-pad.
- **Settings** (`Core/GameSettings`, `Core/SettingsMenu`, `user://settings.json`): render distance
  in tile rings (6..40, default **15**), detail preset (Low/Medium/High = inner ring table + road/
  building reach, `LodPolicy.Create`), horizon km, fog on/off (**off by default** — the shaders keep
  their fog code and `FogUniforms.Apply` pushes `fog_start/end` past the far plane when off), parallel
  tile builds (0 = auto: `ProcessorCount` from local disk, 6 when `ChunkStreamer.ServerReachable`),
  mesh commit budget in **ms per frame** (replaces the fixed 2 meshes/frame: a stride-50 tile is 441
  vertices and a stride-1 one a million, so a count was sized for the wrong one), VSync, window
  mode (windowed / borderless / exclusive fullscreen) and window size, and **3D resolution** — a
  dropdown of `Scaling3DScale` presets 25–200% shown as the pixels they produce (864x486 = the 75%
  default). Resolution is deliberately NOT `Root.ContentScaleSize`: in `viewport` stretch mode the UI
  lays out in that same viewport, so changing it would shrink the HUD at 1080p and balloon it at
  360p. Window mode/size are only re-applied when those two settings change, or every unrelated
  setting would snap a hand-resized window back. The panel scrolls — it is taller than 648 px. Every change applies live (`GameSettings.Changed` -> `ChunkManager.ApplySettings`, the
  materials, the cameras' `Far`) and saves. Main menu **Settings** button; `--settings` opens it for a
  screenshot; `--rings N --horizon km --fog on|off --detail low|medium|high` override for one run
  without being saved. The last ring is always **stride 50** (`LodPolicy.FarStride`, 21x21 verts,
  from the `.terrc`), which is what makes 40 rings (6,561 tiles) cost about what 9 used to.
  A server ignores all of it and keeps 2 rings of full grids around each player.
- **Performance overlay** (`Core/PerfOverlay`, **F3** cycles Off / FPS / Detailed, saved as
  `GameSettings.PerfOverlay`, also in Settings; `--perf off|fps|full` for one run). Detailed shows
  frame avg/p99/max over 2 s, draws/prims/memory, and the loader via `ChunkManager.GetPerfStats()`:
  queue depths, builds/s, per-stage worker ms per tile, and tile latency over the last 128 builds —
  **ground** (build start -> first surface committed) and **complete** (-> last result committed).
  Both are timed on the main thread, so they include waiting behind the commit budget. Hidden
  during a video export (`OfflineMode`); shows up in `--shot` captures, which is how to screenshot it.
- **Session perf log** (`Core/PerfRecorder`, **F4** start/stop, `--perflog [seconds]` from boot,
  Settings -> "Open folder"): writes `user://perf_logs/<timestamp>/` with `frames.csv` (per frame:
  frame/GPU/render-CPU ms, draws, prims, memory, GC counts, loader queues, commits, camera LV95 +
  speed, mode), `builds.csv` (per tile: latency + worker ms of every stage), `commits.csv`,
  `events.log` (hitches tagged commit/gc/gpu/other, teleports, mode and settings changes) and
  `summary.txt` ending in a diagnosis. **Use the viewport's measured GPU time, not
  `Performance.TimeProcess`, to tell GPU- from CPU-bound**: TimeProcess absorbs the wait for the
  renderer and read 50 ms on a frame the GPU spent 35 ms of. A frame's delta pays for the
  PREVIOUS frame's commits, so the recorder runs last (`ProcessPriority`) and shifts its commit/GC
  context by one frame. The per-build stage array also times `blend+tail` (road-blended collision +
  visual re-mesh), which `BuildTimeReport` never counted and which measured ~33% of worker time.
- **Teleport** (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at
  construction. Flying camera gets ground + 220 m, a `CharacterBody3D` gets ground + 2 m and
  has its velocity zeroed and its placement pass re-armed.

## Commands

- Spawn elsewhere: `<godot> --path . -- --at <lv95E>,<lv95N>` (default: Riddes,
  2583250/1113250), or `--goto <town>` to name it instead of looking up coordinates.
  `SpawnPoint` drops the camera to ground + 220 m once the chunk beneath it streams in — the
  height cannot be known at boot.
- Screenshot without the editor: `<godot> --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png`
  (also prints fps/prims/draws — the way to verify rendering when the godot-ai MCP is down).
  `ClientWorld` skips `SpawnPoint` when `--shot`/`--probe` is given, otherwise the spawn
  drop overwrites the requested y with ground + 220 m and every close-up shot comes back
  as an aerial one. `ShotRunner` also re-claims `Current` every frame — a mode entered from
  a deferred call (GPX replay) would otherwise steal the camera after the shot was set up.
  Add `--menu` to capture the mode picker.

## Gotchas

- **Never capture "the thing the player controls" at startup.** The teleport search held the
  spectator camera from `_Ready`, so in multiplayer — where you are an on-foot networked
  `FootPlayer` — Tab silently moved a camera that was not even current and nothing appeared to
  happen. `Teleporter.ActiveTarget` is a delegate resolved per jump.
- **`FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame**, so a focused
  `LineEdit` does not stop them: typing "west" in chat walks you into a lake. Every text-entry
  UI registers with `Core/UiFocus` and both controllers check it.
- **A mode that owns the screen must drop the anchors of the mode it replaced.** `ClientWorld`
  registers the spectator camera as a streaming anchor at boot and never removed it on entering
  GPX replay, so a run at Veigy streamed a second full 361-tile box around Riddes, 100 km away and
  permanently off camera — and the video exporter waited for it before every frame.
  `ParkExploreAnchor` takes it off for the duration; `ToggleMode` (T) is now refused during replay,
  since swapping underneath it would both steal the camera and put the box back.
- **Never default the world origin to LV95 0/0.** Switzerland is 2.6 million metres from
  there, so float precision collapses the moment real data arrives. With no manifest,
  `WorldOrigin.SwissDefault()` is used, and a client with zero tiles then *adopts* the
  server's origin via `Rebase` rather than refusing the mismatch — refusing is right when two
  populated worlds disagree, wrong when you have no world at all. Rebasing changes what every
  world coordinate means, so `ClientWorld.RespawnAfterRebase` puts the player down again.
