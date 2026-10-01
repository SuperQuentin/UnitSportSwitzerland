# Road markings

- **No dataset**: swisstopo publishes NO lane/marking dataset (`tlm_strassen_strasseninfo` is
  junctions and POIs, not lanes). Markings are *inferred* at build time.
- **v1/v2 tiles**: a `MarkingStyle` from width class + `belagsart` + `richtungsgetrennt`, baked
  into uv2.x and drawn by `ps1_road.gdshader` as stripes from uv = (metres along, lateral in
  [-1,1]). Divided carriageways get edge lines and no centre line.
- **v3 tiles (#116)**: paint geometry in the `PANT` layer (`road-format-v3`), written by the
  network stage, `tools/RoadGen/Meshing/PaintEmitter.cs`, from each finished segment (trimmed at
  its junctions, so nothing is painted inside one). A tile flagged `Network` draws no stripes
  (`RoadMeshBuilder.MarkingStyleFor`); rail ballast keeps its sleeper stripes.
  - Rules (today's stripes, now per lane): paved `Minor` and up only. One direction (ATTR
    `oneWay` != 0, divided, motorway, expressway, ramp): solid edge lines at 0.87 x half width on
    motorway/expressway/ramp/divided, dashed dividers between `max(lanesFwd, lanesBwd)` lanes
    (0 = 2 on motorway/expressway, else 1). Two-way: dashed lines between
    `lanesBwd + lanesFwd` (0 = 1 each) equal lanes, backward lanes on the left (keep right).
    Ramps lost today's centre dash. Lines 0.30 m (real paint is 0.10-0.20, invisible at 0.35x),
    dashes 6 m / 3 m (the shader's cadence).
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
  - Cost (Martigny-Sion, 513 tiles): 13,977 primitives on 233 tiles, 505 game triangles/tile
    average, max 4,804; +1 draw call per road tile with paint (Riddes views: +29..35 draws on
    ~700, +0.2 % primitives). File +2.9 KB/tile raw, +2.0 KB deflated.
  - No paint LOD: roads are only built within `RoadMaxDist` (4 rings) and never rebuilt per
    stride, so a coarse-ring skip has nothing to hook into; the dither takes far lines out.
