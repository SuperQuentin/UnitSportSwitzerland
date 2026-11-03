# Bowling centres (#1235, epic #1232)

The generator for OSM's `leisure=bowling_alley` solids (`entertainment-venues`). Research numbers:
`venue-bowling`.

- **Branch**: `InteriorGenerator.Generate` takes the venue (`Generate(tile, ...)` reads `VenueOf`); a
  bowling venue tries `TryBowling` before the industrial and flats rules. It plans two levels if the
  height allows, then one, keeping only a plan `InteriorValidator` passes; otherwise it gives the
  layout back untouched (an industrial solid then still gets its site). `layout.Type = Bowling`,
  `Shop = None`. Stored plans: `GroupPrint` gives `Bowling<BowlingRules>`, so bump `BowlingRules` to
  re-plan centres only (a run reloads the plan stored for that building: bump it after any change
  you want to see).
- **Whole building or not** (`Venues.WholeBuilding`, shared with the facade): a solid at most 13 m
  tall and not a house or flats is the centre's own box: no window grid (`BuildingMeshBuilder`), one
  level up to 7.6 m of wall, two above (storeys of `wall * 0.88 / 2`, 3.2-4.4 m). A taller solid keeps
  its facade storeys and the centre takes its bottom one or two (a mall at La Praille, 36 m).
- **Lane frame** (`BowlFrame`): `u` across the lanes, `v` from the concourse end to the pins; lanes
  along the long axis, along X starting at the end nearer the door. Every rect, point, wall side and
  piece turn goes through it, so both orientations are one code path.
- **Along v**: full blocks (27 m: approach 4.6, lane to the pit 19.2, pinsetter and aisle) when the
  solid is 35 m long, mini ones (13.2 m) from 20.6 m, none below; settees 3.4 m (mini 2.8); the
  concourse is the rest, 4.6-16 m; past that a plain `Hall`. Blocks stop `RearAisle` (0.7 m) short of
  the far wall: a back door there (#498 puts extra doors round long buildings) found a lane in its
  doorway.
- **Across u**: 3.50 m a pair, up to 13; a walkway of 0.8 m each side (2.2 m on the door's side when
  the door opens onto the lanes, along X), up to 12 m of games side past the last lane (pool tables);
  in a mall the programme hugs the door and a plain `Hall` takes the rest.
- **Rooms**: a `Concourse` with a service column at its end away from the door (the public toilets,
  `public-toilets` #1285, and a `Storage`, doors into the concourse), the `BowlingLanes` hall (`Span` 2 on two levels), the arch
  between them over the whole width (the hall's side the full height, so the mezzanine looks over the
  lanes). On two levels: a straight stair along the concourse's end wall and a `Mezzanine` over the
  whole concourse, its lane-side wall an arch from 1.05 m (a parapet). `Mezzanine` gives up (one
  level) when the stair does not fit away from the door.
- **Furnishing** (`HallLayout` -> `BowlingLayout`, plan facts kept per layout in a
  `ConditionalWeakTable`): a `LanePair` per pair and a `Settee` bay in front (0.7 m off the arch);
  the concourse: `Confetti` (the carpet's neon print, walked over, exempt like floor paint), the shoe
  counter by the door, the bar before anything else when there is no mezzanine (a small centre has
  room for one), ball racks, one or two pinballs, 2-4 cabinets, darts, then pool tables and tables
  (also downstairs when the concourse is 150 m2 or more); the mezzanine: carpet, bar, pool, tables,
  darts. Bars fall back to 2.4 then 1.8 m on any wall.
- **Pieces** (`InteriorMeshBuilder.Venues`, authored back to -Z): a lane pair (approach and foul
  line, two lanes, gutters, capping, glowing arrows, ten pins a deck at 0.305 m, pits, a themed masking
  unit with lit lane numbers and the bowl light, pinsetters, the ball return's hood with three balls,
  two score screens on poles over the approach facing the settees; walked on, only the hood and the
  machine end collide), settee bay, shoe counter with its cubby wall of hire shoes, ball rack, bar
  counter with back bar and taps, stool, pool table with its lamp, pinball, arcade cabinet, dart board.
  The model viewer shows each at its size (`BowlingPieces`) and two whole centres ("Bowling centre").
- **Self-lit parts**: vertex alpha 0.2-0.5 (`InteriorMeshBuilder.Lit`) draws unlit in every style
  (`interior.gdshaderinc`); glass stays under 0.2. `Box` keeps alpha on side faces (x0.7 at most).
- **Cosmic bowling**: `RoomLights.Tint` 1 for the lane hall; its lamps blend to blacklight purple with
  `world_night`. By day the maple lanes under plain light.
- **Outside**: a 2.4 m double door where the wall run allows (real walls come in short runs, so a real
  centre's door is often the run's width: left so, rather than moving entrances off the street); the
  name sign over the door and the type's word big (1.2 m letters) 2.4 m above it (`VenueSigns`).
- **Staff** (`InteriorNode.Staff`): a skinned figure behind each shoe counter and bar, a look from the
  piece's place, standing (rest pose), built once; no collision (the counter keeps players out).
- **Cost**: two-level 30 x 48 m centre ~60k vertices, one-level 24 x 45 m ~47k, a bowl bar ~24k.
- **Check**: `--bowlingcheck --systems ui` (`BowlingProbe`): six hand-made solids (two levels, one,
  a 13 x 24 m bowl bar, lanes along X, a 180 x 120 m mall, one too small), each planned, validated,
  meshed, its door and pieces asserted; SVG plans in `test_output/bowling/`. In game: BowlinGland,
  Gland (origin 2510000,1140000, `--time 22`): lanes `394.4,i1.6,-938.8,-6,313.4,4,<png>`,
  concourse `...,133.4,...`, shoe counter `...,170,...`, facade `398.5,g1.7,-934.4,14,25,6,<png>`.
- **Not here**: sound and music (sub-issue 6), bowling for real (next epic: each pair is one piece
  with a stable index to hang its state on), the realistic style's look.
