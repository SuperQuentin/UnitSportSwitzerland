# Terrain runtime: streaming, LOD, meshing, collision (`src/Terrain/`)

Chunk streaming, LOD rings, mesh builders, collision, horizon, cover/pattern shading. How the road, tunnel, bridge, ropeway, watercourse and wall data is *built* is in `tools/CLAUDE.md`; how tiles travel over the network is in `src/Net/CLAUDE.md`.

Index only: one line per note in `docs/notes/terrain/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/terrain`.

## Architecture

- `roads-merged-into-terrain-collision` — Roads are merged into terrain collision, not just draped over it
- `sidewalks-tunnels-runtime` — #119 runtime: sidewalk slabs + chamfered kerb collision, blend under slabs/caps/bores, road depth bias, tunnel mouths (punch at surface ends only), bore floor/wall collision, safety nets skip bores and cut ramps, checks; #120 side profiles (grass, bike path, sloped kerbs) from `RoadStreetSection`
- `road-embankments-walls` — Road embankments (#125): level cross-section, 2:3 fill / 1:1 cut clamp in RoadBlend, retaining walls planned in RoadGen (LPRP), 40 cm crown with a cover over the heightfield step, cost
- `road-railings` — Road railings (#126): guardrails on fill-wall crowns, above drops and as back-to-back median beams; fences; RailingBuilder mesh and collision strip, racing-line obstacle
- `surface-patterns` — Surface patterns: `CoverPalette` writes a `SurfacePattern` code into vertex-colour alpha in quarter steps (0 none,...
- `water` — Water: its own mesh on the tile's still water layer (2 m near, 4 m far, wave scale in UV.x, no collision); legacy tiles from the cover; PS1 translucent, wave-displaced (#299)
- `water-level-layer` — In-memory still water per tile (#299): `WaterTile` (501² levels, NaN dry, optional fetch) from `IChunkSource.LoadWaterAsync`, `WaterLayer` (+ wave scale), legacy from cover, `ChunkManager.TryGetWaterLevel`; the shape #298 fills
- `windows` — Windows: `BuildingMeshBuilder` bakes facade UVs (metres along the wall, storey index) from the *triangle* normal;... fake rooms behind the glass, occupancy cues
- `facade-relief` — Recessed windows (parallax into the opening, reveals lit by the sun), sills, shutters, string courses and plinth, all in the building shader from the facade UVs
- `building-types` — Building types: `BuildingTypes` groups a tile's solids (a church's nave + bell tower) at runtime; one church interior, every...
- `ikea` — the nine IKEA stores named by position (#501): strict containment, not proximity (St. Gallen's point is 0.4 m from a tile edge); the blue box, the pylon, bins of Blåhajs; `--ikeacheck`
- `building-triangles` — read building triangles with `b.Tri(t)`; wall/roof split is `BuildingTriangles.RoofNormalY`, never a local copy
- `industrial-sites` — Industrial sites (#497, #496 phase 1): `BuildingTypes.SiteFor` invents warehouse/works/depot/body shop/dealership from the building alone; `InteriorGenerator.Industrial.cs` plans a full-height hall + a low service block (`RoomPlan.Clear`), aisles/lines/bays/plinths laid out by the hall itself, 25 new pieces, flat `SiteAbundance` loot, high-bay `RoomLights`, plan v13, `--sitecheck`
- `loading-bays` — Loading bays (#528, #496 phase 2): `DoorBudget.Bays` per site type, placed beside the main door and on both sides of it, `Hang = RollUp, Vehicle = true` and nothing else needed; the pier between bays is not `MinGap`; a showroom's front door is no longer a shutter; `--doorcheck` had encoded "every extra door is a pedestrian door"
- `generated-works` — A works in every generated village past its last house (#531), yielded LAST so no index moves; `tools/baycheck.sh` drives a lorry through a loading bay with a second peer watching — the first drive ever, and it found three bugs: furniture in every bay lane, a 4.5 m bay arriving at a 1.8 m doorway inside (plan v16), and the drive aiming at the office door
- `construction-sites` — Building sites (#606, epic #605): `ConstructionSites.Plan` reads the phase off the surveyed `Im Bau` height against GWR floors (foundations / shell / topped out), lays a rectangle round the plan box with the yard on the street side, drops hoarding panels on roads, places cranes (per share of the long side, inside the building when no room or reach), office, heaps, materials and machine slots with stable ordinals; `SiteObstacles`, `SitePlans`; 38 ms a site, plan views found three bugs the unit tests missed; #607: a site per generated village (hashed, yielded last), `SitesNear`, `--constructioncheck` with plan views (`SitePlanSvg`); #608: the solid replaced by a walkable half-built shell (`ShellPlans`, `SiteShellBuilder`, prop mesh, collision in the road cells), no door, interior, occluder or perch, `--shellwalkcheck`; #609: the yard dressed (`SiteDressings`: hoarding, containers, toilets, skips, materials, a walkable soil heap `ShellMound`, the builder's board) in the same mesh and collision; loot is #627; #610: tower and self-erecting cranes (`CranePlans`), posed by `CraneMotion` from the clocks (hours from the environment clock, motion from the simulation clock), `SiteCranes` per tile, a slewing jib box, MultiMesh obstacle lamps, the stair core moved off an inside mast
- `cellars-and-room-variety` — Cellars (`Below`, `FloorY`), shelters with blast doors, basement program (laundry, guest room, cinema, carnotzet, music room), new room/furniture types, logical room order, plan v8 (#213)
- `door-portals` — Doors open (shared, auto-close) and you walk (or drive, garages and barns) through them: `DoorLink` map, portal camera + clip plane, sill crossing, third-person arm through doors, near/far by a doorway, `--doorcam` check, vehicles, linked spaces, building sounds
- `interior-light` — Rooms lit by the hour in every style (#388): one interior body + PS1/Cartoon/Realistic wrappers, `RoomLights` table (window daylight, sun patches, lamps), indoor ambient, portals tonemapped once
- `wall-mirrors` — A mirror over every washbasin (`Interiors/WallMirror`, #439): reflected-eye viewport like the cab mirrors, only the nearest in front renders, every other frame; shows the VR player's own body and hands; `--portaldemo` shot `pd_mirror.png`
- `several-doors` — Several doors on one building (#498): `DoorKey` = building + slot (slot 0's text unchanged), `DoorBudget` spacing rules, extra doors along a long facade and round the back, a barn's pedestrian side door, `DoorHang`/`Vehicle` per door (a loading bay is one line), a doorway each inside, `--doorcheck`
- `apartment-blocks` — Apartment blocks (#557): a stairwell (lobby, switchback, elevator from 3 levels) per front door, flats laid out from their front door (T, spine, gallery, studio), corridors for wide and deep blocks, shops under flats, a shared basement with car park, laundries, compartments, shelter; working elevator (server `LiftRide`, group teleport), flats' front doors (leaf, synced, 40% locked, the dial); `--flatcheck`, `--flattour`, `tools/liftsynccheck.sh`, plan v21 (#571: half-landing stairwells, every living room and bedroom on a facade; #576: one-door bedrooms, 2 m corridors; #577: L/U/courtyard blocks planned wing by wing from the roof outline)
- `garage-ramp` — Underground garage door on a block of flats (#558, PR 1 of 3): `GarageRule` (flats, 3 front doors, the generator's own car-park strip rule, 40 % roll) shared by footprint and generator; `GarageLink.Choose` picks pavement + bollards (road edge within 8 m), an access-road stub (8-25 m, or a main road) or no door (no street, or a motorway, within 25 m) from `RoadTile.Segments`; drawn in the building mesh, stub collided; the door reads as locked until the ramp (PR 2)
- `door-size` — How big a door is, outside and inside (#509): `BuildingFootprint.FitUnderEave` is the one place a main door's height is settled (kind's own, capped under `box.Eave` by `DoorUnderEave`, floored at `MinDoorHeight`), a wall too low is demoted not squashed, `InteriorLayout.DoorHeight`/`EntrancePlan.DoorHeight` carry it inside so the opening matches, plan v15
- `perf-door-portals` — `DoorPortals`/`DoorwayGhosts`/`DoorLights` allocate nothing per frame (reused lists, static `StringName`s, `live` written on change, ghosts scanned at 10 Hz); interior `ArrayMesh` built on the worker, collision a frame later
- `runtime` — Runtime: (`src/`): `Terrain/ChunkManager` streams LOD rings around anchors (workers build arrays, main thread...
- `coarse-tiles` — Coarse tiles: every `.terr` has a `.terrc` companion — the same grid point-decimated at stride 10 (51x51, 5.2 KB...
- `far-horizon` — Far horizon: (`HorizonLayer`, `horizon.bin`, `tools/TerrainFormat/HorizonFormat.cs`): every tile decimated to a 100...
- `stutter-main-thread-commit-problem` — Stutter is a main-thread commit problem, and every commit is now cheap
- `build-cancellation` — Build cancellation: (`ChunkState.Cts`/`Generation`): every `source.Load*Async` gets the tile's token and the worker...
- `view-cone-priority` — The load queue is `ring × view weight`: on-screen tiles first, from the live camera's cone (FOV, 16 sectors, cheap re-sort)
- `generated-fill` — Generated fill: every tile with no real data is generated and blended into the real tiles beside it (ownership, anchor, blend, merge, horizon, server, off switch)
- `generated-relief` — The generator: 500 m heightmap of CH embedded, lakes, drainage -> rivers/roads/rails/villages, 25 m + 5 m field lattices, gotchas (carve only down, wall span)
- `generated-shaped-buildings` — Shaped buildings in generated villages (#598): L, U, T, courtyard, trapezoid, cut corners, bent bar, skewed block (`Plan.Outline`, `AddShaped`, last in a tile); angled outlines planned as one wing; `--shapedcheck`, coordinates near the spawn
- `generated-roads-roadgen` — #559: generated road tiles go through RoadGen's network stage at runtime (halo 1, line keys, stride-5 ground, shared UrbanField, the stage writes into its input), BlendCheck --generated-roads, --generated-roads raw
- `cachingchunksource` — `CachingChunkSource`: decorates the source chain with a byte-budgeted LRU of decoded tiles, so ground that is left...
- `road-markings` (tools note) — v3 road paint: `RoadPaintBuilder` draws the `.road` PANT layer as a second road surface (style 6, depth bias, dither fade)
- `download-job` — Background region downloads (#515): `DownloadJob` runs MapCore's Planner steps on a worker, survives worlds deliberately, polled progress; new tiles arrive on the next world load
- `data-location` — Data location: `--chunks` > `UNITSPORT_CHUNKS` > `terrain_location.json` (MapSetup's drive picker) > `terrain_chunks/`; game and server alike
- `perf-lod-trees` — Ring strides by screen-space error, trees thinned by ring (`VisibleInstanceCount`), shared unit tree meshes, free every replaced mesh
- `landings` (world) — `PierMeshBuilder`: a tile's piers and jetties as one more roads-mesh surface (Prop role) and road collision cells; `IChunkSource.LoadLandingsAsync`; `ChunkManager.RebuildPiers` when the landings change
- `parking-runtime` — Car parks at runtime (#499): the pad through `PavementBuilder` with an embankment round its edge (#603), planters through `IslandBuilder`, walks as #119 sidewalks, bay lines and the disabled roundel as `PNT2`, `ParkingBuilder` for the boom/kiosk/shelter/P sign, the `PARK` bay list as a wire contract
- `perf-collision-commits` — Collision is queued and committed one 4x4 cell piece a frame, nearest a body first; a new collision layer must go through that queue
- `perf-static-city` — what a city's static things cost (#553): CPU-bound frames, buildings 1.1–1.6 ms of GPU, the dormant layer ~0.3 ms, shop signs 1 300 draws → drawn to 200 m; tiles past the building/road ring shed them (`TileUnfurnished`)
- `occlusion-culling` — buildings as occluders near the ground (#553): 125 m building cells round the camera, box occluders inset 0.5 m for buildings that fill their plan box, cells only under 40 m above ground, off in VR and during a sightline cut
- `perf-ring-key` — `EvaluateRings` compares its inputs in `RingKeyChanged()` (no string key); a new desired-set input goes there

## Commands

- `commands` — Commands: --builds, --fly, --horizon, --path, --probe, --rings, --shot, BlendCheck, --generated, --roadcheck --sidewalks/--floorat, --roadperf

## Gotchas

- `collision-build-load-road-tile` — Any collision or near-field build must load the road tile even when the roads are already drawn (or the ground swallows them after flying away and back, or on the #553 street-level cell switch, #603)
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
