# UnitSportSwitzerland

A Godot 4.7 / C# multiplayer game that streams real Swiss geodata as a low-poly,
PS1-styled world, and can replay GPX tracks on it as ghost races. The long-term goal is
for all of Switzerland to be navigable.

Everything in the world is real: terrain from swissALTI3D, roads and railways from
swissTLM3D, LoD2 buildings from swissBUILDINGS3D joined to the federal building register,
land cover, water and forest from swissTLM3D.

---

## Quick start

```bash
dotnet build UnitSportSwitzerland.csproj
```

Then press play in the Godot editor, or:

```bash
godot --path . 
```

A mode menu opens first:

| Mode | What it is |
|---|---|
| **Explore** | fly and walk the terrain freely |
| **GPX replay** | run a recorded track, or race several as ghosts |
| **Join a server** | connect to a dedicated server and walk it with others |

**Esc** reopens the menu at any time, so you are never stuck inside a mode. Naming a mode on
the command line (`--gpx`, `--connect`) skips the menu; `--menu` forces it open.

In **Explore** you spawn over **Riddes** in the Rhône valley, looking across the village.
To start somewhere else, pass an LV95 easting/northing:

```bash
godot --path . -- --goto Lausanne          # by name
godot --path . -- --at 2538000,1152000     # or by LV95 easting/northing
```

- **WASD + mouse** — fly around. **Shift** boost. Click to take the mouse back after a menu
- **T** — drop onto the ground and walk. **T** again to fly
- **Tab** — search for a town and teleport there (only places with terrain are listed)
- **G** — load one or more `.gpx` tracks and watch them race
- **H** — hide the interface for a clean view (a small button top-right brings it back)
- **Enter** — chat, when connected to a server (**/** for a command)
- **Esc** — back to the mode menu

The Godot binary used during development:

```
C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe
```

---

## Replaying a GPX track

Press **G**, then pick one or more `.gpx` files — selecting several starts a **ghost race**
where they all begin together and you watch the gaps open.

| Key / button | Action |
|---|---|
| **G** | add track(s) |
| **Space** | play / pause |
| **C** | cycle camera: chase → first person → cinematic → free |
| **F** | follow the next runner |
| **H** / *Hide UI* button | show or hide the interface |
| timeline slider | scrub anywhere in the race |
| speed button | 0.25× up to 32× |
| **+ Add ghost** | add another runner to a race in progress |
| **Exit replay** / **Esc** | leave replay and go back to the mode menu |

When the race reaches the finish the clock holds there and a banner offers **Watch again**
or **Exit replay**.

From the command line, `--gpx` may be repeated:

```bash
godot --path . -- --gpx run1.gpx --gpx run2.gpx
```

Notes on how tracks are handled:

- Runners are aligned by **elapsed time**, not recording date, so runs from different
  months compare meaningfully.
- The avatar is placed on **our** terrain, not the GPX elevation — consumer GPS elevation
  is off by 10–20 m and would leave the runner floating. Recorded elevation is kept only
  as a drift statistic.
- Positions, heading and speed are all smoothed; raw 1 Hz GPS jitter is the same size as
  the distance travelled between fixes and otherwise makes the runner weave and surge.
- A track outside the prepared tiles will play, but over empty sky. See below.

---

## Preparing terrain for a new area

The runtime never reads raw geodata. Everything is converted first into compact binary
tiles under `terrain_chunks/`. Adding a new area means downloading the source tiles for it
and re-running the pipeline.

### 1. Work out which tiles you need

Tiles are 1 km squares named by their south-west corner in **LV95** coordinates, e.g.
`2577-1110` covers E 2577000–2578000, N 1110000–1111000.

To find the tiles a GPX track needs, convert its bounding box to LV95. The game prints a
warning when a track starts outside the prepared area.

### 2. Download swissALTI3D

Query the swisstopo STAC API for the tiles, then download the `.xyz.zip` assets into
`ressources/data/swiss_chunks/`.

```bash
# bbox is WGS84 lon/lat: minLon,minLat,maxLon,maxLat
curl -s "https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.swissalti3d/items?bbox=7.14,46.14,7.17,46.19&limit=100" -o stac.json
```

The response contains several survey years per tile — keep the newest. Each tile is a
~110 MB zip of XYZ text at 0.5 m spacing.

> `curl`, not Python. The conda GDAL install replaces openssl and breaks Python's HTTPS
> with `ASN1: NOT_ENOUGH_DATA`.

### 3. Build the terrain chunks

```bash
dotnet run --project tools/TerrainPreprocessor -c Release -- \
  --in ressources/data/swiss_chunks --out terrain_chunks --verify
```

This parses every XYZ zip into a 501×501 height grid at 2 m spacing and writes one `.terr`
per tile plus `manifest.json`. Pass 1 is cached in `terrain_chunks_temp/`, so re-running
only parses tiles it has not seen.

`--verify` checks that neighbouring tiles share bit-identical edges. `--dump-png <dir>`
writes hillshade mosaics, which is the quickest way to spot a bad tile.

### 3b. Importing a large region

The pipeline scales to thousands of tiles, but plan for the disk and the wait:

| Per 1000 tiles | |
|---|---|
| source zips | ~19 GB |
| parse cache (`terrain_chunks_temp/`) | ~8 GB |
| output `.terr` | ~0.5 GB |
| parse time at `--jobs 8` | ~2 min |

So a 6,700-tile region (roughly 125 x 90 km of western Switzerland) is about **125 GB of
zips, 54 GB of cache, 3.4 GB of output and 15 minutes**. Use `--jobs` to match your core
count; 8 sustains roughly 9 tiles/second.

Pass 1 is **resumable** — the cache is keyed by tile, so an interrupted run picks up where
it stopped, and adding tiles later only parses the new ones. The cache can be deleted once
the `.terr` files exist.

Two things change when the area grows:

- `manifest.json` recomputes `suggestedOriginLv95` as the centre of everything, so world
  coordinates shift. That is handled at runtime, but any hard-coded world position (a
  screenshot command, a saved camera) will move.
- Beyond ~100 km from the origin, float32 world coordinates lose sub-centimetre precision.
  Fine for a region; a country-scale world eventually wants a floating origin.

### 4. Add roads, land cover and buildings

These read the terrain chunks (to drape onto them), so they run after step 3.

```bash
# roads, railways, tunnels, bridges, land cover, water, trees
dotnet run --project tools/TerrainPreprocessor -c Release -- \
  --out terrain_chunks --features-only \
  --tlm ressources/data/tlm3d/SWISSTLM3D_2026_LV95_LN02.gpkg \
  --route-keys ressources/data/routes/route_keys.sqlite \
  --cover
```

Buildings need one extra step, because swissBUILDINGS3D ships as FileGDB which needs GDAL
(Python only). Export the region to a GeoPackage first, then run the C# stage:

```bash
python tools/export_buildings.py --bbox 2577000 1110000 2586000 1115000

dotnet run --project tools/TerrainPreprocessor -c Release -- \
  --out terrain_chunks --features-only \
  --buildings ressources/data/buildings3d/buildings.gpkg \
  --gwr ressources/data/gwr/data.sqlite
```

Cycling routes are a one-off export, only needed if you refresh the ASTRA data:

```bash
python tools/export_route_keys.py
```

### 5. Check it

```bash
# screenshot without opening the editor; also prints fps / primitives / draw calls
godot --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png

# verify tunnel portals are physically open
godot --path . -- --probe lv95E,lv95N,seconds
```

---

## Where the source data comes from

All of it is swisstopo / federal open data, free to use with attribution.

| Dataset | Contents | Source |
|---|---|---|
| **swissALTI3D** | terrain, 0.5 m XYZ | STAC `ch.swisstopo.swissalti3d` |
| **swissTLM3D** | roads, rail, land cover, land use, leisure grounds, sports pitches, airfields, water, individual trees | STAC `ch.swisstopo.swisstlm3d`, GeoPackage (4.8 GB) |
| **swissBUILDINGS3D 3.0** | LoD2 building solids | STAC `ch.swisstopo.swissbuildings3d_3_0` (14 GB nationwide) |
| **GWR / RegBL** | building register: year, floors, category | `https://public.madd.bfs.admin.ch/{canton}.zip` |
| **Veloland / Mountainbikeland** | cycle route networks | STAC `ch.astra.veloland`, `ch.astra.mountainbikeland` |

Data lives under `ressources/data/` (spelling is deliberate — it is referenced throughout).
Both that folder and `terrain_chunks/` carry a `.gdignore` so the Godot editor never tries
to import several gigabytes of geodata.

Two things worth knowing before extending the pipeline:

- **swissBUILDINGS3D declares an `EGID` field but leaves it entirely null**, so the key
  join to the building register does not work. The cadastre is joined **spatially**
  instead, which matches ~96%.
- **Switzerland publishes no lane-marking dataset.** Road markings are inferred from width
  class, surface and whether the carriageway is direction-separated.
- **swissTLM3D maps no farmland.** There is no arable, meadow or pasture parcel anywhere in
  it — those are simply the gaps between the layers it *does* map, which is why unclassified
  ground still falls back to altitude banding. Crop-level detail would need the cantonal
  agricultural areas (`geodienste.ch`, *Landwirtschaftliche Kulturflächen*), a separate
  download per canton.
- **Vineyards and orchards are a land *use*, not a ground *cover*.** They live in
  `tlm_areale_nutzungsareal`; looking for them in `tlm_bb_bodenbedeckung` finds nothing at
  all and leaves the Valais slopes as generic pasture.

---

## Multiplayer

```bash
# dedicated server
godot --headless --path . -- --server [--port 7777] [--admin-password <pw>]

# client
godot --path . -- --connect 127.0.0.1 [--name Syra]
```

The server runs the same chunk streaming with meshes disabled — it only needs height data
around each player. Transforms are client-authoritative and relayed by the server.

### Terrain streaming

**A client does not need the world to play in it.** Anything it is missing is streamed from
the server and cached, so a 32 MB client can walk into a city it never shipped with.

Three tiers, tried in order: the local `terrain_chunks/` directory, then
`user://chunk_cache/`, then the server. Streamed files are cached under their ordinary
filename, so the ordinary decoders read them back — and the cached index is saved too, which
means terrain you streamed yesterday still renders with no server running today.

```bash
# a client pointed at a partial copy, with its own cache
godot --path . -- --chunks ./small_region --cache ./my_cache --connect 127.0.0.1
```

| | |
|---|---|
| Bandwidth | 3 MB/s per client, metered server-side on its own ENet channel |
| Fragment | 24 KB, deflated when that helps |
| Integrity | CRC-32 checked before anything is cached |
| Cache cap | 2 GB, oldest-first eviction |

Measured on one machine: a client shipping 25 tiles (32 MB) joined a server holding all 6,699
(5.3 GB), spawned in Sion which it did not have, and pulled **952 files / 226 MB in 75 s** —
356 chunks, 356 cover rasters, 81 road tiles, 49 building tiles, 49 tree tiles. The road and
building counts match the LOD rings exactly (9×9 and 7×7). Revisiting fetched **0** new files.

### Chat

**Enter** opens the chat box, **/** opens it already holding a slash, **Up/Down** walk back
through what you sent, **Esc** cancels. The log fades back after a few seconds and returns the
moment anything is said.

| Command | Who | What |
|---|---|---|
| `/help` `/who` | anyone | commands you can run; who is online |
| `/name <name>` | anyone | change your display name |
| `/city <town>` | anyone | teleport yourself, same index as the **Tab** search |
| `/me <action>` | anyone | emote |
| `/login <password>` | anyone | become an operator (needs `--admin-password`) |
| `/say <text>` | operator | server announcement |
| `/tp <player>` | operator | go to a player |
| `/bring <player>` | operator | pull a player to you |
| `/tpall <town>` | operator | move everyone to a town |
| `/kick <player> [reason]` | operator | disconnect a player |
| `/admin list \| add <name> \| remove <name>` | operator | manage the persisted operator list |

### Operators

Identity is the ENet peer id, which a client cannot forge; the display name is a *request*
that the server sanitises and deduplicates. Two ways in:

- **`user://admins.json`** — a name list, granted automatically on join. `/admin add <name>`
  writes to it.
- **`/login <password>`** — checked against `--admin-password`. Without that argument the
  command is disabled entirely, so a server that never sets one cannot be elevated by guessing.

Every permission check runs on the server. A client-side check would be one the client can edit.

The dedicated server reads commands from **its own stdin**, always as an operator — that is how
the first admin gets granted on a fresh server:

```
[net] server listening on 7777
[chat] Syra joined
/admin add Syra
[admin] granted to Syra
/say Martigny stage start in 5 minutes
/tpall Martigny
```

> The link is plain ENet with no encryption, so `/login` sends the password in clear. Fine on a
> LAN or a trusted link; not fine over the open internet without wiring up Godot's DTLS support.

---

## Layout

```
src/
  Core/      boot, world origin, screenshot + probe helpers
  Terrain/   chunk streaming, mesh builders, LOD, cover palette
  Player/    walking controller, spectator camera
  Net/       ENet setup and player replication
  Gpx/       GPX parsing, race clock, runners, cameras, HUD
tools/
  TerrainFormat/       binary formats shared by preprocessor and game
  TerrainPreprocessor/ the offline pipeline
  export_buildings.py  FileGDB -> GeoPackage (needs GDAL)
  export_route_keys.py cycle route keys
shaders/     ps1_terrain, ps1_road, ps1_building, ps1_tree, ps1_water
terrain_chunks/  generated output: .terr .road .cover .trees .bldg .holes
```

`CLAUDE.md` holds the architecture notes and a list of hard-won gotchas — read it before
changing the formats, the shaders, or anything that has to line up with the terrain grid.
