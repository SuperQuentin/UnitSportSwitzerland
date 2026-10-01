# swiss_relief.py: the generated terrain's heightmap

- `python tools/swiss_relief.py [--spacing 500] [--out src/Terrain/swiss_relief.gz]` rebuilds the
  coarse heightmap the generated terrain is shaped on (`terrain/generated-relief`). Needs GDAL's
  Python bindings (`from osgeo import gdal`) and nothing else; takes ~5 s.
- Reads swisstopo **swissALTIRegio** (`ch.swisstopo.swissaltiregio`, a single 10 GB cloud-optimised
  GeoTIFF in LV95 covering CH, FL and the neighbours' border areas) through `/vsicurl/`: `gdal.Warp`
  with `average` picks the overview just finer than the target, so only a few MB come down.
- Nodes are LV95 multiples of the spacing, each the mean of the cell centred on it; holes (none at
  500 m) are filled from their neighbours. Format: gzip of `SWRL`, u16 version 1, u16 0, i32 minE,
  maxN, spacing, cols, rows, then u16 decimetres, row 0 north, little-endian.
- Changing the file changes the whole generated world on every peer: client and server must run
  the same build.
