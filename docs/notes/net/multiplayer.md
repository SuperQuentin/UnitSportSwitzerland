# Multiplayer

- **Multiplayer**: client-authoritative transforms, MultiplayerSpawner + Synchronizer,
  ENet port 7777. Server runs ChunkManager with BuildMeshes=false (grid-only, for
  height queries around players).
- **Every peer has its own origin** (#185): no world-space `Vector3` position crosses the wire, only
  LV95 (`net/positions-on-the-wire`). The server never adopts nor checks a client's origin, and
  connecting starts with a version check (`net/protocol-handshake`).
