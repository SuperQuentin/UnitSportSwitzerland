# Roads/rail

- **Roads/rail**: swissTLM3D GeoPackage (`ressources/data/tlm3d/*.gpkg`, SQLite + R-tree,
  read directly from C# — no GDAL) → `.road` binary per km tile in `terrain_chunks/`
  (~2 MB for 42 tiles). Classified by width (`objektart`), surface paved/dirt
  (`belagsart`), hiking (`wanderwege`), cycling (Veloland `TLM_ID` join via
  `TerrainPreprocessor --export-route-keys`, #537), and bridge/tunnel/stairs (`kunstbaute`). Polylines are
  clipped to tile boundaries, densified to ≤4 m, and draped onto the terrain.
