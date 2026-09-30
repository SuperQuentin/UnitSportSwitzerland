# Multiplayer

- **Multiplayer**: client-authoritative transforms, MultiplayerSpawner + Synchronizer,
  ENet port 7777. Server runs ChunkManager with BuildMeshes=false (grid-only, for
  height queries around players).
