# Urban streets: sidewalks, road heights, town paving (#119, build side)

- **Where**: the RoadGen network stage (`TileRewriter`), all from the tile's final segments plus the
  building walls (`.bldg`, so `TerrainPreprocessor` now writes buildings before the network stage).
  Plan, measured numbers and every round of fixes: `docs/plans/urban-streets.md`.
- **`UrbanField`** (`Network/UrbanField.cs`): how built-up a place is, from walls alone, on a 2 m
  LV95 lattice: a node is *near* if a wall stands within 18 m; density = max(share of near nodes
  within ±25 m, share within ±150 m × 0.4/0.45). The city scale matters: a quay, a park side or a
  station square has no wall at hand but is in town. Position-only, so seams, junction arms and split
  pieces agree. Urban at 0.4.
- **`RoadHeights`**: the extractor drapes roads 0.35 m (+ class lift) over the terrain; the stage
  moves at-grade roads **and rails** to +0.08 m over the ground outside towns and to ground − 0.12 m
  (kerb) in town, smoothstep on the density 0.15..0.4 (transition grade p99 ~2 %). The class lift is
  kept (it separates overlapping ribbons; dropping it z-fought everything). Motorways never take the
  town height; bridges, tunnels and 30 m round their ends keep theirs.
- **`StreetPlanner`**: candidates = paved at-grade Major..Lane and Square. Every 2 m, per side, a
  ray from the carriageway edge: first wall (`Facades`, wall triangles rasterised at 0.5 m; not roofs,
  eaves overhang) and first other line (`Obstacles`). Width = to the facade if within 5 m, else 2 m
  (front yard / open); none on a median (another carriageway or tram line alongside within 15 m, no
  wall between); a crossing line within the width stops the sidewalk rather than narrowing it;
  parallel paths/tracks do not stop it. 0.5 m steps, pieces ≥ 20 m; the segment is **split** where
  a side changes (paint is emitted on the whole line first, so dashes keep their phase). Kerb 12 cm,
  flush on squares. OSM `sidewalk` forces a side urban.
- **`CornerPlanner`**: APRP `Sidewalk` patches joining the two sidewalks of a junction corner (and
  bare bends > 30°): walks up to 30 m along each arm for the sidewalk start; squared corner where
  compact, else a band following the kerb round the cap at the sidewalks' width (narrowed 0.2 m short
  of a wall), emitted as a strip of independently checked quads; rejected on a wall or carriageway.
- **Approach roads** of a tunnel mouth (`TileRewriter.RampShoulders`): flush shoulders out to the
  bore's half width for 60 m, so the ground is levelled and the ramp walls line up with the bore.
- **Cover** (`CoverStage`): `TownPaving` = TLM-uncovered ground within 8 m of a town street or round
  its caps (medians, islands, plazas); `TunnelRoof` = open/paved ground < 2.5 m over a bore's crown.
  Trees are masked off sidewalks (mask radius includes them).
- **Trams in town** outside any carriageway: ATTR `PavedBed`, drawn as a paved strip with groove
  paint, no ballast; trams on a road bridge are embedded in the deck (`RailRoadOverlap`).
- **Cost** (central Zürich, 20 tiles, vs the #114 branch): 342 → 433 KB/tile raw, 236 → 284 KB on
  the wire; network stage 14 → ~20 s; blend 12.4 → 19.8 ms/tile. Riddes/Sion 6 tiles +28 % raw.
- **Review tools**: `RoadGen --street-svg E,N [--size M] --chunks DIR --svg FILE` (walls,
  carriageways, bridges blue, tunnels magenta, sidewalks by width, corners, walls);
  `--dump-street E,N` (lines within 15 m with heights, corner patches); `--rewrite --debug-street
  E,N` (per-station trace of the street and corner decisions there); `--street-check` (synthetic
  self-check). Stress test: Zürich Bahnhofbrücke station end (#114 comment).
