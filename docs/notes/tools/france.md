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
