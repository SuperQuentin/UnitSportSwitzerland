# Source reads follow the tiles, not their bounding box (#678)

- **Why**: every TLM read was one R-tree query over the bounding box of the tile set, with the
  rows coming back in R-tree order. Two costs, both measured on a map made of two areas (Geneva
  and the Valais, 826 tiles in a 94 x 55 km box), on a hard disk:
  - the box holds 214,670 road lines, of which 100,543 touch a built tile;
  - R-tree order is random access into the 4.8 GB GeoPackage: 0.81 ms a row cold, against
    0.15 ms in rowid order (land cover, two cold 30 x 30 km areas).
- **`TileRegion`** (`tools/TerrainPreprocessor/TileRegion.cs`, pure, unit tested): a tile set,
  its box and a margin. `Touches(box)` is true when the box comes within the margin of a wanted
  tile, edges included, as the box test of the query includes them. On a map that is one rectangle
  it keeps exactly what the box keeps.
- **`GeoPackageReader.TileRows(conn, table, columns, region)`** replaces `BboxQuery` +
  `ExecuteReader` in the heavy readers. Ids and boxes come from the R-tree alone (0.4 s for
  214,670), are filtered by `Touches`, fetched in rowid order 50,000 at a time, and handed back
  **in the order the R-tree gave them**: the same rows in the same order as before, minus the
  ones that touch no wanted tile. Order is kept on purpose: every extractor's output is built in
  it (segment order in a `.road`), so the tiles stay byte-identical. Rows are a small
  `GeoPackageReader.Row` (values buffered), read by index like a `SqliteDataReader`.
- **Margins**: lines get `TileRegion.RingM`, one tile around (`RoadExtractor`: a road ramps up to
  the bridge it joins and a tunnel end asks what it meets; `OsmOverlay`: a divided road's partner
  carriageway). Areas and points get none (`CoverExtractor`, surveyed trees,
  `ParkingAreaExtractor`): what touches no wanted tile draws on none.
- **OSM overlay**: the TLM lines one tile around the built tiles, and the OSM nodes kept in memory
  likewise (the lat/lon box first, then the projection). The PBF itself is still decoded whole: the
  format has no spatial index. `osm_overlay.tsv` and `osm_nodes.tsv` therefore no longer hold
  rows for lines and signals in the gaps of a map: 116,193 and 6,310 lines there instead of
  227,768 and 7,028.
- **Building sheets** (`--buildings-gdb`): a sheet is opened when its extent touches a tile of the
  batch, not only the batch's box.
- **Numbers**, the 826-tile map, overlay step: 114 s before on a cold cache (TLM 102.7 s); in one
  session, old code 67 s (TLM 43.9 s, the file partly cached by then), new code 16 s (TLM 4.3 s,
  OSM read 8.7 s, conflation 1.6 s). Road extraction of 8 tiles in two areas 85 km apart: 9.1 s
  -> 0.5 s; their cover pass read 12,758 rings before and 281 now (its time is mostly not the
  read: 98 s -> 68 s). The old code ran first in that pair, so the new one had the warmer cache.
- **Checked**: 8 tiles in two areas built with the old and the new readers (overlay, roads,
  buildings, cover, water): all 55 tile files and the raw roads byte-identical. Overlay of the
  826-tile map: the 6,047 node rows inside built tiles identical; of the 96,602 rows of the lines
  the raw roads use, 5 differ, all on two motorway lines about 3 km long at the edge of the built
  area, in the part more than a kilometre outside it; the network stage writes the same `.road`
  for the tile that uses them.
- **Limit**: an OSM segment whose two nodes are more than a kilometre apart, with one of them
  beyond the ring, is dropped where the old box kept it; the stretch of TLM line under it then
  has no OSM row. None on the test map. The old code had the same cut at the edge of its box.
- **Not converted**: `AirportStage` and `LandingStage` (a few hundred rows over the whole map),
  the legacy `--buildings` GeoPackage route, the GWR register (loaded whole, seconds).
