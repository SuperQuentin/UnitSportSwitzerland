# Never default the world origin to LV95 0/0

- **Never default the world origin to LV95 0/0.** Switzerland is 2.6 million metres from
  there, so float precision collapses the moment real data arrives. With no manifest the origin
  is the spawn point (where the fallback world is built), and a client whose only world is
  generated then *adopts* the server's origin via `Rebase` rather than refusing the mismatch —
  refusing is right when two populated worlds disagree, wrong when you have no real world at all.
  `FallbackActive` must be checked alongside `AvailableTileCount`, which counts generated tiles.
  Rebasing changes what every world coordinate means, so `ClientWorld.RespawnAfterRebase` puts
  the player down again.
