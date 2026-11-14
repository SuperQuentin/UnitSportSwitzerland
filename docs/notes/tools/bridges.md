# Bridges

- **Bridges**: `kunstbaute = Bruecke` segments keep their surveyed deck Z and get a deck
  slab (soffit + side fascia), edge parapets, and piers spaced ~25 m
  (`RoadMeshBuilder.AppendBridgeStructure`). Piers sample the terrain via the tile's
  `ChunkGrid` and are skipped above `MaxPierHeight` — TLM3D has no bridge-type attribute,
  so tall gorge crossings are left as unsupported spans rather than sprouting 130 m columns.
- **A deck's junction caps collide** (#1768): RoadGen trims the arms back to a cap's edge, so the cap (`Layer` 1, drawn at
  the deck's lift; `Layer` -1 in a bore) is part of the deck. `BuildBridgeCollisionFaces` adds them; without them Pont
  des Bergues' branch to Île Rousseau and Place des Nations' Major (#1757) were holes a walker or car fell through.
