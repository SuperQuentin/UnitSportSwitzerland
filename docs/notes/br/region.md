# Battle Royale region (`src/BattleRoyale/BrRegion.cs`)

- **Size**: a square, 5 km under 10 players, 6 km up to 20, 7 km beyond (`SideFor`). It is sized at
  `/br open` for the peers online then; `/br open 5|6|7` forces it.
- **Random pick** (`Pick`, pure): the inputs are `places.json` towns (with their `Buildings` counts),
  the manifest tiles (with their min/max heights) and the recent centres.
  1. Draw a town, shift the square by up to a quarter of its side, snap to 50 m.
  2. **Reject** the square when any of these holds:
     - a tile is missing, or a tile has min <= 1 m (off the edge of the Swiss data, beyond the border);
     - more than 30 % of the tiles reach above 2,400 m;
     - its towns hold fewer than 120 buildings;
     - it lies within 8 km of one of the last 5 regions (`user://br/history.json`).
  3. **Score** the rest: log(buildings, capped at 3,000) + 0.6 x villages (up to 6) + relief
     (max-min of the tile maxima, up to 1,500 m) / 600.
  4. Keep the best of 60 valid draws. A pick takes about 7 ms.
- **Generated world** (no towns/tiles): a random square within 5 km of the default spawn.
- **Overrides**: `/br open <town>` (place search) or `/br open here` (the admin's position). The
  name shown is that of the biggest town inside the square.
- Not yet: the water/glacier share (needs cover data), road reachability.
