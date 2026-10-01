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
  dropdown of `Scaling3DScale` presets 25–200% shown as the pixels they produce (864x486 = the 75%
  default). Resolution is deliberately NOT `Root.ContentScaleSize`: in `viewport` stretch mode the UI
  lays out in that same viewport, so changing it would shrink the HUD at 1080p and balloon it at
  360p. Window mode/size are only re-applied when those two settings change, or every unrelated
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
- Also kept here: `PlayerName` (asked the first time the Multiplayer screen opens), `LastHost`,
  `RecentGpx` (the track picker's recents). Saved servers are `user://servers.json` (`Net/ServerBook`).
