# Buildings

- **Buildings**: swissBUILDINGS3D 3.0 LoD2 TINs, read straight from the published FileGDB zips by
  `FileGdb`/`FileGdbGeometry` (#537, `--buildings-gdb`; no GDAL and no GeoPackage step). Formerly `tools/export_buildings.py` (GDAL, the
  only step needing it) -> `buildings.gpkg` -> `.bldg` per tile. GWR cadastre is joined
  **spatially** (EGID is null in the 3.0 Beta); classification uses GKLAS, not GKAT.
  Roof vs wall is decided per triangle by normal; year built tints tone.
  Solids are **re-seated on our heightfield** (median of a 3x3 footprint sample, base set
  0.8 m below ground): the source foundation block is referenced to swisstopo's terrain,
  not ours, which buried every building by ~3 m and some by over 5 m.
  **Stray faces are dropped first** (`BuildingExtractor.DropStrayFaces`): nationwide, 798
  "single houses" in swissBUILDINGS3D 3.0 span over 200 m (the worst 4.3 km) because their solid
  carries faces far from the building, and some carry a face 300 m below it. Left in, the 3x3
  re-seat sample landed on a distant hillside (shifts of 770 m) and the faces drew slivers across
  the map. A face goes when a vertex is further than max(150 m, 4x the median face distance) from
  the median face centre, or 180 m above/below the median face height. A whole solid that is
  consistently off (164 m in one Geneva batch) is not stray — re-seating is what fixes that.
