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
  pointing at each other, get a straight connector edge (Mollendruz tile: 400 dead ends → 36).
  `Degree`/`Leaving` also look in the 8 neighbouring snap cells (two ends 0.1 m apart can round apart).
- **Traffic lights** (#353, plans `traffic-signals`): `LaneGraph.Build` reads each tile's `SGNL`
  records into `SignalSite`s and ties every signalised approach to the edge end nearest its stop
  line (within 15 m, driven into the junction along the arm, `LaneEdge.SignalAtEnd/AtStart` with
  the stop line's distance before that end). `Traffic.ObeySignal` looks up to 80 m ahead along the
  route's legs (an approach can be a short edge after another), takes the group for the car's
  next turn (where the route goes 15 m past the junction: a pocket's arrow, else the main head)
  and reads it on `ClockSync.ServerNow`, so every peer's traffic stops for the same red. Red or
  red+yellow: stop 0.5 m short of the line; yellow: stop if it can at 3 m/s², else clear it
  (`ClearingAmber`, kept while it turns red); a car that first sees a red it cannot stop for at
  6 m/s² (just spawned or turned in) clears it too. The yield bits are ignored on a signalised
  approach (`GiveWay`). `--trafficcheck` prints signalised approaches, cars at red, stops at red,
  lines crossed, and fails on a red run. Geneva centre, 300 cars asked, two 40 s runs at LV95
  2499901,1118599: 614 approaches matched (of 765), up to 5 at red, 9 and 4 stops, no red run.
  **Lanes** (`LaneFor`): on an arm with a left pocket a car not turning left moves 3 m right
  (onto the widening) between 45 and 25 m before the line; with a right pocket one turning right
  a lane further (35 to 20 m); `Vehicle.Lane` follows at 1.2 m/s, added to its keep-right. A left
  turn from a shared lane on green waits at the line while a car on the opposite approach is
  moving within 40 m of its line or in the junction (`Oncoming`). With them: 4 and 4 stops at red,
  no red run (two runs). Not done: lane topology in the tile (positions are the pocket shapes'
  shortest lengths, not the built ones), pockets at junctions without lights (#123), a queue not
  entering a blocked junction; racers ignore lights; shot mode runs no traffic, so no picture.
- Around a race the cars behave like drivers who see it coming: `traffic-and-races`.
