# The map screen: where to start, and what terrain to download

- **One screen, three roles** (`Ui/MapScreen`, #515, reworked for first-time players in #799).
  **Library** ("Map" on the title screen, titled "Download Swiss terrain"): numbered steps, 1. Find a
  place, 2. Choose an area (Small/Medium/Large around the marker, draw, or a whole canton), 3. Download;
  the layers sit under "Advanced: what to build". **Landing** (`GameShell.LaunchVia` before a solo
  world, "Where do you want to start?", button "Start here") and **ServerSpawn** (the Host dialog,
  "Where do players start?", `CreateServerSpawn`) are point pickers: search or popular places, the
  marker described in words, one click to confirm. Joining a server never shows it: the server picks
  (`net/arrival`).
- **One marker in every role**, placed by a click, a search result or a popular place (`GoTo`). It is
  said in words under the heading: the nearest town (`MapScreen.PlaceNear`, also used by the Host
  dialog) and whether real terrain is ready within 4 km. With none, a picker offers "Download real
  terrain here · <size>, ~<time>" (8 km around the marker) that starts the background job at once;
  the draw tools and layers are folded under "Advanced: download more terrain".
- **Input** (`Ui/MapCanvas`): the default tool is **Move**, as on any web map: left-drag pans, a
  click (under 5 px of travel) puts the marker down, Enter / pad A puts it on the centre. Rectangle,
  Brush and Erase draw; Shift-click still places the marker from a drawing tool. Right-drag or the
  stick pans, wheel or the shoulders zoom. `map_zoom_in/out`, `map_tool` and `map_search` are bound on
  **letters, not punctuation** — physical keycodes, and a symbol key is labelled differently on every
  layout. `map_tool` cycles Move → Rectangle → Brush → Erase (and opens Advanced when picking). VR is
  deferred: the screen renders on the `XrPad` panel but the map is not pointable yet.
- **The work is `tools/MapCore`**, shared with the terminal wizard (`tools/MapSetup`): `CountryData`
  (embedded `switzerland.bin`, read once per run by `MapScreen.SharedCountry`), `Selection`,
  `LocalState`, and `Planner`, whose estimates the panel shows and `DownloadJob` runs. The game builds
  its `Paths` with `Paths.ForGame`, which has **no repository root** — anything needing one (the
  Python helpers, `dotnet build`) throws, so a plan step that would need it must `Skip` instead.
- **Drawing** (`Ui/MapCanvas`): three layers, and it **redraws only when something changes**, which
  is what lets `_Draw` afford labels and borders at all. (1) a sampled region of one relief texture
  (`Ui/MapRelief`, hillshade + elevation ramp + lakes from `ProceduralWorld.Relief`'s 500 m lattice,
  built once on a worker) — panning and zooming sample it, never rebuild it; (2) a 1 px-per-kilometre
  status image written into one reused `byte[]` when the selection, the running job or what is on
  disk changes; (3) vectors — canton borders in a **single `DrawMultiline`** (a few thousand
  `DrawLine` calls a redraw would cost more than the rest of the screen), then labels decluttered
  biggest-first, the marker (a pin) and the hovered tile (drawing tools only).
  `TextureFilter.Nearest`, so a selected tile is a crisp 1 km square.
- **The relief is free**: `src/Terrain/swiss_relief.gz` is already embedded for the generated
  terrain, so the map costs no new data. `ProceduralWorld.Relief` is `internal` (not `private`) for
  exactly this; do not copy the lattice out of it.
- **Check**: `--mapcheck` (`Ui/MapCheck`, `--systems ui`, in `checkmap.txt`) drives search (the marker
  on the result, named), the default Move tool, Small around the marker, rectangle, brush, erase, zoom,
  clear, the landing role (Advanced folded, confirm) and the server start point role, and asserts the
  landing plumbing directly (`SpawnPoint.ParseTarget` with and without a landing). It runs headless,
  so the relief picture is never drawn and is not what it checks. A look, windowed:
  `--map --uishot <png> 4` (library) or `--landing --uishot <png> 4` (the solo start picker).
