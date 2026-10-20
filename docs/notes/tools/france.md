# France (cross-border)

- **France (cross-border)**: IGN **BD TOPO®** via the Géoplateforme WFS (`data.geopf.fr`, Licence
  Ouverte 2.0) -> `FranceStage`, run as `--france minLon,minLat,maxLon,maxLat`. No GDAL, no
  download: a bbox query returns GeoJSON, projected WGS84 -> LV95 on arrival so French data lands
  on **the same kilometre lattice** as the Swiss and the two share a tile.
  **swissALTI3D already covers a few km past the border**, so terrain was never the gap — only
  the things standing on it. Buildings come as footprint + `hauteur` + `altitude_min/max_toit`,
  i.e. eave *and* ridge, so `FranceBuildings` pitches a roof where those genuinely differ (568 of
  1361 around Veigy) and leaves the rest flat rather than inventing a shape. Roads map
  `nature` -> `RoadClass` and use the surveyed `largeur_de_chaussee`, which is better than the
  Swiss side, where width is inferred from a class.
  **The stage merges, never replaces**: border tiles already hold Swiss data (2506/1125 is 1.1 MB
  of Swiss buildings), so it decodes, appends and writes back. Re-runs are safe — French buildings
  are marked with `YearBuilt = 1` (BD TOPO has no build year, so nothing French ever has a real
  one) and roads keep a `.road.swiss` copy of the original.
  BD TOPO also fills the v3 attributes (#117): `sens_de_circulation` -> one-way,
  `nombre_de_voies` -> lanes, `largeur_de_chaussee` -> width, `importance` 1-2/3/4 -> priority
  rank 3/2/1, `Rond-point` -> Roundabout (not run against the WFS yet).
  Not imported: land cover, trees, and cycle routes — the `amenagement_cyclable_*` fields come
  back null from this WFS, so no French road is ever flagged `Cycle`.

## Terrain: RGE ALTI 1 m (#936)

- `--in <swiss> --rgealti <dir> --rgealti-box minE,minN,maxE,maxN --rgealti-dz -0.28 --out <chunks>`:
  IGN **RGE ALTI 2.0 1 m** GeoTIFFs (Lambert-93 / IGN69, per département from
  `data.geopf.fr/telechargement/resource/RGEALTI?zone=D074`) fill every LV95 tile in the box that
  swissALTI3D lacks, plus the **-9999 gaps** of swissALTI3D's border tiles. Tiles are resampled onto
  `XyzParser`'s 0.5 m cell lattice, so reducer, seams, `.terr` and manifest are the Swiss path.
  The French list is kept in `<temp>/rgealti_tiles.txt` so a rerun rebuilds them, not skips them.
- **swissALTI3D's no-data is -9999**, and `XyzParser` used to clamp it to **0 m**: every border
  tile reaching into France was half 0 m pits (2510_1133: 1.7 M of 4 M cells). It is a missing
  cell now (row-neighbour fill without `--rgealti`). Tiles built before that fix elsewhere on the
  border (Italy, Germany, Austria) still hold the pits until reparsed (delete their `.edge`).
- **Projection**: `RgeAlti.Lv95ToLambert93` is rigorous (Bessel oblique Mercator inverse,
  CH1903+ -> ETRS89 3-parameter shift, Lambert-93) and matches GDAL EPSG:2056 -> 2154 to the mm.
  `SwissProjection`'s approximate formulas are **1-2.5 m off** around Lake Geneva — fine for a
  GPX, not for a terrain seam. Exact at 25 m knots, bilinear in between.
- **Vertical**: `--rgealti-check` compares with swissALTI3D where both exist: median Swiss - French
  **-0.28 m** over the clean tiles; best horizontal shift within 1 m of zero. Lake tiles (Swiss bed
  carved, French surface) and steep Alpine tiles disagree by metres and are not a datum signal.
- **RGE ALTI already holds Léman's bed** on the French side (93 m minimum), but French tiles have no
  `.cover`, so the water pass gives them no `.water`: the French half of the lake is dry ground
  until a French cover (BD TOPO hydrography/vegetation) exists.
- `TiffFloat` reads IGN's strip float32 TIFFs (LZW, predictor 2) without GDAL. 16 GB for D074;
  a 1 735-tile fill took ~16 min on the NAS at 6 jobs, peak a few hundred MB.
