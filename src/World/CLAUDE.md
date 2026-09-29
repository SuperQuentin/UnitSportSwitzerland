# World systems (`src/World/`)

Day/night and traffic.

## Architecture

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
- **Traffic and trains** (`World/Traffic`, `World/LaneGraph`): local and cosmetic per client (not
  replicated), but solid — every unit is an `AnimatableBody3D` box. A `LaneGraph` of drivable roads
  (5×5 km) and railway (7×7 km) is rebuilt off-thread whenever the view enters a new tile, endpoints
  snapped to 0.5 m so tile seams join. **Divided carriageways get a direction from where their
  partner lies** (right-hand traffic: the other carriageway is on your left); TLM records none, and
  ~70% find a partner. A `Route` grows legs at junctions and trims behind: a car is one unit on it,
  a train a locomotive + carriages at fixed offsets, which keeps a train on one line through points
  (trains only take legs that carry straight on). Cars keep right on undivided roads, slow for
  bends (2.5 m/s² lateral) and for the car or player ahead. Density: Settings → Time of day (`TrafficCars`,
  35, ~half at night; `Trains`); `--traffic N`. Check: `<godot> --path . -- --trafficcheck[,out.png]
  [--time h]` — 40 s over the nearest motorway, chases a car then a train, fails if nothing moved.
