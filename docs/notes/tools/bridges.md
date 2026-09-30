# Bridges

- **Bridges**: `kunstbaute = Bruecke` segments keep their surveyed deck Z and get a deck
  slab (soffit + side fascia), edge parapets, and piers spaced ~25 m
  (`RoadMeshBuilder.AppendBridgeStructure`). Piers sample the terrain via the tile's
  `ChunkGrid` and are skipped above `MaxPierHeight` — TLM3D has no bridge-type attribute,
  so tall gorge crossings are left as unsupported spans rather than sprouting 130 m columns.
