# Traffic walkers (#1033)

Pedestrians as blocky figures (`TrafficModeMeshBuilder.Walker`, #1147; `WalkerLook`, 0.5 x 1.7 x 0.4 m, 1.35 m/s +-25 %) on the shared base (`traffic-modes`): local and
cosmetic per client, not replicated. Off by default, the user's choice (`GameSettings.TrafficWalkers` 0, 0..300 in Settings,
x0.3 at night), `--walkers N`.
In the test world walkers come only from its sidewalk generators, whatever the setting (#1134, `traffic-generators`).
Files: `World/LaneGraph.Walkers.cs` (the graph), `World/Traffic.Walkers.cs` (the rules), `World/TrafficProbe.Walkers.cs` (the check).

## The walk graph (`LaneGraph.BuildWalks`, worker thread, 3 x 3 km about the view's tile)

- **Sidewalks**: one edge per side of every car road with `SidewalkDm > 0`, on the sidewalk's middle (width/2 + shift + path and
  grass before it, `RoadStreetSection.For`'s bands), at kerb height. A side with no sidewalk but a separated path (`HasTrack`):
  the path's middle (walkers share it with riders). `Class` stays the road's class (spawn weight 1).
- **Paths**: TLM `Path` (weight 0.4) and `Track` (0.1) centrelines.
- **Lit crossings**: each lit junction's `CPAR` walker lines (`SignalSite.Lines`, `Mover == Walker`), each with its pedestrian
  group; a two-stage crossing's halves (#1030) are two lines and two groups, joined across their island by an island link
  (`WalkCrossing.Island`). A walker line runs on past the sidewalk over the paths beside it: each end is cut back to where the line
  passes nearest a walkway (within 6 m) and tied there.
- **Zebras without lights**: read off the tile paint (a `YellowSolid` triangles paint whose quads are ~3 x 0.5 m bars,
  `TileRewriter.EmitCrossing`), skipped within 3 m of a lit line. Where each crosses the cars' road edges (`ZebraHits`).
- **Ties**: a crossing's end to the nearest walkway beyond the kerb; a stray end (a sidewalk stopping at a junction corner or a
  tile seam, a path ending beside a street) to the nearest walkway within 30 m (paths 10 m) the link reaches **without crossing a
  carriageway** (any car edge, junction path or connector of the roads graph), never the other side of its own street.
- **Verge walkways**: a lit crossing whose kerb has no walkway within 6 m (a junction out of town, J7 of the signal test region)
  gets a 40 m walkway out along its arm, away from the junction (shortened before any road it would cross), so walkers reach it.
- Crossing edges are `Connector` (no spawns) and found by `LaneGraph.CrossingOf(edge)` (a weak table).
- **Unmarked crossings** (the user's choice): at a junction mouth with neither lights nor a zebra within 30 m, straight over a
  side arm (class Minor or lower, or giving way there; not divided) 3 m back from its mouth, sidewalk to sidewalk, at most 14 m,
  over that road only (`WalkCrossing.Unmarked`). The cars do not yield there.

## Rules (`StepWalker`)

- Looks 4 m ahead for the next crossing. **Lit**: goes only on its group's green, and not if the green ends before it would be on
  it plus 2.5 s (`LastGreen`); else stands 0.6 m back from the kerb (`Holding`). Once cleared, too close to stop at a walker's pace,
  it goes. On a two-stage crossing it stands on the island (at most half its width back) for the second half's group.
- **Zebra**: claims it (`WalkCrossing.Claimed`, the traffic clock) while it stands at it or is on it; waits only for a car on it
  or coming too fast to stop at 4 m/s2 (`CarAtZebra`). **Cars** (`ZebraWait`, one line in `StepCar`): a claimed zebra on their
  legs within 50 m makes them stop short of its 3 m band unless already too close to stop.
- **Unmarked**: no claim; the walker waits until no car is on it, within 8 m of it moving, too fast to stop, or reaching it
  sooner than its walk over plus 2 s (`CarAtZebra` with a gap).
- **Permissive turns** (#1046): the cars wait for walkers on the crossing through the shared crossers (`CrosserComing`), no
  walker code.
- Its line across the walkway: a little right of the middle, spread by its habit over the width, eased at 0.6 m/s.

## Check

`--trafficcheck --mover walker [--at E,N] [--walkers-near N]`: every 5 s a `walkers:` line (live, waiting, onto lit crossings,
on red, on the clearance amber, waits at a red kerb / on an island / at a zebra, zebras crossed, car-seconds stood for zebras,
unmarked crossed and waits for a gap, car-seconds at turns for walkers). With `--at` it also spawns walkers within 60 m of it (2 a second). Fails on a step onto a lit crossing at
red. A walker cleared on green may reach the crossing in the pedestrian group's clearance amber (counted apart: the lights run
on the simulated clock, the walkers on the frame's; at `--fixed-fps` the two drift).

Measured (PR #1065, headless `--fixed-fps 60`):

- Signal test region, J7 (`--at 2917500,1322500 --traffic 150 --dense --seconds 120 --walkers 120`): walk graph 368 edges (38 lit
  crossings, 2 islands, 5 unmarked); 159 onto lit crossings, 0 on red, 125 waits at a red kerb, 20 on J7's east island, cars stood
  at a turn for walkers 153 s.
- Geneva rewrite (`D:/UnitSportSwitzerland/test1030/new5/chunks`, `--at 2499916,1118335 --walkers 200`): walk graph 22 751 edges
  (8 366 sidewalks, 1 456 paths, 670 lit crossings with 16 islands, 41 zebras, 488 unmarked, 104 crossings left out with no
  walkway at an end); 101 onto lit crossings, 0 on red, 14 zebras crossed with cars standing 78 s for them, 28 unmarked crossed,
  5 waits for a gap. Car tick cost 3.6 ms mean without walkers, 4.0 ms with 200 massed at `--at` (same machine, back to back;
  `FollowCrossers` skips all when no crosser is in a lane).
- **Three-stage crossings over a tram reservation** (#1176, `tools/traffic-signals`): the carriageway parts and the middle stage
  over the tracks are lit crossings joined across their islands as #1030's halves; an unlit middle stage (group 255 in the
  signal's lines) is a `WalkCrossing.TramYield`: the walker waits on the island for a gap in the trams (`TramAtCrossing`). The
  probe counts unlit stages crossed, waits for the trams, and fails on a walker stepping on with a tram within its braking
  distance. Test region (180 s, 150 walkers, 10 trams): J19 172 lit crossings, 9 island waits, 0 on red; J20 49 unlit stages
  crossed, 6 waits for the trams, 0 stepped on with a tram near; trams 0 on red at both.
  After #1179 (refuges, bounded waits, walkers never stepping on at red): J19 193 lit crossings, 0 on red, 62 island waits; J20
  43 unlit stages crossed, 9 waits for the trams, 0 on red; J7 unchanged (0 on red).
