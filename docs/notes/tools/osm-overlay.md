# OpenStreetMap overlay (#118)

- **What**: optional build-time input for the road network stage (#115). OSM ways are conflated
  onto swissTLM3D road lines (`tlm_strassen_strasse`) and only their attributes are kept: one-way,
  lanes, width, sidewalks, cycleways, turn lanes, roundabout, tram. TLM geometry, heights,
  bridges and tunnels are never touched. The road network stage reads
  `<temp>/osm_overlay.tsv` when it exists (`OsmOverlayReader`): per output segment it takes the
  row overlapping its along-line interval most (>= half of it) and fills v3 one-way (only where
  the divided-carriageway inference gave none), lanes, width, sidewalks (1.5 m), cycleways
  (lane 1.5 m, track 2 m), roundabout, tram, and a placeholder Urban (sidewalk mapped, or
  `highway` residential/living_street/pedestrian) until #119. Martigny-Sion: 32,808 of 38,386
  road segments, 475 of 513 tiles flagged Osm. Without the file the build has no OSM data.
- **Commands**:
  `TerrainPreprocessor --out <chunks> --tlm <gpkg> --osm-overlay <data>/osm/switzerland-YYMMDD.osm.pbf [--tiles-file f]`
  covers the manifest's tiles (or the tiles file) and writes `<chunks>_temp/osm_overlay.tsv` and
  `osm_overlay_report.txt`. `--osm-check` is the self-check (synthetic lines, exit 1 on failure).
  MapSetup: `--layers roads,osm` (never implied by `all`); the download is `swiss_data.py osm`.
- **Download gotcha**: Geofabrik's `switzerland-latest.osm.pbf` answered with a 301 to itself
  (2026-09). `swiss_data.py osm` reads the `europe/` index and takes the newest dated
  `switzerland-YYMMDD.osm.pbf`, falling back to `-latest`. MapSetup skips the download when any
  `switzerland-*.osm.pbf` is in `<data>/osm/` (delete it to refresh). The PBF itself is not
  clipped (that needs osmium); the overlay file is the clip, it only holds the region's lines.
- **Reader**: `PbfReader.cs`, no package. OsmSharp 6.2 builds on Linux without GDAL, but read the
  CH extract in 238 s single-threaded and pulls 33 transitive packages (protobuf-net 2.3 and
  netstandard1.x shims). The hand decoder (zlib only, parallel blocks) reads it in ~7 s.
  Gotcha found writing it: `_p += (int)Varint()` reads `_p` before `Varint()` advances it.
- **Matching** (`OsmOverlay.Conflate`): each TLM line is sampled every 5 m; a station takes the
  nearest OSM segment within half the TLM width + 5 m whose heading is within 30 degrees (either
  way round; the sign gives the direction). Runs of one way in one direction are kept when
  >= 15 m or >= half the line; a line is "matched" when kept runs cover >= 50% of it.
  `railway=tram` ways are matched separately and only set the `tram` column.
- **Conflicts** (TLM wins, listed in the report): on a `richtungsgetrennt` line the traffic
  direction is inferred from the partner carriageway (right-hand traffic: the partner lies on
  the left, the way LaneGraph does it). An OSM one-way the other way round, or an OSM two-way
  way on a divided line, is logged and its `oneway` emptied. Most are short runs in
  interchanges; the long ones are a TLM carriageway matched to the OSM way of the other one.
- **Key for #115**: `uuid` (TLM `tlm_strassen_strasse.uuid`, braces included, unique per row)
  + `part` (index of the LineString inside the MultiLineString, 0 almost always) + `[from_m, to_m]`
  = 2D distance along that part **from its first vertex in TLM drawing order**, 0.1 m. RoadExtractor
  densifies and RoadGen may split or trim lines, so the stage should carry the uuid and the
  source along-line distance on each piece and take the rows overlapping it.
- **File** (`osm_overlay.tsv`, v1): line 1 `# osm_overlay v1 osm=<pbf> tlm=<gpkg> bbox=...`, line 2
  the header, then one row per interval sorted by uuid (ordinal), part, from_m. Same inputs give
  byte-identical output (checked, 6 and 3 jobs). Columns: `uuid part from_m to_m osm_way dir`
  (`+` OSM drawn like TLM) `highway` (OSM value) `oneway` (`1` with TLM drawing, `-1` against,
  `0` two-way, empty = unknown or dropped by a conflict; `junction=roundabout` and
  `highway=motorway` imply one-way) `lanes lanes_fwd lanes_bwd width` (metres) `sidewalk_left
  sidewalk_right cycleway_left cycleway_right` (OSM values, e.g. `yes/no/separate`,
  `lane/track`) `turn_lanes_fwd turn_lanes_bwd roundabout tram`. Every per-side and
  per-direction column is already in TLM's drawing direction (swapped when `dir` is `-`).
- **Numbers**, Martigny-Sion (tiles E 2570-2595, N 1104-1121, 30,085 TLM lines): 8.7 s total
  (OSM read 6.7 s warm cache, 16.5 s cold), peak working set 1.6 GB (server GC). Matched lines:
  motorway 100%, 10m/8m/6m/4m/3m Strasse 98/99/97.5/97.5/95.5%, 2m Weg 85%, 1m Weg 71%,
  Verbindung 28%. One-way intervals on 1,666 lines (1,086 plain one-way streets, 390 roundabout
  lines); lanes on 1,860 lines, sidewalks 668, cycleways 525, turn:lanes 134; 67 conflicts
  (Riddes alone: 6).
- **Ceiling**: every node inside the region box is held in memory, fine for a region, ~2 GB for
  the whole country (two passes would fix it).
