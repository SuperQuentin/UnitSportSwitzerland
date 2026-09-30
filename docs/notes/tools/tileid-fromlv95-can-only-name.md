# `TileId.FromLv95` can only name ONE of the tiles that share a lattice line

- **`TileId.FromLv95` can only name ONE of the tiles that share a lattice line.** Every
  boundary vertex belongs to two tiles (four at a corner). Resolving by coordinate alone
  therefore (a) leaves the neighbour's edge row unclassified in `.cover`, which opens a 4 m
  gap in the water surface along every seam a river crosses, and (b) returns a null height
  for road vertices at a batch edge. `TerrainSampler` tries all the sharing tiles;
  `CoverExtractor.MarkIn` stamps all of them.
