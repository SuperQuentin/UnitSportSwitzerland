# A collision build must load the road tile even when the roads are already drawn

- **A collision build must load the road tile even when the roads are already drawn.** Both the
  road-blended floor and the bridge-deck collision are built in `StartBuild`'s tail from the road
  tile, but `EvaluateRings` only asked for roads when they were *missing*. The ordinary way to
  play — fly over an area (roads load, no collision: the fly camera does not ask for it), then
  drop on foot — therefore built every tile's collision from BARE terrain and no bridge collision
  at all: the player stood the full `DrapeOffset` (0.35 m + class lift) inside every road and
  path, and fell straight through every bridge. `StartBuild(..., roadsForCollision:)` now fetches
  the (cached) road tile for the blend without re-meshing the roads. Measured with
  `--roadcheck`: floor − ribbon went from −0.42 m mean to ±0.005 m.
