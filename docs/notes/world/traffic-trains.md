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
- Around a race the cars behave like drivers who see it coming: `traffic-and-races`.
