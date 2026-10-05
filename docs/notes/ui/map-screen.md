# The map screen: what terrain you have, what to download, where to land

- **One screen, two roles** (`Ui/MapScreen`, #515). **Library**: "Map" on the title screen — look at
  what is downloaded, select more, start a download. **Landing**: shown by `GameShell.LaunchVia`
  before every world from the menu — the same map plus a marker you confirm. They are one screen
  because they answer the same question: "is there real ground where I am going?" is answered by
  looking at the map you would have downloaded it on. The landing warning (nothing real within 4 km)
  is therefore a line on that screen with a "Select 8 km around the marker" button, not a modal.
- **The work is `tools/MapCore`**, shared with the terminal wizard (`tools/MapSetup`): `CountryData`
  (embedded `switzerland.bin`), `Selection`, `LocalState`, and `Planner`, whose steps and estimates
  the side panel shows and `DownloadJob` runs. The game builds its `Paths` with `Paths.ForGame`,
  which has **no repository root** — anything needing one (the Python helpers, `dotnet build`)
  throws, so a plan step that would need it must `Skip` instead.
- **Drawing** (`Ui/MapCanvas`): three layers, and it **redraws only when something changes**, which
  is what lets `_Draw` afford labels and borders at all. (1) a sampled region of one relief texture
  (`Ui/MapRelief`, hillshade + elevation ramp + lakes from `ProceduralWorld.Relief`'s 500 m lattice,
  built once on a worker) — panning and zooming sample it, never rebuild it; (2) a 1 px-per-kilometre
  status image written into one reused `byte[]` when the selection, the running job or what is on
  disk changes; (3) vectors — canton borders in a **single `DrawMultiline`** (a few thousand
  `DrawLine` calls a redraw would cost more than the rest of the screen), then labels decluttered
  biggest-first, the marker and the cursor. `TextureFilter.Nearest`, so a selected tile is a crisp
  1 km square.
- **The relief is free**: `src/Terrain/swiss_relief.gz` is already embedded for the generated
  terrain, so the map costs no new data. `ProceduralWorld.Relief` is `internal` (not `private`) for
  exactly this; do not copy the lattice out of it.
- **Input**: drag draws with an explicit tool (rectangle, brush, erase) rather than a modifier
  nobody would find; right-drag or the stick pans, wheel or the shoulders zoom. `map_zoom_in/out`,
  `map_tool` and `map_search` are bound on **letters, not punctuation** — these are physical
  keycodes, and a symbol key is labelled differently on every layout ("/" sits where "-" is on a
  Swiss keyboard), which made the hint line read nonsense. They share letters with world actions
  because every letter is already bound and only this screen reads them. VR is deferred: the screen
  renders on the `XrPad` panel but the map is not pointable yet.
- **Check**: `--mapcheck` (`Ui/MapCheck`, `--systems ui`, in `checkmap.txt`) drives search,
  rectangle, brush, erase, zoom, clear, then the landing role, and asserts the landing plumbing
  directly (`SpawnPoint.ParseTarget` with and without a landing). It runs headless, so the relief
  picture is never drawn and is not what it checks.
