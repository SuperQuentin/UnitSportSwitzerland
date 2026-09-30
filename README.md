# UnitSportSwitzerland

A Godot 4.7 / C# multiplayer game that streams real Swiss geodata as a low-poly,
PS1-styled world, and can replay GPX tracks on it as ghost races. The long-term goal is
for all of Switzerland to be navigable.

Everything in the world is real: terrain from swissALTI3D, roads and railways from
swissTLM3D, LoD2 buildings from swissBUILDINGS3D joined to the federal building register,
land cover, water and forest from swissTLM3D.

---

## Quick start

**New to the project? Follow [SETUP.md](SETUP.md)**: toolchain versions (Godot 4.7.1 .NET,
.NET 8 + 9 SDKs), first build, a smoke test, and how to get a small region of terrain.

```bash
dotnet build UnitSportSwitzerland.csproj
```

Then press play in the Godot editor, or:

```bash
godot --path . 
```

> **A fresh clone has no terrain data.** The generated chunks are 5.3 GB and the source
> geodata 155 GB, so neither is in the repository. The game starts anyway, on **generated
> terrain**: an alpine valley with a river, a road, a railway, villages, farms, forest, rock and
> snow, so everything can be tried. Wherever there is no real terrain it is generated, and it
> bends to meet the real tiles beside it, so a partial region is surrounded by land rather than
> void (a small "generated terrain" note says which ground you are on; Settings → Generated
> terrain or `--generated off` turns it off). Real terrain takes over tile by tile as you get
> some:
>
> - **join a server** and the whole world streams in and is cached (see *Terrain streaming*);
> - **generate it yourself** by downloading the swisstopo data and running the preprocessor
>   (see *Building the terrain* below).

A mode menu opens first:

| Mode              | What it is                                            |
| ----------------- | ----------------------------------------------------- |
| **Explore**       | fly and walk the terrain freely                       |
| **GPX replay**    | run a recorded track, or race several as ghosts       |
| **Join a server** | connect to a dedicated server and walk it with others |

**Esc** reopens the menu at any time, so you are never stuck inside a mode. Naming a mode on
the command line (`--gpx`, `--connect`) skips the menu; `--menu` forces it open.

In **Explore** you spawn over **Riddes** in the Rhône valley, looking across the village.
To start somewhere else, pass an LV95 easting/northing:

```bash
godot --path . -- --goto Lausanne          # by name
godot --path . -- --at 2538000,1152000     # or by LV95 easting/northing
```

- **WASD + mouse** — fly around. **Space** up, **Shift** down, **Ctrl** boost. Click to take the mouse back after a menu
- **T** — drop onto the ground and walk. **T** again to fly
- On foot: **Shift** run, **Space** jump, **Ctrl** slide, **Space** against a wall to wall jump
- **M** — search for a town and teleport there (only places with terrain are listed)
- **R** — travel menu: bike, skis, paraglider and, offline or as a server admin, vehicles
- **E** — interact: get in or out of a vehicle, search furniture, open a door
- **I** / **Tab** — inventory (Minecraft-style: drag stacks, right-click halves, shift-click moves)
- **F1** — every control, as bound on your keyboard and pad; the bottom-right hints show the ones that apply now
- **G** — load one or more `.gpx` tracks and watch them race
- **H** — hide the interface for a clean view (a small button top-right brings it back)
- **Enter** — chat, when connected to a server (**/** for a command)
- **Esc** — back to the mode menu

### Moving on foot

Walking is deliberately slow — 1.6 m/s walking, 4.6 m/s running — because that is what makes a
10 m building read as 10 m tall. The two momentum moves are the exception:

- **Slide** (**Ctrl**, or **C**): only from a run, never from a walk — a slide out of a standstill
  would be a free speed boost. It launches at 7 m/s, then gains speed downhill and loses it to
  friction on the flat. **A/D** steer it; they do not drive it. **Space** ends it and keeps the
  horizontal speed, which is how you get distance off a descent. The capsule shrinks to 0.9 m
  while down, and you will not stand back up under something too low to stand in.
- **Wall jump** (**Space** in the air, touching a wall): 4.6 m/s up and 5.4 m/s away from the
  face. A surface counts as a wall past ~70°, so scree slopes do not qualify. Twice per airtime,
  and never twice on the same face — two opposing walls in a gully chimney, one flat wall does
  not become a ladder.

Both launches bleed back to running pace on their own, so neither raises your top speed on
flat ground.

The Godot binary used during development:

```
C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe
```

---

## Replaying a GPX track

Press **G**, then pick one or more `.gpx` files — selecting several starts a **ghost race**
where they all begin together and you watch the gaps open.

| Key / button              | Action                                                |
| ------------------------- | ----------------------------------------------------- |
| **G**                     | add track(s)                                          |
| **Space**                 | play / pause                                          |
| **C**                     | cycle camera: chase → first person → cinematic → free |
| **F**                     | follow the next runner                                |
| **H** / *Hide UI* button  | show or hide the interface                            |
| timeline slider           | scrub anywhere in the race                            |
| speed button              | 0.25× up to 32×                                       |
| **+ Add ghost**           | add another runner to a race in progress              |
| **Exit replay** / **Esc** | leave replay and go back to the mode menu             |

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

### The easy way: MapSetup

```bash
dotnet run --project tools/MapSetup
```

A map of Switzerland opens in the terminal. Pick a zone:

| Key | Selects |
|---|---|
| **R** … **R** | a rectangle (move to the opposite corner in between); **E** … **E** erases one |
| **Space** | the tile or pixel under the cursor |
| **B** / **N** | paint / erase while moving; **[** **]** set the brush radius in km |
| **F** | a town, summit or pass, then a radius |
| **C** | a whole canton |
| **+** / **-** | zoom (8, 4, 2 or 1 km per pixel); **H** lists every key |

Colours show what is already on the machine (built, downloaded, available). The side panel
updates as you select: tiles, download size, disk space and an estimated time. **Enter**
continues to the layers (roads and land cover, buildings, cadastre, cycle routes, place
index) and a table with size, disk and time per step. The download estimate uses a 6 s
measurement of your connection. After each run the processing estimates are corrected with
this machine's real rates, saved in `terrain_chunks_temp/mapsetup_stats.json`.

Then it runs every step below in order:

1. download (via `tools/swiss_data.py`)
2. unpack TLM and GWR
3. GDAL exports
4. terrain build
5. feature extraction for the selected tiles only
6. RoadGen junctions
7. place index

Each step is skipped when its output already exists, so running it again after adding a
valley only does the new part. Logs are written to `terrain_chunks_temp/mapsetup_logs/`.
**Ctrl+C** stops a run; `--resume` continues it.

Without the map:

```bash
dotnet run --project tools/MapSetup -- --town Zermatt --radius 4 --layers terrain,roads --yes
dotnet run --project tools/MapSetup -- --canton GE --plan-only      # estimate only
dotnet run --project tools/MapSetup -- --bbox 2580,1110,2585,1115 --layers all
```

The map, tile sizes, cantons and place names come from the committed
`tools/MapSetup/switzerland.bin`, so nothing is downloaded before you confirm.
`--bake` rebuilds that file from swisstopo. It is slow (about 40k HEAD requests) and only
needed when swisstopo publishes new surveys.

The sections below are the same pipeline by hand.

### 1. Work out which tiles you need

Tiles are 1 km squares named by their south-west corner in **LV95** coordinates, e.g.
`2577-1110` covers E 2577000–2578000, N 1110000–1111000.

To find the tiles a GPX track needs, convert its bounding box to LV95. The game prints a
warning when a track starts outside the prepared area.

### 2. Download swissALTI3D

```bash
python tools/swiss_data.py swissalti3d --bbox 2579000 1109000 2586000 1115000
python tools/swiss_data.py swissalti3d --tiles-file my_tiles.txt    # one "2583-1113" per line
```

This queries the swisstopo STAC API. It keeps the newest survey of each tile and downloads
the `.xyz.zip` assets into `ressources/data/swiss_chunks/`. Each tile is a ~19 MB zip of
XYZ text at 0.5 m spacing. Re-runs skip files that are already current.

### 3. Build the terrain chunks

```bash
dotnet run --project tools/TerrainPreprocessor -c Release --   --in ressources/data/swiss_chunks --out terrain_chunks --verify
```

Sources can live anywhere — another drive, a NAS share, several folders at once. `--in` is
searched recursively and may be repeated; when a tile appears twice the newest survey year wins.
Only the 0.5 m product is picked up (2 m files in the same folder are ignored). Bare `.xyz`
files work as well as `.xyz.zip`.

```bash
dotnet run --project tools/TerrainPreprocessor -c Release --   --in D:/swissalti3d --in X:/geodata/alti3d --out terrain_chunks --io-jobs 2
```

`python tools/swiss_data.py --out D:/swissalti3d swissalti3d --bbox ...` downloads straight there.
The downloader runs 8 files in parallel (`--jobs`), fetches files over 256 MB as parallel byte
ranges, checks every file against swisstopo's SHA-256, and resumes interrupted `.part` files.
With `--fill-disk` it downloads what fits instead of refusing, stopping `--reserve-mb` (default
500) above empty and taking tiles nearest the bbox centre first, so a full drive holds one
contiguous area. Measured: 37 tiles / 737 MB in 4 s (was 21 s), the 4.8 GB swissTLM3D in 21 s,
and a re-run with nothing new in 0.3 s without contacting the server.

Each tile is read, inflated, parsed and reduced to its 1001×1001 grid in one parallel pass, and
written as soon as the tiles around it have been parsed (they share its edges). `--verify` reads
every tile back and checks that neighbouring tiles share bit-identical edges. `--dump-png <dir>`
writes hillshade mosaics, which is the quickest way to spot a bad tile.

| Option         | Default      |                                                                               |
| -------------- | ------------ | ----------------------------------------------------------------------------- |
| `--jobs N`     | all cores    | tiles processed at once (inflate + parse is the CPU cost)                     |
| `--io-jobs N`  | 4            | sources read at once — lower it (1-2) for a spinning disk or a slow share     |
| `--temp <dir>` | `<out>_temp` | edge cache, 16 KB per tile                                                    |
| `--force`      |              | re-parse every source, ignoring the cache                                     |
| `--fresh`      |              | the dataset is exactly this run's sources; forget tiles built by earlier runs |

### 3b. Importing a large region

| Per 1000 tiles                            |        |
| ----------------------------------------- | ------ |
| source zips                               | ~19 GB |
| edge cache (`terrain_chunks_temp/*.edge`) | 16 MB  |
| output `.terr` + `.terrc`                 | ~2 GB  |
| build time, 12 cores from NVMe            | ~40 s  |

The build is **incremental**. The edge cache keeps each tile's 16 KB of seam data, so a
tile whose `.terr` exists and whose source has not changed (size + timestamp) is not parsed
again. When a new neighbour arrives, only its seam is recomputed from the existing `.terr`.
You can therefore import Switzerland one download batch or one drive at a time, and an
interrupted run resumes where it stopped. Tiles from earlier runs stay in `manifest.json` even
when their sources are no longer under `--in`, unless you pass `--fresh`.

`<temp>/*.raw` files left by the old two-pass preprocessor (8 MB each, ~50 GB for this region)
are still read instead of the zip when present, which skips inflating. Nothing needs them any
more, though, so delete them once a build has finished.

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

All of it is swisstopo / federal open data, free to use with attribution, except the optional
OpenStreetMap overlay (ODbL, below).

| Dataset                         | Contents                                                                                               | Source                                                      |
| ------------------------------- | ------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------- |
| **swissALTI3D**                 | terrain, 0.5 m XYZ                                                                                     | STAC `ch.swisstopo.swissalti3d`                             |
| **swissTLM3D**                  | roads, rail, land cover, land use, leisure grounds, sports pitches, airfields, water, individual trees | STAC `ch.swisstopo.swisstlm3d`, GeoPackage (4.8 GB)         |
| **swissBUILDINGS3D 3.0**        | LoD2 building solids                                                                                   | STAC `ch.swisstopo.swissbuildings3d_3_0` (14 GB nationwide) |
| **GWR / RegBL**                 | building register: year, floors, category                                                              | `https://public.madd.bfs.admin.ch/{canton}.zip`             |
| **Veloland / Mountainbikeland** | cycle route networks                                                                                   | STAC `ch.astra.veloland`, `ch.astra.mountainbikeland`       |
| **OpenStreetMap** (optional)    | one-way, lanes, width, sidewalks, cycleways, turn lanes on roads (`--layers osm`)                      | Geofabrik `switzerland-YYMMDD.osm.pbf`                      |

The OpenStreetMap overlay is © OpenStreetMap contributors, available under the
[Open Database License](https://www.openstreetmap.org/copyright) (ODbL). Road tiles built with it
are a derived database: if they are ever distributed, they must be offered under the ODbL too
(`docs/notes/tools/osm-odbl-licence.md`). The in-game **Settings > Licenses** page lists every source.

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
                                      [--bind <ip>] [--stream-bandwidth <MB/s>]

# client
godot --path . -- --connect 127.0.0.1 [--name Syra]
```

The server binds to **all interfaces** by default and prints where it can be reached:

```
[net] server listening on UDP 7777 (all interfaces)
[net]   reachable at 192.168.178.38:7777
[net]   reachable at 100.85.45.114:7777
```

### Playing over Tailscale or a forwarded port

**ENet is UDP.** That is the one thing that decides whether a given tunnel works:

|                                     |                                                                 |
| ----------------------------------- | --------------------------------------------------------------- |
| **Tailscale**                       | works as-is — connect to the 100.x address or the MagicDNS name |
| **Port forwarding**                 | works, but the router rule must be **UDP**, not TCP             |
| **ngrok (free), Cloudflare Tunnel** | **will not work** — TCP/HTTP only                               |
| **WireGuard, ZeroTier, Hamachi**    | work, same as Tailscale                                         |

```bash
# tailnet only: nothing is listening on the public interface even if the router forwards
godot --headless --path . -- --server --bind 100.85.45.114 --stream-bandwidth 1

# clients — any of these forms parse
godot --path . -- --connect 100.85.45.114
godot --path . -- --connect 100.85.45.114:7777
godot --path . -- --connect myserver.tail1234.ts.net
godot --path . -- --connect "[fd7a:115c:a1e0::1]:7777"
```

**Set `--stream-bandwidth` for anything off-LAN.** The 3 MB/s default is sized for a local
network; over the internet that is 24 Mbit/s *per client*, which will saturate a home uplink
with two players on it. `--stream-bandwidth 1` is 8 Mbit/s each and still fills a city in
about a minute.

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

|           |                                                                |
| --------- | -------------------------------------------------------------- |
| Bandwidth | 3 MB/s per client, metered server-side on its own ENet channel |
| Fragment  | 24 KB, deflated when that helps                                |
| Integrity | CRC-32 checked before anything is cached                       |
| Cache cap | 2 GB, oldest-first eviction                                    |

Tiles are fetched **nearest first**, and only a few at a time, so what is under your feet
arrives before the horizon. Measured on a client with no terrain at all, joining and spawning
in Sion:

| after joining | detail on screen                                     |
| ------------- | ---------------------------------------------------- |
| 15 s          | 4.34 M prims — buildings, roads and trees around you |
| 30 s          | 5.06 M prims                                         |
| 60 s          | 5.19 M prims, rings filled out to the horizon        |

The town index (`places.json`) is sent too, so the **M** search works on a client that shipped
without it. Revisiting somewhere already streamed fetches **0** new files.

### Chat

**Enter** opens the chat box, **/** opens it already holding a slash, **Up/Down** walk back
through what you sent, **Esc** cancels. The log fades back after a few seconds and returns the
moment anything is said.

| Command                                      | Who      | What                                                |
| -------------------------------------------- | -------- | --------------------------------------------------- |
| `/help` `/who`                               | anyone   | commands you can run; who is online                 |
| `/name <name>`                               | anyone   | change your display name                            |
| `/city <town>`                               | anyone   | teleport yourself, same index as the **M** search   |
| `/me <action>`                               | anyone   | emote                                               |
| `/login <password>`                          | anyone   | become an operator (needs `--admin-password`)       |
| `/say <text>`                                | operator | server announcement                                 |
| `/tp <player>`                               | operator | go to a player                                      |
| `/bring <player>`                            | operator | pull a player to you                                |
| `/tpall <town>`                              | operator | move everyone to a town                             |
| `/kick <player> [reason]`                    | operator | disconnect a player                                 |
| `/admin list \| add <name> \| remove <name>` | operator | manage the persisted operator list                  |

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
