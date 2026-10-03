# `.road` format v3 (#115)

- **Shape**: every v2 byte unchanged (header, segments, junctions), then `sectionCount u32` and
  sections `tag u32 (FourCC) + byteLength u32 + payload`. A reader skips unknown tags by length,
  so a later issue adds a section (turn lanes, ...) with no version bump and old game builds
  still load the file. v1/v2 decode as before (attributes all zero, no layers).
  Code: `tools/TerrainFormat/RoadFormat.cs` (types), `RoadCodec.cs`; self-check
  `RoadGen --format-check`.
- **Header flags word** (reserved before v3): `Osm` (1) = some segment carries OSM attributes,
  the tile is an ODbL derived database; `Network` (2) = written by the network stage. A v3 tile
  without `Network` is raw extractor output, the only valid stage input.
- **Record size** (#120): 32 B, the 24 below plus per side `shiftStartCm, shiftEndCm u16` (left
  then right): the side starts that far out from the ribbon's edge, linear along the segment (a
  turn lane's widening lies between). Readers accept 24 and skip anything past what they know.
- **`ATR2`** (always, #116b; replaces `ATTR`, which readers still accept): `distinct varint`
  (7-bit, `BinaryWriter.Write7BitEncodedInt`), `recordSize u8` (24; readers skip a longer
  record's tail), the distinct records, then one varint index per segment. Record layout = the
  `ATTR` record below. Region: 2.25 -> 0.37 KB/tile raw (most segments share a few records).
- **`ATTR`** (always): `count u32` (= segments), `recordSize u16` (24; readers skip a longer
  record's tail), pad u16, then per segment, in drawing direction:
  `flags u16` (`RoadAttrFlags`: Urban, Roundabout, Osm, Tram, YieldAtStart, YieldAtEnd,
  OwnerFederal, OwnerCanton, OnStreet = TLM `auf_strasse` rail, Embedded = rail piece inside a
  carriageway, #124 `railways`, PavedBed = tram track laid in town paving, #119), `oneWay i8` (+1 with drawing, -1 against, 0 both/unknown),
  `layer i8` (TLM `stufe`, else bridge +1 / tunnel -1), `lanesFwd u8`, `lanesBwd u8` (per
  direction; a one-way segment has all its lanes in its travel direction; 0 = none/unknown),
  `priority u8` (high nibble `verkehrsbedeutung` rank 0..3, OSM highway where TLM has none; low
  nibble 12 - class), pad u8, `widthCm u16` (paved carriageway width, = `RoadSegment.Width`, both
  decided by the network stage: rules in `road-widths-lanes-oneway`; lanes lie inside it by
  `RoadCrossSection`), left and right side 6 B each: `sidewalkDm, bikeKind, bikeDm, kerbCm,
  vergeDm, pad`, pad u16.
- **`PNT2`** paint (#116b; replaces `PANT`, still read): `styleCount varint`, styles of 19 B
  (`shape, type, variant u8, rgba u32, width, dash, gap f32`), `count varint`, per primitive a
  varint `style * 2 + kind`. Kind 1, a line **along a segment** of the tile: `segment varint,
  offset zigzag-varint mm, from varint cm, to varint cm (0 = to the end)`; the decoder rebuilds
  the vertices with `RoadPaintGeometry.Along` (segment line offset like the ribbon edges, bridge
  lift, cut, 2 cm Douglas-Peucker), the same call the network stage made, so the game draws the
  stage's exact vertices (`RoadGen --rewrite` re-decodes every tile: "0 decoded differently").
  Kind 0, geometry (rail grooves, #121 teeth and give-way lines, anything not along a segment):
  `vertexCount, indexCount varint`, xyz as zigzag-varint mm deltas from the previous vertex
  (first from 0), varint indices. In memory a referenced line is a `RoadPaint` with `Segment`
  set (`RoadPaint.AlongSegment`); a reference to a segment the tile no longer holds is written
  as geometry. Region: PANT 1.03 -> 0.09 KB/tile raw, 0.74 -> 0.06 deflated.
- **`PANT`** paint (#116, pre-#116b, still read): `count u32`, per primitive `shape u8` (0 polyline ribboned at runtime,
  1 triangle list), `type u8` (`PaintType`: WhiteSolid, WhiteDashed, YellowDashed, YellowSolid,
  SharkTooth, Arrow, BikeSymbol, StopLine, GiveWayLine, RailGroove, Hatch), `variant u8`
  (arrow bits Left/Straight/Right), pad, `rgba u32`, `width f32`, `dash f32`, `gap f32`,
  `vertexCount u16`, `indexCount u16`, xyz f32 vertices, u16 indices. Tile-local, heights on the
  surface painted (a bridge deck's included: + `BridgeLift` 0.15). Colour sRGB. A polyline's
  dash pattern starts with a dash at its first vertex (the writer phases it by where it starts
  the line, `road-markings`); `dash` 0 = solid. Written by `PaintEmitter` (#116): white lines
  and `RailGroove` (#124, one line per rail of an embedded piece, colour 0x9A9893); #120/#121/#123
  add types through the same layer.
- **`PPRP`** point props (#121 yield sign, #122 roundabout sign): `count u32`, `recordSize u16`,
  pad, records of 24 B: `type u8, variant u8, flags u16 (Solid), x, y (foot), z, heading
  (rad about +Y, 0 = -Z), height f32`.
- **`LPRP`** linear props (#125 retaining walls fill/cut, #126 Guardrail/Fence/MedianDouble):
  `count u32`, per prop `type u8, variant u8, flags u16, thickness f32, param f32 (type-specific,
  e.g. post spacing), pointCount u16, pad u16`, then x, y (foot), z, height f32 per point. A
  wall run whose height varies along it is one prop.
  Retaining walls (#125, `road-embankments-walls`): `RetainingWallFill`/`Cut`, points every 2 m
  on the FACE line, y = foot, height = top - foot; the solid lies on the LEFT of the point order,
  `thickness` (3 m) deep, open air on the right (a fill wall runs with its road on its left, a cut
  wall with it on its right); `flags` Solid; `variant` 0 generated, 1 a surveyed TLM wall stands
  there (frees the ground, not drawn). A railing on a wall (#126) goes on the face line at the top.
  Region: 2,933 walls, +1.5 KB/tile raw (+2.9 %).
- **Sidewalks (#119, `urban-streets`)**: ATTR `Urban`, per side `sidewalkDm` and `kerbCm` (12, 0 =
  flush) are written by `StreetPlanner`; a segment is split wherever a side's sidewalk changes.
- **Bike infrastructure (#120, `bike-infrastructure`)**: per side `bikeKind` (`Lane` painted in
  the carriageway, `Track` path at sidewalk height, `TrackMid` halfway down), `bikeDm`, `vergeDm`
  (grass between carriageway and path) and `bufferDm` (grass between path and sidewalk; the side's
  former pad byte, older readers skip it); `RoadStreetSection` lays the bands out. Paint:
  `YellowDashed` lane and path lines, `BikeSymbol` as a 1 m line along the segment (`Width` = size
  across, `Variant` 1 = reversed, glyph built by `RoadPaintGeometry.BikeSymbol`), `BikeCrossing`
  (12) a wide red polyline across a junction. A line offset past the carriageway edge lies on
  that side's profile.
- **`APRP`** area props (#122 island, splitter island; #119 junction-corner sidewalk, written by
  `CornerPlanner`: height = kerb, Solid when kerbed):
  `count u32`, per prop `type u8, variant u8, flags u16, height f32 (raise above the vertices,
  e.g. 0.12 kerb), vertexCount u16, indexCount u16`, xyz vertices, u16 indices.
- **`SGNL`** traffic lights (#349/#350): `RoadSignal` in `SignalPlan.cs`, rules in `traffic-signals`.
- **`LANE`** lanes per approach (#353, `RoadApproach.cs`): one record per approach with a #123
  left pocket (lights or not), a #348 right pocket, or lights, in the junction's home tile.
  `count u32, version u8` (1; a reader skips a version it does not know, a build before #353 the
  tag), per record `x, y, z f32` (the stop line's middle as `SGNL` stores it, else the middle of
  the approach lanes at the pocket's stop bar), `heading f32` (the arm's outward heading, as
  `SignalArm`), `signal i16` (index into the tile's `SGNL` records, -1 none), `arm u8` (plan arm),
  `banned u8` (`SignalMoves` an OSM restriction forbids), `laneCentre f32` (the original lane's
  centre right of the segment's centre line), `laneCount u8`, lanes left to right of 18 B:
  `offset, fullFrom, taperFrom, stopBehind f32, moves u8, kind u8` (offset right of the original
  lane at the line; full width from `fullFrom` m before the line; opens or starts moving at
  `taperFrom`; stops `stopBehind` behind the line, a bike box 4 m, an advanced bike line -3 m;
  `Car` or `Bike`). Geneva: 815 records, +1.46 KB/tile raw, 0.75 deflated alone. Tier 0:
  `TerrainFormatTests.Road_lane_section_*`.
- **Varying along a link** (a sidewalk width per sample, OSM lanes changing mid-line): the
  stage splits the segment; attributes are per segment. Segments already meet end to end at
  tile seams and junction trims.
- **Size** (Martigny-Sion, 513 tiles, with OSM): 49.5 -> 51.8 KB/tile raw (+4.6 %), on the
  wire (deflate Fastest, net8 as the game) 39.0 -> 39.3 KB/tile (+0.7 %): the 24 B record is
  mostly zeros. Worst tile x1.83 (a tile of almost only short segments). Budget: +10 % raw.
- **Size after #116b** (513 tiles, net8 `RoadGen --rewrite --dry-run`; KB/tile raw / deflated alone):

  | Part | before | after |
  |---|---|---|
  | v2 bytes (header, segments, junctions) | 49.54 / 39.04 | 49.54 / 39.04 |
  | ATTR -> ATR2 | 2.25 / 0.21 | 0.37 / 0.13 |
  | PANT -> PNT2 | 1.03 / 0.74 | 0.09 / 0.06 |
  | LPRP (#125 walls, untouched) | 1.52 / 1.33 | 1.52 / 1.33 |
  | whole tile | 54.3 (+9.6 %) / 41.4 | 51.5 (+4.0 %) / 40.6 |

  The per-part line is printed by `--rewrite` ("parts"). Next target: LPRP (floats, deflates badly).
