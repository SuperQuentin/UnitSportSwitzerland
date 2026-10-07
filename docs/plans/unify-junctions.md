# Unify junction layout (#711)

Status: **plan**, reviewed by the user (decisions below). Branch `feat/711-unify-junctions`, stacked on #704.

## User's decisions (Oct 7 2026)

- Shared by every junction: equal lane widths with the exit lane continuing the through lane; right-turn pockets.
- **Right-turn pockets only on higher-speed roads**, at every kind of junction. The data has no speed yet: import OSM
  `maxspeed` into the overlay first, then build a right pocket only where the approach's speed is above 50 km/h.
- Stay lights-only: left-turn bike lanes, exit-hatch islands without a crosswalk.
- **Crosswalks follow the data at the lights too**: where OSM maps crossings around a lights junction, crosswalks go
  only on the mapped arms. A lights junction without crossing data keeps one on every arm with a sidewalk.
- The four rules the user listed are examples. Further differences are settled as they come up.

## Why

Junctions with traffic lights (`PriorityPlanner.Kind.Signal`) and without them (`Kind.Main` and the other kinds) are laid
out by two code paths in `tools/RoadGen/Rewrite`. The lights got the complete layout (#348, #351, #682). The other
kinds kept #123's simpler one, and several features were built for one path only. Sion's main road (LV95
2506433,1138281, #704's review) shows the result: the kerb does not round a widened corner, and sidewalk corners are
missing beside a path crossing.

**Goal:** every junction is built by one layout. Only the markings, signals and a few rules differ by kind, and each of
those is a deliberate, listed rule.

## The split (inventory of the code, Oct 2026)

### Shared by every junction (today lights only, or priority only, without a reason)

| Feature | Today | Where |
| --- | --- | --- |
| Kerb arcs round every corner (widened or not), sidewalk, verge and path bands round them | lights only (`EmitTownCorners`); priority: corner from the original edge, rejected beside a widening | `TileRewriter.Bikes.cs` `EmitTownCorners`, `TurnLanes.cs` `Corners` |
| Corner pavement patch on every corner | lights; priority only beside a widening | `TurnLanes.cs` `Corners` |
| Bike crossing square across a widened mouth (`SquareCrossing`), straight band beside kerb arcs | lights only | `Bikes.cs` `EmitBikeCrossings` |
| Equal lane widths across an approach, the exit lane continuing the through lane | lights only (`LanesOf(equal)`, `SetExit`) | `TurnLanes.cs` |
| Right-turn pockets | lights only | `TurnLanes.cs` (`RightPlan`) |
| Left-turn bike lane beside a pocket | lights only | `TurnLanes.cs` (`bikeLeft`) |
| Arrows on a multi-lane approach without OSM lane data (`InferredLanes`) | lights only | `TurnLanes.cs` |
| In-place lane lines solid over the last metres before the junction | lights only | `Wish.cs` `OwnArrows` |
| Guide between two lanes making the same turn (`EmitPairGuides`) | lights only | `Signals.cs`, `Wish.cs` |
| Left-turn guide through the junction (`EmitLeftGuides`) | lights only (toward an island) | `Crossings.cs` |
| Through-lane guide past the pocket (`ThroughGuide`) | priority only | `TurnLanes.cs` |
| Exit-hatch island: the hatch keeps its minimum width against split shares | lights only | `TurnLanes.cs` |
| Pedestrian refuge where a crosswalk crosses an exit hatch; hatch and stripes cleared before the crosswalk; bars only over road and paths | priority only (OSM crosswalks) | `DataCrossings.cs`, `Crossings.cs` |
| Lane records (`LANE`) for every approach | lights; priority only with a left pocket | `Lanes.cs` |

### Different by kind (deliberate rules)

User's rules (examples, not the full list): bike-lane markings through the junction only along the priority road; a yielding arm gets give-way markings, not a stop line; the priority road gets no stop line; bike boxes, advanced bike stop lines and the like only at the lights.

The list as the code has it, to confirm:

- **Lights only:** signal poles and heads (main, second, island repeater, pedestrian, bike); the stop line, 0.50 m and set back 3.6 m with room for the crosswalk and the bike crossing; bike boxes and advanced bike stop lines; bike signals and the (a)/(b) bike lane layout the plan forces; a bike lane solid up to its yellow stop line; a bike crossing that is red only where a car crosses it in the same phase; a crosswalk on every arm with a sidewalk (without lights only where OSM maps one); the 3.03 sign on the pole; the exit hatch starting level with the exit arm's stop line.
- **Without lights only:** give-way teeth and the 3.02 sign on yielding arms, moved back behind a path crossing; the main road's centre line and edge guides through the junction, dashed across a joining road's mouth; the pocket's own 0.40 m stop bar at the mouth; a bike crossing that is red wherever a road joins (SSV 74a); every pair of straight arms carried through at junctions with no main road.
- **Planning:** at the lights every approach may get a pocket (the exit is the straightest arm); without lights only the priority road's arms.

## Phases

1. **Corners** (Sion's bug): kerb arcs and bands round every corner at every junction; priority junctions run the
   corner code the lights use, and `CornerPlanner` corners are replaced the same way. Check: Sion and the test region
   have no bare ground between the kerb and the sidewalks; `--street-check`.
2. **Lanes**: equal lane widths and exit continuity, inferred arrows, solid lines before the junction, and lane
   records for every approach, at every junction. Right pockets at every junction where the speed is above 50 km/h
   (OSM `maxspeed` imported into `osm_overlay.tsv`, read by `OsmOverlayReader`).
2b. **Crosswalks from data at the lights**: an arm with an OSM crossing gets one, an arm without gets none, wherever
   the junction has any crossing data; otherwise every arm with a sidewalk.
3. **Guides and islands**: pair guides, left guides, through guides, exit islands and refuges at every junction.
4. **Rules by kind**: the listed differences become one place (a per-kind rule set) that the shared layout asks.
   Tests: the same arms with and without lights differ only in the listed markings.

Each phase: tier 0, `--test-region`, `--signal-check`, `--priority-check`, the 20 real test tiles; pictures to the
user.
