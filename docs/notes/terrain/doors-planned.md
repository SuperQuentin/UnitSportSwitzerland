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
- **A hall in pieces**: a hall-like kind (works, sheds, garages) peels its roof into up to 12 pieces of 4 m or more
  (`PlanOutline.Pieces`), not 4 wings of 6 m; the slivers the peel leaves between two facing pieces (up to 1.5 m)
  are closed (`Close`), or a piece touching none of the others is dropped as unreached. A works of several halls
  round yards and an alley is one hall in pieces, its doors on the yards opening into them.
- A restaurant or a bar in the building no longer keeps it off the wing plan (`DiningUnits` takes a room of
  whichever plan); a hall venue (bowling, cinema, arcade), a landmark or a bank still does. A wing plan is only
  kept if it validates no worse than the other, furnished (a machine across a piece's angled wall did not).

## A way in behind the door (`Vestibules`)

After `FitOutline`, every street door still without a doorway:
- **A room right behind it** (0.6 m in): its doorway was never planned (the box's side had no room for it), so it
  is fitted as `MoveEntries` fits the others (`FitDoorway`: never into a bedroom, a bathroom or a flat).
- **No room behind it**: a vestibule 1.8 m wide, a four-sided shape square to the door, runs in to the wall of the
  first room it meets, up to 20 m, that wall at most 60° off the door's, and opens into it through an arch: if the
  room is one a street door may open into, the vestibule stays inside the outline and clear of every room, and its
  1 m core square fits. Each is kept only if the plan validates no worse.

## Measure

`--outlinesurvey` (see `interior-outlines`) also prints locked doors by kind, plan type, wall and the room 0.6 m
behind them, why wing plans failed, and with `--key E_N_I` one building's doors and wings. `--no-wings` plans as
before; `--svgkeys a,b` draws those plans to `test_output/survey-plans/` (the first dozen planned wing by wing are
drawn both ways); `test_output/survey-locked/` holds examples of each locked kind, a magenta dot at the door.

Oct 28, 4 tiles of Geneva and Lausanne: street doors 3,156 → 2,715 (flats 1,432 → 992: they lose their end and back
doors), locked 1,388 → 688 (flats 716 → 307, civic 222 → 118, commercial 193 → 162, industrial 114 → 36, garages
79 → 11), civic outline filled 65 → 77 %, ground floor outside the outline 28 → 20 %, a doorway more than 1 m off
its door 453 → 434, turned more than 10° 133 → 179 (fitted doorways on angled walls), invalid plans 0. 105
buildings planned wing by wing or in pieces; 95 doors fitted and 53 vestibules made (the three candidate plans
counted). 296 → 309 ms a plan, the worst 8.4 s.

## Known limits

- A door whose room behind is a bedroom, a bathroom or a flat's own keeps locked: most of what is left.
- A door with no room within 20 m (a wedge no wing or piece covers, a flats wing that does not plan), or on a
  set-back piece of a stepped front.
- A building whose outline is not its box is planned up to three times (front rectangle, box, wings), each
  validated when compared: 296 → 309 ms a plan on the survey's tiles, the worst 8.4 s.
