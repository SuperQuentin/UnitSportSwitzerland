# Foreign terrain: the neighbours' elevation models (#936, #965)

`ForeignDem` fills the LV95 tiles swissALTI3D lacks, and the -9999 gaps of its border tiles, from a
neighbour's DEM resampled onto `XyzParser`'s 0.5 m cell lattice (reducer, seams, `.terr` unchanged).

```
--in <swiss_chunks> --out <chunks> --dem-box minE,minN,maxE,maxN \
  --dem utm32,<bw dir>,0.23 --dem laea,<bev dir>,0.12 --dem utm32,<bz dir>,0.08 [--dem-reparse] --no-hash
```

- `--dem <grid>,<dir>[,<dz>]`, repeatable, filled in the given order (the next only fills what is
  missing). Grids: `lambert93`, `utm32`, `laea` (`DemProjection`, rigorous from LV95; unit tests
  against GDAL). `--rgealti <dir> [--rgealti-dz]` is `--dem lambert93,...`.
- Readers: GeoTIFF block by block (`TiffFloat`: classic/BigTIFF, either byte order, strips/tiles,
  LZW/deflate, predictors 2/3, ModelTransformation) and zipped XYZ kilometres. Decoded blocks in a
  384 MB LRU per DEM, so BEV's 10 GB-decoded 50 km tiles run in a few hundred MB.
- `--dem-probe E,N` prints each DEM's height at an LV95 point (check against GDAL; all four
  readers agree to the mm). `--dem-check` measures dz on the overlap with built Swiss tiles.
- `--dem-reparse` parses again the swissALTI3D tiles with foreign data under them (their -9999
  gaps were 0 m pits before #936); old `.edge` files go to `<temp>/edges_before_dem/`.
- A **new** tile needs data under 13 of 25 probe points; one the data only clips (the Rhine bank
  at Lörrach) is left to the generated fill instead of being stretched from a few rows.
- Foreign tiles are listed in `<temp>/foreign_tiles.txt` (plus the legacy `rgealti_tiles.txt`) so
  a rerun rebuilds them. On the nationwide build use `--no-hash`: the #924 hash pass after a run
  reads every file (120 GB over SMB the first time).

| Country | Source (licence) | Grid, format | dz (Swiss - theirs) |
|---|---|---|---|
| France | IGN RGE ALTI 1 m (Licence Ouverte) | Lambert-93, LZW strip TIFF per km | -0.28 m |
| Germany | LGL-BW DGM1 (dl-de/by-2.0), `opengeodata.lgl-bw.de/data/dgm/dgm1_32_<E>_<N>_2_bw.zip` (odd E, even N, 2 km) | UTM32, XYZ text | +0.23 m |
| Austria | BEV ALS-DTM 1 m (CC BY 4.0), `data.bev.gv.at/download/ALS/DTM/20220915/ALS_DTM_CRS3035RES50000m<tile>.tif` | LAEA, 50 km LZW BigTIFF | +0.12 m (shift 0) |
| Italy | Bolzano DTM 0.5 m (CC0), WCS `geoservices9.civis.bz.it` at SCALEFACTOR=0.5 | UTM32, big-endian tiled TIFF | +0.08 m (4 tiles) |

- Liechtenstein is in swissALTI3D already. Lombardy, Piedmont, Aosta Valley: not looked at.
- Bavaria does not border Switzerland (only Lake Constance); BW does.
- `XyzParser.FillMissing` used to leave rows below the data at MissingCell: 4700 m walls in a
  tile whose foot had no data. It copies the nearest filled row either way now.
