# Settings

- **Settings** (`Core/GameSettings`, `Ui/SettingsScreen`, `user://settings.json`): render distance
  in tile rings (6..40, default **15**), detail preset (Low/Medium/High = inner ring table + road/
  building reach, `LodPolicy.Create`), horizon km, fog on/off (**off by default** — the shaders keep
  their fog code and `FogUniforms.Apply` pushes `fog_start/end` past the far plane when off), generated
  terrain on/off (`GeneratedFill`, live via `ChunkManager.SetFallbackEnabled`; see `generated-fill`), parallel
  tile builds (0 = auto: `ProcessorCount` from local disk, 6 when `ChunkStreamer.ServerReachable`),
  mesh commit budget in **ms per frame** (replaces the fixed 2 meshes/frame: a stride-50 tile is 441
  vertices and a stride-1 one a million, so a count was sized for the wrong one), VSync, window
  mode (windowed / borderless / exclusive fullscreen; **F11 / Alt+Enter** toggle it from anywhere, in
  `Core/DisplaySettings._Input` so chat does not open on Alt+Enter, back to the last fullscreen kind) and window size, and **3D resolution** — a
  dropdown of render scales 25–200% **of the window's real pixels** (100% = native,
  `canvas_items` stretch since #306), **except in PS1**, where it is of the 1152x648 UI canvas so the
  PS1 look stays as low-res on a 1440p monitor as on a laptop (75% = 864x486 at 16:9).
  `DisplaySettings.EffectiveScale` turns the setting into `Scaling3DScale`, re-applied on a settings
  change, a window resize and `/style` (`StyleKit.ChoiceChanged`: not `Chosen`, whose listeners
  mean "a world exists"). The labels are the pixels they give in the window as it is when the
  screen opens. Resolution is deliberately NOT `Root.ContentScaleSize`: the UI lays out
  in that 1152x648 canvas, so changing it would shrink or balloon the HUD. Window mode/size are only re-applied when those two settings change, or every unrelated
  setting would snap a hand-resized window back. `DisplaySettings` belongs to the shell, so all of it
  holds on the title screen too.
- **Tabs**: Video (window, size, 3D resolution, VSync, fog, speed lines), Audio (volumes, engine
  voice), Gameplay (movement, third person, camera shake, LAN discovery), Vehicles (wear, gearbox,
  cockpit), Controls (stick, invert, vibration, the F1 overlay), World (time, day length, traffic,
  trains, generated terrain, occasions), Performance (render distance, detail, horizon, builds,
  commit budget, overlay, logs). LB / RB or Q / E change tab. The same screen serves the title and
  the pause menu, and the solo screen repeats the time/traffic/trains rows for the session.
- Every change applies live (`GameSettings.Changed` -> `ChunkManager.ApplySettings`, the materials, the
  cameras' `Far`) and saves; there is no Apply button. `--settings` opens it for a screenshot;
  `--rings N --horizon km --fog on|off --generated on|off --detail low|medium|high` override for one run
  without being saved. The last ring is always **stride 50** (`LodPolicy.FarStride`, 21x21 verts,
  from the `.terrc`), which is what makes 40 rings (6,561 tiles) cost about what 9 used to.
  A server ignores all of it and keeps 2 rings of full grids around each player.
- Also kept here: `TutorialDone` (the first-run tutorial, `ui/tutorial`), `PlayerName` (asked the first time the Multiplayer screen opens), `LastHost`,
  `RecentGpx` (the track picker's recents). Saved servers are `user://servers.json` (`Net/ServerBook`).
