# Traffic riders (#1056, tracking #1058)

Bikes as cosmetic, local traffic on the shared base (`traffic-modes`): blocky bike-and-rider models (#1147) in 0.6 x 1.7 x 1.8 m boxes at 5 m/s +-25 %,
30 by day (`GameSettings.TrafficRiders`, 8 on a phone, x0.3 at night; `--bikes N`; Settings -> World "Bikes").
In the test world riders come only from its bike generators (#1134, `traffic-generators`): no density spawn there.

## Files

- `World/LaneGraph.Riders.cs`: the ride graph (`LaneGraph.BuildRides`), `RideSide`, `RideWay`, `RideLateral`, `RideReach`.
- `World/Traffic.Riders.cs`: the mode's seams (spawn weight, step, lights, line, next leg) and the probe's counters.
- `World/TrafficProbe.Riders.cs`: the riders' lines and failures in `--trafficcheck`.

## The ride graph

- **Street pieces** riders may use (Major, Road, Minor, Lane; no motorway, expressway, ramp, tunnel, stairs, ford): each
  direction rides the side on its right (`Right` going in drawing order, `Left` against it): its painted lane (`Lane`), its
  separated path (`Track`), else the car lane 0.9 m in from the edge (`Road`: the rider is `InLane`, cars follow it).
- **One-way streets**: riders with the cars; against them only where the side on their right has a lane or a path
  (`opposite_lane` / `opposite_track` as OSM maps them, #769). The data has no `oneway:bicycle=no` yet (#769 open): a one-way
  street with nothing for riders is not ridden against.
- **Ways** with no cars (`Way`): tracks, and paths or squares in the Veloland / Mountainbikeland networks.
- **Riders' junction paths**: every `JPTH` of kind `Bike` (from a painted lane) or `Path` (from a separated path, round the
  core), with its signal site and group (its tile's `Signals` index offset into the car graph's `Signals`, same tile order).
- **The cars' junction paths and links** (copied): riders in the car lane take them, and so does anyone at a junction
  without riders' paths. `JoinTrimmedEnds` joins the ways' trimmed ends.
- Street pieces take the car graph's same pieces' approaches (by their end keys): riders read the same stop lines.
- Every edge is a copy with its own points (the car graph's edges move with the origin once). The per-edge facts live in a
  static `ConditionalWeakTable` (`LaneGraph.RideOf`): a rider still on an edge of the graph before a rebuild reads them.

## Riding

- **Line** (`RideLateral`): on a transition's piece (#818) its strips: the bike lane of its direction (the left-turn bike
  lane when its path ahead turns left), else the path, else the kerbside car lane's right part; on a plain piece its
  side's lane centre or path centre, moved out by a widening's shift. The last 30 m before a junction path it eases onto the
  path's first lane (`FromOffset`). On a new leg it carries on from where it is and eases (faster when far: a U-turn).
- **Next leg** (`NextRide`): arriving where junction paths start, one of them: the riders' own of its kind (a lane's `Bike`,
  a path's `Path`), else any riders' own, else the cars' (from the lane nearest its line); a rider in the car lane takes the
  cars'. One exit at random, straight on twice as likely. Off a path, its road. Elsewhere ways with a lane or path 3x as
  likely; never straight back but at a dead end.
- **Lights** (`RideLights`): its group is its path's (a bike group, a left-turn bike lane's own phase #979, a right-turn bike lane's own group #1165, the lead
  green are in the plan) or the arm's bike group of its move, else the cars'. Its line is its bike lane's (`StopBehind`: an
  advanced stop line lies ahead of the cars', #351; a bike box lies in front of the cars' line). Red: stops (2.5 m/s²),
  `Holding`. Amber: stops if it can. **Right on red** where the arm has `BikeRightOnRed` (5.18, #711): goes at 3 m/s.
  Without a junction path the turn is read 20 m past the approach's end along its legs.
- Follows the rider ahead on its line, and in the car lane the car ahead. Bends at ~2.5 m/s² sideways.
- Cars give way to riders at their conflict points (#1046) through `CrosserComing`, unchanged.

## Checks

`--trafficcheck --mover rider [--bikes N] [--at E,N --dense]` (`--dense` also spawns riders into the lit approaches within
120 m of `--at`). Lines: the graph's make-up and where riders rode (lane, path, car lane, way, junction paths); stops at
red, red runs, amber clears, green crossings (on the bike lead green, lefts on a bike group), rights on red (and the 5.18
approaches loaded, the first one's LV95), cars that waited for a rider at a permissive turn, how far past the street's
outer edge (sides included) a settled rider got, and how far off its wanted line. Fails on a red run or a rider more than
0.5 m past its street's edge.

Measured (#1056 PR, headless `--fixed-fps 60`): signal test region J1/J2/J3b/J5a, 60-80 riders, 90-120 s: no red run,
28-44 stops at red, lefts on a bike group at every junction, bike lead greens seen, cars waiting for riders at J3b (3);
Geneva centre (test copy of 2026-10-21) 100-150 riders: 0 red runs, 62-126 stops, 49 cars waiting for riders with
`--dense`.

## Not done

- Cars do not overtake a rider in the car lane: they follow it (`FollowCrossers`). Passing it needs the car to swing out
  (car code, `StepCar`).
- Right on red (5.18) is wired but no run saw a rider turn right on red: the test region has no 5.18 approach and in the
  Geneva copy (5 approaches) the riders that came reached it going straight on or left.
- Contraflow on one-way streets only where a lane or path is mapped (#769 data).
- Riders do not give way to cars anywhere (no yield signs, no unlit junction rules), nor to walkers.
