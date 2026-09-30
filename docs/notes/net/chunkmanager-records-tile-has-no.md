# `ChunkManager` records "this tile has no roads/buildings/trees" after ONE empty load

- **`ChunkManager` records "this tile has no roads/buildings/trees" after ONE empty load.**
  Correct for local files, wrong over a network, so `NetworkChunkSource` retries transient
  failures internally rather than letting a null reach the manager.
