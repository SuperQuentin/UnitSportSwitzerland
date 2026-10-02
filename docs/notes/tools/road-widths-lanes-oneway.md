# Road widths, lanes, one-way and the motorway median (#117)

- **Where**: `tools/RoadGen/Network/CrossSectionPlanner.cs`, run by the network stage
  (`TileRewriter`) on the raw extractor lines of each block + halo BEFORE the geometry pipeline
  (the width sizes the junction trims, the motorway shift moves the centrelines). Lane layout
  rules shared with the game: `tools/TerrainFormat/RoadCrossSection.cs`. Self-check:
  `RoadGen --plan-check`. Stats: printed by every `--rewrite` ("carriageways (#117, raw lines)").
- **Width** = `RoadSegment.Width` = ATTR `widthCm` (paved carriageway, the ribbon drawn):
  car roads (Major..Lane) take TLM's nominal class width (10m/8m/6m/4m/3m Strasse; 10m and 8m
  are no longer both 9 m); motorway, expressway, ramp are built from lanes: one-way carriageway
  = inner margin 0.5 + lanes x 3.75 (expressway 3.5) + shoulder (motorway 2.5, expressway/ramp
  1.5): 2-lane motorway 10.5 m, ramp 5.75 m; undivided high-speed = 2 x lanes x lane + 2 x 0.5.
  Divided ordinary roads keep the extractor's 0.55 x class width; tracks/paths their class width.
  OSM `width` replaces it when within 0.6..1.6 x the inferred width (car roads only).
- **Lanes** (ATTR `lanesFwd/lanesBwd`, per direction; one-way: all in the travel direction):
  source value (France) > OSM `lanes:forward/backward` or `lanes` > class default: divided
  motorway/expressway 2, undivided 1 + 1, ramp 1, Major..Lane and Square 1 + 1 (a two-way road
  under ~5.5 m shares its two lanes: no centre line, the paint layer decides by width),
  tracks/paths/links 0.
- **Priority**: extractor's (`verkehrsbedeutung` rank << 4 | 12 - class); where TLM has no
  importance, OSM `highway` motorway/trunk/primary gives 2, secondary/tertiary 1.
- **One-way order**: a value the source set (France) > roundabout rings counter-clockwise (TLM
  `kreisel` or OSM roundabout; ring = lines sharing ends, sign of the turn about its centroid) >
  partner rule for divided non-ramp lines > OSM for ramps > connectivity > partner rule as ramp
  fallback > OSM for any other car road. TLM beats OSM on divided carriageways (#118's conflict rule).
- **Partner rule fixes** (measured): partner points 1..30 m to the side (the 3 m minimum of the
  runtime rule missed TLM's 2.3 m motorway pair wherever it ran straight), and a motorway or
  expressway only pairs with one of those classes (at the Riddes interchange a ramp alongside
  outvoted the real partner and flipped two 700 m carriageways).
- **Connectivity**: a line with no direction takes it at each end from the known one-way line
  whose outward heading is most nearly parallel (< 45 deg: alongside; it lies on that line's
  right = diverging/merging ramp = same sense, on its left = the other carriageway = opposite)
  or anti-parallel (> 135 deg: carries on, opposite sense). Repeated until stable.
- **Median measurement** (`osm_overlay_report.txt`, "carriageway median probe", Martigny-Sion,
  5 m stations): TLM Autobahn partner distance p10/median/p90 **1.8/2.3/4.1 m**; the OSM way of
  the same direction lies **3.8 m** outward of TLM's line (p10 3.2, p90 4.4), OSM carriageways
  9.9 m apart (9.3..11.5). Autostrasse: TLM 2.1 m, OSM 2.4 m outward (96 stations only). So TLM
  draws both carriageways almost on the median axis; a 10.5 m carriageway cannot fit by
  narrowing (it would have to be 2.3 m wide).
- **Decision: shift outward.** Each one-way motorway/expressway carriageway (not tunnels: their
  bore is carved into the terrain) moves `width/2 + 0.7 - 1.15` m (4.8 m for 2 lanes) to the
  right of its traffic, so its inner paved edge is 0.7 m off the axis (lanes' middle 3.8 m out,
  as OSM). Ends shared by shifted lines take their average shift; lines attached to a shifted
  node (ramps, service roads) are dragged and taper back over 60 m (lerp if shorter than 120 m);
  shifted lines taper to 0 over 60 m at a tunnel end. Heights stay the TLM line's (nearest
  point), so the cliff guard is skipped on shifted lines and their terrain change is reported
  separately (region: mean 0.19 m, p99 1.78 m, worst 5.9 m near Sion).
- **Overlap, motorway+motorway** (`--rewrite --measure`, Martigny-Sion 513 tiles, halo blocks
  counted as the stage sees them): v3 before #117 (6.05 m, centred) **198,735 m²**; real widths
  centred (`--no-shift`) much worse (Riddes 24 tiles: 30,195 -> 65,970); shifted **19,530 m²**
  (Riddes: 0). Ramp+ramp went 1,050 -> 3,505 m² (ramps 3.3 -> 5.75 m wide). Total overlap after
  junctions 375,390 -> 196,436 m².
- **Region stats** (Martigny-Sion): divided lines with a direction 924/931 (99.2 %; partner 831,
  connectivity 24, fallback 3, OSM 66), against OSM one-way 738/752 agree; ramps 76 (OSM 62,
  connectivity 11, fallback 3); 474 roundabout lines; OSM overrides one-way 759, lanes 2,157,
  width 199, priority 2,636; 207 lines (72.4 km) shifted, 59 attached lines dragged. Widths
  (km): Motorway 10.5 m 69.2 (6.8 m 2.6 OSM 1 lane, 14.2 m 1.5 OSM 3 lanes), Major 10 m 28.8 /
  8 m 21.8, Road 6 m 130, Minor 4 m 473, Lane 3 m 740, Ramp 5.75 m 8.1. Byte-identical rebuild.
- **Runtime**: `LaneEdge.RightLane` = `RoadCrossSection.RightLaneOffset` (0 on v1/v2 widths);
  traffic on a one-way edge drives that lane. `--trafficcheck` fails if a car is ever on an edge
  against its direction; the `[traffic]` line prints a one-way fingerprint to compare peers.
- **Known gaps**: the tree road mask (`CoverStage`) is built from the raw (unshifted) lines, so
  trees may stand on the outer part of a shifted motorway until cover reads the final tiles;
  the shader markings still put the lane line at the ribbon's centre (the paint layer, #116,
  should place it from `RoadCrossSection`: inner margin left, shoulder right).
