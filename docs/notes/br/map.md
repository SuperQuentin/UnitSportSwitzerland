# Battle Royale map, minimap and compass (#190, part 3 of #177)

- **Map image** (`BrMapImage.BuildAsync(source, area)`): 1024² RGBA, pixel (0, 0) = the square's north-west corner.
  - Source: the client's own chunk source (`ChunkManager.Source`, so it streams from the server like
    the terrain): `LoadHorizonAsync` (100 m lattice), plus `LoadCoverAsync` / `LoadRoadsAsync` /
    `LoadBuildingsAsync` for every tile under the square.
  - Ground: the land-cover colour times a hillshade lit from the north-west. Meadow turns browner
    above 1,300 m; water is not shaded.
  - Strokes, drawn small first: rivers, tracks, lanes, roads, railways (dashed), major roads,
    motorways. Tunnels are skipped.
  - Buildings: every shell triangle is projected from above and filled. A building smaller than
    one pixel becomes a dot.
  - Output is quantised to 5 bits per channel.
  - Tile-local frame: X east, Z south, from the tile's NW corner (`ToPixel`).
  - Built off the main thread when a state with a new area arrives (`BrManager.BuildMap`).
    Measured: 2.5-3 s for 6 km of real tiles (36 of them). `MapTexture` stays null until done.
    `--brmapsave` writes `test_output/br_map.png`.
- **Overlays** (`BrMapDraw.Overlays`, shared). Zone points are metres east/north of the centre;
  `Screen()` turns north up.
  - The storm: a purple band drawn as one very wide `DrawArc` outside the circle.
  - The edge (white) and the next circle (dashed).
  - The waypoint pin.
  - A dashed line from you to the nearest point of the next circle when you are outside it.
  - Your arrow: blue, or orange for the player you watch.
- **Minimap** (`Minimap`, a child of `BrHud`): top right, 220 px, 1,200 m across, north up,
  centred on `BrManager.ViewPoint()` (you, or the watched player), with a 200 m scale bar.
  `ClipContents` clips the map texture. The kill feed moved below it.
- **Compass strip** (`BrCompass`): top centre, 560 px, ±80°.
  - Ticks every 5°, numbers every 15°, N/NE/E/... every 45° (N in red). It fades towards the ends.
  - A caret and the exact bearing sit under the centre.
  - Markers: the safe zone (purple diamond, distance to its edge) and the waypoint (yellow, distance).
    They are pinned to the edge with an arrow when behind you.
  - Bearing = `atan2(east, north)` of the camera's forward. The BR phase line moved down below it (`BrHud.Y0`).
- **Full map** (`BrMap`, CanvasLayer 20): M (`PlayerInput.Teleport`) during a match calls
  `BrManager.ToggleMap()` instead of the place search (`ClientWorld._UnhandledInput`).
  - Grid: letters west→east and numbers north→south, a kilometre a square; "you are in C2".
  - Town names come from `PlaceSearchUi.All` (`BrManager.Towns`); hamlets show only when zoomed in.
  - Wheel zooms about the pointer (1-8×), left-drag pans, right click sets the waypoint (on the pin: clears it).
  - M / Esc / menu closes it. It closes on its own when the match ends.
  - The waypoint is local, never sent (`BrManager.Waypoint`), and cleared at release / a new region.
- **Server**: `/city` is refused while you are alive in a running match (`BrManager.Playing`).
- Checks: `--brcheck` renders a real region to `test_output/br_map_check.png`. `tools/brcheck.sh`
  expects the minimap texture, sets a waypoint, opens and closes M (`br_a_map.png`, `br_*_dropped.png`).
- Not yet: teammates on the map, shared pings, airdrop markers, a minimap that turns with you.
