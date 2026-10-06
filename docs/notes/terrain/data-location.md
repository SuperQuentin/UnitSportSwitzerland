# Data location: terrain on another drive

- **Where the game looks for tiles** (`src/Core/TerrainPaths.FindChunkDir`, resolved once per
  process and logged as `[paths] terrain chunks: <dir> (<source>)`): `--chunks <dir>` >
  `UNITSPORT_CHUNKS` env var > the `"chunks"` of a `terrain_location.json` next to the exe, then in
  the project folder > `terrain_chunks/` next to the exe > `res://terrain_chunks`. A missing
  directory at any step warns and falls through. Client, dedicated server, ambience, place search
  and the tunnel probe all go through it, so one setting moves everything.
- **`terrain_location.json`** (repo root, gitignored): `{"data": "...", "chunks": "..."}`,
  absolute or relative to the file; either key may be missing (= the repo's own folder). MapSetup
  writes it (`--pick-location`, "Storage location..." under "Go?", or `--save-location` to keep
  the `--data`/`--chunks` of that run) and reads it back as its defaults; flags override it for one
  run. Choosing the repo's folders for both deletes the file. The game only reads `"chunks"`.
- **Nothing is moved** when the location changes: MapSetup rescans the new place (tiles already
  there count) and prints what was left behind. Its state (`mapsetup.json`, logs, edge cache) is
  in `<chunks>_temp`, so move that along with the tiles. The selection and layers carry over; the
  measured rates are the machine's and stay.
- **Worktrees** have their own root, so they do not see the main checkout's file: copy it in, set
  `UNITSPORT_CHUNKS`, or pass `--chunks`.
- **The Python helpers default to the repo's `ressources/data`**: MapSetup must hand them the data
  location explicitly (`export_buildings.py --src/--out`,
  `swiss_data.py --out`), or on another drive they fail (`sqlite3 ... unable to open database file`).
