# Ground start: Explore from the menus begins on foot, never in a house (#517)

- `ClientWorld.TrackLoading`, Explore with `WorldLaunch.FromCommandLine == false`: once the spawn
  point has settled, the body is made (as T does) and put on foot under the spawn, and
  `Core/GroundStart` holds the loading screen ("Placing you") until it stands somewhere clear.
- `GroundStart` waits (10 s at most) for the ground cell and the tile's building collision
  (`ChunkManager.BuildingCollisionDoneAt`: every building cell committed; before that a spot can look
  free and be inside a house), then searches rings 3 m apart out to 150 m, nearest first, for a spot
  that is dry (`TryGetSurface` not above `TryGetHeight`), has open sky (a ray from 80 m up meets the
  ground first, not `BuildingBody`, a roof, a bridge or a tree) and room for a capsule
  (r 0.45, 1.8 m). The body is `PlaceAt` there; "[spawn] on foot at ..., N m from the spawn point".
- **Command-line runs keep the fly-camera start** their tools expect (`--shot`, probes, `--connect`).
  Multiplayer already spawns the body on foot from the server. Pressing T still flies.
- Checked at Riddes with `--autostart --at 2583244,1113256` (inside a house: moved 6 m, out in front
  of its wall).
