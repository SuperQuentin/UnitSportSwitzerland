# Traffic modes: walkers, riders, trams (#1060, tracking #1058)

The shared base the modes beyond cars and trains plug into: walkers (#1033), riders (#1056), trams (#1057). Like the cars
(`traffic-trains`): local and cosmetic per client, solid, a density each, rebuilt as the view enters a new tile. Every new
mover collides as a solid box and looks like a blocky low-poly model (#1147, below). The base adds no mode behaviour: each density is 0 by default and every builder
returns no graph, so nothing spawns and the cars run exactly as before.

## Files: one per mode, the shared part apart

- `World/Traffic.Modes.cs` (shared): graphs, the slow movers, trams as vehicles, the crossers, probe/debug helpers.
- `World/Traffic.Walkers.cs`, `Traffic.Riders.cs`, `Traffic.Trams.cs`: each mode's own seams (stubs). A mode's work goes
  here, plus its own probe file (`TrafficProbe.Walkers.cs`, ...), so the three can be built side by side.
- `World/MoverKind.cs` (plain, unit-tested): `MoverKind { Car, Train, Walker, Rider, Tram }`, `MoverKinds.Conflict`
  (Walker -> `ConflictMover.Walker`, Rider -> `Bike`, Tram -> `Tram`), `Parse` (`--mover`).
- `ConflictMover.Tram` (`tools/TerrainFormat/RoadConflictPair.cs`, written by no stage yet) and `ConflictPoints.TramBody`
  (1.2 m) leave room for the tram paths of the intersection planner (#1057: tram tracks as approach lanes).

## Graphs

- `RebuildIfMoved` builds, after the roads and rails and on the same worker, `BuildWalkGraph` / `BuildRideGraph` /
  `BuildTramGraph(ModeTiles)` for each mode whose density is > 0 (`ModesWanted`). `ModeTiles`: the 7x7 km tiles (`Wide`),
  the 5x5 km ones (`Near`), the build's `OriginFrame`, the road and rail graphs just built. Null: no graph, nothing spawns.
- A mode builds with `LaneGraph.Build(tiles, origin, keep, MoverKind.X)` or `LaneGraph.Empty(origin)` + `Add(edge)` (edge
  `Kind = MoverKind.X`, keys from `LaneGraph.Key(e, n)` on its LV95 ends so it joins across seams). `LaneGraph` is partial: a
  mode's own building code may go in `LaneGraph.<Mode>.cs`.
- Held in `_walks`, `_rides`, `_tramLines` (`GraphOf(kind)`), shifted with the origin (`ShiftModes`; edges shared with the
  roads or rails move once), dropped by `Forget`. A density raised from 0 rebuilds at once (`_modesBuilt`).

## Walkers and riders: `SlowMover`

- One unit (`BoxUnit`: the model's shared mesh under the cars' `_bodyMaterial`, its lamps if any under `_lampMaterial`,
  a collision box of the `MoverLook` size, `AnimatableBody3D` named by kind) on a
  `Route`; `Lateral` m right of its edge's line; `Holding` (waiting on purpose, never culled as stuck), `InLane` (in a car
  lane: cars follow it), `Group`, `Wait`, `State` free for the mode. Pooled (hidden, collision off) when dropped.
- `MoverLook` per kind (size, colour, cruise ± spread, spawn ring `Near` / `SpawnMin`-`SpawnMax`, `Despawn`, `Drawn`):
  `WalkerLook` 0.5x1.7x0.4 m orange 1.35 m/s, `RiderLook` 0.6x1.7x1.8 m green 5 m/s, in each mode's file.
- `StepMovers` per tick: spawns four times a second (a burst while under half), each mover's `StepWalker` / `StepRider`
  (the speed it wants), ease (1.5 up, 3 m/s² down), `Route.Advance` with `NextWalk` / `NextRide` (default `NextAny`:
  at random, back at a dead end), `WalkerLateral` / `RiderLateral`, cull (out of range, stuck 30 s, one too many).
  Spawn: `WalkerWeight` / `RiderWeight` per edge, then `WalkerSpawned` / `RiderSpawned` set its line and the rest.
- Density: `Want(kind, night)`, walkers and riders x0.3 at night.

## Trams: `Vehicle`s like trains

- `AddTram(edge, forward, arc, lengths[], gap, cruise)`: box sections at fixed offsets (`Vehicle.UnitHalf` = a section's
  bogie half-spacing; 0 keeps the car/carriage rule), in `_trams`, culled at `TramLook.Despawn`.
- Seams: `SpawnTram(focus)` (default: 3 sections 9/7/9 m on a random tram-graph spot), `StepTram` (default: a train's run,
  `NextTrack(graph, leg)`: the trains' straight-on rule, which `NextRail` now calls on `_rails`), `TramInLane`.

## What the cars mind: the crossers

- `CollectCrossers` (each tick before the cars) fills `_crossers` with every walker, rider and tram section (pos, vel,
  `MoverKind`, `ConflictMover`, `InLane`, half length), sorted by X. A mode only keeps its movers live; they are in it.
- `Coming` (#1046): at a conflict point that gives way to `PathYield.Mover`, a crosser of that kind in the band or reaching it
  within `AcceptGap` counts (`CrosserComing`, the players' rule, its half length added).
- `StepCar` follows an `InLane` crosser ahead like a car (`FollowCrossers`, its own length; +inf with none).
- Not yet: `GiveWay` (yield signs), `RoomPast`, the racers' `CarsNear` do not see crossers; a mode needing it adds it.

## Settings and command line

- `GameSettings.TrafficWalkers` (0..300, its row shows since walkers landed, still 0 by default: `traffic-walkers`), `TrafficRiders` (0..150), `Trams` (0..20), the others 0; `--walkers N`, `--bikes N`
  (`--riders` is the drivecheck's), `--trams N`; zeroed without the `traffic` system. The settings rows (World tab, solo
  screen: `SettingsScreen.TrafficModeRows`) show once `Traffic.WalkersReady` / `RidersReady` / `TramsReady` is true (each
  in its mode's file).

## Checks and debug

- `--trafficcheck --mover walker|rider|tram [--mode-graphs]`: that kind chased from 20 s (trams from afar), a density of 80
  walkers / 40 riders / 4 trams if the settings ask none, a `modes:` line every 5 s (counts, speeds, graph edges, crossers
  seen by the cars), fails `no walker spawned` / `no moving walker`. A mode's own counts and rules: implement
  `partial void WalkersTick(double)` / `WalkersReport(ref string? failure)` (Riders, Trams) in `TrafficProbe.<Mode>.cs`.
  Without a mode on, every line and the RESULT are as before.
- Debug menu: Mode graphs (`ShowModeGraphs`, each graph within 300 m in its colour); Vehicle paths also draws each mover's
  way ahead.

## Measured (unchanged cars and trains)

See PR #1061: `--trafficcheck` on the signal test region (J7, fed by the endpoints) and on real terrain (Geneva, dense),
headless at `--fixed-fps 60`, before and after: the same counts line for line.

## Trams landed (#1057)

`TramsReady` is true: `traffic-trams` (the graph, driving, lights and stops) and `tools/tram-approaches` (RoadGen's side).
Shared code touched: `LaneGraph.Build` adds the tram paths at lights as conflict ways (cars give way to trams) and calls
`TramYields` for the cars' paths of cores without lights.

## Models (#1147)

- `Avatar/TrafficModeMeshBuilder.cs`, the traffic cars' style (`TrafficMeshBuilder`): a few vertex-coloured boxes each,
  authored +Z forward (built facing -Z), origin at the bottom middle, inside the mover's collision box.
  `Walker(look)` (8 clothes x 4 skins, hair), `Rider(look)` (octagon tyres, spoke cross, bar frame, rider leaning to the bars,
  helmet; 6 bike/jersey x 4 skins), `TramSection(livery, length, cabFront, cabBack, pantograph, bellows)` (low skirt, window
  band, pair doors, cab noses with raked windscreens, head/tail and destination lamps, a bellows over the 0.6 m gangway, the
  pantograph on the middle section; liveries Geneva, Geneva older, Zurich, Basel). Cached per key: one mesh per look.
- A pooled walker/rider keeps the look it was built with (`_rng` at creation); a tram draws one livery for all its sections.
- No animation (no leg swing): nothing per frame. Model viewer: Traffic (`Walker`, `Walkers, looks`, `Rider`, `Riders, looks`,
  `Tram, <livery>`, `Tram section, front cab`).
