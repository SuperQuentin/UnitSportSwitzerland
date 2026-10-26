# POI pipeline: `PoiStage` -> `pois.bin` (#1224)

The adventure's places (plan `docs/plans/adventure-places.md` 1): every summit, pass, lake, castle,
station... the national survey names, as one region-wide file next to the chunks.

## Commands

- Standalone (needs a built region): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks
  --tlm <SWISSTLM3D.gpkg> --pois [--boundaries <swissboundaries3d.gpkg>] [--osm-pbf <switzerland.osm.pbf> [--gwr <data.sqlite>]]
  [--pois-file <path>] [--atlas-cards <json>]`. 7 s without castles, 18 s with (the OSM read).
- It also runs after every `--places` pass given `--tlm` (MapSetup's "Place index" step passes `--osm-pbf`
  and the bake's cached swissBOUNDARIES3D when they are there).
- `--pois-file` writes elsewhere: build from a live or someone else's region without touching it (the
  stage only reads `manifest.json`, `places.json` and, for elevations TLM lacks, the `.terr` tiles).
- `--poisstats [--pois-file f]`: counts per kind, tier, canton and the curated aliases that do not resolve.
- `--wikidata [--temp dir]` (network, after `--pois`): Wikidata facts into `atlas_facts.json` next to
  `pois.bin`; one SPARQL query per class, cached in `<temp>/wikidata/`. 2 min the first time.
- Local data (this machine): TLM and GWR in `ressources/data/`, swissBOUNDARIES3D in
  `terrain_chunks_temp/mapsetup_bake/` (MapSetup's bake downloads it).

## Rules

- **Ids are made from the source alone, before the region filter**: `<tag>:<slug>.<hash>`, hash = 40 bits
  of SHA-256 over the source identity (`PoiId.Hash`), so a region build and the nationwide one agree.
  The slug is cosmetic: `PoiCatalogue.Resolve` matches tag + hash. A collision within a kind fails the
  build (`PoiPick.Collisions`).
- **Source identities**: the TLM `uuid` without braces, lower case (`PoiPick.SourceUuid`); a lake its
  `tlm_gewaesser_name_uuid` (a lake is many shore lines); a tower, church, chapel or hut its
  `tlm_bauten_name_uuid` (the Berner Münster is 31 footprints of one name); a cable-car end the line's
  uuid + `/top` or `/bottom`; a covered bridge the uuid of its longest segment; a castle `osm:way/123`,
  `osm:node/123` or `egid:123`.
- Read everything nationwide first (43 571 candidates in 15 s), then filter by manifest tile. Merges and
  dedups run on the whole table, so they are region-independent too.
- Elevation: TLM `hoehe` (peaks, passes), else the geometry's Z (TLM is 3D), else the `.terr` sampled
  (castles from OSM). Numbers through invariant culture.
- Canton: `CantonRings` (swissBOUNDARIES3D `kantonsgebiet`, the smaller ring wins: enclaves), shared with
  MapSetup's bake; without `--boundaries` the canton of the nearest `places.json` town within 5 km.
- Tiers: 1 = a curated card resolves to it (`assets/adventure/atlas_cards.json`, absent so far); 2 =
  Hauptgipfel, Alpiner Gipfel, Strassenpass, castle, tower, named waterfall, lake >= 10 ha, hut, cable-car
  top, covered bridge, dam; 3 = the rest (`PoiPick.Tier`).
- `VillageStop`: per `places.json` town, the stop nearest its core whose name holds the town's name
  (within 5 km), else the nearest within 400 m. All bus stops are kept (decided).

## Sources (swissTLM3D 2026), and the traps

| Kind | Source | Kept |
|---|---|---|
| peak, pass | `tlm_namen_name_pkt` POINT | all named |
| cross | `tlm_eo_einzelobjekt` Gipfelkreuz | within 150 m of a peak: the peak's `Cross` flag; else alone, "Summit cross near X" |
| waterfall | same, Wasserfall | named only |
| lake | `tlm_gewaesser_stehendes_gewaesser` See: **LINESTRING shores**, not polygons | lines of a name joined into rings (`PoiPick.Rings`), the largest >= 1 ha, Douglas-Peucker 50 m, <= 128 points |
| tower, church, chapel | `tlm_bauten_gebaeude_footprint` Turm / Sakrales Gebaeude + Sakraler Turm / Kapelle | named, grouped by name object, then one per name within 100 m |
| hut | same, any objektart whose name says Hütte/Huette/Cabane/Capanna/Chamanna/Rifugio | as above |
| bridge | `tlm_strassen_strasse` kunstbaute Gedeckte Bruecke | segments touching end to end merged, >= 20 m |
| dam | `tlm_bauten_staubaute` (POLYGON) Staumauer, Staudamm, Wehr | >= 50 m long |
| station, busstop, ship | `tlm_oev_haltestelle` | all |
| cablecar | `tlm_oev_uebrige_bahn` Luftseilbahn, Gondelbahn ends | both ends, the higher `Top` (a viewpoint), named after the `Uebrige Bahnen` stop within 150 m |
| castle | `CastleStage.FromOsm` + `FromGwr` (no solid check) | named, one per name within 1 km |

## Counts (2026 data)

| Kind | Nationwide | Plan | Dev region (6 699 tiles: GE, VD, FR, part of BE, Western Valais) |
|---|---|---|---|
| peak | 10 096 | 10 096 | 1 243 |
| pass | 2 433 | 2 433 | 402 |
| cross (alone) | 215 (+231 on peaks) | 446 | 50 (58 crossed peaks) |
| waterfall | 157 | 157 | 22 |
| lake | 653 | ~1 500 | 88 |
| tower | 173 | ~200 | 7 |
| church / chapel | 131 / 244 | ~1 000 | 6 / 34 |
| castle | 928 | ~600 | 143 |
| bridge | 246 | ~250 | 23 |
| dam | 248 | ~150 | 34 |
| station | 3 157 | 3 157 | 572 |
| busstop | 23 331 | 23 331 | 4 186 (325 village stops) |
| ship | 363 | 363 | 40 |
| cablecar | 932 | ~900 | 161 (81 tops) |
| hut | 264 | ~450 | 72 |
| **total** | **43 571** | ~45 000 | **7 082**, 398 KB; tier 2: 878 |

- The plan's church/chapel and hut estimates counted footprints; a building of several footprints is one
  place. Lakes: 1 239 named, 653 close into a ring of 1 ha or more.
- Castles: 928 is OSM + GWR names without `CastleStage`'s solid check (which needs the built `.bldg`).
- Wikidata on the region: 1 832 of 7 082 POIs matched (peaks 962/1 243, passes 317/402, stations
  352/572, lakes 67/88, castles 72/143); 21 ambiguous dropped.

## Format

`tools/TerrainFormat/PoiFile.cs`: "POI1", strings, cantons (code, tier 1, tier 2, land fog cells), 37-byte
records (hash 5, kind, flags, tier, canton, discover radius / 2 m, reveal radius x 100 m, E/N decimetres
from 2 480 000 / 1 070 000, elevation i16, name index, line key i64, outline index), lake outlines as
decimetre offsets. Raw on disk, deflated on the wire. Land fog cells: 250 m cells of the region's tiles whose
centre is in the canton and in no lake outline.

## How to check

- `tools/test.sh unit`: `PoiIdTests`, `PoiFileTests`, `PoiPickTests` (`tests/.../PoiTests.cs`).
- The region: `--pois --pois-file test_output/pois/pois.bin` then `--poisstats --pois-file ...`.
