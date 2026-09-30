# Terrain runtime: streaming, LOD, meshing, collision (`src/Terrain/`)

Chunk streaming, LOD rings, mesh builders, collision, horizon, cover/pattern shading. How the road, tunnel, bridge, ropeway, watercourse and wall data is *built* is in `tools/CLAUDE.md`; how tiles travel over the network is in `src/Net/CLAUDE.md`.

Index only: one line per note in `docs/notes/terrain/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/terrain`.

## Architecture

- `roads-merged-into-terrain-collision` — Roads are merged into terrain collision, not just draped over it
- `surface-patterns` — Surface patterns: `CoverPalette` writes a `SurfacePattern` code into vertex-colour alpha in quarter steps (0 none,...
- `water` — Water: built at runtime from the Water cover class, not a separate file — swissALTI3D already models lakes/rivers as...
- `windows` — Windows: `BuildingMeshBuilder` bakes facade UVs (metres along the wall, storey index) from the *triangle* normal;... fake rooms behind the glass, occupancy cues
- `building-types` — Building types: `BuildingTypes` groups a tile's solids (a church's nave + bell tower) at runtime; one church interior, every...
- `door-portals` — Doors open (shared, auto-close) and you walk (or drive, garages and barns) through them: `DoorLink` map, portal camera + clip plane, sill crossing, vehicles, linked spaces, building sounds
- `runtime` — Runtime: (`src/`): `Terrain/ChunkManager` streams LOD rings around anchors (workers build arrays, main thread...
- `coarse-tiles` — Coarse tiles: every `.terr` has a `.terrc` companion — the same grid point-decimated at stride 10 (51x51, 5.2 KB...
- `far-horizon` — Far horizon: (`HorizonLayer`, `horizon.bin`, `tools/TerrainFormat/HorizonFormat.cs`): every tile decimated to a 100...
- `stutter-main-thread-commit-problem` — Stutter is a main-thread commit problem, and every commit is now cheap
- `build-cancellation` — Build cancellation: (`ChunkState.Cts`/`Generation`): every `source.Load*Async` gets the tile's token and the worker...
- `generated-fill` — Generated fill: every tile with no real data is generated and blended into the real tiles beside it (ownership, anchor, blend, merge, horizon, server, off switch)
- `generated-relief` — The generator: 500 m heightmap of CH embedded, lakes, drainage -> rivers/roads/rails/villages, 25 m + 5 m field lattices, gotchas (carve only down, wall span)
- `cachingchunksource` — `CachingChunkSource`: decorates the source chain with a byte-budgeted LRU of decoded tiles, so ground that is left...
- `data-location` — Data location: `--chunks` > `UNITSPORT_CHUNKS` > `terrain_location.json` (MapSetup's drive picker) > `terrain_chunks/`; game and server alike

## Commands

- `commands` — Commands: --builds, --fly, --horizon, --path, --probe, --rings, --shot, BlendCheck, --generated

## Gotchas

- `collision-build-load-road-tile` — A collision build must load the road tile even when the roads are already drawn
- `road-s-collision-core-takes` — A road's collision core takes the height at the cell's perpendicular foot on the centreline, nearest segment wins
- `concavepolygonshape3d-one-sided-collision-unless` — `ConcavePolygonShape3D` is one-sided for collision unless told otherwise, and geometry that "looks right" can still...
- `flat-shaded-quad-mesh-bilinear` — A flat-shaded quad mesh is NOT a bilinear surface, and a height query must match whichever one is actually on screen
- `window-rows-crop-unless-storeys` — Window rows crop unless storeys divide the wall exactly
- `never-derive-surface-tangent-from` — Never derive a surface tangent from the screen-space normal
- `vertex-colours-raw-linear-shader` — Vertex colours are raw linear; shader uniforms marked `: source_color` are not
- `mesh-holes-shrink-grow-lod` — Mesh holes must shrink, not grow, with LOD: Dropping a rendered quad when *any* full-res cell under it is carved...
- `jolt-heightmapshape3d-treats-nan-heights` — Jolt HeightMapShape3D treats NaN heights as holes
- `settled-wrong-question-frame` — `Settled` is the wrong question for a frame: It asks "is anything, anywhere, still loading". `SettledNear(eye,...
- `fresh-clone-has-no-terrain` — A fresh clone has NO terrain: the generated data is gitignored — so a missing `manifest.json` is an ordinary state,...
- `publish-terrain-before-things-stand` — Publish the terrain before the things that stand on it
- `rendering-coarse-preview-from-height` — Rendering a coarse preview from the height grid alone is WORSE, despite sounding better
- `build-throws-release-tile` — A build that throws must release its tile: `PendingStride` is only cleared on commit, so an exception or a missing...
- `commit-loop-gate-budget-result` — The commit loop must gate on the budget the result actually needs
- `tile-loads-chains-unordered-concurrency` — Tile loads are CHAINS, and unordered concurrency starves them
- `tile-worker-create-godot-object` — A tile worker must not create a Godot object after the engine starts tearing down
- `blend-convex-not-additive` — Blend two terrains with a convex mix, never an additive correction: the additive one dug a trench 150 m below the Rhône
- `judge-terrain-blend-shaded-relief` — Judge a terrain blend by shaded relief, and turn what the eye finds into a BlendCheck number
- `saved-max-builds-one-looks-broken` — A saved `maxConcurrentBuilds: 1` makes the loader look broken: check settings.json, pass `--builds 0` to probes
