# AutoPilot and RaceRoute

- **AutoPilot and RaceRoute** (`Player/AutoPilot.cs`, `Player/RaceRoute.cs`): the scripted racing driver
  and its road, shared by `--drivecheck` (all cars in one scene) and multiplayer races (`--raceauto`,
  see `src/World/CLAUDE.md`). `RaceRoute.BuildAsync` walks the main road from a point over the `.road`
  tiles (bridging RoadGen's trimmed junctions within 18 m), `FromPoints` rebuilds one a server sent.
  `AutoPilot.Drive(dt, go, others)` plugs into `FootPlayer.RideControls`; `others` are the other cars
  (position, speed, wreck?) for racecraft.
