# Settings

- **Settings** (`Core/GameSettings`, `Core/SettingsMenu`, `user://settings.json`): render distance
  in tile rings (6..40, default **15**), detail preset (Low/Medium/High = inner ring table + road/
  building reach, `LodPolicy.Create`), horizon km, fog on/off (**off by default** — the shaders keep
  their fog code and `FogUniforms.Apply` pushes `fog_start/end` past the far plane when off), generated
  terrain on/off (`GeneratedFill`, live via `ChunkManager.SetFallbackEnabled`; see `generated-fill`), parallel
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
  screenshot; `--rings N --horizon km --fog on|off --generated on|off --detail low|medium|high` override for one run
  without being saved. The last ring is always **stride 50** (`LodPolicy.FarStride`, 21x21 verts,
  from the `.terrc`), which is what makes 40 rings (6,561 tiles) cost about what 9 used to.
  A server ignores all of it and keeps 2 rings of full grids around each player.
