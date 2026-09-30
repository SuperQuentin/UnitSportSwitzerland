# Data pipeline and tools (`tools/`)

Preprocessing, data formats and feature extraction: swissALTI3D/TLM/GWR/BD TOPO -> `.terr`/`.road`/`.cover`/`.trees`/`.bldg`. Runtime streaming and rendering of the result is in `src/Terrain/CLAUDE.md`.

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

## Commands

- **Region setup wizard**: `dotnet run --project tools/MapSetup` (`tools/MapSetup/`, Spectre.Console).
  Terminal map of CH (raw 24-bit ANSI, half-block pixels) to select tiles (rectangle, brush, town +
  radius, canton), an estimate table (download / disk / time per step), then it chains the whole
  pipeline below as subprocesses. Every step skips when its output exists, and the state lives in
  `terrain_chunks_temp/mapsetup*.json`. The map comes from the committed
  `tools/MapSetup/switzerland.bin` (per-km tile: zip size, survey year, canton, max elevation;
  places; buildings sheets; nationwide file sizes). `--bake` rebuilds it from STAC +
  swissBOUNDARIES3D. Non-interactive: `--town X --radius km | --canton VS | --bbox E0,N0,E1,N1 |
  --tiles-file f | --resume`, `--layers`, `--plan-only`, `--yes`. The tile-list plumbing it relies
  on: `swiss_data.py --tiles-file/--progress-json`, TerrainPreprocessor
  `--features-only --tiles-file` and `--places-only` (places without re-running roads, which
  would strip junctions), RoadGen `--tiles-file --skip-rewritten`, and `export_buildings.py --src`
  (per-sheet zips).
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
- French features for a box (needs the terrain built there already; merges into existing tiles):
  `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --france 6.21,46.26,6.27,46.30`

## Gotchas

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
- **Drape road/feature polylines only after densifying them.** TLM3D emits vertices only
  where a line changes direction, so straight runs span 50 m+ and the ribbon cuts through
  terrain bumps between drape samples, appearing dashed.
