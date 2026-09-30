# Terrain runtime: streaming, LOD, meshing, collision (`src/Terrain/`)

Chunk streaming, LOD rings, mesh builders, collision, horizon, cover/pattern shading. How the road, tunnel, bridge, ropeway, watercourse and wall data is *built* is in `tools/CLAUDE.md`; how tiles travel over the network is in `src/Net/CLAUDE.md`.

## Architecture

- **Roads are merged into terrain collision, not just draped over it.** The player always
  physically stood on the bare-terrain `HeightMapShape3D` — roads had no collision of their own
  at all — which was invisible on flat ground but a real mismatch wherever a road's surveyed
  height genuinely diverges from raw terrain (an embankment, a cut, a graded approach
  swissALTI3D never modelled) and **severe** on a bridge: zero collision under the deck, so a
  player walking onto a visual span fell straight through to the valley floor below.
  `TerrainMeshBuilder.ComputeRoadBlend` closes the ordinary case: for every at-grade
  road/path/rail segment (excluding `RoadFlags.Bridge`/`Tunnel`, aerial ropeways, watercourses
  and walls — none of those is a ground-level surface), it walks the segment's own densified
  polyline and smoothsteps the terrain **collision** floor toward the segment's own stored
  height — already carrying `RoadExtractor`'s approach-ramp blend from preprocess time, so no
  height is re-derived — from full weight at the road's own half-width out to zero
  `CorridorFalloffM` (3 m) beyond it — stamped every 1 m, so the pull **compounds** and a
  shoulder ends much nearer the road than one smoothstep says. That compounded shape is what the
  game has, so it is kept: the recursion is linear in ground height, so each cell is carried as
  `ground·P + S` and ONE sparse pass (`RoadBlend`) serves collision (clearance 0) and the visual
  mesh (`VisualBlendClearance`), with the stamp weights tabled per road. The visual tail is
  `PatchSurface` on the interim mesh's core — only corridor vertices move — not a second
  million-vertex build; verified bit-identical to a rebuild, and heights within 1 mm of the old
  blend. blend+tail went 17.8 -> 6.7 ms/tile (488 -> 194 ms at stride 1). A distance-field blend
  (true single smoothstep) was tried and is ~17 cm different on average, up to 55 m on cliffs:
  it is a look change, not an optimisation. Bridges are excluded on purpose: a heightfield has one
  height per (x, z) column, so it cannot represent a deck floating above the gorge it crosses —
  blending toward deck height there would fill the gorge in. Those get `RoadMeshBuilder.
  BuildBridgeCollisionFaces` instead, a small `ConcavePolygonShape3D` for the deck TOP only
  (mirroring `BuildingMeshBuilder.BuildCollisionFaces`'s pattern) — piers and parapets stay
  visual-only, since falling through the deck was the actual reported problem, not clipping a
  pier. Both run in `ChunkManager.StartBuild`'s **tail**, after the road tile has loaded: the
  bare-terrain collision still publishes immediately in the **interim** result so the ground
  never waits on roads (see the interim/tail split below), and gets silently replaced with the
  blended version once available — `CommitReadyResults` already re-applies `SetCollision`
  whenever a later `BuildResult` carries a non-null `CollisionMap`, so no new commit path was
  needed, only a second call to `BuildCollisionMap` with the road tile it didn't have the first
  time. Tunnel interiors are not touched here — the existing hole-carving at the portal already
  works, mostly by the coincidence that undisturbed rock blocks a player; verify with `--probe`
  before assuming that needs its own collision too.
- **Surface patterns**: `CoverPalette` writes a `SurfacePattern` code into vertex-colour
  **alpha in quarter steps** (0 none, 0.25 parking bays, 0.5 vine rows, 0.75 mown stripes)
  and `ps1_terrain.gdshader` dispatches on `int(COLOR.a * 4 + 0.5)`. TLM records no
  orientation for any of them, so every pattern runs on world axes — and the direction
  cannot be recovered from the screen-space normal (see the confetti gotcha below).
  Alpha interpolates across a class boundary, so a pattern bleeds one 2 m cell.
- **Water**: built at runtime from the Water cover class, not a separate file —
  swissALTI3D already models lakes/rivers as flat surfaces at water level, so the terrain
  height at a water cell *is* the water level, and rivers keep their downstream gradient
  for free (`WaterMeshBuilder`, +0.12 m lift, `ps1_water.gdshader`).
- **Windows**: `BuildingMeshBuilder` bakes facade UVs (metres along the wall, storey
  index) from the *triangle* normal; the shader draws the window grid from those. Storey
  height comes from GWR `GASTW` (69% coverage), else wall height / 2.9 m. Barns, garages,
  tanks and anything under 3 m opt out with uv.y < 0.
- **Runtime** (`src/`): `Terrain/ChunkManager` streams LOD rings around anchors (workers
  build arrays, main thread commits ≤2 meshes + 1 collision per frame);
  `HeightMapShape3D` collision on d≤1 tiles (CollisionShape3D scale derived from
  `ChunkFormat.SpacingM`, currently 1 m — see the road-collision-merge entry above for how it
  gets blended toward road height, and a second small `ConcavePolygonShape3D` body for bridge
  decks);
  `shaders/ps1_terrain.gdshader` does vertex snap, flat shading via derivatives, palette
  bands, Bayer dither, fog. Fidelity knobs: `rendering/scaling_3d/scale` (0.75) and the
  per-shader `snap_resolution` (640x480) — lower both for a grittier PS1 look, raise for
  crispness.
  **The game renders at a fixed 1152x648 and Godot scales that to the window**
  (`display/window/stretch/mode = "viewport"`). Not `canvas_items`: there the 3D renders at the
  window's real size, so the same world is sharper on a 1440p monitor than on a laptop and the
  PS1 look drifts with the display. `aspect = "expand"` means a non-16:9 window gets a wider or
  taller view rather than black bars — measured 1152x648 in a 1920x1080 window and 1152x864 in an
  800x600 one, no distortion either way. Side effect worth knowing: `--shot` and the video
  exporter now always write frames at the internal resolution, whatever the window is. LOD rings live in `LodPolicy` (stride 1 underfoot, out to 40 m quads at d=9). `Core/Main` boots ServerWorld (`--server` /
  dedicated_server feature) or ClientWorld (`--connect host[:port]`, offline otherwise).
- **Coarse tiles**: every `.terr` has a `.terrc` companion — the same grid **point-decimated at
  stride 10** (51x51, **5.2 KB** against 490 KB). The LOD rings render one vertex in ten or twenty
  past ring 4, so 280 of the 361 tiles an anchor wants were reading a 490 KB file to use 5 KB of
  it. Decimation, not averaging, is what makes it free: `TerrainMeshBuilder.BuildSurface` already
  samples `HeightMetersAt(c * stride, r * stride)`, so the kept vertices are *exactly* the ones
  the mesh uses and the geometry is bit-identical (the `--coarse` pass asserts this per tile at
  both strides). A tile reads the full grid whenever anything is built **onto** it — collision,
  roads, watercourses, building footings all sample the heightfield — so the rule is full at
  d <= `RoadMaxDist`, coarse beyond. `ChunkGrid` carries its own `Stride` and `RequireFull()`
  guards the callers that cannot take a 20 m lattice. The whole region's companions are 33 MB.
- **Far horizon** (`HorizonLayer`, `horizon.bin`, `tools/TerrainFormat/HorizonFormat.cs`): every tile
  decimated to a **100 m lattice** (11x11 samples, 242 B) and packed into ONE region-wide file by the
  preprocessor's `--horizon` pass (also run at the end of a full build and of `--coarse`; 6,699 tiles ->
  1.6 MB in 3.6 s, read from the `.terrc` companions since stride 10 divides 100). The client reads it
  once and meshes **10x10 km blocks** (101x101 verts, altitude-band colours only — no cover) out to
  `HorizonKm` (setting, default 60, `--horizon <km>`), one block committed per frame. It is what makes
  the world an open map: the snow peaks 50 km down the Rhône are on screen for ~100 draws. The blocks
  use their **own** `ps1_terrain` material instance carrying a **per-tile coverage texture**
  (`HorizonLayer.SetCovered`, one R8 texel per km tile over the region, sampled by world XZ) inside
  which the shader `discard`s — so the lattice never shows through a tunnel floor or a carved portal,
  and the tile material (`use_cover = false`) never pays for the test. A texel is set the frame a
  tile's surface mesh commits and cleared when it unloads. **Not a ring rectangle**: that was tried
  first and was wrong both ways — it dropped the horizon under tiles that had not arrived yet (a
  visible gap while loading) and kept it under tiles that had left the rings but not yet unloaded. Streamed like `places.json` (`AssetKind.Horizon`, fetched during `ClientTerrainSync`,
  `HorizonReceived` -> `HorizonLayer.Reload`). Camera `Far` follows it (`GameSettings.CameraFar`).
- **Stutter is a main-thread commit problem, and every commit is now cheap** — measured with
  `--fly x,y,z,yaw,speed,seconds` (`FlightProbe`, prints p50/p95/p99/max frame time and counts
  frames over 20 and 33 ms, non-zero exit on any >33 ms). Baseline after the first async pass: 14
  frames over 33 ms in a 12 s flight at 150 m/s, worst 147 ms. Four causes, each found by logging
  commits over 15 ms: (1) **collision** — a 1001² `HeightMapShape3D` is ~80 ms to build, and it was
  built under the fly camera, which never touches the ground; collision now goes only to anchors
  that ask for it (`AddAnchor(node, collision:)`, default `node is PhysicsBody3D`; `FootPlayer`
  registers itself, `TunnelProbe` registers its point). It is also committed **once**, not interim
  then blended: from local disk the road tile is milliseconds behind, so the ground waits for it
  (`publishInterimCollision`); over the network it does not. (2) **Trees** — `SetInstanceTransform`
  + `SetInstanceColor` per tree was two native calls × 60k; the worker now packs the 16-float
  instance buffer (`ChunkNode.BuildTreeBuffers`) and builds the `MultiMesh` itself with a
  `CustomAabb`, since assigning a buffer without one makes the server walk every instance for the
  bounds on the main thread. (3) **ArrayMesh** creation moved to the worker for terrain, roads,
  buildings and water (`ChunkNode.ToArrayMesh`; `RenderingServer` is thread-safe) — the main thread
  only assigns the resource. (4) **Building collision** (`ConcavePolygonShape3D`, a BVH build, up to
  80 ms for a town tile) now rides with the terrain collision — only the tile a body stands on — not
  with every building mesh. Every result counts against the ms budget, not just surfaces. And the
  ring evaluation's scan + sort (6,561 tiles at 40 rings, previously every 0.1 s and after every
  commit — a 51 ms frame) is cached and redone only when an anchor's tile, the ring table, the view
  octant or the tile set changes. After: **0 frames over 20 ms** at 15 rings, max 12.7 ms; at 40
  rings max 22 ms; at 6 rings / 150 km horizon / 32 builds max 15.9 ms.
- **Build cancellation** (`ChunkState.Cts`/`Generation`): every `source.Load*Async` gets the tile's
  token and the worker checks it between stages. A tile that leaves the desired set (beyond
  `MaxDist + UnloadSlack`) or whose *pending* stride is finer than what it now wants past
  `RoadMaxDist` is cancelled on the spot — the worker slot frees now instead of when the chain it was
  reading finishes, and `CommitReadyResults` drops any result whose generation is stale. Measured: 12
  in-flight Riddes builds cancelled within one evaluation of a 50 km jump (`SettleReport` prints
  `cancelled=`). Tiles **behind the camera** (`ChunkManager.ViewDirection`, set per frame from the live
  camera) are queued three rings later than those in front, never skipped.
- **`CachingChunkSource`** decorates the source chain with a byte-budgeted LRU of decoded tiles,
  so ground that is left and returned to is not decoded twice — `france.gpx` retreads 30.5% of
  its own route. `ChunkManager.OfflineMode` unlocks the per-frame commit budget and the build cap
  for a video export, where nobody is watching and a hitch costs nothing.

## Commands

- Tunnel collision check: `<godot> --path . -- --probe lv95E,lv95N,seconds`
- Generated fill check (no Godot): `dotnet run --project tools/BlendCheck -c Release` — synthetic real
  blocks beside the generator, then the real `FallbackChunkSource` + `CachingChunkSource` chain:
  seams generated|real and generated|generated at both resolutions, coarse = decimated full, horizon =
  grid, point path = grid path, no cliff, no trench, invalidation, horizon.bin knots = tile knots,
  the fill switched off. Non-zero exit on any failure. In game: `--chunks <partial region>
  --generated on|off`; a server with no terrain: `--server --generated-world`.
- Streaming smoothness: `<godot> --path . -- --fly x,y,z,yawDeg,speedMps,seconds [--rings N --horizon km --builds N]`
  — flies straight at that speed and prints the frame-time distribution; exits non-zero on any
  frame over 33 ms. The way to check a loader change, since a hitch never shows in a `--shot`.

## Gotchas

- **A collision build must load the road tile even when the roads are already drawn.** Both the
  road-blended floor and the bridge-deck collision are built in `StartBuild`'s tail from the road
  tile, but `EvaluateRings` only asked for roads when they were *missing*. The ordinary way to
  play — fly over an area (roads load, no collision: the fly camera does not ask for it), then
  drop on foot — therefore built every tile's collision from BARE terrain and no bridge collision
  at all: the player stood the full `DrapeOffset` (0.35 m + class lift) inside every road and
  path, and fell straight through every bridge. `StartBuild(..., roadsForCollision:)` now fetches
  the (cached) road tile for the blend without re-meshing the roads. Measured with
  `--roadcheck`: floor − ribbon went from −0.42 m mean to ±0.005 m.
- **A road's collision core takes the height at the cell's perpendicular foot on the
  centreline, nearest segment wins** (`ComputeRoadBlend`). Letting the last stamp win, or a
  neighbouring road's fade-out pull it, left 0.1–0.4 m of scatter (feet sinking into one path,
  hovering over the next); stamps are 1 m apart, which on a 30% alpine path is 15 cm by itself.
  The core is at least one lattice spacing wide, or a 1.2 m footpath can miss every corner of the
  quad its centreline crosses. Check with
  `<godot> --path . -- --roadcheck [--bridges] [--at E,N]`: drops a body on real road (or bridge
  deck) points and prints ribbon vs floor vs body, non-zero exit if it rests >5 cm below.
- **`ConcavePolygonShape3D` is one-sided for collision unless told otherwise, and geometry that
  "looks right" can still be on the wrong side of that test.** The bridge-deck collision
  (`RoadMeshBuilder.BuildBridgeCollisionFaces`) shipped with the shape genuinely present at
  exactly the right position and height — confirmed by dumping its face vertices — and a
  straight-down `PhysicsRayQueryParameters3D` probe still passed clean through it to the terrain
  metres below, reproducing the exact fall-through the collision exists to prevent. Godot's
  `ConcavePolygonShape3D.BackfaceCollision` defaults to **false**, so a ray or a `MoveAndSlide`
  approaching from the "back" of the triangle winding is not stopped at all — not a near miss,
  a complete pass-through, and nothing about the visual mesh rendering correctly (or the face
  data looking sane on inspection) says anything about which side that is. Set
  `BackfaceCollision = true` on any collision shape a player approaches from a direction its
  winding was not deliberately authored for — a deck walked on from above is exactly that case.
  **This was caught only by actually raycasting the running game with the godot-ai MCP**
  (`game_eval` + `PhysicsRayQueryParameters3D.create`), not by reading the code, not by checking
  the shape's face data, and not by a visual `--shot` — the geometry inspection said everything
  was fine. `BuildingBody`'s collision shape has the same `BackfaceCollision: false` default and
  was not touched — its winding comes from the visual mesh, which had to be correct for
  rendering to look right, so there is no equivalent evidence it is broken, but it has not been
  verified with a raycast either.
- **A flat-shaded quad mesh is NOT a bilinear surface, and a height query must match whichever one
  is actually on screen.** `ChunkGrid.SampleHeight` blends all four corners of a quad smoothly;
  `TerrainMeshBuilder.BuildSurface` splits every quad into two FLAT triangles along a fixed
  diagonal. At full resolution (2 m spacing) the two agree to a few centimetres and nobody
  notices. At the coarse LOD strides most of a streamed world renders at beyond ring 4 (20-40 m
  spacing), they diverge by up to **1.4 m on real terrain here** (measured: stride-10 tile,
  400 random samples, max 1.44 m, mean 5 cm) - enough that a GPX ribbon's 0.28 m tread lift was
  nowhere near enough to clear it, and the route visibly sank under the ground the player could
  see. `ChunkGrid.SampleMeshHeight` replicates the mesh's own triangle split exactly (verified
  continuous across the diagonal), and `ChunkManager.TryGetHeight` - the avatar, the GPX ribbon,
  the cinema camera's ground and `CanSee` checks, all of it - now goes through that instead.
  `SampleHeight` itself is untouched: the preprocessor and `RoadMeshBuilder` call it against
  always-full-resolution grids (`RequireFull()`-guarded), and their baked output was generated
  against it, so changing it would need a full re-preprocess of already-built terrain for no gain.
- **Window rows crop unless storeys divide the wall exactly.** Pick the storey height so
  `usable / count` is whole, and pass the count via UV2 so the shader stops below the wall
  plate — otherwise the eave slices the top row.
- **Never derive a surface tangent from the screen-space normal.** The derivative normal
  jitters per pixel, so a facade grid built on it turns to confetti. Bake per-vertex UVs
  from the exact triangle normal instead, and fade fine patterns out with `fwidth` before
  they hit the 0.35x internal render resolution.
- **Vertex colours are raw linear; shader uniforms marked `: source_color` are not.**
  Godot converts sRGB→linear for `source_color` uniforms automatically but never for
  baked vertex colours, so authored colours must go through `Color.SrgbToLinear()` in C#
  or dark tones render washed out (asphalt 0.30 displayed as 0.58 grey).
- **Mesh holes must shrink, not grow, with LOD.** Dropping a rendered quad when *any*
  full-res cell under it is carved inflates a 6 m portal into a 40 m gash at stride 20.
  Require *all* covered cells to be carved, and skip holes entirely past stride 4.
- **Jolt HeightMapShape3D treats NaN heights as holes** (Godot's default physics engine
  here). One NaN vertex removes the four quads touching it, so collision opens slightly
  wider than the visual mesh — which is the safe direction.
- **`Settled` is the wrong question for a frame.** It asks "is anything, anywhere, still loading".
  `SettledNear(eye, rings)` asks what a frame actually needs, and distance makes the difference
  invisible rather than merely acceptable: `fog_color` and the environment background are the
  **same colour** and `fog_end` is 8 km, so a tile missing past ring 8 renders as exactly the
  colour it would have had. It must test the *desired* set, not the loaded one — `EvaluateRings`
  breaks out of its loop at the build cap, so tiles further down the nearest-first order have no
  state at all, and checking only the states that exist reports a world as settled before most of
  it has been asked for.
- **A fresh clone has NO terrain** — the generated data is gitignored — so a missing
  `manifest.json` is an ordinary state, not an error. `LocalChunkSource` returns an empty
  manifest and the client boots into generated terrain with a message; it used to
  throw `FileNotFoundException` out of `ClientWorld._Ready` and take the game down. A *server*
  still fails fast, because it is the authority on where the world is and has nothing to serve.
- **Publish the terrain before the things that stand on it.** A tile build fetches chunk →
  holes → cover → roads → buildings and used to commit all of it at once, so a streaming client
  saw nothing until the last link landed. `ChunkManager` now enqueues an *interim* `BuildResult`
  carrying just the surface mesh and collision, then a second one with roads/buildings/trees.
  Same files, same order, same worker — the ground simply stops waiting for the tail. Measured
  against a loopback server with no local terrain: at 2 s, **346 → 504,270 primitives**; full
  load unchanged at ~3 s. `Interim` results must NOT clear `PendingStride`, or the ring
  evaluator starts a second build for a tile whose first is still running.
- **Rendering a coarse preview from the height grid alone is WORSE, despite sounding better.**
  Tried it: a separate pass that builds a stride-10 mesh from just the `.terr`. It adds a second
  serialised stage per tile and both stages compete for the same six streaming slots, so the
  full builds starve. Measured 5,746 prims at 6 s where the baseline had finished at 3 s —
  roughly three times slower to a complete world. Removed.
- **A build that throws must release its tile.** `PendingStride` is only cleared on commit, so
  an exception or a missing file left the tile pending for ever and it was never retried or
  drawn — one failed worker permanently deleted that piece of the world. Failures now go on
  `_failedBuilds` for the main thread to reset. (Found because a bad preview stride threw on
  every tile and the whole map stayed empty.)
- **The commit loop must gate on the budget the result actually needs.** It read
  `while (meshBudget > 0 && collisionBudget > 0 ...)`, so the single allowed collision commit
  ended the loop for that frame with the mesh budget untouched — throttling commits to about one
  tile per frame exactly when tiles arrived fastest. Peek first, and break only on the budget
  that result requires.
- **Tile loads are CHAINS, and unordered concurrency starves them.** Each tile awaits chunk →
  holes → cover → roads → buildings. Starting all 361 tiles at once means every chain's first
  request goes out before any chain's second, so a streaming client downloads 361 height grids
  and renders none of them — no tile has its cover yet. `MaxConcurrentBuilds` (6) plus
  nearest-first ordering in `EvaluateRings` fixes it: measured 175k prims after 55 s before,
  **4.34 M after 15 s** after. Neither change affects local loading, where the per-frame commit
  budget is the limiter — measured byte-identical at caps of 6 and 24.
- **Generated fill** (`Terrain/ProceduralWorld`, `ProceduralWorld.Blend`, `Terrain/FallbackChunkSource`,
  issue #27): every tile with no real data is generated, and generated tiles sit **beside** real
  ones with no visible seam — round a MapSetup zone, a server streaming part of the country, a
  download with holes, and everywhere on a fresh clone. The generator: an alpine valley running
  east-west through its **anchor** with a river on a flat bed (water from the cover raster, like the
  real one), a road and a railway along the floor, villages with side streets and a church every
  ~2.6 km, farms and alpine huts, forest to a wandering tree line, rock, scree, glacier, vineyards on
  the sunny side, orchards; high massif far from the valley. All in the **ordinary formats**, served
  through the ordinary `IChunkSource` seam under `CachingChunkSource`, so roads, traffic, trains,
  doors, interiors, collision and gathering all work on it unchanged. Everything is a pure function
  of LV95 position, so seams are bit-identical and the stride-10 grid equals the decimated full one.
  Noise is sampled on a **world-anchored 5 m lattice** and interpolated: evaluated per vertex it cost
  320 ms a tile; now ~30-40 ms, cover ~10 ms (classified from 10 m samples).
  **Ownership**: a tile is real if it is in `ChunkManager._available` (manifest + anything merged),
  generated if not real and inside the **fill domain** — the spawn tile's box and the real set's
  bounding box, each grown by 40 tiles (`FallbackChunkSource.FillRadiusTiles`); it only grows.
  `FallbackChunkSource` holds an immutable `Snapshot` (real set, domain, version) swapped by `SetReal`
  and read lock-free; the rings ask `IsAvailable = _available || Covers`. `_available`,
  `AvailableTiles` and `AvailableTileCount` stay **real only** — corridor surveys must never request
  a generated tile over the network, and "does this client have a world of its own" means real.
  **Anchor**: the generator is centred on `SpawnPoint.DefaultLv95E/N` (Riddes) on every peer and the
  server, not on this run's spawn, so everyone generates the same world. The origin with no local
  terrain is still this run's spawn point.
  **The blend** (`ProceduralWorld.Blend`): `h = (1 − W)·G + W·R + D`, a convex mix, so blended ground
  always lies between the generated and the real (see the trench gotcha). R is the real low-pass
  carried outward: each real tile within 3 km (`Band`) contributes its **100 m knots** (the
  `horizon.bin` lattice) at its nearest point, weighted by inverse distance — continuous where
  nearest-edge extrusion jumps on the medial axis of a hole or a notch. Carried d metres, the knots
  are read from a **pyramid** (`RealTile.Levels`: knots, then tent area-averages on 200 m / 500 m /
  1 km lattices, then the mean) at a spacing of ~d/2, blended across levels by a smoothstep in
  log-distance: the nearest point is constant along every line across the edge, so at one spacing
  real relief was extruded as 3 km streaks. Every lattice is interpolated **Catmull-Rom** (C1, exact on
  a node), not bilinear, whose slope break at each node became a 100 m crease carried across the
  band. W = 1 − smoothstep(reach / 3 km), reach a **soft minimum** (150 m) of the distances with each
  tile's term faded by its own distance — a hard minimum creased on the medial axis (an X across a
  one-tile hole), an unfaded soft one jumped when a tile left the band. D, within 40 m
  (`DetailBand`, which must stay under 100 m or horizon = grid breaks), continues the real surface
  to first order from the edge neighbours' grids: residual `R − L0` plus the real slope across the
  edge, the slope over 1 m on a full grid (10 m at a real corner, the only point two tiles share)
  and faded out within the first 10 m (`SlopeFade`), so it is zero at every 10 m point. That makes the
  seam **C1** (BlendCheck: kink 0.012 m against the ground's own 0.053 m) while a coarse tile stays
  exactly the decimation of the full one. W and W·R live on a world-anchored **10 m lattice**, so
  coarse tiles and the horizon read stored values; a generated vertex on a real edge **copies** the
  real quantised height (bit-identical seam; the mix alone is within 11 cm, the 10 m lattice's
  interpolation of a cubic). Rivers are not drawn where the blend tilts the bed past 1.5%.
  `FallbackChunkSource.BlendFor(tile, full)` loads the real neighbours through the cache above it
  (`Neighbours`), shared per tile and LRU 24; full grids only for the four edge neighbours of a
  full-resolution tile, coarse otherwise; knots from `horizon.bin` when loaded, else extracted from
  the coarse grid — the same bits. Cover uses the coarse blend (identical at the 10 m points it
  reads), so far tiles never read a real full grid.
  **Merging real tiles** (`ChunkManager.MergeAvailableTiles`): `SetReal`, then the new ids and every
  generated tile within 4 of them are **unloaded** (not rebuilt in place: a commit whose roads,
  buildings or trees are null leaves the old ones standing), dropped from the cache
  (`CachingChunkSource.Invalidate(predicate)`, which bumps the epoch so a straddling fetch is not
  cached), the horizon reloads, and `TerrainReplaced(affected)` fires — `ClientWorld` re-places the
  player only if their own tile is affected. 87 tiles merge in 6 ms. `ResetAll(moveOrigin)` throws
  everything away for a rebase (`ClientTerrainSync.Adopt`, when the client has no real tiles).
  **Horizon**: `FallbackChunkSource.LoadHorizonAsync` merges the real index with generated samples for
  the domain + 60 tiles (knots-only blend near real ground; unblended samples cached across reloads):
  45k tiles in ~0.2 s. `HorizonLayer.Reload` queues a re-run asked for mid-load and keeps old blocks
  drawn until their replacements commit, since every merge reloads it.
  **Server**: `ServerWorld` runs the same source (and a cache), so its grid-only `ChunkManager`, the
  interiors and loot see generated ground and houses; its status line prints the ground under each
  player and whether it is generated. `--generated-world` starts a server with no terrain at all
  (origin at the anchor, served as `ChunkStreamer.ManifestOverride`); without it an empty server
  still refuses to start. **Off switch**: Settings → World → Generated terrain
  (`GameSettings.GeneratedFill`, live via `ChunkManager.SetFallbackEnabled`), `--generated off` for
  one run (also on a server). A faint "generated terrain" note (`Core/GeneratedTerrainNote`) shows
  while the camera is over generated ground — a note, not a tint, since the blend exists so the
  border cannot be seen. Cost: a blended full tile ~46 ms against ~24 ms plain. `--fly` across a
  border at 150 m/s (992 tiles, 16 workers): 4 of 7 runs had no frame over 33 ms, the others one
  each (33, 50, 62 ms), which the perf log files as "other" — no slow commit, GC or GPU frame
  behind them; over purely generated ground, 0 in 2 runs. Suspected: blend maths on every core
  starving the main thread. **Open.** Check: `dotnet run --project tools/BlendCheck -c
  Release` (see Commands).
- **Blend two terrains with a convex mix, never an additive correction.** The first blend was
  `G + (Rs − Gs)`: keep the generator's relief, shift it to meet the real low-pass. Every synthetic
  check passed — seams, resolutions, slope bound — and it still dug a **trench 150 m below the Rhône**
  on real tiles at Riddes, found only because a `--ride bike` dropped from 539 m to 324 m. Where a
  steep generated flank meets a real valley floor, the flank's fall away from the seam is kept at
  full size, so the ground drops below both surfaces. `(1 − W)·G + W·R` always lies between them.
  `tools/BlendCheck` now checks exactly that ("blended ground past both surfaces"), and the lesson
  generalises: test a terrain blend on the worst mismatch, a steep generated slope meeting flat real
  ground, not on offset copies of similar ground.
- **Judge a terrain blend by shaded relief, and measure what the eye finds.** Every seam, resolution
  and slope check passed while hillshades showed streaks, a comb along the edges and creases. Each
  became a number in `tools/BlendCheck` (seam kink, streak RMS against the ground's own relief along
  lines parallel to an edge) and `--render` writes the before/after hillshades to
  `test_output/blend/`. Two traps in the measures themselves: a moving-average high-pass lets km-scale
  relief through and reads it as streaks (use a local quadratic fit), and continuing a steep 1 m
  slope across the whole detail band extrapolates metres — keep a continued slope to one coarse
  cell.
- **A saved `maxConcurrentBuilds: 1` makes the loader look broken.** A test run that loads one tile
  at a time (3.8/s) shows a world full of holes that reads like a streaming bug. Check
  `user://settings.json` first, or pass `--builds 0` (auto) to probes.
- **A tile worker must not create a Godot object after the engine starts tearing down.** Workers
  make `ArrayMesh`/`MultiMesh` themselves, and one that did so during quit was `Fatal error.
  0xC0000005` in `ArrayMesh..ctor` — the process died on exit. It only showed once something was
  always building at quit, which a generated world is (3 of 3 fly probes crashed).
  `ChunkManager._ExitTree` cancels every build and waits up to 3 s for `_buildsInFlight` to reach
  0, and every worker checks its token right before each Godot call; either alone leaves a race.
  Related: `ClientTerrainSync` continues on the thread pool, so anything it raises that touches UI
  or nodes must be marshalled (`Status` is deferred; the rebase/merge runs via `OnMainThread`).
