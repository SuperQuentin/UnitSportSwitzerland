# A camera placed relative to raw TERRAIN can end up under the ROAD the runner is actually on

- **A camera placed relative to raw TERRAIN can end up under the ROAD the runner is actually on.**
  `AnkleCam` and `LowHeroPass` computed their ground height from `ctx.Ground(p)` — the bare
  terrain grid — with `ctx.Subject.Y` as a fallback only when the tile hadn't streamed in yet.
  But a runner on a road is not always AT terrain height: a graded cut or a low embankment sits
  measurably above it, which swissALTI3D does not model (see the bridge-approach gotcha below).
  For a camera placed a couple of metres from the runner, the runner's OWN elevation — already
  correct, road-matched or draped, whichever applies — is a far better local reference than the
  bare grid, and using terrain alone put the lowest-angle shots' cameras under the visible road
  surface on exactly the stretches where the two disagreed, which is also where a low angle makes
  the clipping most obvious. `ShotContext.GroundNear` takes `Math.Max(Ground(p), Subject.Y - cap)`
  instead — a cap, not the runner's height outright, so AnkleCam's own by-design offset below the
  runner is not clamped away.
