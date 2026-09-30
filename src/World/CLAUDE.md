# World systems (`src/World/`)

Day/night, traffic, tree collision and player races.

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
- **Trees are solid** (`World/TreeColliders`, issue #14): no per-tile tree collision — 100k+ trees
  a forest tile. A **pool** of `StaticBody3D` + `CylinderShape3D` trunks is laid out only around
  `ChunkManager.CollisionAnchors` (the local `FootPlayer`, a moving `VehicleBody`), 45 m round the
  anchor and round a point 1.2 s ahead along its velocity (capped at 40 m), from the same `.trees`
  files in 10 m world cells. Cells are handed out / taken back as the anchor moves (a released body
  is `ProcessMode.Disabled`, which removes it from the space, and reused), at most 64 trunks placed
  a frame. Trunk radius = the drawn trunk (0.10 × crown radius, clamped 0.12–0.5 m), height = the
  whole tree; shrubs (kind 1) stay walk-through; no crown shape, so a plane only hits a treetop's
  axis. **Layer 2** (`TreeColliders.Layer`): `FootPlayer`/`VehicleBody` add it to their mask, the
  camera pull-in rays (`FootPlayer.CameraMask`) leave it out so a chase camera is never shoved in by
  a trunk; combat rays use the default all-layers mask and hit trunks. Measured at the Col du
  Mollendruz (51% forest): 146–330 trunks live, **0.86 ms/frame max while riding**, ~3.8 ms the
  frame the pool first grows its bodies. Check: `<godot> --path . -- --treecheck[,out.png]
  [--at E,N]` rides a bike at a real trunk 25 m away; non-zero exit if it gets through or no
  impact registers. Loaded tree tiles are never evicted (~5 MB a forest tile) — the ceiling on a
  very long drive.
- **Car races between players** (`World/RaceManager`, `World/Race` on server and clients — RPCs route by
  path, like `World/Chat`; issue #24). `/race start [metres]` (any player) opens a race on the main road
  from the host's position (`Player/RaceRoute`, built on the server from its `.road` tiles), 15 s to
  `/race join`, `/race leave`, `/race cancel` (host or console). The server sends each entrant the route
  (centreline + widths to just past the finish), its slot on a single-file grid and a **relative**
  countdown in seconds (never a clock time: the machines' clocks need not agree). The client stands its
  player on the grid in a car (mounts the AE86 if on foot), holds the **handbrake** until GO (the brake at
  a standstill selects reverse), then gives the car back to the player — or with `--raceauto` to an
  `AutoPilot`. **The server times everything**: a client only reports checkpoints (every 200 m, accepted
  in order only — a shortcut misses one) and crossing the line; the finish time is the server's own
  clock from its own start. Standings go out on chat; DNF at a deadline of the distance at 10 m/s.
  Check: `tools/racecheck.sh [E,N] [metres]` — a dedicated server and two `--raceauto` clients on
  loopback (`--racestart`, `--racejoin` script the chat), passes when the server classifies both.
  Measured at the Col du Mollendruz, 1.5 km: Takumi (AE86) 0:58.13, Keisuke 1:01.46.

