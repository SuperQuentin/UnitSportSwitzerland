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
- **Drawing** (`FarmField.Draw.cs`, `FieldMeshBuilder`, `FieldClip`): one `MeshInstance3D` per 100 m
  chunk (25² cells) within 360 m of the camera, built on a worker, committed one a frame, with the
  prop material (`StyleKit.Material(Prop)`: PS1, Cartoon, Realistic through the style chain), an
  **indexed** mesh (a corner shared by neighbouring quads with the same colour is one vertex),
  vertex colours raw linear with **alpha 0** (the prop shader reads alpha as a lamp mask). Draped on
  `ChunkManager.GridAt(tile).SampleMeshHeight`. Rebuilt only when a cell of the chunk changed
  (`FieldCells.ChunkVersions`), its detail ring changed (lod 0 under 160 m, lod 1 beyond: cover and
  plain slabs), the ground grid was replaced, the month changed, or a growing cell passed its next
  tenth (`NextChange`). The worker gets the cells' looks plus a 2-cell ring (`FieldMeshBuilder.Ring`)
  and the tile's outlines (`FieldTile.Fields`).
- **Edges follow the parcel**: a cell an outline crosses (found by walking the field's segments) is
  cut to the outline (`FieldClip.Cut`: trapezoids between the segments' x breakpoints, even-odd
  counted from below, so holes and concave outlines work); a cell inside stays one square. A cell
  outside every field but crossed by a neighbour's outline gets that field's sliver with the
  neighbour cell's look. Slabs get walls along the outline segments and towards lower neighbours.
- **Looks**: rows run along the field's long axis (second moment of its outer ring), laid out in
  tile metres so they carry on across cells and chunks; ridge feet are cut to the piece on their own
  (no spikes over the outline). Heights and colour jitter come from hashed world cell corners
  (bilinear), so tops have no cell seams; each field gets a tint from its id. Ploughed: furrow
  ridges; sown: soil ridges, then thin sprout rows; stubble: pale stripes over brown ground; mown:
  windrows; potato/beet/vegetables: soil ridges with a leafy crown; standing crops are slabs (wheat
  0.95 m gold, barley paler, maize 2.4 m, rapeseed yellow in flower, sunflower 1.6 m) with a darker
  ear band and a ragged top edge (maize: tall tassels), and on top (lod 0) sawtooth drill rows with
  tramlines every 18 m (grain), rapeseed bumps, maize row roofs, sunflower heads.
  **Untouched grass is not drawn**: the terrain already is grass; only mown cells are.
- **Cost** (`--farmperf`, windowed, Windows, PS1, July; before -> after the outline cut, #494 look
  pass): at 2592500,1182500, 15 chunks, 24.7 k -> 7.3 k vertices; with the camera at a field edge
  (lod 0 near) 33 k vertices / 11 k triangles -> 15.5 k vertices / 18.5 k triangles. Town spot
  2580500,1200500 (August): 47 k -> 15 k vertices, near 68.5 k vertices / 22.8 k triangles -> 25.6 k /
  34.7 k. Main thread unchanged: 0.001-0.003 ms a frame, worst farm frame ~2 ms (the first commit
  pays the material); worker 0.1 -> 0.5-0.7 ms a chunk.
