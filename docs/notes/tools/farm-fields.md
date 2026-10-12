# Farm fields data (#494)

- **Source**: MGDM 153.1 "LWB Nutzungsflächen", per canton on geodienste.ch (INTERLIS 2.3 XTF in a zip).
  Credit: "Landwirtschaftliche Nutzungsflächen, Kantone / geodienste.ch" (`Core/Licenses.cs`).
  `python tools/swiss_data.py lwb` fetches the 21 "Frei erhältlich" cantons into `ressources/data/lwb/`
  (~1.15 GB); VD, NE, TI, NW, OW (and FL) are gated. BS publishes an empty file.
- **Stage**: `FieldStage` (`tools/TerrainPreprocessor`) streams each XTF from its zip with XmlReader
  (`LwbReader`, no GDAL), one task per canton. `Nutzungsart` reference = LNF code in both model versions
  (v2.0 TIDs equal the codes), so no catalogue is read at build time.
- **Mapping**: `FieldCodes.Table`, one data-driven table, German name as comment. Vines, orchards, berries,
  nurseries, forest, hedges, trees overlay codes (92x), greenhouses, litter, unproductive -> `None` (dropped).
  Report lists dropped and unmapped codes (none unmapped in the real run).
- **Geometry**: Douglas-Peucker 0.5 m, parts under 200 m2 dropped, ring 0 outer, rest holes, written whole
  into every manifest tile the bounds touch. Id = hash of canton + `Identifikator` + part (top bit clear);
  OSM ids have the top bit set.
- **OSM fallback** (`OsmFields`): closed ways and simple multipolygons with `landuse=farmland` (crop from
  `crop=*`, else hash-weighted: wheat 30, maize 20, barley 12, rapeseed 8, sugar beet 6, potato 4,
  vegetables 4, sunflower 2, legumes 4, other 10) and `landuse=meadow` -> Meadow. swissTLM3D has no canton
  layer, so the gated cantons are taken from the GWR building canton on a 100 m grid (`--gwr data.sqlite`);
  without `--gwr`, the fallback applies to tiles with no LWB field at all. OSM fields are ODbL.
- **Commands**: `dotnet run --project tools/TerrainPreprocessor -c Release -- --fields ressources/data/lwb
  --osm-pbf ressources/data/osm/switzerland-latest.osm.pbf --gwr ressources/data/gwr/data.sqlite
  --out terrain_chunks [--tiles-file f]` writes `fields_E_N.fld` + `fields_report.txt` only (stale own
  files removed). `--fields-check` self-check. MapSetup: `--layers fields`.
- **Whole-Switzerland run** (43548 tiles): 21 cantons, 1,289,586 LWB fields (OSM +19,192), 1,308,778 fields
  / 1,652,547 tile copies in 31,850 tiles, 298 MB, 56 s with 2.3 GB peak. Area (ha, LWB): meadow 500k,
  pasture 477k, wheat 73k, maize 57k, barley 20k, rapeseed 15k, sugar beet 13k, vegetables 12k, potato 9k,
  legumes 5k, sunflower 5k, fallow 5k, other 2k.
