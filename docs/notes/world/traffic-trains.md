# Traffic and trains

- **Traffic and trains** (`World/Traffic`, `World/LaneGraph`): local and cosmetic per client (not
  replicated), but solid — every unit is an `AnimatableBody3D` box. A `LaneGraph` of drivable roads
  (5×5 km) and railway (7×7 km) is rebuilt off-thread whenever the view enters a new tile, endpoints
  snapped to 0.5 m so tile seams join. **Divided carriageways get a direction from where their
  partner lies** (right-hand traffic: the other carriageway is on your left); TLM records none, and
  ~70% find a partner. A `Route` grows legs at junctions and trims behind: a car is one unit on it,
  a train a locomotive + carriages at fixed offsets, which keeps a train on one line through points
  (trains only take legs that carry straight on). Cars keep right on undivided roads, slow for
  bends (2.5 m/s² lateral) and for the car or player ahead. Density: Settings → Time of day (`TrafficCars`,
  35, ~half at night; `Trains`); `--traffic N`. Check: `<godot> --path . -- --trafficcheck[,out.png]
  [--time h]` — 40 s over the nearest motorway, chases a car then a train, fails if nothing moved.
