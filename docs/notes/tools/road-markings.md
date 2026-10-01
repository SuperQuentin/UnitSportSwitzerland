# Road markings

- **Reference drawings** (check shapes here before drawing a marking): Wikimedia Commons
  [Diagrams of road markings of Switzerland](https://commons.wikimedia.org/wiki/Category:Diagrams_of_road_markings_of_Switzerland),
  one SVG per SSV marking number, e.g. 6.06 Einspurpfeile
  [CH-Markierung-606-Einspurpfeile.svg](https://upload.wikimedia.org/wikipedia/commons/4/4b/CH-Markierung-606-Einspurpfeile.svg):
  their path data can be traced directly (the #123 lane arrows are). Dimensions: the Stadt Bern
  Normalien C 2.10.x sheets (e.g. 2.10.17 Einspurpfeile, revised 2019). A first attempt from
  memory drew the old Swiss turn arrow (a branch bent off the shaft); the current one jogs.
- **No dataset**: swisstopo publishes NO lane/marking dataset (`tlm_strassen_strasseninfo` is
  junctions and POIs, not lanes). Markings are *inferred* at build time.
- **v1/v2 tiles**: a `MarkingStyle` from width class + `belagsart` + `richtungsgetrennt`, baked
  into uv2.x and drawn by `ps1_road.gdshader` as stripes from uv = (metres along, lateral in
  [-1,1]). Divided carriageways get edge lines and no centre line.
- **v3 tiles (#116)**: paint geometry in the `PANT` layer (`road-format-v3`), written by the
  network stage, `tools/RoadGen/Meshing/PaintEmitter.cs`, from each finished segment (trimmed at
  its junctions, so nothing is painted inside one). A tile flagged `Network` draws no stripes
  (`RoadMeshBuilder.MarkingStyleFor`); rail ballast keeps its sleeper stripes.
  - Rules (Swiss, SSV Art. 73/76/90; values below): paved `Minor` and up only. Motorway,
    expressway and ramp lines lie on the lanes `RoadCrossSection` lays out (#117, same rules as
    traffic): on a one-way carriageway, in its travel direction, a solid Randlinie just outside
    the lanes on each side (the left one in the 0.5 m inner margin, the right one between the
    slow lane and the hard shoulder, Art. 90 al. 1; a Randlinie is not part of the lane, ASTRA
    11001), dashed Leitlinien on the lane boundaries; ramps: edge lines only. An undivided
    high-speed road: the lanes centred, margins both sides. Unknown direction or lanes that do
    not fit the width (v1/v2): the old proportional layout (edges at 0.87 x half width). Other
    one-way/divided roads: that proportional layout. Two-way roads: dashed lines between
    `lanesBwd + lanesFwd` (0 = 1 each) equal lanes, none at all under the minimum width.
    No Randlinien on ordinary rural roads yet (they would grow PANT; after its compaction).

    | Element | Value | Source |
    |---|---|---|
    | Leitlinie (centre/lane line) width, ordinary roads | 0.15 m | SN 640 850a as quoted by LU 653.201 (2024) p. 9, BE Handbuch Markierung 1 (2022) ch. 16, ZH ABC der Strassenmarkierung |
    | Randlinie width, ordinary roads | 0.15 m, axis 0.225 m in from the carriageway edge | BE Handbuch ch. 21; LU 653.201 p. 9 |
    | Leitlinie built-up (`Urban`) | 3 m dash / 3 m gap | LU 653.201 p. 9 |
    | Leitlinie outside built-up areas | 3 m / 6 m ("Regelfall") | LU 653.201 p. 9; BE ch. 16; ZH ABC |
    | Minimum width for a centre line | 5.5 m built-up, 6.0 m outside | BE ch. 16; FR SPC 906 F (SN 640 862: Leitlinie from 5.50 m) |
    | Motorway/expressway Leitlinie width | 0.15 m (Schmalstrich) | ASTRA 11001 Normalprofile (2022), **seen only as a search excerpt** |
    | Motorway/expressway Randlinie width | 0.30 m (Breitstrich) | ASTRA 11001, **search excerpt only**; ASTRA 15002 (2023) 6.2.1 confirms it is wider than the Leitlinie |
    | Motorway/expressway Leitlinie dash/gap | 6 m / 12 m | **UNVERIFIED**: ASTRA 11001 PDF returned 502 on 2026-10-01; confirm when reachable |
    | Colour | white (yellow: bus/bike/pedestrian, orange: works) | SSV Art. 72 al. 2, 73, 74a, 76 |
    | Randlinie between lanes and hard shoulder / road edge | always on Autobahn/Autostrasse | SSV Art. 90 al. 1 |

    Paint stays `White` 0xE0DED1 (render colour, shader-matched). Widths and dashes are stored
    per primitive, so these values cost no PANT bytes. A 0.15 m line is ~1.5 px at ~17 m at
    0.35x: the dither takes it out further away (the old 0.30 m was chosen for legibility).
  - Lines are offset from the segment's tile-local points like the ribbon edges (bisector, no
    miter), so they lie on the ribbon; then simplified (3D Douglas-Peucker, 2 cm).
  - Dash phase: from the raw input's TLM along-line metre (`roads_raw/*.keys`), so dashes run on
    across a tile seam; the dash the seam cuts is a short solid lead-in. Toward a junction or a
    dead end no stub under 40 % of a dash. Self-check: `RoadGen --format-check`.
  - Runtime: `src/Terrain/RoadPaintBuilder.cs` ribbons the polylines
    (`RoadPaintGeometry.Runs`, shared with RoadGen's cost report) into a second surface of the
    road mesh, same material, style 6. No z-fighting at 0.35x: +2 cm lift and the shader pulls
    paint 0.2 % of its distance toward the eye (view space, so independent of reverse Z). A line
    under ~1.5 px dissolves by ordered dither (discard) instead of aliasing.
  - Cost before the Swiss rules (Martigny-Sion, 513 tiles): 13,977 primitives on 233 tiles, 505 game triangles/tile
    average, max 4,804; +1 draw call per road tile with paint (Riddes views: +29..35 draws on
    ~700, +0.2 % primitives). File +2.9 KB/tile raw, +2.0 KB deflated.
  - Cost with the Swiss rules (#114 integration build, 513 tiles): 4,201 primitives on 145 tiles,
    168 game triangles/tile (max 3,484); 4 m Minor roads lost their centre dash (under 5.5/6 m).
    Tiles 54.3 KB raw, 41.4 KB deflated (net8 Fastest; the net9 TerrainPreprocessor prints 48.7
    for the same bytes: its zlib-ng Fastest compresses worse, compare with `RoadGen --rewrite --dry-run`).
  - No paint LOD: roads are only built within `RoadMaxDist` (4 rings) and never rebuilt per
    stride, so a coarse-ring skip has nothing to hook into; the dither takes far lines out.
