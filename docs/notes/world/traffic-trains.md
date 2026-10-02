# Traffic and trains

- **Traffic and trains** (`World/Traffic`, `World/LaneGraph`): local and cosmetic per client (not
  replicated), but solid — every unit is an `AnimatableBody3D` box. A `LaneGraph` of drivable roads
  (5×5 km) and railway (7×7 km) is rebuilt off-thread whenever the view enters a new tile, endpoints
  snapped to 0.5 m so tile seams join. **Divided carriageways get a direction from where their
  partner lies** (right-hand traffic: the other carriageway is on your left); TLM records none, and
  ~70% find a partner. v3 tiles store it (`RoadAttributes.OneWay`, decided at build time, 97.5%);
  the runtime inference only runs for divided edges with no stored direction (v1/v2). On a
  one-way edge a car drives the rightmost lane (`LaneEdge.RightLane`, from the stored width and
  lanes, #117); `--trafficcheck` also fails if any car is on an edge against its direction, and
  the `[traffic]` line ends with a one-way fingerprint to compare two peers. A `Route` grows legs at junctions and trims behind: a car is one unit on it,
  a train a locomotive + carriages at fixed offsets, which keeps a train on one line through points
  (trains only take legs that carry straight on). Cars keep right on undivided roads, slow for
  bends (2.5 m/s² lateral) and for the car or player ahead. Density: Settings → Time of day (`TrafficCars`,
  35, ~half at night; `Trains`); `--traffic N`. Check: `<godot> --path . -- --trafficcheck[,out.png]
  [--time h]` — 40 s over the nearest motorway, chases a car then a train, fails if nothing moved.
- **Junction ends joined** (`LaneGraph.JoinTrimmedEnds`, #85): the road generator trims roads back from
  their junction polygon, so ends at a junction do not share a key; unjoined, every such junction was a
  dead end and cars turned round on the spot mid-junction. Ends nothing else meets, ≤ 18 m apart and
  pointing at each other, get a straight connector edge (`LaneEdge.Connector`; Mollendruz tile: 400 dead ends → 36).
  `Degree`/`Leaving` also look in the 8 neighbouring snap cells (two ends 0.1 m apart can round apart).
- **Approaches** (#353, `LaneApproach`, records `LANE` in `road-format-v3`, built in `turn-lanes`):
  `LaneGraph.Build` ties every `LANE` record (and, in an older tile, every signalised `SGNL`
  approach, as one lane with all its movements) to the edge end nearest its stop point, within
  15 m, driven into the junction along the arm (`LaneEdge.ApproachAtEnd/AtStart`, with the stop
  line's distance before that end; the nearest record wins an end). `Traffic.Approach` looks up to
  110 m ahead along the route's legs. Once per approach a car takes its next turn (`WayPast` /
  `TurnOf`: 15 m into the road it takes, past a junction connector; at lights as the plan names
  it, else through within 45 deg), the lane whose arrows show it (`LaneFor`, fewest other moves)
  and the group for it (a pocket's arrow, else the main head). **Lanes**: it moves from its usual
  line onto the original lane's centre over the 15 m before the furthest lane starts, then rides
  the lane its own branches from (`Parent`: the next car lane beside it that starts further back)
  until its own opens, then moves over its taper (a pocket appearing at once beside a closing
  hatch: over 12 m, from 3.6 m before it opens); `Vehicle.Lane` follows at max(1.2, 0.35 x speed)
  m/s and goes back to its usual line past the junction. Pockets at junctions without lights
  (#123) are driven too. **Routing** (`NextRoad`) drops a turn the approach bans (OSM
  restrictions) unless nothing else is left.
- **Traffic lights** (#353, plans `traffic-signals`): the group is read on `ClockSync.ServerNow`,
  so every peer's traffic stops for the same red. Red or red+yellow: stop 0.5 m short of the
  lane's line (a pocket behind a bike box: 4 m further back); yellow: stop if it can at 3 m/s²,
  else clear it (`ClearingAmber`, kept while it turns red); a car that first sees a red it cannot
  stop for at 6 m/s² (just spawned or turned in) clears it too. The yield bits are ignored on a
  signalised approach (`GiveWay`). On green a left turn from a shared lane waits at the line while
  a car on the opposite approach is moving within 40 m of its line (`Oncoming`), and **a queue
  does not block the junction**: within 30 m of the line, a car that can still stop at 4 m/s²
  waits while a car stands (< 2 m/s, its way) within 7 m of the point 6 m into the road it takes
  (`RoomPast`, on the `_byX` window). Racers ignore lights (`traffic-and-races`).
- **`--trafficcheck --at E,N`**: the camera stays over the point (45 m up, 15 m south), the
  traffic lives around it; `--dense` adds a car within 300 m of it 4 times a second (up to the
  asked count), `--seconds N` runs longer. At the end: the tick cost (`perf-traffic-tick`) and,
  per approach (the 15 nearest, then any with a pocket left, a wait or a red run): stopped at red,
  entered on red (cleared yellow apart), on green (crossed without lights), lefts from the pocket,
  permissive lefts that waited, waits for room, and per lane the cars and their offset at the
  line; then totals. Fails on a red run. Final runs (logs `test_output/final/`), Geneva copy
  (D:, `traffic-signals`), `--traffic 300`, **no red run in any**:
  - `--at 2499901,1118599` (OSM lights), four 40 s runs: 13-17 stops at red, 2-3 yellows cleared,
    14 crossings each; `--dense`, three runs: 31-41 stops, 8-10 yellows cleared, 0-2 waits for room.
  - inferred lights at 2499883,1116759, two dense runs: 12 and 21 stops at red.
  - `--at 2499641,1118692` (lights, L|T|R pocket approach), dense 150 s: 108 stops, 5 lefts from a
    pocket, 1 permissive wait, 3 waits for room; through cars cross the line at +5.0 m (their lane +5.0).
  - #123 pockets without lights, dense 120 s: Geneva 2499916,1118335, 5 lefts from pockets (at
    -0.8 m: the original lane's centre, a bike lane on the right), through cars at +2.5 m (lane
    +3.3, the lane-change ramp still on the hatch's taper); Valais 2584437,1110600: 15 through
    cars at +3.0 m (lane +3.7), none turned left there.
  Screenshots from above (windowed `--trafficcheck,<png>`): cars queued at the red of the
  Geneva junction and in the through lane past the pocket. Not done: a capture of a queue moving
  off on green; two clients comparing group states (traffic is local, the lamps' `State` is
  the shared part, #350).
- Around a race the cars behave like drivers who see it coming: `traffic-and-races`.
