# Day/night

- **Day/night** (`World/DayNight`): four **global shader uniforms** declared in `project.godot`
  `[shader_globals]` — `world_sun_dir`, `world_tint`, `world_sky`, `world_night` — written once a
  frame reach every `ps1_*` world shader (each multiplies by the tint just before its Bayer
  quantise and fogs toward the sky) with no material touched. The environment background and
  ambient follow the sky, because avatars and vehicles are standard materials lit only by that.
  Simple Swiss-summer sun (up 6h, 62° at noon, down 18h); below the horizon the shading follows a
  moon so mountains keep a lit side. **Tint and sky are authored as seen and converted
  `SrgbToLinear` before upload** — the shaders multiply linear values, and unconverted night's 0.13
  displayed as 0.40. Buildings light far more windows at night and those glow through the dark.
  Settings → Time of day (start hour, day length, default 24 min, 0 = stopped); `--time <h>` fixes
  the hour for one run.
