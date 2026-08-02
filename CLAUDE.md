# UnitSportSwitzerland

Godot 4.7 C# multiplayer game streaming real swissALTI3D terrain as low-poly PS1-style
world. Long-term goal: all of Switzerland navigable. Plan: `~/.claude/plans/i-want-to-build-dynamic-metcalfe.md`.

## Architecture

- **Data pipeline**: swissALTI3D XYZ zips (`ressources/data/swiss_chunks/`, LV95/EPSG:2056,
  0.5 m grid, 1 km tiles) → `tools/TerrainPreprocessor` → `.terr` binary chunks +
  `manifest.json` in `terrain_chunks/` (501×501 vertices, 2 m grid, global uint16
  quantization so tile seams are bit-identical). Runtime never parses XYZ.
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
  `HeightMapShape3D` collision on d≤1 tiles (CollisionShape3D scale (2,1,2));
  `shaders/ps1_terrain.gdshader` does vertex snap, flat shading via derivatives, palette
  bands, Bayer dither, fog. Fidelity knobs: `rendering/scaling_3d/scale` (0.75) and the
  per-shader `snap_resolution` (640x480) — lower both for a grittier PS1 look, raise for
  crispness. LOD rings live in `LodPolicy` (stride 1 underfoot, out to 40 m quads at d=9). `Core/Main` boots ServerWorld (`--server` /
  dedicated_server feature) or ClientWorld (`--connect host[:port]`, offline otherwise).
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
  bracket, saddle at 0.90 m) because a bike is a shape everyone knows. `Cyclist` combines them
  and splits the mesh three ways — frame, rider, and per-leg — so the cranks turn with cadence
  and the knees follow by a two-bone solve rather than keyframes. Preview with
  `<godot> --path . -- --avatars <seconds> <out.png> [--view deg] [--focus 0..3]`.
- **A riding position is derived from the bike, never eyeballed.** The three contact points are
  fixed — hips on the saddle, hands on the drops, feet on the pedals — so the shoulder is the
  one place a 0.52 m torso and a 0.58 m arm can both reach. Hand-placing those joints produced a
  rider lying horizontally in front of the bars; with the ends pinned, the middle is not a free
  choice.
- **Judge model proportions with a long lens.** The avatar preview's focus camera sits 9 m back
  at 13° FOV, near-orthographic. A close wide-angle view of a bicycle enlarges whichever end is
  nearer and makes correct geometry look wrong — that cost an iteration of "fixing" a rider that
  was already right.
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
- **GPX ghost racing** (`src/Gpx/`): `GpxParser` -> `GpxTrack` (LV95 via `SwissProjection`,
  cumulative time + distance). `RacePlayback` owns ONE clock; each `Runner` samples its own
  track at that shared time, so several GPX files start together and race as ghosts —
  alignment is by *elapsed* time, not wall-clock date, so runs recorded months apart still
  compare. `TrackRibbon` draws the focused runner's course draped on terrain,
  `PlaybackCamera` has chase / first-person / cinematic / free, `PlaybackHud` gives the
  timeline scrubber, speed multiplier, and a leaderboard with gaps in metres and seconds.
  Each runner is a **streaming anchor**, so terrain loads around the race not the camera.
  Elevation is draped (GPS ele kept only as a drift statistic — measured ~1 m on a real
  track, a good check that projection and heightfield agree).
  Keys: **G** add track(s), **Space** play/pause, **C** camera, **F** follow next runner,
  **H** show/hide UI (the toggle button lives outside the hidden panels, or hiding the UI
  would remove the only way back).
  `--gpx <path>` may be repeated to start a race from the command line.
- **Multiplayer**: client-authoritative transforms, MultiplayerSpawner + Synchronizer,
  ENet port 7777. Server runs ChunkManager with BuildMeshes=false (grid-only, for
  height queries around players).
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
- Tunnel collision check: `<godot> --path . -- --probe lv95E,lv95N,seconds`
- Riding check: `<godot> --path . -- --ride bike|skis,seconds[,out.png] [--at E,N]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu` (with `--shot`) to capture the picker.

## Gotchas (learned the hard way)

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
