# `RoadNetwork` must snap endpoints to rejoin tile-clipped roads

- **`RoadNetwork` must snap endpoints to rejoin tile-clipped roads.** `.road` segments are clipped
  at every kilometre boundary, so a road crossing one arrives as two features with coincident
  ends. Without a snap tolerance every tile edge is a dead end, no route crosses one, and the
  transition term then scores every step near a seam as impossible.
