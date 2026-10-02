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
  `osm_overlay_report.txt` (and `osm_nodes.tsv`, below). `--osm-check` is the self-check
  (synthetic lines and a synthetic crossroads for the nodes file, exit 1 on failure). `--out` is
  only read for its manifest when there is no `--tiles-file`; `--temp` sends the output elsewhere.
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
- **`turn:lanes`** (#347): `OsmOverlayReader.Row.TurnLanesFwd/Bwd` parse the two columns per lane
  (`RoadGen/Import/TurnLanes.cs`, `TurnMove` flags: left/slight/sharp, through, right/slight/sharp,
  reverse, merge_to_*, `None` for an empty lane or `none`, `Unknown` otherwise). Each list keeps
  OSM's lane order, left to right as the drivers on it see it. Read, not used yet (#348).
- **Numbers**, Martigny-Sion (tiles E 2570-2595, N 1104-1121, 30,085 TLM lines): 8.7 s total
  (OSM read 6.7 s warm cache, 16.5 s cold), peak working set 1.6 GB (server GC). Matched lines:
  motorway 100%, 10m/8m/6m/4m/3m Strasse 98/99/97.5/97.5/95.5%, 2m Weg 85%, 1m Weg 71%,
  Verbindung 28%. One-way intervals on 1,666 lines (1,086 plain one-way streets, 390 roundabout
  lines); lanes on 1,860 lines, sidewalks 668, cycleways 525, turn:lanes 134; 67 conflicts
  (Riddes alone: 6).
- **Ceiling**: every node inside the region box is held in memory, fine for a region, ~2 GB for
  the whole country (two passes would fix it).

## Signals, bike boxes, turn restrictions (#347): `osm_nodes.tsv`

- **What**: the same `--osm-overlay` run writes `<temp>/osm_nodes.tsv` (`OsmNodes.cs`) beside the
  overlay: nodes with `highway=traffic_signals` or `crossing=traffic_signals` (pedestrian-only
  signals too), `cycleway[:left|:right|:both]=asl` nodes, and `type=restriction` relations
  (`restriction` or `restriction:motorcar` = `no_left_turn no_right_turn no_straight_on no_u_turn
  only_left_turn only_right_turn only_straight_on`). Nodes and via nodes inside the region box
  only. Nothing reads it yet (#348, #349); `RoadGen/Import/OsmNodesReader.cs` is the reader.
  MapSetup reruns the overlay step when either file is missing.
- **Reader**: `PbfReader.Filter` also keeps chosen tagged nodes (dense `keys_vals`, only turned
  into tags when a wanted key shows up) and chosen relations (members: type, ref, role).
- **Snapping a node**, car ways (`motorway..service`, `busway`, `*_link`) first, then any way:
  1. **overlay row**: rows of the ways through the node; the TLM line passing nearest (within
     half its width + 10 m), the row whose interval holds the projection preferred (30 m slack);
  2. **way geometry**: the way's direction at the node (10 m each side) against the nearest
     parallel TLM line (30 deg, as the conflation). Needed because Geneva splits ways at every
     junction (restrictions require it): the stubs are under the 15 m run rule, so not conflated;
  3. **nearest line**: any TLM line within half its width + 10 m, no direction.
  `junction`: `node` when car ways meet at the node (degree >= 3: 2 per way passing, 1 per way
  ending), else `approach` when the projection is within 30 m of a TLM line end, else `mid`.
  `line_end` and `end_e end_n` name that end (also for `node` within 30 m), for the network stage
  to match its junction node. A TLM line is split at every attribute change, so an end is not
  always a junction. `dir`: `traffic_signals:direction` (else a plain `direction`) translated with
  the way's direction against TLM (row `dir`, or the way-geometry match): `+` faces traffic in TLM
  drawing order, `-` against, `both`, empty = untagged or placed by nearest line. Geneva: 82% of
  the directed approach rows face their `line_end` (the rest: far-side heads, non-junction ends).
- **Restrictions**: via-way relations are dropped and counted. From- and to-way each map to the
  TLM line end nearest the via node within 30 m: by the way's overlay rows (the row's interval
  must reach within 30 m of that end), else by the way's direction leaving the via node against
  the line's direction leaving its end (30 deg). The row sits on the from-line at its end.
- **File** (`osm_nodes.tsv`, v1): line 1 `# osm_nodes v1 osm=<pbf> tlm=<gpkg> bbox=...`, line 2 the
  header, rows sorted by kind, uuid (ordinal), part, along_m, osm_id; byte-identical reruns
  (checked, 16/3 and 16/5 jobs, Debug/Release). Columns: `kind` (`asl|restriction|signal`)
  `osm_id` (node, or relation) `e n` (node / via node, LV95 0.1 m) `uuid part along_m` (from-line
  for a restriction) `junction` (`node|approach|mid|via`) `line_end` (`start|end|` empty) `end_e
  end_n` `dir` `value` (restriction only) `to_uuid to_part to_end` (restriction only) `tags`
  (`k=v;k=v` sorted, of `highway traffic_signals traffic_signals:direction direction crossing
  button_operated traffic_signals:sound traffic_signals:vibration cycleway*`, or `except=` for a
  restriction; a `;` inside a value is written `,`). A node that is both a signal and an asl gives two rows.
- **Report** (`osm_overlay_report.txt`, "OSM nodes"): signals read (highway / crossing-only / with
  a direction), per `junction`, unmatched; asl likewise; restrictions read, mapped, via-way,
  unmatched, other values, outside the region; per value; how each was placed and why the
  overlay row did not do it; TLM lines with turn:lanes.
- **Numbers** (2026-10-01 extract): Geneva, tiles E 2498-2503 N 1115-1120 (36 tiles, more than
  the commune): 2,123 signal nodes (1,100 `highway=traffic_signals`, 1,023 crossing-only, 526 with
  a direction): 24 on a junction node, 2,044 approach, 51 mid-block, 4 unmatched; placed 1,679 by
  row, 411 by way geometry, 29 by nearest line. 43 asl (42 approach). 827 restrictions: 703
  mapped (no_left_turn 302, only_straight_on 200, no_u_turn 75), 3 via-way, 121 unmatched (47
  malformed). turn:lanes on 691 lines. Sion, E 2591-2595 N 1118-1121: 34 signals (17 highway, 4
  with a direction): 9 junction node, 25 approach; 10 asl; 154 restrictions, 105 mapped, 5
  via-way, 43 unmatched; turn:lanes on 69 lines. The OSM read took 20-45 s on a loaded machine,
  as long as main's reader at the same time.
