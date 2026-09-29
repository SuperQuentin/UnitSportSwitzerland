# UnitSportSwitzerland

Godot 4.7 C# multiplayer game streaming real swissALTI3D terrain as low-poly PS1-style
world. Long-term goal: all of Switzerland navigable. Plan: `~/.claude/plans/i-want-to-build-dynamic-metcalfe.md`.

## Working with subagents

- Use the cheapest model that can do the job. Delegate to `model: "haiku"` for mechanical work
  with no design decisions: transcribing/porting code to an existing pattern, settings/menu
  plumbing, docs updates, running builds and check commands (`--shot`, `--flycheck`, ...) and
  summarising their output, bulk renames, file searches.
- Use `model: "sonnet"` for well-specified implementation inside an interface that already
  exists (one DSP voice, one generator, one mesh builder), where the spec is written down.
- Keep on the main (Opus) model: architecture and interfaces, anything touching threading /
  the streaming loader / networking authority, debugging with unclear causes, and reviewing
  what subagents produced.
- Pin the shared interface before fanning out; give each agent an explicit list of files it may
  create or edit, so parallel agents never write the same file.
- **Haiku currently cannot start in this environment**: the connected MCP servers (kicad, godot-ai,
  Trello, ...) bring the system prompt plus tool definitions to ~230k tokens, over Haiku's 200k
  window, so every Haiku agent fails before its first step ("Prompt is too long"). Until fewer
  MCP servers are enabled for this project, send mechanical work to Sonnet instead.
- Parallel agents share one session usage limit: fanning out 6+ agents at once can exhaust it
  and kill all of them together. Prefer 2-3 at a time.

## Architecture

- **Data pipeline**: swissALTI3D XYZ zips (`ressources/data/swiss_chunks/`, LV95/EPSG:2056,
  0.5 m grid, 1 km tiles) → `tools/TerrainPreprocessor` → `.terr` binary chunks +
  `manifest.json` in `terrain_chunks/` (**1001×1001 vertices, 1 m grid** — format `Version = 2`,
  global uint16 quantization so tile seams are bit-identical). Runtime never parses XYZ.
  1 m was chosen over matching the source's 0.5 m exactly: it still recovers real detail the old
  2 m grid discarded, at 4× the vertex count per tile rather than 0.5 m's 16×, and `GridSize-1`
  (1000) still divides every LOD/coarse stride (1, 2, 4, 10, 20) cleanly, so the ring table needed
  no redesign. **The source-cell averaging (`TileReducer`) is ratio-general** (`Ratio =
  XyzParser.CellsPerSide / (GridSize-1)`, currently 2): it used to hardcode a 4-cell window
  regardless of ratio, which was only correct by coincidence at ratio 2 and silently discarded 12
  of the 16 cells a 2 m vertex's true footprint covered at the old ratio 4 — real, measurable
  aliasing, confirmed by rebuilding the same tiles both ways and comparing (field boundaries and
  mountain rock texture visibly sharper after the fix; a furrow micro-pattern in flat fields was
  entirely smoothed away before it). **Cover-class boundaries are blended, not hard-edged**:
  `TerrainMeshBuilder.BoundaryBlendedColor` averages a vertex's colour with any of its four
  cardinal neighbours that hold a different class, so a forest/meadow edge is a gradient across
  one quad instead of an instant jump — worse before this at coarse LOD strides, where a single
  20-40 m quad could straddle the whole boundary. A region built before `Version = 2` has 2 m
  tiles and must be fully re-preprocessed (`--verify`, then `--coarse`, then `RoadGen --rewrite`)
  before its coarse companions and junctions are valid again.
- **Shared format code**: `tools/TerrainFormat` classlib (TileId, ChunkFormat, ChunkGrid,
  ChunkCodec, TerrainManifest) — referenced by both the preprocessor and the game csproj.
  The game csproj excludes `tools/**` from its wildcard compile.
- **Roads/rail**: swissTLM3D GeoPackage (`ressources/data/tlm3d/*.gpkg`, SQLite + R-tree,
  read directly from C# — no GDAL) → `.road` binary per km tile in `terrain_chunks/`
  (~2 MB for 42 tiles). Classified by width (`objektart`), surface paved/dirt
  (`belagsart`), hiking (`wanderwege`), cycling (Veloland `TLM_ID` join via
  `tools/export_route_keys.py`), and bridge/tunnel/stairs (`kunstbaute`). Polylines are
  clipped to tile boundaries, densified to ≤4 m, and draped onto the terrain.
- **Aerial ropeways**: `tlm_oev_uebrige_bahn` -> `RoadClass.Cableway/Chairlift/SkiLift/RopeTow`,
  carried in the `.road` file but **not draped** — TLM digitises these along the *cable*, verified
  against our own heightfield: chairlifts run a median 11.9 m up, gondolas 14.6 m, and the
  Isérables tramway spans the Rhône valley 200 m clear of the ground. `RoadMeshBuilder` draws the
  cable as two ribbons crossed in a plus (a single flat one vanishes edge-on) and grows a tower
  from the terrain under every vertex — which is where the real pylons are, because that is where
  a cable changes direction. `Foerderband` and `Lift` are dropped: a conveyor and a building lift
  are not ropeways.
- **Watercourses**: `tlm_gewaesser_fliessgewaesser` -> `Watercourse`/`DryChannel`/`Bisse`, draped
  like roads (their Z sits on the ground — median offset −0.14 m) and meshed by
  `WaterMeshBuilder` so they take the water material instead of being drawn as narrow blue roads.
  This is what puts water in the mountains at all: the cover raster only finds water wide enough
  to register on the 2 m lattice, which in alpine terrain is almost none of it. `Druckstollen`
  (a pressure tunnel, 232 m *underground*) and `Druckleitung` (a penstock ~5 m above ground) are
  excluded — they are hydro plumbing, and drawing them would run rivers through mountains — as
  is anything whose `verlauf` is `Unterirdisch`, which is 42% of the channels around Riddes and
  would otherwise put streams down the middle of village streets.
  **The raster wins over the lines**: `WaterMeshBuilder` drops any channel that mostly runs
  inside already-mapped water, because the Rhône is in this dataset as a centreline like every
  other river and drawing it laid a 2.5 m creek down a 50 m channel.
- **Protective works and walls**: `tlm_bauten_verbauung` -> `AvalancheBarrier`/`TorrentWorks`/
  `DryStoneWall` and `tlm_bauten_mauer` -> `Wall`, carried in the `.road` file and extruded
  upward by `RoadMeshBuilder.AppendWall` rather than laid flat. They keep their surveyed Z
  because for a `Schutzverbauung` that Z is the **top** of the structure — measured a median
  2.80 m above our heightfield with a p90 of 5.81 m, the real height range of snow bridges — so
  each is grown from the terrain up to it. Walls and torrent works are digitised much closer to
  the ground (+0.15 to +0.95 m) and are clamped to a per-class minimum, or they would render as
  kerbstones. Region: 5.8k barriers (166 km), 3.7k torrent works, 3.7k dry-stone walls (657 km).
- **Named summits and passes**: `tlm_namen_name_pkt` -> `places.json` alongside the GWR towns,
  so **Tab** finds mountains. `Place.Kind` and `Elevation` are what let search rank a peak among
  peaks by height and a town among towns by size — a mountain has no buildings, so without a
  per-kind `Rank` every summit sorted below the smallest hamlet. Names are multilingual and
  pipe-separated (`Nordend | Punta Nordend`); the first form is kept. Region: 329 towns, 1,243
  summits, 402 passes. **This layer is POINT geometry** — `GeoPackageReader.ParseLines` returns
  nothing for it and fails silently; use `ParsePoints`.
- **swissTLM3D maps NO ski pistes.** Checked every one of the 41 layers: the only piste values
  are `Graspiste`/`Hartbelagpiste`, which are airfield runways, and `Skisprungschanze`, a ski
  jump. Downhill runs would have to come from OSM (`piste:type=downhill`) or be synthesised.
- **Railways**: `tlm_oev_eisenbahn` carries gauge (`objektart` Normalspur/Schmalspur),
  `anzahl_spuren`, `zahnradbahn` (rack), `standseilbahn` (funicular), `ausser_betrieb`
  and `auf_strasse`. Rendered as a ballast ribbon plus real rail geometry
  (`RoadMeshBuilder.AppendRails`) at 1.435 m / 1.0 m gauge, doubled for two-track lines.
  Sleepers are a shader stripe, not geometry — at 0.65 m spacing they would cost tens of
  thousands of triangles per km for a few pixels.
- **Type changes blend**: swissTLM3D splits a road wherever any attribute changes, so a
  widening or an asphalt-to-gravel change is two features sharing an endpoint, and ribboning
  them independently leaves a step — a shoulder sticking out of the carriageway plus a hard
  colour seam. `RoadMeshBuilder.FindTypeJoins` indexes segment endpoints per tile, and where
  exactly **two** meet (three is a junction, already covered by its polygon) pulls both to the
  *mean* width and colour at the shared vertex, then eases each back to its own over 5–22 m.
  Taking the mean is what closes the step: tapering each side toward the other's value still
  arrives at two different numbers. Smoothstep, not a linear ramp — a straight ramp leaves a
  crease where the rate of change jumps. Roughly 94 such joins per 16 tiles.
- **Road markings**: swisstopo publishes NO lane/marking dataset (`tlm_strassen_strasseninfo`
  is junctions and POIs, not lanes). Markings are therefore *inferred* at build time from
  width class + `belagsart` + `richtungsgetrennt` into a `MarkingStyle`, baked into uv2.x,
  and drawn by `ps1_road.gdshader` from uv = (metres along, lateral in [-1,1]).
  Divided carriageways deliberately get edge lines and no centre line.
- **Tunnels**: `kunstbaute` (Tunnel/Unterfuehrung/Galerie) segments keep their surveyed Z
  and get an extruded arch bore (`RoadMeshBuilder.AppendTunnelBore`). `TunnelCarver`
  writes a `.holes` file per affected tile listing terrain quads to omit, so portals are
  open; `TerrainMeshBuilder` skips those quads and writes NaN into the collision map —
  Jolt treats NaN heightfield cells as holes, so tunnels are enterable (verified with
  `--probe`). The bore is extruded 5 m PAST each end of the centreline and capped with a
  headwall (wall with the arch cut out) plus wing walls — carving alone leaves the ground
  mesh with raw edges and the bore floating in the gap, so the wall is what actually joins
  tunnel to terrain. `TunnelCarver` extends its carve by the same 5 m.
  The **sides of the cut are lined by `TerrainMeshBuilder.AppendCutWalls`**, generated from
  the hole mask and the same height grid the surface uses — geometry built from the road
  centreline can never meet a hole quantised to the 2 m lattice, which is why the earlier
  wing walls floated. Both the bore height and the headwall are **clamped to the cover that actually exists**
  (`MinCover` / `TerrainAbove`): `Unterfuehrung` under a rail embankment may have only 3 m
  over it, and a fixed-height bore would stand above the track it passes under.
- **Bridges**: `kunstbaute = Bruecke` segments keep their surveyed deck Z and get a deck
  slab (soffit + side fascia), edge parapets, and piers spaced ~25 m
  (`RoadMeshBuilder.AppendBridgeStructure`). Piers sample the terrain via the tile's
  `ChunkGrid` and are skipped above `MaxPierHeight` — TLM3D has no bridge-type attribute,
  so tall gorge crossings are left as unsupported spans rather than sprouting 130 m columns.
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
- **Buildings**: swissBUILDINGS3D 3.0 LoD2 TINs -> `tools/export_buildings.py` (GDAL, the
  only step needing it) -> `buildings.gpkg` -> `.bldg` per tile. GWR cadastre is joined
  **spatially** (EGID is null in the 3.0 Beta); classification uses GKLAS, not GKAT.
  Roof vs wall is decided per triangle by normal; year built tints tone.
  Solids are **re-seated on our heightfield** (median of a 3x3 footprint sample, base set
  0.8 m below ground): the source foundation block is referenced to swisstopo's terrain,
  not ours, which buried every building by ~3 m and some by over 5 m.
  **Stray faces are dropped first** (`BuildingExtractor.DropStrayFaces`): nationwide, 798
  "single houses" in swissBUILDINGS3D 3.0 span over 200 m (the worst 4.3 km) because their solid
  carries faces far from the building, and some carry a face 300 m below it. Left in, the 3x3
  re-seat sample landed on a distant hillside (shifts of 770 m) and the faces drew slivers across
  the map. A face goes when a vertex is further than max(150 m, 4x the median face distance) from
  the median face centre, or 180 m above/below the median face height. A whole solid that is
  consistently off (164 m in one Geneva batch) is not stray — re-seating is what fixes that.
- **France (cross-border)**: IGN **BD TOPO®** via the Géoplateforme WFS (`data.geopf.fr`, Licence
  Ouverte 2.0) -> `FranceStage`, run as `--france minLon,minLat,maxLon,maxLat`. No GDAL, no
  download: a bbox query returns GeoJSON, projected WGS84 -> LV95 on arrival so French data lands
  on **the same kilometre lattice** as the Swiss and the two share a tile.
  **swissALTI3D already covers a few km past the border**, so terrain was never the gap — only
  the things standing on it. Buildings come as footprint + `hauteur` + `altitude_min/max_toit`,
  i.e. eave *and* ridge, so `FranceBuildings` pitches a roof where those genuinely differ (568 of
  1361 around Veigy) and leaves the rest flat rather than inventing a shape. Roads map
  `nature` -> `RoadClass` and use the surveyed `largeur_de_chaussee`, which is better than the
  Swiss side, where width is inferred from a class.
  **The stage merges, never replaces**: border tiles already hold Swiss data (2506/1125 is 1.1 MB
  of Swiss buildings), so it decodes, appends and writes back. Re-runs are safe — French buildings
  are marked with `YearBuilt = 1` (BD TOPO has no build year, so nothing French ever has a real
  one) and roads keep a `.road.swiss` copy of the original.
  Not imported: land cover, trees, and cycle routes — the `amenagement_cyclable_*` fields come
  back null from this WFS, so no French road is ever flagged `Cycle`.
- **Land cover**: **six** TLM area layers are rasterised onto the 501x501 vertex lattice ->
  `.cover` (deflate, ~2 KB/tile) -> baked into terrain vertex colours by `CoverPalette`.
  They are drawn in order of increasing specificity, each overriding the last:
  `tlm_bb_bodenbedeckung` (forest, rock, scree, boulders, water, wetland, glacier,
  snowfield) -> `tlm_areale_nutzungsareal` (vineyard, orchard, nursery, allotment,
  cemetery, park, quarry, landfill, industrial, institutional, clearcut, military) ->
  `tlm_areale_freizeitareal` (sports ground, golf, pool, campsite, zoo) ->
  `tlm_bauten_sportbaute_ply` (the pitch itself, tighter than the surrounding ground) ->
  `tlm_bauten_verkehrsbaute_ply` (runways, grass strips, station platforms) ->
  `tlm_areale_verkehrsareal` (parking, last so a car park beats everything).
  Unmapped ground falls back to altitude bands — TLM maps **no arable parcels at all**, so
  farmland, meadow and the ground between village houses genuinely are not in the data.
- **A land-use polygon must never overwrite Water in the cover raster.** The layers are stamped
  in order of increasing specificity, which is right for land use — an allotment inside a park
  should win — but a `nutzungsareal` polygon is an administrative boundary, not a ground surface,
  and several are drawn straight across a river. The gravel extraction areas beside the Rhône at
  Riddes are mapped as `Abbauareal` *over the water*, which erased the river from the raster: the
  Rhône rendered as a gap in the middle of its own course. `CoverExtractor.MarkIn` now refuses to
  overwrite `Water`. A quarry does not flow.
- **Vineyards live in `nutzungsareal`, not `bodenbedeckung`.** `Reben` is a *land use*, so
  looking for it among the ground-cover classes silently returns nothing and the whole
  Valais renders as generic pasture. Same for orchards (`Obstanlage`).
- **Surface patterns**: `CoverPalette` writes a `SurfacePattern` code into vertex-colour
  **alpha in quarter steps** (0 none, 0.25 parking bays, 0.5 vine rows, 0.75 mown stripes)
  and `ps1_terrain.gdshader` dispatches on `int(COLOR.a * 4 + 0.5)`. TLM records no
  orientation for any of them, so every pattern runs on world axes — and the direction
  cannot be recovered from the screen-space normal (see the confetti gotcha below).
  Alpha interpolates across a class boundary, so a pattern bleeds one 2 m cell.
- **Trees** come from three sources into one `.trees` file, told apart by `Kind`:
  **0/1** random scatter in the wooded classes, **2** orchards and nurseries planted on a
  world-anchored grid (6 m / 4 m — a random scatter at the same density reads as scrub, the
  rows are the point), **3** `tlm_bb_einzelbaum`, TLM's 11.5 M *surveyed* single trees in
  villages, along field boundaries and beside roads. All three respect
  `CoverStage.BuildRoadMask`. `ChunkNode.SetTrees` builds **two** MultiMeshes per tile
  because a MultiMesh carries exactly one mesh: a cone for 0/1 and a bipyramid crown on a
  trunk for 2/3, since a broadleaf drawn as a spire turns an orchard into a plantation.
  **The cone stands on a trunk too** (foliage from 28% of the height): it used to reach down to
  15%, so a 25 m spruce was 13 m wide at eye level and walled in every forest trail. Same apex and
  ring, so the canopy from above is unchanged; 20 triangles per conifer instead of 10, no measured
  frame-time cost. **`ps1_tree` also dissolves trees close in front of ANY camera** (`near_fade*`
  uniforms: fully gone inside 1.5 m, solid past 8 m, only within the forward cone so the
  periphery still encloses you) with the same Bayer discard as the sightline cut. It is pure
  view-space shader maths, so on foot, the ride chase cam, free fly and the replay cameras all
  get it with no C# per frame.
  Current region: ~40 M trees, of which 0.87 M planted and 1.65 M surveyed.
- **Water**: built at runtime from the Water cover class, not a separate file —
  swissALTI3D already models lakes/rivers as flat surfaces at water level, so the terrain
  height at a water cell *is* the water level, and rivers keep their downstream gradient
  for free (`WaterMeshBuilder`, +0.12 m lift, `ps1_water.gdshader`).
- **Windows**: `BuildingMeshBuilder` bakes facade UVs (metres along the wall, storey
  index) from the *triangle* normal; the shader draws the window grid from those. Storey
  height comes from GWR `GASTW` (69% coverage), else wall height / 2.9 m. Barns, garages,
  tanks and anything under 3 m opt out with uv.y < 0.
- **RoadGen** (`tools/RoadGen/`, standalone, no Godot): a lab for road *geometry*. Builds a
  road network graph (endpoint snapping, X-crossing noding, T splitting, all layer-aware),
  fits **clothoid** spiral-arc-spiral corners so curvature never jumps, then makes junctions
  **explicit polygons** that the roads stop at instead of overlapping. Markings are offset
  curves generated only between the junction trims. Exports SVG plan views and OBJ, and
  self-checks (seam gap, chord budget, endpoint drift, degenerate triangles) with a non-zero
  exit on failure. `--demo` runs four hand-built scenes with no data at all; `--tiles` reads
  real `.road` files; `--synth` grows a network from a tensor field. Not wired into
  `TerrainPreprocessor`; `--rewrite` post-processes built `.road` tiles in place instead, which
  is what actually gets junctions into the game — see its README for why that seam and what is
  missing.
- **`.road` format v2** adds junction polygons after the segments, counted in the header word
  v1 left reserved, so every v1 offset is unchanged and v1 files still decode. A region built
  before the rewrite renders exactly as it did. `RoadMeshBuilder.AppendJunction` draws the caps
  with no lane markings — painting them would put back the crossing lines the junction exists to
  remove. **The rewrite is not idempotent and refuses to run twice**: the second pass trims
  already-trimmed roads and replaces the full-size caps with near-zero ones, leaving a hole at
  every junction.
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
- **Camera sightline cut** (`ChunkManager.SetSightlineCut`, driven per frame by
  `PlaybackCamera.UpdateSightlineCut`): when a ray from the camera to the runner's head hits
  something, a corridor along that segment is **dissolved with a Bayer-dither `discard`** in
  `ps1_building`/`ps1_tree`. Dither, not alpha: those shaders are `unshaded` with no blend mode,
  the trees are one MultiMesh sharing a single material so per-instance transparency is not
  available, and a dithered dissolve is already this renderer's visual language. One uniform write
  reaches the whole streamed world however much has loaded since. The radius is **ramped**, never
  switched - a corridor that snaps open reads as geometry popping out of existence - and it settles
  to exactly 0 so the shaders take their disabled branch. **Terrain and roads are deliberately
  excluded**: dissolving ground opens a hole straight through to the sky, which looks far worse
  than the hillside it was hiding, and a camera behind a ridge is already rejected outright by
  `ShotContext.CanSee` before the shot is committed. A road lying flat never occludes anything.
- **Zoom bubble** (`ZoomBubble`, driven from `PlaybackCamera.Step`): whenever the ACTIVE camera
  is far enough from the runner that they are a few pixels (a wide Locked-off tripod, a Free
  camera flown across the valley), a comic speech bubble pops up holding a **live close-up** of the
  runner, its tail pointing at where they are in the main picture. It replaced a red "HERE" arrow,
  which said where the runner was but still left them too small to see. The inset is a 256² 
  `SubViewport` with `OwnWorld3D = false`, so it renders the same streamed world with no extra
  loading (the runner is already an anchor), from a chase camera 4.5 m behind along `Heading`,
  eased and clamped above the ground; its update mode is `Disabled` whenever the bubble is hidden,
  so it costs nothing up close. `CanvasLayer` 6: above `LensLayer` (5) so the barrel distortion
  does not bend it, below the HUD (10). A runner off screen or behind the lens pins the bubble to
  the nearest edge, tail pointing outward. Trigger is pure distance with hysteresis (shows past
  35 m, hides under 25 m). Exported videos include it — the layer draws into the root viewport.
  **HUD Bubble button / `--bubble off`** (`--arrow off` still accepted) turns it off.
- **Modes** (`Core/MainMenu`, `GameMode`): Explore / GpxReplay / Multiplayer. `ClientWorld`
  owns the switching; **Esc** opens the picker, and it is shown at boot unless a mode was
  named on the command line (`--connect`, `--gpx`) or a verification tool is running
  (`--shot`, `--probe`). Each mode owns the camera while it runs, so `GpxSession.Begin`/`End`
  activate the playback camera + HUD and hand the previous camera back on the way out —
  `SetReturnCamera` matters because Explore may have swapped to the on-foot camera since.
  The menu also owns the mouse: opening releases the pointer, closing recaptures it, which
  is why `SpectatorCamera` no longer handles Esc. `--menu` forces the picker open (and is
  how it gets screenshotted).
- **Avatars** (`src/Avatar/`): procedural low-poly figures and a road bike, built from two
  primitives only — a tapered tube and a box (`MeshScratch`) — so each is one surface and one
  draw call. `HumanMeshBuilder` poses a figure from a table of joint positions (Standing,
  Running, Cycling); `BikeMeshBuilder` uses real 700c geometry (0.99 m wheelbase, 0.27 m bottom
  bracket, saddle at 0.90 m) because a bike is a shape everyone knows. Walking and running are
  **one gait** in `HumanMeshBuilder.GaitRig`, solved from the constraint that a planted foot
  travels backwards at exactly the body's speed; the walk/run changeover is the duty factor
  crossing 0.5, which is what creates the flight phase. `Cyclist` combines them
  and splits the mesh three ways — frame, rider, and per-leg — so the cranks turn with cadence
  and the knees follow by a two-bone solve rather than keyframes. Preview with
  `<godot> --path . -- --avatars <seconds> <out.png> [--view deg] [--focus 0..4]`;
  `--crank <rad>` parks the cranks and `--stride <m/s>` lays one gait cycle out as a strip.
  Neither a crank's direction nor a foot's slip can be judged from a single frame.
  All limbs go through `Limb.Solve` (two-bone IK), never keyframes.
- **A riding position is derived from the bike, never eyeballed.** The three contact points are
  fixed — hips on the saddle, hands on the drops, feet on the pedals — so the shoulder is the
  one place a 0.52 m torso and a 0.58 m arm can both reach. Hand-placing those joints produced a
  rider lying horizontally in front of the bars; with the ends pinned, the middle is not a free
  choice.
- **Judge model proportions with a long lens.** The avatar preview's focus camera sits 9 m back
  at 13° FOV, near-orthographic. A close wide-angle view of a bicycle enlarges whichever end is
  nearer and makes correct geometry look wrong — that cost an iteration of "fixing" a rider that
  was already right.
- **Input** (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered
  **in code** at boot (`PlayerInput.Install`, called from `ClientWorld._Ready` after
  `GameSettings.Load` so the saved deadzone applies), bound to keyboard (physical keycodes, so
  AZERTY still works), mouse and gamepad. Query through the static facade (`Move`, `LookRate`,
  `Steer`, `Held`, `Strength`, `Rumble`) rather than `Input.IsPhysicalKeyPressed`: it returns
  neutral while `UiFocus.TextEntryActive`, so callers no longer each check for typing. Pad layout:
  left stick move/steer, right stick look (squared response, `StickSensitivity`/`InvertY`),
  A jump, B slide, L3 sprint (latched until the stick is released), RT/LT throttle/brake (analog
  straight into `RideInput`), X tuck/sprint, Y mount picker, R3 camera toggle, Start menu,
  D-pad down fly/foot toggle. Menus call `PlayerInput.FocusFirst` on open so Godot's built-in
  `ui_*` actions drive them with the D-pad, and `MainMenu` holds `UiFocus` while open or the
  stick navigating it would also walk the player. Tab (place search) stays keyboard-only: a pad
  can't type in it. The facade is the seam an OpenXR backend plugs into later.
  Godot's built-in `ui_accept`/`ui_cancel` have **no** face buttons by default (the D-pad moved
  focus but A pressed nothing), so `RegisterActions` adds A/B and the left stick to the `ui_*`
  actions. **GPX replay** has its own pad layout in `GpxSession.HandlePad` (A play/pause, Y
  camera, X snap, RB next runner, LB hide UI, D-pad ←/→ seek 10 s, ↑/↓ speed; right stick looks
  in Free), read in `_Input` rather than `_UnhandledInput` because a HUD button left focused by a
  mouse click would otherwise swallow A and the D-pad.
- **Third / first person** (`FootPlayer`, **V / R3**, saved as `GameSettings.ThirdPerson`, default
  third; `--view first|third` for one run). On foot the mouse/stick turn a **view yaw**
  (`_viewYaw`), not the body: first person sets the body to it every render frame (the old
  behaviour exactly), third person lets the body turn to face its travel (`FaceTravel` — toward
  the input while there is some, else the velocity) and orbits a spring-arm camera
  (`UpdateThirdPersonCamera`) from above the right shoulder in **global** space — parented to the
  turning body it would swing round every direction change. Movement is relative to the view, so
  forward is into the screen in both. Both cameras update in `_Process`, not physics, or look lags
  the mouse by up to a physics tick. The local body is the same `HumanMeshBuilder` figure remote
  players see: solved gait grounded, `Running` pose airborne (>0.12 s), `Tucked` sliding, and the
  landing-dip spring spent as a squash. Mounted first person sits at the figure's own eye
  (`Rideable.FirstPersonEye` from `MountsForPose`), rolled with the lean.
  Screenshot the player's view with `--ride foot|bike|skis,seconds,out.png` (`foot` stands still).
- **Feel layer** (`Player/PlayerFeel`, child of the LOCAL `FootPlayer` only): sound, camera shake,
  speed lines, particles, pad rumble and a small HUD (km/h when mounted, "AIR x.x s" popup after
  >0.7 s airborne). It only **listens** — `FootPlayer` raises `Landed(fallSpeed)`, `Jumped`,
  `WallJumped`, `SlideStarted`, `Impacted(lostSpeed)` and exposes `GroundSpeed`, `Motion`,
  `LastRideInput`, `IsViewing` — so nothing in it can move the player, and it mutes and hides
  itself whenever another camera is on screen. Intensity is `Excitement`: speed against what is
  ordinary *for the current mount* (foot 4.8→9, bike 9→18, skis 9→22 m/s). **All audio is
  synthesised at startup** (`Audio/SfxSynth`: shaped noise → `AudioStreamWav`, loops crossfaded
  so the seam does not click) — the project has no audio files; replace any property with a
  sample to upgrade one sound. **No wind loop**: a synthesised one was tried and removed at the
  user's request — shaped noise reads as hiss, not air; wind needs a real recording. Shake goes through `Camera3D.HOffset/VOffset` (trauma², decaying),
  which no camera placement code writes, so it never fights the rigs. Speed lines are
  `shaders/speed_lines.gdshader` on a CanvasLayer at 4. Settings → Feel: volume, shake, speed lines.
- **Audio** (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus
  that `SfxBus.Ensure()` creates at boot). **One-shots vary**: `SfxBank` bakes 6-8 variants per sound,
  each drawing its own parameter jitter before any noise, and `Pick` never repeats the last one and
  adds ±4% pitch / ±1.5 dB. `SfxSynth.XxxBank` for the classics; the old properties return variant 0.
  The trick chime climbs a major pentatonic on a streak (`PlayerFeel.PlayChime`).
  **Engines are live** (`EngineSynth`, an `AudioStreamGenerator` filled from `_Process`): firing
  pulses at rpm·cyl/2 with uneven cylinders, a fixed exhaust comb resonance (it must NOT follow rpm —
  that is what a pitch-shifted loop got wrong), intake noise, prop beat, decel crackle; the
  turboshaft does blade slap + whine. Its per-sample `EngineFrame` goes to an `IChipVoice` picked by
  `GameSettings.EngineVoice` (Settings dropdown, `--voice realistic|ps1|nes|sid|genesis`): PS1 SPU
  (real 28-sample ADPCM codec + gaussian playback, default), NES 2A03 (period-register pitch steps,
  short-mode LFSR, nonlinear mixer), C64 SID 6581 (hard sync + resonant filter), YM2612 (4-op FM,
  feedback, PSG noise, 9-bit ladder DAC). `Surfaces.At` picks footstep/landing banks and ski hiss
  colour from road-under-feet then cover (`Surfaces.Origin` must be set); `ReverbZones` eases the
  bus reverb (indoors/tunnel/forest/valley/high). `Ambience` (volume setting): cowbells on pasture
  700-2450 m, a Farnell bubble brook near watercourses, per-forest bird species seeded by tile,
  church bells on the hour at towns < 1.5 km (local clock — the game has none), alpine rockfall.
  Check: `<godot> --headless --path . -- --soundcheck <dir>` writes every bank variant and a 7 s
  rev sweep per voice × profile as WAV, non-zero exit on NaN or clipping.
- **Game / Sim profile** (`GameSettings.RideProfile`, Settings → Movement, `--profile game|sim`,
  default Game; `Rideable.Arcade`). Game is an arcade layer on the SAME equations: bike 350/900 W,
  0.88 rad lean, harder brakes; skis deeper edges, half the carve scrub, faster skating; running
  5.8 m/s. Sim is the untouched real-world model, the only one where `Bicycle.RiderWatts` (the
  home-trainer input) means anything. `--ride` forces Sim unless `--profile` is given, so its
  reference numbers (180 W → 32.7 km/h) stay checkable.
- **Tricks, landings, boost** (mounted, `FootPlayer`): hold **Trick (F / RB)** in the air and the
  stick flips (`_airPitch`) and spins (`_airSpin`) the rider+machine VISUAL — the body keeps its
  heading. Released, leftover rotation eases to the nearest whole turn. `GradeLanding` (air > 0.3 s)
  grades the residual angle: < 0.5 rad clean (named trick, speed kick, boost), < 1.1 sloppy (−45%
  speed), else bail (stopped, 1.2 s on the ground). **Boost (Q / LB)**, Game only: +7 m/s² while the
  meter lasts (0.4/s); filled by clean air and tricks. `Announced(text, good)` drives the popup +
  chime in `PlayerFeel`. The visual is rotated about a pivot 0.9 m up, not its origin at the
  contact patch, or a flip swings the bike through the ground.
- **Flying** (`Player/Flight.cs`, meshes in `Avatar/AircraftMeshBuilder`): a `Flyer` is a
  `Rideable` whose `Step` is unused — it owns a full 3D velocity and attitude (`FlightMotion`),
  because a ground vehicle is a speed along a heading and none of climbing, diving or banking fits
  that. `FootPlayer.FlyPhysics` carries the velocity through `MoveAndSlide`, turns anything the
  world took off past `CrashSpeed` into a crash (on foot, dazed 1.5 s — not a respawn), and poses
  the visual from the attitude about `Flyer.Pivot` while the capsule stays upright and yaw-only.
  `RideKind` 3–7 appended (never reordered: replicated as an int).
  - **Base jump**: not a mount. On foot, Jump while falling (vy < −3) with > 12 m under you →
    **wingsuit** (lift/drag polar, point mass; a fall pulls out into a glide on its own). A bare
    polar porpoises for ever (measured −36 m/s dive → 13:1 zoom → repeat), so sink is damped toward
    the polar's steady glide: settles at **133 km/h, 2.7:1, 13 m/s sink**. Jump again → **parachute**
    (glide 2.1, 4.2 m/s sink, opening shock from 145 to 36 km/h in 1 s); touching ground → on foot.
    Wingsuit touching ground over 12 m/s = SPLAT. Proximity (< 20 m AGL at > 30 m/s) is scored.
  - **Paraglider** (picker): the same `Canopy` model at 9.1:1 / 38 km/h; on the ground push forward
    to run, Jump to launch; stays worn after landing.
  - **Helicopter** (picker): the look sets the heading (`LookSteers`, mouse/right stick turn
    `_viewYaw`, not `_lookYaw`), stick flies, Space/RT up, Ctrl/LT down, release holds altitude.
  - **Plane** (picker): throttle is a LEVER (Shift/RT up, Ctrl/LT down) — nobody holds a key for a
    whole flight. Stick pitches/rolls, heading follows bank (coordinated turn), roll AND pitch
    self-level hands-off. Thrust 4.5 m/s² — at 11 it beat gravity and a pull-up climbed vertically
    for ever. **Airspeed is carried as state** (`FlightMotion.Airspeed`): re-deriving it as
    velocity·nose fed the stall sink back in as speed once the nose dropped (112 → 394 km/h in 2 s).
  - Check any of them: `<godot> --path . -- --flycheck wingsuit|glide|paraglider|heli|plane[,out.png]
    [--at E,N]` — scripted sortie with the real input actions, speed/sink/glide/AGL every second,
    non-zero exit on a crash or ending under the terrain. `FootPlayer.DebugLaunch` puts a craft in
    the air for it (no runway or launch slope needed to test a flight model).
  - Sound: synthesised helicopter rotor (4.5 Hz blade "whop") and piston engine loops, pitch by
    spool/throttle. No wingsuit/canopy wind — see the removed wind loop above.
- **Vehicles vs equipment** (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane)
  splits machines that are **left in the world** from equipment that is worn and ends when taken
  off (skis, wingsuit, canopies). While driven, a vehicle still lives inside the driver's
  `FootPlayer` — the proven ride/flight physics, cameras and tricks are untouched. Getting out
  (E / Y, **anywhere, mid-air included**, with the vehicle's momentum), a crash, or being thrown off
  a bike hands its `VehicleState` to a `VehicleBody` (CharacterBody3D) that carries on alone: a bike
  rolls to a stop and tips over, a plane keeps its throttle and flies on until it hits something,
  a helicopter with no pilot **falls** (`FlightInput.Piloted = false` — autorotation needs a pilot).
  Getting in (E / Y within 3.5 m) hands the state back (`VehicleManager.Claim`). It sleeps at rest
  and only anchors collision streaming while moving. **Parked vehicles spawn 0.15 m up**: the
  terrain collision is a one-sided heightfield, and a box starting exactly on it fell 125 m through
  the mountain in five seconds. `FootPlayer.FindExit` stands the pedestrian on the *ground* beside
  the seat — measuring the uphill side at seat height read it as blocked and put the player on
  the vehicle's roof, which pushed the vehicle through the terrain.
  - **Engine** (`engine_toggle`, **I / D-pad ↑**, `FlightInput.Engine`): helicopter off → rotor spools
    down (0.18/s), lift fades below spool 0.6 into autorotation (9 m/s sink); on → ~3 s to lift.
    Plane off → zero thrust, it glides. Entering starts the engine.
  - **Damage**: vehicle HP (`FootPlayer.VehicleHealth`, `VehicleBody.Health`) loses `(impact−4)×10`
    per knock; past `CrashSpeed` or at 0 HP it becomes a **wreck**: `Explosion` (fireball, debris,
    smoke, flash, synthesised 3D boom) + charred visual burning 30 s, cleared after 90 s. Every peer
    watches the synced `Wrecked` flag and explodes it locally. Player: `FootPlayer.Health` 100, fall
    damage above 11 m/s landing, blasts via the static `Explosion.Blast` (each client hurts only its
    own player — client-authoritative), regen after 6 s, 0 → knocked out 3.5 s and revived at the
    last safe grounded spot. The occupant of a wreck is **thrown clear** and hurt by the blast.
  - **Network**: `World/Vehicles` + `World/VehicleSpawner` on server and clients (same path — RPCs
    route by it). Clients `RequestPark`; the server spawns for everyone with the parker as
    authority (it simulates, the server has no collision). `RequestClaim` is granted once — the
    server frees the node everywhere and returns its state — so two players cannot take one
    vehicle. A leaving peer's vehicles are removed. Untested with two real clients.
  - Check: `<godot> --path . -- --vehiclecheck[,out.png] --at 2585000,1110000` — 24 checks:
    helicopter up, bail out mid-air into wingsuit and canopy, empty helicopter falls and explodes;
    bike parked, stays, re-entered; plane engine off/on; plane crashed with the player in it.
    Location matters: at Riddes the engine-off plane glides into the mountainside.
- **Inventory** (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus an 18-slot pack,
  stacks, `Changed` — saved to `user://inventory.json` by item **name** (a starter kit when absent).
  Local only, never replicated; what is in the hand is, as `FootPlayer.HeldItemId`, so others see it.
  `ItemController` (owned by `ClientWorld`, player resolved per frame, falling back to whoever owns
  the current camera so probes work) does the items: binoculars (Aim → 9° FOV, `ScopeView` puts
  third person at the eye), camera (Aim frames, Use saves `user://photos/*.png` after
  `FramePostDraw`), GPS (LV95/altitude/heading readout), Swiss flag (plant on ground flat enough to
  stand, Use on a planted one picks it up), energy bar / water (heal). Everything it pushes on the
  player (`FovOverride`, `ScopeView`, `LookScale`) is re-asserted every frame, so dropping Aim or
  mounting needs no special case. **Items work on foot only** — the shoulders they use (RB use,
  LB aim) are trick/boost when mounted. `HeldItemVisual` draws the item: a swaying viewmodel on the
  camera in first person, else on `FootPlayer.HandLocal` (the wrist from the same rig the body is
  posed from). Controls: **1–6** / wheel / D-pad → select, **hold X / D-pad ←** radial quick wheel
  (aim with mouse or right stick, release), **K / Back** inventory (click to pick up, click to place:
  same item stacks, else swap; right-click uses). Screenshot with `--ride foot,5,out.png` plus
  `--hold <item>`, `--aim`, `--inventory`.
- **Mantle** (on foot): pushing into a wall whose top is 0.45–2.1 m above the feet, with open air
  over it and standing room on it, pulls you up (automatic in the air, needs Jump on the ground so
  walking into garden walls does not vault them). Jump + mantle therefore reaches ~3 m. Moved
  directly, not through MoveAndSlide, which exists to stop exactly this contact. Ground coyote
  time 0.12 s. Check: `<godot> --path . -- --mantlecheck` (1.4 m and 2.8 m must climb, 3.6 m not).
- **Steering by lean** (`Rideable.SteerByLean`): the input sets a target bank that eases in over
  ~0.2 s (out 1.6x faster) and the yaw rate is what that bank sustains, `g·tanφ/v`. Setting the yaw
  rate straight from the input made every correction a jerk — the "stiff" feel. Ski edge scrub is
  quadratic in bank, so a moderate carve holds speed. The chase camera trails the turn
  (`_turnLag` ∝ yaw rate) instead of being bolted behind the rider.
- **On foot** (`src/Player/FootPlayer.cs`): WASD + Shift at 1.6 / 4.6 m/s, Space to jump, plus
  two momentum moves — **slide** (Ctrl, run only, launches at 7 m/s, gains speed downhill, ends
  keeping horizontal speed if you Space out of it) and **wall jump** (Space in the air against
  a surface past ~70°, twice per airtime, never twice on the same face). Both launches decay
  back to `RunSpeed` through `AirDrag`, so neither raises the top speed on flat ground; the
  air branch *steers without braking* above running pace, because the ordinary `MoveToward`
  air control kills a launch in half a second and makes both moves pointless. Sliding shrinks
  the capsule to 0.9 m, so it fits where standing does not.
- **Mounts** (`src/Player/Rideable.cs`): **E** opens a picker (`RideUi`) — On foot / Road bike /
  Skis. A vehicle is a table of numbers plus a mesh: everything touching the body, the network,
  the camera and the UI lives once in `FootPlayer`, so adding one is a class plus a line in
  `Rideable.Create`. Both share one model — mass, a resistive force, `SlopeAccel` — and differ
  only in where propulsion comes from. Mounted, speed is a **scalar along a heading**, not a
  velocity vector: a bike goes where it points, and strafing is something people do, not
  vehicles. The camera goes third-person with a raycast pull-in, and the machine's lean is
  *derived* (`tan φ = v·ω/g`), never authored.
  - `Bicycle` runs the real power equation, `m·a = P/v − ½ρ·CdA·v² − Crr·m·g − m·g·sinθ`.
    Nothing is tuned: 180 W gives 32.7 km/h flat, 9.3 km/h up 8%, and 63.8 km/h freewheeling
    down it. Steering is lean-limited, so the turn radius grows with speed. `RiderWatts` is the
    input **because a home trainer measures watts** — RideLink drops straight into it.
  - `Skis` have no engine. Turning *costs* speed (`EdgeScrub`), which is the whole of skiing:
    pointed straight down a 30% face you reach 80 km/h, and carving across the fall line is the
    only brake. W is a capped poling shuffle, because skis on the flat would otherwise strand you.
  - `FootPlayer.RideControls` replaces the keyboard when set — one movement path for a keyboard
    rider and a pedalling one, and the seam `RideProbe` and the trainer both use.
  - `RideKindId` is replicated, so remote players are seen on the bike rather than sprinting
    at 40 km/h in a running pose.
  - **Space hops** on either mount (`FootPlayer.RideJumpVelocity`, 3.2 m/s, edge-triggered, ground
    only): a bunny hop or a pop off a lip that carries the momentum it already had. The free-fly
    camera uses the same keys vertically — **Space** up, **Shift** down (Q/E still work), boost
    moved to **Ctrl**.
- **GPX ghost racing** (`src/Gpx/`): `GpxParser` -> `GpxTrack` (LV95 via `SwissProjection`,
  cumulative time + distance). `RacePlayback` owns ONE clock; each `Runner` samples its own
  track at that shared time, so several GPX files start together and race as ghosts —
  alignment is by *elapsed* time, not wall-clock date, so runs recorded months apart still
  compare. `TrackRibbon` draws the focused runner's course draped on terrain,
  `PlaybackCamera` has chase / first-person / cinematic / free, `PlaybackHud` gives the
  timeline scrubber, speed multiplier, and a leaderboard with gaps in metres and seconds.
  Each runner is a **streaming anchor**, so terrain loads around the race not the camera.
  Elevation is draped (GPS ele kept only as a drift statistic — measured ~1 m on a real
  track, a good check that projection and heightfield agree). Each ghost's legs run the shared
  gait at its own measured speed, on the **replay** clock — at 4x playback the legs turn over
  four times as fast, or the runner skates. The gait raises and drops the hips itself, which is
  what the old hand-written head bob was standing in for. **The avatar matches the recording**:
  `GpxTrack.Kind` (from the GPX `<type>` element) puts a real pedalling `Cyclist` — the player's
  own bike rig, not a second one — on a ride recorded as cycling, tinted to the runner's
  leaderboard colour rather than the rig's own rider-index palette; anything else still runs.
  Cadence is driven from the sampled speed by the same curve the player's own bike uses.
  **Snap to roads** (`TrackMatcher`/`RoadNetwork`, HUD button or **R**): map-matches a recording
  onto the mapped network so a ghost runs *on* the road rather than 5 m beside it. Hidden Markov
  model in the style of Newson & Krumm — emission from GPS-to-road distance (σ 8 m), transition
  from |route distance − GPS distance| over a bounded Dijkstra, Viterbi over the whole track.
  Pointwise nearest-road snapping is what this replaces: the nearest road is very often the wrong
  one, and the runner then flickers between a carriageway and the cycle path beside it. Each
  runner keeps **both** variants (`Runner.Track`/`Snapped`, `Active`), so the toggle is a fair
  comparison and not a reload; matching runs off the main thread (93 ms for 16 km, tiles
  included) and is applied back on it. Measured on a real 16 km ride: 98% of fixes near a road,
  mean move 2.9 m, p95 10.6 m, length +0.2%.
  Keys: **G** add track(s), **Space** play/pause, **C** camera, **F** follow next runner,
  **Video export** (`VideoExporter`, HUD button): renders the whole run to an mp4 with the camera
  and speed as set, **not in real time**. It drives the clock in exact 1/fps steps and hands that
  same step to the runners and the camera instead of `delta`, so a frame that took eight seconds
  is indistinguishable from one that took eight milliseconds. Before each frame it waits for
  `ChunkManager.SettledNear(camera, 6)` — what the frame can see, not the whole world — and terrain
  that has not arrived is a hole that cannot be fixed afterwards. It also sets
  `ChunkManager.OfflineMode` and starts an `ExportPrefetcher` that reads the entire route into the
  tile cache in route order, so no frame is ever the first to ask for a tile.
  Frames are **streamed raw into ffmpeg's stdin** (`-f rawvideo`, via `System.Diagnostics.Process`
  — Godot's `OS.CreateProcess` has no stdin pipe), so nothing is PNG-compressed on the main thread
  and the encoder works while the game renders the next frame; `frame_00000.png` is still written
  as a known-good reference still, which is how a flipped or colour-swapped pipe would be caught.
  Without ffmpeg on PATH it falls back to the PNG sequence plus `encode.bat`.
  `--export <dir>[,fps][,speed][,warmup]` does the same headlessly.
  **R** snap to roads,
  **Lens** button cycles a simulated optic (`LensLayer`, `shaders/lens.gdshader`): a full-screen
  `CanvasLayer` at layer 5 - under the HUD's 10, so the controls are never bent - doing barrel
  distortion, chromatic aberration and a vignette, paired with a per-profile **FOV bias** applied
  in `ShotContext.Place`. The bias is not decoration: distortion warps the picture that was drawn
  and cannot widen it, so curvature without extra field of view reads as a warped photo rather
  than a wide lens. It tops out around 150 degrees of true FOV; a real >=180 fisheye needs the
  scene rendered to a cube, which is 3-5x the draws and was not worth it for a look.
  **Path** slider fades the course ribbon out (`TrackRibbon.Opacity`, `ps1_path.gdshader` gained
  `blend_mix` + an `alpha` uniform). At 0 the node is hidden outright and stops rebuilding. The
  opacity lives on `GpxSession`, not the ribbon, because `RefreshRibbon` destroys and rebuilds the
  ribbon on every snap toggle and focus change.
  **Pace** button (Cinema only) scales `Director.Pacing`, i.e. every shot's min and max duration.
  **Shot** dropdown (Cinema only, `Director.SetForced`/`PlaybackCamera.ForcedCinemaShot`) pins the
  director to one named shot picked by hand — "Auto" gives the choice back. `Begin` is still
  tested every time the pin (re)starts, so it never opens on a bad vantage, but once running it
  is held regardless of `StillGood`, the event timeline, or Pacing, none of which mean anything
  once a human has taken over. `--forceshot <name>` is the headless equivalent, for screenshotting
  or exporting one shot on its own rather than hoping the director cuts to it in time.
  **H** show/hide UI (the toggle button lives outside the hidden panels, or hiding the UI
  would remove the only way back).
  `--gpx <path>` may be repeated to start a race from the command line.
- **Multiplayer**: client-authoritative transforms, MultiplayerSpawner + Synchronizer,
  ENet port 7777. Server runs ChunkManager with BuildMeshes=false (grid-only, for
  height queries around players).
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
- **Settings** (`Core/GameSettings`, `Core/SettingsMenu`, `user://settings.json`): render distance
  in tile rings (6..40, default **15**), detail preset (Low/Medium/High = inner ring table + road/
  building reach, `LodPolicy.Create`), horizon km, fog on/off (**off by default** — the shaders keep
  their fog code and `FogUniforms.Apply` pushes `fog_start/end` past the far plane when off), parallel
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
  screenshot; `--rings N --horizon km --fog on|off --detail low|medium|high` override for one run
  without being saved. The last ring is always **stride 50** (`LodPolicy.FarStride`, 21x21 verts,
  from the `.terrc`), which is what makes 40 rings (6,561 tiles) cost about what 9 used to.
  A server ignores all of it and keeps 2 rings of full grids around each player.
- **Performance overlay** (`Core/PerfOverlay`, **F3** cycles Off / FPS / Detailed, saved as
  `GameSettings.PerfOverlay`, also in Settings; `--perf off|fps|full` for one run). Detailed shows
  frame avg/p99/max over 2 s, draws/prims/memory, and the loader via `ChunkManager.GetPerfStats()`:
  queue depths, builds/s, per-stage worker ms per tile, and tile latency over the last 128 builds —
  **ground** (build start -> first surface committed) and **complete** (-> last result committed).
  Both are timed on the main thread, so they include waiting behind the commit budget. Hidden
  during a video export (`OfflineMode`); shows up in `--shot` captures, which is how to screenshot it.
- **Session perf log** (`Core/PerfRecorder`, **F4** start/stop, `--perflog [seconds]` from boot,
  Settings -> "Open folder"): writes `user://perf_logs/<timestamp>/` with `frames.csv` (per frame:
  frame/GPU/render-CPU ms, draws, prims, memory, GC counts, loader queues, commits, camera LV95 +
  speed, mode), `builds.csv` (per tile: latency + worker ms of every stage), `commits.csv`,
  `events.log` (hitches tagged commit/gc/gpu/other, teleports, mode and settings changes) and
  `summary.txt` ending in a diagnosis. **Use the viewport's measured GPU time, not
  `Performance.TimeProcess`, to tell GPU- from CPU-bound**: TimeProcess absorbs the wait for the
  renderer and read 50 ms on a frame the GPU spent 35 ms of. A frame's delta pays for the
  PREVIOUS frame's commits, so the recorder runs last (`ProcessPriority`) and shifts its commit/GC
  context by one frame. The per-build stage array also times `blend+tail` (road-blended collision +
  visual re-mesh), which `BuildTimeReport` never counted and which measured ~33% of worker time.
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
- **Terrain streaming** (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves
  generated files to clients that lack them. `IChunkSource` was already the seam, so
  `NetworkChunkSource` decorates `LocalChunkSource` with three tiers — shipped -> cache
  (`user://chunk_cache/`, overridable with `--cache`) -> server. The transfer unit is the
  **raw file**, cached under its ordinary filename, so the ordinary decoders read a streamed
  tile exactly like a shipped one and the server does no decoding. Files are sliced into 24 KB
  fragments on transfer channel 2 (bulk data on the default channel head-of-line blocks every
  position update behind it), deflated when that helps, and CRC-checked before being cached.
  `ClientTerrainSync` fetches the server manifest on join and merges its tile list into
  `ChunkManager._available` — without that merge the LOD rings skip unknown tiles and nothing
  is ever requested. It also saves that index to the cache, so tiles streamed in an earlier
  session are reachable offline.
- **Chat and commands** (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at
  `World/Chat` — the path must match, because Godot routes RPCs by node path. Clients only
  submit text and render replies; **every** decision (permissions, names, teleport
  destinations) is taken on the server, since a client-side permission check is one the client
  can edit. **Enter** opens the input, **/** opens it pre-filled, Up/Down walk the history.
- **Admin** (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge;
  the display name is a *request* that the server sanitises and deduplicates. Operators come
  from `user://admins.json` (granted on join) or `/login <pw>` against `--admin-password`.
  Without that argument `/login` is disabled entirely. `Net/ServerConsole` reads the dedicated
  server's own stdin on a background thread (`Console.ReadLine` blocks, so it cannot be on the
  main loop) and runs commands as peer id 0, which is always an operator — that is how the
  first admin gets granted on a fresh server.
- **Teleport** (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at
  construction. Flying camera gets ground + 220 m, a `CharacterBody3D` gets ground + 2 m and
  has its velocity zeroed and its placement pass re-armed.

## Commands

- Preprocess: `dotnet run --project tools/TerrainPreprocessor -c Release -- --in ressources/data/swiss_chunks --out terrain_chunks --verify --dump-png terrain_chunks_png`
  `--in` is recursive and repeatable (sources on any drive/share), `--jobs` defaults to all cores,
  `--io-jobs` (4) caps concurrent source reads. **One pass, no parse cache**: `TerrainBuild`
  reduces each tile straight to its interior vertices plus 16 KB of perimeter *partial sums*
  (`<temp>/E_N.edge`); a tile is written once every tile around it has published its partials, so
  the old 50 GB pass-1 `.raw` cache and its single-threaded, lock-per-cell pass 2 are gone.
  Output is byte-identical to the old pipeline (checked on all 6,699 tiles). Incremental: tiles
  with a valid `.terr` + `.edge` and an unchanged source (size + mtime) are skipped, and only the
  seams of their new neighbours are refreshed from the existing `.terr`. `--force` re-parses,
  `--fresh` drops tiles from earlier runs. Numbers are read by a fixed-point scanner
  (`mantissa / 10^d` is one correctly rounded division, i.e. the same double `Utf8Parser` gives);
  the tool targets net9.0 for its zlib-ng inflate.
- Coarse companion tiles (needed once for a region built before they existed; a normal build
  emits them): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --coarse --jobs 8`
  — 6,699 tiles in 10 s, 3,207 MB read -> 33 MB written, every tile verified bit-identical.
- Far horizon (needed once for a region built before it existed; a normal build and `--coarse` emit
  it): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --horizon --jobs 8`
- Build game: `dotnet build UnitSportSwitzerland.csproj`
- Dedicated server: `<godot> --headless --path . -- --server [--port N]`
- Client: `<godot> --path . -- --connect 127.0.0.1` (no args = offline, T toggles
  spectator/on-foot)
- Godot exe: `C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe`

- Buildings export (needs GDAL): `python tools/export_buildings.py --bbox 2578500 1108500 2586500 1115500`
- Features: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --features-only --tlm <tlm.gpkg> --route-keys ressources/data/routes/route_keys.sqlite --cover --buildings ressources/data/buildings3d/buildings.gpkg --gwr ressources/data/gwr/data.sqlite`
- Roads preprocessing: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --roads-only --tlm ressources/data/tlm3d/SWISSTLM3D_2026_LV95_LN02.gpkg --route-keys ressources/data/routes/route_keys.sqlite`
- Junctions (run **after** roads preprocessing, rewrites `.road` in place as v2):
  `dotnet run --project tools/RoadGen -c Release -- --rewrite --chunks terrain_chunks`
  (`--dry-run` to measure only, `--tiles "E,N;E,N"` to limit, `--force` to override the
  already-rewritten guard)
- Place index: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --features-only --places --gwr ressources/data/gwr/data.sqlite --tlm <tlm.gpkg>`
  (`--tlm` adds named summits and passes; without it the index is towns only). **This also
  re-runs roads, which strips the junction polygons — always finish with `RoadGen --rewrite`.**
  writes `places.json`; **Tab** in game opens the teleport search (`PlaceSearchUi`). A
  municipality is placed at its **densest 500 m GWR cell**, not its centroid — communes
  stretch up the hillside, so the centroid of Riddes lands on the mountain above it.
- Spawn elsewhere: `<godot> --path . -- --at <lv95E>,<lv95N>` (default: Riddes,
  2583250/1113250), or `--goto <town>` to name it instead of looking up coordinates.
  `SpawnPoint` drops the camera to ground + 220 m once the chunk beneath it streams in — the
  height cannot be known at boot.
- Dedicated server with operators:
  `<godot> --headless --path . -- --server --admin-password <pw>`; type commands straight into
  its stdin (`/admin add <name>`, `/say ...`, `/tpall <town>`). Client: add `--name <n>`.
- Server binds the IPv6 wildcard (`::`, dual-stack) so it answers on every interface including
  Tailscale; `--bind <ip>` restricts it to one. **ENet is UDP** — a forwarded port must be a
  UDP rule and TCP-only tunnels (ngrok free, Cloudflare Tunnel) cannot carry it.
  `--stream-bandwidth <MB/s>` caps terrain streaming per client; the 3 MB/s default is
  24 Mbit/s each and is a LAN figure.
- Screenshot without the editor: `<godot> --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png`
  (also prints fps/prims/draws — the way to verify rendering when the godot-ai MCP is down).
  `ClientWorld` skips `SpawnPoint` when `--shot`/`--probe` is given, otherwise the spawn
  drop overwrites the requested y with ground + 220 m and every close-up shot comes back
  as an aerial one. `ShotRunner` also re-claims `Current` every frame — a mode entered from
  a deferred call (GPX replay) would otherwise steal the camera after the shot was set up.
  Add `--menu` to capture the mode picker.
- French features for a box (needs the terrain built there already; merges into existing tiles):
  `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --france 6.21,46.26,6.27,46.30`
- Tunnel collision check: `<godot> --path . -- --probe lv95E,lv95N,seconds`
- Streaming smoothness: `<godot> --path . -- --fly x,y,z,yawDeg,speedMps,seconds [--rings N --horizon km --builds N]`
  — flies straight at that speed and prints the frame-time distribution; exits non-zero on any
  frame over 33 ms. The way to check a loader change, since a hitch never shows in a `--shot`.
- Replay verification flags (all alongside `--gpx <track>`): `--snap` road matching, `--cinemamode`
  Absolute Cinema, `--speed <n>` the playback multiplier, `--lens <n>` a lens profile by index,
  `--path <0..100>` course-line opacity, `--forceshot <name>` pins Absolute Cinema to one named
  shot (matches the HUD's override list, e.g. `"Ankle cam"` — quote it, names have spaces),
  `--bubble off` disables the zoom bubble, and `--cinemastats <screenSeconds>` which runs the
  director for that much SCREEN time and prints
  cuts, rejections and seconds-per-shot, then quits. The last two are how the pacing claim is
  actually checked: "a scene is as long at 32x as at 1x" is a number, and eyeballing cannot tell a
  director cutting twice too often from one cutting twenty times too often — and a forced shot
  held for the whole window (1 cut, not a fresh one every few seconds) is how the manual override
  itself is checked, the same way. Example:
  `<godot> --path . -- --gpx seb.gpx --cinemamode --speed 32 --cinemastats 40`
- Riding check: `<godot> --path . -- --ride bike|skis,seconds[,out.png] [--at E,N]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu` (with `--shot`) to capture the picker.

## Gotchas (learned the hard way)

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
- **A visibility test that CUTS AWAY defeats a dissolve that was built to avoid cutting away.**
  `LockedOff`, `DroneOrbit` and `LowHeroPass` all re-tested `ctx.CanSee` in `StillGood`, so the
  instant anything drifted between the camera and the runner the Director scored the shot
  "broken" and cut to something else — before the sightline-cut shader ever got a frame to
  dissolve it in. The dissolve existed and worked; Absolute Cinema simply never gave it the
  chance, because pre-empting a shot happens the same frame the obstruction appears and a fade
  needs several. `CanSee` still gates `Begin` — a shot never STARTS aimed at a wall — but once
  running these three now trust the dissolve instead of testing sight afresh every frame. This is
  also why "the old Cinematic mode sees through things and Absolute Cinema doesn't" was reported
  as a difference between modes when the shader code was actually identical for both: Cinematic
  never had a competing cut-away trigger to race against, and Cinema's own `StillGood` was
  quietly winning that race every time.
- **A camera placed relative to raw TERRAIN can end up under the ROAD the runner is actually on.**
  `AnkleCam` and `LowHeroPass` computed their ground height from `ctx.Ground(p)` — the bare
  terrain grid — with `ctx.Subject.Y` as a fallback only when the tile hadn't streamed in yet.
  But a runner on a road is not always AT terrain height: a graded cut or a low embankment sits
  measurably above it, which swissALTI3D does not model (see the bridge-approach gotcha below).
  For a camera placed a couple of metres from the runner, the runner's OWN elevation — already
  correct, road-matched or draped, whichever applies — is a far better local reference than the
  bare grid, and using terrain alone put the lowest-angle shots' cameras under the visible road
  surface on exactly the stretches where the two disagreed, which is also where a low angle makes
  the clipping most obvious. `ShotContext.GroundNear` takes `Math.Max(Ground(p), Subject.Y - cap)`
  instead — a cap, not the runner's height outright, so AnkleCam's own by-design offset below the
  runner is not clamped away.
- **Two systems computing "how high is this road" independently will not agree to the
  centimetre, and a lift sized for one will not clear the other.** The GPX ribbon's tread sits
  `TreadLift` above the height `TrackMatcher` interpolated along the `.road` polyline; the
  rendered road tread `RoadMeshBuilder` draws from the SAME polyline adds its own render-time
  offsets on top for reasons that have nothing to do with the recording — `BridgeLift` (0.15 m,
  purely to stop a deck z-fighting the terrain), and a little more at junctions and type-change
  joins where width and height are blended across the seam. None of that is visible to
  `TrackMatcher`, so the base 0.28 m tread lift — sized to clear terrain noise on a DRAPED course
  — was not always enough to clear the render-time offsets on a SNAPPED one, and the ribbon sank
  under the road it was following rather than the ground beneath it. Fixed with a second,
  larger `RoadClearance` margin applied only when `ElevationIsSurface` is true.
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
- **A GPX recording's activity comes from the file, not an assumption.** `Runner` always built a
  running figure, so a bike ride played back as someone jogging alongside their own bicycle.
  `GpxParser` now reads the standard `<trk><type>` element (Strava, Garmin and most exporters
  write it; matched by substring - "cycling", "biking", "road biking", "1" all count, since
  exporters do not agree on the string) into `GpxTrack.Kind` (`UnitSport.Player.RideKind`, the
  same enum the player's own mount picker uses), carried through `TrackMatcher` so a road-matched
  copy keeps it. `Runner` builds the real `Cyclist` rig - the one E mounts, not a second one - for
  `RideKind.RoadBike`, via `Cyclist.CreateWithTint` rather than `Cyclist.Create(riderIndex)`:
  the existing factory colours from `HumanPalette.ForRider(index)`'s hue formula, which is a
  *different* colour than the fixed six-entry leaderboard palette `Runner.Tint` already uses for
  a human avatar, and a bike ghost whose rider colour disagreed with its own leaderboard row would
  be its own small bug. Cadence is driven from `Runner.Speed` through the same
  `speed * 60 / 6.2` clamp(40,112) formula `Bicycle.cs` drives the player's own legs from - there
  is no wattage for a recording, but there is a speed, and `Cyclist` already freezes the cranks
  below ~0.01 rpm so a finished or paused ghost simply stops pedalling. Camera mounts (helmet POV,
  ankle cam, etc.) come from `HumanMeshBuilder.MountsForPose(HumanPose.Cycling)`, a fixed-pose
  sibling of the gait-sampled `MountsFor` added for this - a cyclist has no gait phase to sample,
  the legs just turn a crank around a fixed torso. A track with no `<type>`, or an unrecognised
  one, still plays as a runner: this is additive, not a reclassification of every existing GPX.
- **State derived from the course must be re-derived on EVERY path that changes it.**
  `RacePlayback.SetSnapToRoads` raised `SnapChanged` only from the end of a matching pass, so the
  two paths that return early - turning the toggle off, and turning it back on when everything is
  already matched - left the ribbon drawn from one variant while the avatar ran the other. It does
  not read as a stale ribbon; it reads as **the body being rotated off the path**, which is how it
  was reported. The event is now raised from a `finally`, and `EnsureCinemaPlan` is subscribed to
  it too, or the director keeps cutting to corners belonging to the other variant. Same class of
  bug in the HUD: the camera button's shot name was recomputed only inside `Refresh()`, which
  nothing called on a cut, so it showed whichever shot was running the last time any control was
  pressed. `_camera.CinemaCuts` is now in the change-detection string - the count, not the name,
  because two consecutive cuts can land on shots of the same name.
- **Cinema shot LENGTH was never the reason it cut too fast at high speed.** `_target` and `_held`
  were already in screen seconds and already unscaled by the clock. The collapse came from the
  other two triggers: `Imminent`'s lead window widens with the clock, so at 32x it spans 51 track
  seconds while the clock advances 32 per screen second - the window is essentially never empty,
  and the same event re-fired a cut on every frame past `MinSeconds`. Fixed by capping the lead
  (`MaxLeadSeconds`) and by letting an event pull exactly **one** cut (`Director._covered`).
  `Imminent` must still be called unconditionally, never behind a `&&` short-circuit: it is what
  walks `_cursor` past events the clock has left behind, so skipping it parks the cursor on the
  covered event for ever. Second cause: `LockedOff` and `DroneOrbit` guard themselves with fixed
  metre distances that a runner eats in about a second at 32x, where `LowHeroPass` already scaled
  by `ClockSpeed`. Measured on a 4 km track, 40 screen seconds: **1x 7 cuts, 8x 8, 32x 12 -> 9**.
- **The video exporter fed the camera TRACK seconds where it wanted SCREEN seconds.**
  `_step` is `clockSpeed / fps`; `ShotContext.Dt` and every easing rate in `PlaybackCamera` are
  screen rates, and `ctx.Follow()` re-applies `ClockSpeed` itself - so the multiplier was counted
  twice and an export paced visibly differently from the preview the player had just set up at the
  same speed. It is `_camera.Step(1.0 / _fps)`; only `_race.StepTo` takes `_step`.
- **A facing look-ahead must be bounded by DISTANCE, not just time.** `HeadingLookahead` is 2.5 s
  either side, which is ~17 m on foot and 60-100 m on a bike. That is fine for outrunning GPS
  jitter on a raw recording and wrong on a road-matched one, which has real corners: a hairpin is
  chorded straight across, and on a switchback the two samples land on opposite legs so the
  difference collapses toward the degenerate guard and the heading **freezes**. Bounded now by
  `MaxHeadingChordM`. The facing slerp also has to be clock-scaled like the position follow next
  to it, or at 8x the body keeps up with the course while its heading lags eight times as far
  behind every corner.
- **Raw GPX motion looks like a boat.** Three separate causes, all handled: positions are
  smoothed at parse over a *distance* window (`GpxParser.SmoothingWindowM`, so dense 1 Hz
  tracks are filtered while sparse ones are untouched); facing comes from a +/-2.5 s
  look-ahead and is slerped (`Runner.HeadingLookahead`); and the rendered position eases
  toward the sample (`Runner.PositionFollow`), which also hides the 2 m heightfield
  stepping underneath. Seeking snaps rather than easing, so scrubbing stays responsive.
- **GPS speed must be averaged over a window, never one segment.** A ~1 Hz recording has
  metres of jitter between consecutive fixes, so differencing a single segment reports a
  walk as a run and never settles. `GpxTrack.SpeedWindow` (6 s either side) makes the
  readout match the avatar's real world speed — verified at 5.8 reported vs 5.6 measured.
- **A hand-built basis must be checked for HANDEDNESS, not just direction.** `right = up × forward`
  and `right = forward × up` differ by a sign, and that sign is the difference between a rotation
  and a **reflection** (determinant −1). Both `PlaybackCamera.Aim` and `Runner.SafeBasis` had the
  operands the wrong way round, so the replay camera rendered the **entire world mirrored** and
  every ghost was mirrored on top of it. It hides extremely well: the −Z column is unaffected, so
  facing still looks right, and terrain is symmetric enough that nothing looks wrong — until you
  follow a route you know and every turn you took comes back the other way. `--shot` never showed
  it, because `ShotRunner` sets `Rotation` as Euler angles instead of building a basis. The rule:
  `right = forward × up`, and if a basis is built by hand, assert `det ≈ +1`.
- **Never call `LookAt` on data-driven transforms.** A degenerate target makes Godot raise
  an error, and an error raised inside a C# callback can take the whole runtime down
  ("Fatal error. Internal CLR error." with a stack ending in `DebuggingUtils.GetCurrentStackInfo`).
  Build the basis manually and guard the degenerate cases — see `TrackPlayback.SafeBasis`.
- **`XmlReader.ReadElementContentAsString()` already advances the reader.** Calling
  `Read()` again after it silently skips the next sibling; that is how every `<time>` after
  an `<ele>` went missing and GPX tracks all fell back to an assumed pace.

- **Player scale is set by speed, not by size.** A 1.8 m capsule moving at 6-14 m/s reads
  as a giant next to 10 m buildings. Realistic 1.6 / 4.6 m/s plus head bob and a running
  FOV kick is what makes the world feel human-sized. `FootPlayer` reads *physical keys*,
  so `Input.action_press` will not drive it in tests — use godot-ai `game_manage input_key`.
- **`IsOnWall()` flickers between adjacent physics frames.** Pressed flat against a building
  face, the solver reports contact on roughly every *other* frame, so a wall jump gated on
  same-frame contact silently misses about half of all attempts — it looks like an input bug,
  not a physics one. `FootPlayer` remembers the last qualifying wall normal for 0.18 s
  (`WallCoyoteTime`) and the last jump press for 0.14 s (`JumpBufferTime`), and jumps when
  both are live. Verified: v.y = 4.6 and 5.41 m/s along the wall normal, one frame after press.
- **A held movement key that starts a state must be edge-triggered.** A spent slide ends at
  ~2 m/s, the walk puts you back over the 2.6 m/s entry threshold in about a second, and a
  *held* Ctrl then starts the next one — measured as a permanent 7 m/s crouch-run. Slide entry
  takes a fresh press; holding only sustains the slide you are in.
- **Feeding collision back into a vehicle needs `GetRealVelocity`, and a threshold.** Two wrong
  versions came first. (1) `Velocity` after `MoveAndSlide` is *projected along whatever you hit*,
  and against a slope too steep to climb that projection points up the face and keeps most of its
  magnitude — a skier jammed against a bank reported 22 km/h while its position had not changed
  for twelve seconds. (2) Clamping to `GetRealVelocity` every frame then killed the bike, because
  the ground is a 2 m lattice and crossing each bump costs a little forward motion *every frame*;
  compounded, that bled a bike from 107 m of riding to 11 m on flat ground. Only a shortfall
  that **persists** (smoothed, and past `ImpactTolerance`) is an impact.
- **`get_image()` on the root viewport from `_Process` returns whatever the render thread last
  left there.** `ShotRunner` gets away with it because the scene has been static for seconds by
  the time it grabs. A per-frame exporter does not: measured 75 identical frames of empty sky,
  with `RenderTotalPrimitivesInFrame` reading 0 at the moment of capture while a `--shot` from the
  same camera position drew 5.2 M. Await `RenderingServer.FramePostDraw` first.
- **A stopped figure is not a slow walk.** `HumanMeshBuilder.Cadence` has a floor — it must, or a
  figure inching forward takes one step a minute — and that floor keeps the legs turning over
  when the body has stopped. Everything the gait displaces is scaled by a `moving` factor that
  reaches zero at 0.25 m/s, and `AdvancePhase` freezes below it. Second half of the same bug:
  `RacePlayback` passed the real frame delta to its runners **while paused**, so a paused replay
  ran on the spot.
- **A map-matched track needs its DISPLACEMENT rate-limited, not its position.** Where the model
  changes road, the projection jumps: the two roads meet at a junction but the switch happens
  wherever the fixes stop being nearer one than the other, which is somewhere else. Measured 29
  steps over 10 m in a single fix across 16 km — one visible sideways twitch every ~550 m. Two
  fixes failed first: switching at the thinning-sample boundary made it *worse* (51), and
  bridging unmatched gaps changed nothing. Slew-limiting the snap offset to 1.2 m per fix took it
  to **0**, and costs only that the track is briefly between two roads at a junction instead of
  exactly on one — which looks like cutting a corner, i.e. like a runner.
- **`RoadNetwork` must snap endpoints to rejoin tile-clipped roads.** `.road` segments are clipped
  at every kilometre boundary, so a road crossing one arrives as two features with coincident
  ends. Without a snap tolerance every tile edge is a dead end, no route crosses one, and the
  transition term then scores every step near a seam as impossible.
- **A `.road` file is not a road file.** It also carries cable cars, rivers, avalanche barriers
  and dry-stone walls. Matching a GPS track onto a wall is not a near miss, and a wall or a
  watercourse is often the closest line to a riverside path — `RoadNetwork.IsTravellable` is the
  filter, and railways are excluded too because they parallel valley roads for kilometres.
- **A gait is solved from the no-slip constraint, and the arithmetic has two traps.** The planted
  foot must travel backwards at exactly the body's speed or the figure moonwalks, so the stance
  sweep is `v × stance time` — and (1) **a cycle is two steps**, so stance time is `duty × 2/cadence`;
  dropping that factor of two halves every stride. (2) The **ankle** does not travel that far,
  because contact rolls heel-to-toe along the foot (~0.22 m walking) while the ankle is nearly
  still. Without that term the sweep comes out at roughly twice what a 0.85 m leg can span and
  every stance frame clamps. Measured slip after both: 0% from a walk to 3.5 m/s, 8% at 4.6, and
  26% at a 6 m/s sprint, which is honestly out of the model's reach.
  Two more, both found by rendering a cycle as a strip: the hip is highest at midstance when
  **walking** and lowest when **running** (one sign for both makes one gait look wheeled), and
  arm swing is about a *third* of the leg's — matching the foot needs ±0.6 m from a 0.52 m arm,
  so the elbows straighten and the runner sleepwalks.
- **Avatar meshes are authored facing +Z; a Godot node faces −Z.** `MeshScratch.Build` applies the
  half turn on the way out, once, instead of at each of the four places a figure is parented to a
  node. Skipping it does not look like a modelling error — the body travels correctly and only the
  machine is turned around, which from a chase camera reads as *riding in reverse*, and on a GPX
  ghost as running backwards. It survives a preview turntable, where there is no direction of
  travel to contradict it. Related: facing +Z, the rider's right is **−X**, so a chainring at +X
  is on the wrong side of the bike.
- **A crank turning the wrong way is instantly obvious to anyone who rides.** The bike faces +Z,
  so driving forward turns the chainring with its top moving toward +Z — meaning a crank starting
  at the front goes *down* next. Taking the obvious `(sin, cos)` circle runs it backwards. The
  same sign appears in `BikeMeshBuilder.Cranks` and `Cyclist.UpdateLegs`; they can only disagree
  if one is edited alone. Check it with `--avatars … --crank <rad>`, which parks the cranks —
  rotation direction cannot be judged from one frame.
- **Quitting while tiles stream used to crash the process.** `ChunkStreamer.FetchAsync` runs on
  worker threads and defers onto the main one; the workers outlive the tree, and deferring onto a
  freed native object is a 0xC0000005, not a managed exception. `_shuttingDown` is set in
  `_ExitTree` so the workers stop queueing before Godot frees anything.
- **Crouch states need a headroom test before standing.** `FootPlayer.EndSlide` returns false
  when a standing capsule will not fit (shape query, radius shaved 3 cm), so releasing Ctrl in
  a tunnel keeps you down instead of forcing the body up through the roof — and you cannot
  jump out of a slide you could not stand up in either.

- **A generated roof must be VERIFIED, not reasoned about.** Four rounds of fixing individual
  failure modes each roughly halved the damage and none reached zero: the single-ridge model
  bowtied on concave footprints (236/541 buildings), a fan cap emitted backwards triangles on
  concave rings (759/1239), collinear vertices stalled the ear clipper, and — the subtle one — an
  inset wider than the building's half-width turns the offset ring **inside out while every
  vertex is still inside the original**, so a containment check passes it and the roof faces
  down. What actually worked was building the roof into a scratch list and testing the property
  that matters (`FranceBuildings.FacesUp`: every normal above a 75° pitch), falling back to flat
  when it fails. 0 malformed faces of 14,081. Construct-then-verify beats enumerating the ways
  polygon offsetting can go wrong.
- **BD TOPO's GeoJSON types are not consistent.** `hauteur` and the altitudes come back as JSON
  numbers; `position_par_rapport_au_sol` — the bridge/tunnel level — comes back as the *string*
  `"1"`. Reading only `JsonValueKind.Number` found **zero bridges**, which is indistinguishable
  from a region that has none. `BdFeature.Number` accepts both.
- **swissALTI3D does not stop at the border.** All six tiles under a track at Veigy-Foncenex carry
  real elevations, 374–430 m, no voids. Assuming French terrain had to be imported first (RGE
  ALTI) would have been a week of work to replace data already present — check the tiles before
  believing a coverage claim.
- **GWR: classify on GKLAS, not GKAT.** GKAT only says whether a building is residential
  at all, so using it labels every village house an apartment block. GKLAS 1110/1121 are
  one/two-dwelling houses; 12xx are non-residential.
- **Anything that must meet the terrain has to be built FROM the terrain grid.** Patching a
  carved hole with slabs derived from road geometry leaves floating, disconnected pieces.
  Derive the patch from the same lattice and heights the ground mesh uses.
- **Bridge/tunnel ends need a height blend.** Structures keep their surveyed Z while the
  approach is draped, so the join steps unless the last ~9 m is smoothstepped between the
  two. Blend only at *true* polyline ends (`Piece.AtLineStart/AtLineEnd`) — blending at a
  tile-clip boundary would dip the deck mid-span.
- **Ramp the APPROACH, never the deck.** Two wrong versions came before the right one.
  (1) Interpolating each deck point toward the ground beneath it pulls the middle of a short
  span down to the river bed — measured 904 m -> 886 m -> 904 m across a 12 m bridge, the
  V-notch in the middle of a viaduct. (2) Gating that blend on a small height mismatch keeps
  decks flat but leaves a hard step wherever the ground genuinely is metres below the
  abutment. The deck is right and the drape is wrong: swissALTI3D does not model the
  embankment that climbs to a bridge. So a *draped* line whose true end coincides with a
  structure endpoint takes `delta = structureZ - draped[end]` and fades it out inland
  (`ApproachDelta` + `Falloff`); structures themselves get no blend at all. Region measured:
  joins stepping >0.5 m went 814 -> 33, deck spans over 50% grade 5,770 -> 471.
- **A TLM `Bruecke` feature does not end at the abutment.** It ends where its attributes
  change, so a viaduct is several features meeting in mid-air. `RoadExtractor` buffers every
  line before emitting any, so it can index structure endpoints and tell a mid-span join
  (two structure ends at one position) from a real transition to a draped road.
- **`kunstbaute` is a compound field.** `Bruecke mit Treppe`, `Gedeckte Bruecke`,
  `Bruecke mit Galerie`, `Unterfuehrung mit Treppe`, `Steg` — matching it with `switch`
  equality silently drops ~2,000 structures per country, and a bridge that loses its
  `Bridge` flag is draped, so it dives into the gorge it was crossing. Match with `Contains`.
- **`TileId.FromLv95` can only name ONE of the tiles that share a lattice line.** Every
  boundary vertex belongs to two tiles (four at a corner). Resolving by coordinate alone
  therefore (a) leaves the neighbour's edge row unclassified in `.cover`, which opens a 4 m
  gap in the water surface along every seam a river crosses, and (b) returns a null height
  for road vertices at a batch edge. `TerrainSampler` tries all the sharing tiles;
  `CoverExtractor.MarkIn` stamps all of them.
- **Never fall back to TLM's Z for one vertex of a draped line.** The two height models
  disagree by metres, so the road grows a spike at exactly that vertex. Interpolate across
  the gap from the neighbours instead (`DrapeHeights`).
- **Carrying a height across a plan-view move is only safe where the ground is flat.** The
  rewrite keeps each vertex's altitude from the original line at the nearest point — which is
  right, because the originals hold the drape, the surveyed deck heights and the approach ramps
  that re-draping would destroy. Region-wide that costs a mean of 8 mm and a p99 of 9 cm over
  9.3 M samples, but the worst case was **12 m**: a footpath on a cliff lip, moved 0.48 m, where
  swissALTI3D drops tens of metres between adjacent cells. Two bounds fix it — `MaxOffset` keeps
  smoothing inside the road's own width, and the rewriter's cliff guard snaps the remainder back
  onto the surveyed line (230 vertices in the whole country). Measure this with
  `--rewrite --dry-run`; never assume it.
- **swissTLM3D draws a direction-separated road as TWO centrelines, one per carriageway** — so
  `DefaultWidth`, which describes the whole road, must not be applied to each line. Measured on
  the A9 and its neighbours: the two motorway centrelines run a median **8.1 m** apart while each
  was drawn 11 m wide, a 3 m overlap for the length of every motorway in the country. Fixed by
  `RoadFormat.WidthFor`, which applies `DividedCarriagewayFactor` (0.55) to any `Divided` line;
  `motorway+motorway` overlap went **15,778 -> 6,279 m²**. The residual is real: at an interchange
  the carriageways genuinely converge (25th percentile separation 3.8 m). `railway+railway`
  (~5,100 m²) is untouched, because TLM does not flag parallel tracks as direction-separated.
- **Densifying is for draping, so anything that is not draped must skip it.** `RoadExtractor`
  densifies every line to 4 m to give the drape enough samples. An aerial ropeway is not draped,
  and the renderer puts a tower under every vertex — so densifying turned each gondola line into
  a picket fence of pylons marching up the mountain at 4 m spacing.
- **Junction ribbons need a per-class depth bias.** TLM centrelines meet exactly — an exit
  ramp starts on the motorway centreline — so ribbons draped with the same offset come out
  coplanar and z-fight into flickering stripes at every junction. `ClassLift` adds 1.2 cm
  per class step, invisible but decisive.
- **A draped centreline on a cliff lip is not a spike in the data.** swissALTI3D really does
  drop 2483 m -> 2389 m between adjacent 2 m cells, and a path surveyed a metre either side
  of that edge samples both. `LimitGrade` clamps interior vertices to a per-class gradient;
  it cleans the drivable network but wide excursions on alpine footpaths survive it.
- **`Faehre` and `Autozug` are routes, not carriageways** — drawn as lines over water or
  through a mountain. Rendering them as road ribbons lays tarmac across the lake.
- **Carve only ground that stands ABOVE the carriageway** (`MinCoverAboveRoad`). Using the
  bore's full vertical span punches holes in flat ground beside an underpass, where the
  surrounding terrain sits at road level and so falls inside that span.
- **Trees must be masked off road corridors.** TLM forest polygons cover the whole wood
  including the road cut through it, so scattered trees grow in the carriageway and
  completely hide tunnel portals. `CoverStage.BuildRoadMask` stamps corridors (wider at
  tunnels/bridges) before the scatter. Symptom to recognise: a screenshot that looks like
  "camera inside terrain" is often camera inside a tree canopy.
- **Tunnel segment ends are not always portals.** A long tunnel is several TLM segments,
  so an endpoint may sit mid-mountain. To find a real portal, test that the ground stays
  below road level for 10-25 m outside the end.
- **`Tree` collides with `Godot.Tree`** (the UI node) — the format type is `TreeInstance`.
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
- **Drape road/feature polylines only after densifying them.** TLM3D emits vertices only
  where a line changes direction, so straight runs span 50 m+ and the ribbon cuts through
  terrain bumps between drape samples, appearing dashed.

- **Networked nodes created in code need deterministic names** — auto names
  (`@MultiplayerSynchronizer@N`) differ per process and break replication by path.
- **Never capture "the thing the player controls" at startup.** The teleport search held the
  spectator camera from `_Ready`, so in multiplayer — where you are an on-foot networked
  `FootPlayer` — Tab silently moved a camera that was not even current and nothing appeared to
  happen. `Teleporter.ActiveTarget` is a delegate resolved per jump.
- **`FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame**, so a focused
  `LineEdit` does not stop them: typing "west" in chat walks you into a lake. Every text-entry
  UI registers with `Core/UiFocus` and both controllers check it.
- **An RPC issued off the main thread does not throw — it never arrives.** `ChunkManager` loads
  and meshes tiles on the thread pool, so `ChunkStreamer.FetchAsync` runs there; the request
  and the connectivity check are both `CallDeferred` onto the main thread. The symptom of
  getting this wrong is silence: the tile simply stays blank forever.
- **"Refused because busy" must not be reported as "does not exist".** The first version sent
  one `AssetMissing` for both, and `NetworkChunkSource` cached it, so a momentary backlog
  blanked those tiles for the whole session. There is now a separate `AssetBusy`, and only a
  permanent miss is remembered.
- **`ChunkManager` records "this tile has no roads/buildings/trees" after ONE empty load.**
  Correct for local files, wrong over a network, so `NetworkChunkSource` retries transient
  failures internally rather than letting a null reach the manager.
- **"Not connected to a server" is NOT a transient failure for a game that has no server.**
  This one cost ten minutes on every cold start and hid for months behind plausible explanations.
  `NetworkChunkSource` falls through to the network whenever a local file is absent — and a
  `.holes` file is absent for **6,067 of 6,699 tiles**, because almost nothing has a tunnel. With
  no server, `ChunkStreamer` reported "not connected" as *transient*, so `FetchLoopAsync` retried
  five times with backoff — 0.4 + 0.9 + 2 + 4 = **7.3 s per tile** — while holding one of the six
  global fetch slots. Six slots over 7.3 s is a hard ceiling of **0.8 tiles per second** whatever
  the disk does. Measured before the fix: 887 s of worker time, **100%** of it in
  `holes+cover`, with `.terr` reads at 0%. After: 0.0 s, and a cold start of **0.6 s** where the
  budget had been 600. The fix is `ChunkStreamer.ServerReachable` — a per-frame snapshot, because
  connectivity is only knowable on the main thread — and `ObtainAsync` returning null immediately
  when there is no peer at all. Deliberately *no peer* rather than *not currently connected*: a
  client mid-join has a peer whose status is `Connecting`, and a null reaching `ChunkManager` is
  recorded as "this tile has no roads" for the rest of the session.
  **The lesson generalises**: profile the load path before optimising it. Tile size, mesh cost and
  LOD radius are the obvious suspects and were together under 5% of the time.
- **A mode that owns the screen must drop the anchors of the mode it replaced.** `ClientWorld`
  registers the spectator camera as a streaming anchor at boot and never removed it on entering
  GPX replay, so a run at Veigy streamed a second full 361-tile box around Riddes, 100 km away and
  permanently off camera — and the video exporter waited for it before every frame.
  `ParkExploreAnchor` takes it off for the duration; `ToggleMode` (T) is now refused during replay,
  since swapping underneath it would both steal the camera and put the box back.
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
  manifest and the client boots into an empty world with a message; it used to throw
  `FileNotFoundException` out of `ClientWorld._Ready` and take the game down. A *server* still
  fails fast, because it is the authority on where the world is and has nothing to serve.
- **Never default the world origin to LV95 0/0.** Switzerland is 2.6 million metres from
  there, so float precision collapses the moment real data arrives. With no manifest,
  `WorldOrigin.SwissDefault()` is used, and a client with zero tiles then *adopts* the
  server's origin via `Rebase` rather than refusing the mismatch — refusing is right when two
  populated worlds disagree, wrong when you have no world at all. Rebasing changes what every
  world coordinate means, so `ClientWorld.RespawnAfterRebase` puts the player down again.
- **`places.json` is the one asset the UI reads, not the streamer** — so it was silently left
  out of `AssetKind` and a streaming client connected fine, pulled terrain fine, and showed an
  empty Tab search. It is now `AssetKind.Places`, fetched during sync into the cache, and
  `PlaceSearchUi.ReloadIndex()` re-reads it (the UI is built long before the connection).
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
- **The client needs its own request budget, not just the server's.** The LOD rings reach nine
  tiles out, so arriving somewhere new makes 361 tiles want their .terr at once — ~177 MB.
  Unbudgeted, the client floods the server, most requests are refused, and the retries fight:
  measured 1,135 refusals in 30 s while only 33 MB arrived. A six-slot semaphore in
  `NetworkChunkSource` took the same window to 226 MB at the full bandwidth cap.
- **A MultiplayerSynchronizer's own authority decides who sends.** Children added after
  the parent's `SetMultiplayerAuthority` default to server authority — set it explicitly.
- `ressources/` (sic) and `terrain_chunks/` have `.gdignore` so the editor never imports
  them; don't move data without keeping those.
- French locale machine: never parse/format floats without InvariantCulture (preprocessor
  sets InvariantGlobalization).
- godot-ai MCP: `game_eval` needs `Engine.get_main_loop().root` (no bare `root`) and
  TAB indentation; `editor_manage monitors_get` reads the EDITOR process, not the game —
  use `Performance.get_monitor` inside `game_eval` for game metrics.
