# Farm fields at runtime: tiles, cells, drawing (#494)

- **Data**: `fields_E_N.fld` per tile (`tools/TerrainFormat/FieldFormat.cs`, built by the preprocessor's
  fields stage, absent where a tile has none): field outlines with a `CropKind`. The game farms
  **4 m cells on the LV95 grid** (250² a tile, `FieldFormat.CellAt`), a cell owned by the first field
  whose outline holds its centre (`FieldFormat.Rasterise`).
- **Loading**: `IChunkSource.LoadFieldsAsync` through every decorator: `LocalChunkSource` reads the
  file, `CachingChunkSource` caches it (slot `Fields`), `FallbackChunkSource` gives none on generated
  ground, `NetworkChunkSource` streams it as `AssetKind.Fields = 13` (shipped -> cache -> server; an
  older server answers "missing", so no protocol bump), `FixtureChunkSource` writes the course's
  `FixtureCourse.Fields` (flat: wheat east, potatoes north-east, a meadow west of the spawn).
- **`FarmField`** (`src/Farming/FarmField*.cs`, `World/Farm` on the server and every client, system
  `farming`): a client loads the field tiles whose edge is within 700 m of the camera and frees them
  past 1100 m; per tile, on a worker, decode + `FieldTile.Build` (owner map, one ushort a cell,
  125 KB). The server loads a tile when a peer subscribes to it or works on it, and drops it when
  nobody holds it (its cells stay in the store).
- **Shown stage** (`FarmRules.StageOf`): the stored `CellState` if any, else
  `FarmTables.NaturalStage(fieldCrop, month)`. The month: `--farmmonth N`, else `--birdmonth N`
  (the hunting season's flag), else today's (`FarmRules.MonthFromArgs`); a client takes the server's.
  Working a natural cell first turns it into the state it stands for (`FarmRules.NaturalState`:
  growing = sown half a growing time ago, ripe = sown a whole one ago).
- **Clock**: server Unix seconds (`ClockSync.ServerUnixNow`) + `FarmField.ClockSkew` (the checks'
  fast-forward). Crops grow in compressed time (`FarmTables.GrowSeconds`, 25-55 min).
- **`FarmWork.Sweep`** (pinned): the strip's cells (`FarmRules.CellsInStrip`, a reused list, no
  allocation per call), each worked by `FarmRules.Work`, applied at once on this peer; units = items
  gained (harvest/mow, `YieldPerCell` of the crop that was there) or seed items used (sow, cells / 50).
- **Drawing** (`FarmField.Draw.cs`, `FieldMeshBuilder`): one `MeshInstance3D` per 100 m chunk
  (25² cells) within 360 m of the camera, built on a worker, committed one a frame, with the prop
  material (`StyleKit.Material(Prop)`: PS1, Cartoon, Realistic through the style chain), vertex
  colours raw linear with **alpha 0** (the prop shader reads alpha as a lamp mask). Draped on
  `ChunkManager.GridAt(tile).SampleMeshHeight`. Rebuilt only when a cell of the chunk changed
  (`FieldCells.ChunkVersions`), its detail ring changed (lod 0 under 160 m: furrows, sprout rows,
  stubble, swaths, potato/beet/vegetable ridges, sunflower heads; lod 1: flat cover and crop blocks),
  the ground grid was replaced, the month changed, or a growing cell passed its next tenth
  (`NextChange`). Standing crops are blocks (wheat 0.95 m golden, barley paler, maize 2.6 m,
  rapeseed yellow then brown, sunflower 1.9 m with heads) with walls only towards lower neighbours.
  **Untouched grass is not drawn**: the terrain already is grass; only mown cells are.
- **Cost** (`--farmperf`, windowed, real map at 2592500,1182500, Windows): 15 chunks, 25-29 k
  vertices drawn; standing still 0.001 ms a frame on the main thread; moving 20 m/s, 46 builds in
  20 s at 0.002 ms a frame on average, 0.1-0.3 ms a chunk on the worker; worst farm frame ~2.3 ms
  (one chunk's commit; the first pays the material). In a town at 2580500,1200500: 26 chunks, 50 k
  vertices, same per-frame cost.
