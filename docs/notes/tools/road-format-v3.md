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
- **`ATTR`** (always): `count u32` (= segments), `recordSize u16` (24; readers skip a longer
  record's tail), pad u16, then per segment, in drawing direction:
  `flags u16` (`RoadAttrFlags`: Urban, Roundabout, Osm, Tram, YieldAtStart, YieldAtEnd,
  OwnerFederal, OwnerCanton, OnStreet = TLM `auf_strasse` rail, Embedded = rail piece inside a
  carriageway, #124 `railways`), `oneWay i8` (+1 with drawing, -1 against, 0 both/unknown),
  `layer i8` (TLM `stufe`, else bridge +1 / tunnel -1), `lanesFwd u8`, `lanesBwd u8` (0 = class
  default), `priority u8` (high nibble `verkehrsbedeutung` rank 0..3, low nibble 12 - class),
  pad u8, `widthCm u16` (TLM nominal class width or OSM width; 0 unknown; the render width stays
  `RoadSegment.Width`), left and right side 6 B each: `sidewalkDm, bikeKind, bikeDm, kerbCm,
  vergeDm, pad`, pad u16.
- **`PANT`** paint (#116): `count u32`, per primitive `shape u8` (0 polyline ribboned at runtime,
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
- **`APRP`** area props (#122 island, splitter island; #119 junction-corner sidewalk):
  `count u32`, per prop `type u8, variant u8, flags u16, height f32 (raise above the vertices,
  e.g. 0.12 kerb), vertexCount u16, indexCount u16`, xyz vertices, u16 indices.
- **Varying along a link** (a sidewalk width per sample, OSM lanes changing mid-line): the
  stage splits the segment; attributes are per segment. Segments already meet end to end at
  tile seams and junction trims.
- **Size** (Martigny-Sion, 513 tiles, with OSM): 49.5 -> 51.8 KB/tile raw (+4.6 %), on the
  wire (deflate Fastest, net8 as the game) 39.0 -> 39.3 KB/tile (+0.7 %): the 24 B record is
  mostly zeros. Worst tile x1.83 (a tile of almost only short segments). Budget: +10 % raw.
