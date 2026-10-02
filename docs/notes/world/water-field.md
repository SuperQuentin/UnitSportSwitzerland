# WaterField: waves, sea state, the water query API (#299)

## Rule

- **Ask `World.WaterField`, never the cover raster, whether a point is in water.** API (the contract
  for #301 swimming and #302 boats), world positions in the current frame:
  - `TryLevelAt(Vector3 world, out float level)` / `TryLevelAt(world, double t, out level)`: the
    surface (still level + waves) over a point; false where there is no water or its tile's water
    is not loaded.
  - `IsUnderwater(Vector3)`, `Submersion(Vector3)` (metres under the surface, NaN with no water).
  - `Height(x, z, t)` (waves above the still level, 0 with no water), `Normal(x, z, t)`,
    `Velocity(x, z, t)` (the surface particle's velocity, m/s), `Displacement(rest, t, scale)`
    (what the shader adds to a vertex at rest there).
  - `TryGetStill(world, out still, out scale)`: the still level and the wave scale 0..1
    (`ChunkManager.TryGetWater`, see the terrain note `water-level-layer`).
  - `Now`: the wave clock = `ClockSync.ServerNow` modulo 1200 s. `SeaState` 0..1, `SetSeaState`,
    `SeaStateChanged`. `Bind(ChunkManager?)` (ClientWorld and ServerWorld do it).
- **The wave constants live in `World/WaveSpectrum.cs` only** (plain C#, unit-tested). The shader
  (`shaders/common/waves.gdshaderinc`) has the formula and no number: `WaterField.PushGlobals`
  writes the globals `water_wave_0..5` (kx, kz, omega, phase), `water_amp_a`/`_b` (amplitudes at the
  sea state, the chop in `_b.z`), and `World/WaterSurface` writes `water_time` every frame. Their
  `project.godot` defaults are zero: flat water if C# never pushed. Change a wave in C#, nowhere else.
- **Floating-origin safe by construction**: every wave vector is a whole number of cycles over
  9.6 km (`OriginShifter.PatternPeriodM`), so the phase is a function of LV95 modulo 9.6 km; the
  shader feeds `pattern_xz(world)`, C# `WaveSpectrum.PatternX/Z(LV95)`. Every omega is a whole
  number of cycles over 1200 s, so `t` wraps without a jump and stays small for a float.
- **Sea state is server state**: `--sea-state <0..1|calm|chop|storm|gamey>` (server, or an offline
  client), `/seastate [value]` (anyone asks, an admin sets; offline anyone), sent on join
  (`ServerWorld` → `ChatManager.SendSeaStateTo`) and on change (`SeaState` RPC). calm 0, chop 0.35,
  storm 0.7, gamey 1 (`SeaStateCommand`). Later the wind (#304) sets it.
- **Amplitude per point = the sea state's amplitudes x the layer's wave scale** (fetch x depth):
  rivers and ponds stay flat, beaches calm down, legacy tiles (0.12 m deep) get no waves at all.
- **Every style draws the same surface** (`shaders/body/water.gdshaderinc`, wrapped by
  `ps1_water`, `cartoon_water`, `real_water`): one `vertex()` displaces by the physical waves
  (UV.x = wave scale) and fills `wave_normal`, `wave_lift`, `wave_scale`. The fragment shades:
  - PS1: four dithered bands, the fine ripples near and broad patches far, the swell taking the
    bands over as the sea gets up; alpha by the **vertical** water depth (0.32 at the waterline,
    0.92 from 6 m: the bed shows through the shallows); a bright banded underside.
  - Cartoon: cel-lit (`style.gdshaderinc` `light()`) flat bands by depth (0.9 / 3 / 9 m), a lighter
    ripple tone up close, a broken white foam line in the last 3-5 cm of depth and flecks on crests
    taller than 0.8 of the local amplitude; alpha 0.35 to 0.97 over 4 m.
  - Realistic: Fresnel (Schlick, F0 0.02), the sky and sun from Godot's PBR (`ROUGHNESS` 0.02),
    refraction of the screen behind with per-channel absorption along the path through water
    (clear at the shore, blue-green deep) as `EMISSION`, scatter as `ALBEDO`, detail ripples on the
    normal; Realistic+ on Forward+ adds its own screen-space reflections (`ssr_steps` 24, set by
    `StyleKit.Configure`: Godot's SSR does not reach transparent materials); Snell's window from below.
  - Underwater (`WaterSurface.Look`): PS1 dark teal, 22 m; Cartoon bright turquoise, 20 m;
    Realistic blue-green, 14 m (metres for 63 % of the view).
- **No lattice, at any distance**: the fine ripples (`water_detail`, shading only) are six sine
  trains of 1.35-3.4 m spread round the compass, their phases scrambled by five slower trains
  (6-57 m), all whole cycles over 9.6 km and 20 min (origin- and clock-safe). Tried and dropped:
  the old `sin x + sin z` shimmer in four bands (a grid of identical ellipses on calm water); three
  noise-warped trains (the two strongest still beat into rows); value noise on rotated domains
  (wrapped, it repeated every 9.6 km / |r|, 384 m: a grid over the whole Petit Lac; unwrapped, its
  cells showed as creases); hashed gradient noise (hairline breaks along its cell edges on the
  RTX 4070, Mobile and Forward+). Far off the waves' normal eases to up (400-1500 m): interpolated
  over sub-pixel triangles it drew the mesh grid over the lake.
- **The water writes depth** (`depth_draw_always` on every wrapper): it is drawn in the transparent
  pass, unsorted within a tile, so without it a farther tile or crest painted over a nearer one in
  polygons. The lit wrappers also skip received shadows (`shadows_disabled`).
- **Shallows read as water**: PS1 tints them blue-green over the bed (alpha 0.45 at the waterline
  to 0.92 at 6 m) and draws a broken pale shoreline in the last 3-9 cm; Realistic a wet line;
  Cartoon its foam line. PS1 underwater is a lighter teal (22 m).
- **No wave shorter than four 2 m mesh squares** (8.2 m): 4.3 m waves aliased on the mesh into a
  false lattice. The body fades the short waves with distance (`spacing` = max(2, 0.004 x
  distance)), never by the mesh's own spacing, so tiles meshed 2 m and 4 m apart move their shared
  edge vertices alike.

## Why

- One deterministic function on the server clock: every peer and the GPU get the same surface,
  nothing but one float (the sea state) crosses the wire. What you see is what a hull rides on.
- Gerstner, six waves (64, 41, 27, 17.5, 11.3, 8.2 m): sharp crests, flat troughs, an exact normal
  and particle velocity. Gamey: ~1.25 m summed amplitude (a 1.5-2 m swell crest to trough); calm:
  3 cm. `Chop` 1.2 keeps sum(k A Q) ~0.31 at gamey, far from loops (1).
- The parity of the bed under a server's coarse grid and a client's full grid: the wave scale's
  depth is read on the 10 m lattice both hold (`WaterLayer.BedAt`), or the two peers' waves differed
  on the shelf.

## Placement over water (`ChunkManager.TryGetSurface`)

- `TryGetHeight` is the terrain, which under a lake with a bed (#298) is the bed. Things put down
  use `TryGetSurface` (the ground, or the still level where it is higher): `SpawnPoint`, the
  placement pass in `FootPlayer`, entering foot mode, the void rescue (and `RememberSafe` never
  records a spot under water, so a rescue never lands on a bed), dropped items and `Hearing`, BR
  crates, GPX runners, `BirdLife.Ground` (and no ground bird lands under water), `TargetDrone`,
  occasion decor and creatures, the ambience's ground. Left on the terrain on purpose: getting out
  of a vehicle (shallow water only: cars cannot be in deep water), parking and wrecks (a sunk wreck
  rests on the bed), the vehicle safety nets, clearances for audio/feel, traffic and NPC arrivals
  (roads), the crash ragdoll (physics). `SetRide` refuses a car, motorbike or truck more than
  0.45 m under water (boats are #302).

## Far water

- Tiles past the fine rings drop their water layer, and a lake with a bed would be a pit from afar.
  A tile with a **source** layer (fixture, #298's `.water`) gets a flat surface at the still level
  on every rebuild, as coarse as its ground (`WaterMeshBuilder`, terrain stride >= 10, wave scale 0).
  Legacy tiles need none (their terrain is the surface). **The horizon is #298's**: `horizon.bin`
  is built by the preprocessor from tile heights, so it must take max(bed, still level) there.

## Same logic, preserved

- Legacy tiles: still level = terrain + 0.12 m (where the old mesh was), wave scale ~0, so lakes
  look and drive as before apart from the PS1 alpha (a translucent surface over the blue terrain).
- The old PS1 fragment shimmer (two drifting sines in four bands, sparse glints) is kept; the real
  waves only take over the bands where they are tall or steep enough to see.
- `Ambience`, `Gathering`, `Surfaces` now ask the water layer; `BirdLife` asks it first and keeps the
  cover (the server loads cover for birds and has no legacy water layer).

## Underwater (`World/WaterSurface`, client)

- Camera under `TryLevelAt` (5 cm hysteresis): a full-screen pass (`shaders/underwater.gdshader`,
  render priority max) fogs by the distance through water, up to the bed or up to the still plane
  above when the ray leaves the water first (the surface is transparent, so not in the depth
  buffer); a low-pass (650 Hz) on the master bus. The surface from below is a bright banded skin.

## How to check

- `tools/test.sh unit`: `WaterTests` (periodicity in space and time, dispersion, amplitudes,
  inversion, normal and velocity vs finite differences, layer, `/seastate` parsing, the lake).
- `--watercheck --chunks fixture:lake` (quick, headless, ~20 s): layer, waves calm/gamey, river flat,
  a car wading and a car sinking.
- `tools/watercheck.sh` (net): sea state on join and on an admin change; `/water E N` from the server
  equals the client's surface at the server's wave time (measured: 0.0000 m apart, one clock to 0.04 s).
- `tools/waterparity.sh` (full, windowed): the shader's `water_wave_displace` read back from a
  SubViewport against `WaveSpectrum`: 256 points, gamey, worst 0.31 mm, normal.y 6e-5.
- Screenshots: `--chunks fixture:lake --time 14 --shot-queue <file>` with `/seastate gamey` lines;
  `/water [E N]` prints the water at a point.
