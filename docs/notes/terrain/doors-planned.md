# Doors placed where the plan has a way in (#1304)

Since #1290 every street doorway stands at its real door, and a side door whose doorway would land more than 3 m
away reads as locked (`FarDoor`): 1,388 of 3,156 street doors on the survey's tiles. The doors are placed from the
walls alone (`BuildingFootprint.Compute`), before the plan, so #1304 works on both sides.

## Outside: where the doors go

- **A block of flats** (`BuildingKind.Apartment`): its other doors go only on one facade a wing (the main door's for
  a block in one piece; for an L, a U or a courtyard block, planned wing by wing (#577), each wing's first door's,
  as `TryWings` gives the doors out), and keep `DoorBudget.Spacing` (22 m) apart however the TIN breaks that facade
  into offset pieces (`SameFacade`). The planner builds a stairwell behind each front door far enough from the next
  one; a door on an end or the back wall, or closer, opened into a flat (a bedroom, mostly). A 30 m block now has one
  entrance, a 60 m one three (`--doorcheck`).
- **A courtyard the roof closes round** (`PlanOutline.Enclosed`: empty cells not joined to the grid's border) gets
  no side door, and the main door avoids it. Rare on real tiles: what looked like courtyards were mostly the open
  yards between the halls of one works.

## Inside: planning the far wings

- Any building whose outline peels into two or more wings (`PlanOutline.Wings`) is also planned wing by wing
  (`TryKindWings`), as flats are (#577): a hall a wing for works and sheds (the main door's wing keeps the works'
  own plan, `TryIndustrial`), a cored plan a wing for offices, schools and shops. `TryWings` takes a `WingPlanner`;
  flats' is `TryBlock`. Wings join through arches; a wing no street door reaches is entered from the next one.
- Kept when it scores better: `OutlineScore` plus `DoorWorth` (20 m²) for each street door it opens, against the
  front-rectangle and box plans. Houses stay one home (a wing each was a house each, two kitchens and two stairs).
- `Loose` (not flats): a wing too shallow for a stair stays ground floor only under the others' upper floors
  (`Even`), and the main door goes to the nearest wing within 6 m (the rooms then grow to the facade, and
  `MoveEntries` takes the doorway to the door).
- `Entrances` keys a plan's main doorway by its own door's slot: in a wing it may be door 3 or a link.

## Measure

`--outlinesurvey` (see `interior-outlines`) also prints locked doors by kind, plan type, wall and the room 0.6 m
behind them, why wing plans failed, and with `--key E_N_I` one building's doors and wings. `--no-wings` plans as
before; `--svgkeys a,b` draws those plans to `test_output/survey-plans/` (the first dozen planned wing by wing are
drawn both ways); `test_output/survey-locked/` holds examples of each locked kind, a magenta dot at the door.

Oct 28, 4 tiles of Geneva and Lausanne: street doors 3,156 → 2,715 (flats 1,432 → 992: they lose their end and back
doors), locked 1,388 → 891 (flats 716 → 313, civic 222 → 162, industrial 114 → 88), civic outline filled 65 → 75 %,
a doorway more than 1 m off its door 453 → 437, invalid plans 0. 49 buildings planned wing by wing.

## Known limits

- A works of separate halls under one outline (yards between them, not a courtyard): the wings do not touch, so
  they cannot be linked, and the box hall stands over the yards with its doors deep inside it.
- A door with no room behind it on a wing the plan leaves out (a civic block's far arm the peel misses, a flats
  wing that does not plan), or on a set-back piece of a stepped front.
- A building whose outline is not its box is planned up to three times (front rectangle, box, wings): 296 → 358 ms a
  plan on the survey's tiles, the worst ~10 s (on a loaded machine).
