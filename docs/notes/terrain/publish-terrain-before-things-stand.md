# Publish the terrain before the things that stand on it

- **Publish the terrain before the things that stand on it.** A tile build fetches chunk →
  holes → cover → roads → buildings and used to commit all of it at once, so a streaming client
  saw nothing until the last link landed. `ChunkManager` now enqueues an *interim* `BuildResult`
  carrying just the surface mesh and collision, then a second one with roads/buildings/trees.
  Same files, same order, same worker — the ground simply stops waiting for the tail. Measured
  against a loopback server with no local terrain: at 2 s, **346 → 504,270 primitives**; full
  load unchanged at ~3 s. `Interim` results must NOT clear `PendingStride`, or the ring
  evaluator starts a second build for a tile whose first is still running.
