# Ski slopes' snow: the virtual weather, the wear, the 06:00 reset (#887)

- **Data**: the slopes come from OSM as per-tile `.pst` files (`tools/ski-pistes`). They are snow all
  year, whatever the real month (owner's choice, Oct 2026). The weather only changes the kind of snow.
- **Ski day**: 06:00 to 06:00 on the environment clock (`SnowClock`). The day number is
  `floor((EnvNow + HourShift) / 86400)`, minus one before 06:00, as the construction sites count it.
  At 06:00 every peer clears the wear and groomed slopes are corduroy again. There is no grooming
  gameplay: the reset stands for it.
- **Weather**: `SnowWeather.ForDay(day, Seed)` is deterministic, so every peer computes it alone (SplitMix64, not
  `System.Random`). It gives overnight snow (a storm about one night in five, 5-40 cm), snow during
  the day (one day in seven), the temperature at 2000 m (lowest at 06:00, highest at 14:00) and cloud.
  `DayWeather.TempAt` applies the lapse rate, −6.5 °C/km.
- **Snow state** (`SnowModel.Evaluate`, pure, `SnowModelTests`): five traits, each 0-1.
  - fresh corduroy, worn off by wear;
  - loose snow: on groomed runs only what fell after 04:00, chopped by wear;
  - ice: yesterday's melt refrozen on a cold night, half of it tilled away by the groomers, plus snow
    scraped on cold busy days; the sun softens it;
  - slush: melt degree-hours since 06:00, with the sun's share set by aspect and cloud (`SunMelt` 3 °C);
  - moguls: `piste:grooming=mogul`, or wear on steep ungroomed runs.

  `Condition` names the dominant trait for the HUD. `FrictionScale` and `GripScale` are what the
  skis feel. A slope's record carries its 10th and 90th percentile altitude, so the state is
  evaluated at both and blended by height: icy at the top and slushy at the bottom on the same run.
- **Wear** = the crowd + real skiers.
  - The crowd: `CrowdWear(hour, popularity, cloud)`, 08:30 to 16:30 on an S curve, easy runs the
    busiest. It is computed on every peer, so it costs no traffic.
  - Real skiers: the deciding peer (server, or offline) scans skiing players and bots every 0.5 s.
    Metres skied on the same slope ÷ (width × 400) is added, so a 1 km run on a 30 m slope adds ~0.08.
    The changed slopes are sent every 5 s (`Wear`, OSM id + u16), the full table on join, tagged
    with the ski day so a stale day is dropped.
- **`/weather`** (`WeatherCommand`): anyone asks, an admin changes.
  - Words: `snow <cm>` overnight, `fall <cm>` during the day, `temp <°C>` the afternoon at 2000 m,
    `clear`/`cloudy`, `reroll`, `hold` (keep every day), `auto`.
  - A change lasts until 06:00 unless held.
  - Replicated as JSON (`Weather`), on change and on join.
  - `--weather "<words>"` on the command line sets it held, offline or as a server's start.
- **Drawn** (`PisteLayer`, `shaders/body/piste_snow.gdshaderinc`):
  - Tiles with slopes within 4 km of the camera get a 512² layer (2 m texels) of a `Texture2DArray`:
    the slot, the edge mask and the groom phase. The ground mesh's instance uniform `piste_layer`
    points at it, as `PhotoLayer` does.
  - Slope states live in a 256 × 120 state texture (three rows per layer: low altitude, high
    altitude, then moguls and the two altitudes). It is rewritten only when the service's
    `Version` changes or the hour moved by 0.05, at most every 2 s. Meshes are never rebuilt for it.
- **Gotchas found drawing it**:
  - The lit styles light an albedo well past 1: snow there is 0.30-0.38, or slush and corduroy
    vanish in the white. PS1 keeps 0.8.
  - Corduroy must be a phase stored per texel. `dot(position, filtered direction)` with positions
    up to 1000 m swims by metres for a 0.01 rad wobble.
  - `fwidth` must be taken before any early return, where derivatives are still defined.
  - Wet snow is not glossy: lowering its roughness turned it white again at grazing angles. Only ice shines.
- **Felt**:
  - `Surfaces.At` returns `Snow` on a slope (mask > 0.3), `Ice` where the state's ice is over 0.5.
  - `FootPlayer.LookAtSnow` looks every 15 ticks and gives `RideGround.SnowFriction`; `Skis.Step`
    multiplies its friction by it.
  - Measured on La Pasay-FIS (Bruson, black), 15 s straight down:
    - −10 °C: 270 m, top speed 103 km/h;
    - +12 °C (slush): 241 m, top speed 95 km/h.
  - The full force model is the ski physics issue.
- **Trees**: `CachingChunkSource.LoadTreesAsync` drops trees more than 1 m inside a slope. It is the
  top of every peer's chain, so the drawn trees, colliders, gathering and birds agree.
- **Roads under a slope**: the road shader runs `piste_snow` too, with the same layer on the tile's
  road mesh (`ChunkManager.RoadsAt`, `PisteLayer` tags both). Signal props (styles 7 and 8) are left
  alone. In PS1 the covered road takes the terrain's stepped sun term (`piste_cover`), or it shows as
  a brighter ribbon. In Realistic it is a smoother, slightly lighter strip: no snow texture on roads.
- **Markers** (`SlopeMarkers`, model viewer "Ski"): poles on the furnished tiles near the camera.
  - One every 30 m on both edges of downhill, connection and snow-park slopes, 0.4 m inside the edge
    so the trees lining the run do not hide them; round an area's outline.
  - Coloured blue, red, black, or yellow for an itinerary. The right-hand pole going down (the lower
    end of the path) has an orange top.
  - None in the middle of another slope.
  - Drawn only, no collision: one MultiMesh per colour and side per tile.
- **HUD**: `SlopeHud`, top left on skis: the slope's ref and name in the Swiss colour (blue, red,
  black, yellow itinerary) and its condition. Rebuilt on change only.
- **Ski map** (`SlopeMap`, in `SlopeHud`): top right, below the fps readout, while on skis. It
  shows the slopes within 1.2 km, north up, in their colours (nordic cyan, sled purple), edges a
  shade darker, and your arrow. The 200² picture is sampled from the loaded `.pst` rasters into one
  reused buffer, only after 30 m of travel (or once a second while a tile is still loading). It is
  the only in-game map outside a Battle Royale match, so slopes are shown here, not on the
  terrain-download map screen.
  - The HUD follows the local player on skis, else a non-bot skier this peer drives (a `--ride` probe's own rider).
- **Opt-in**: `Systems.Snow` ("snow").

## On earth leaves (#1735)

Under `--frame earth` the slopes come from the leaves' `Pistes` section (`--cube-pistes`,
`tools/cube-fields-pistes-photos`), through each leaf's stand-in tile (`terrain/gameplay-on-stand-ins`):

- **The stand-in's slopes**: `StandInChunkSource.LoadPistesAsync` → `LeafStandIn.Pistes`: the records' paths moved
  from the anchor's (x, z) to the tile's (east, north), altitudes kept; `Slot`/`Aux` on the tile's 1 m grid, each vertex
  taking the lattice vertex nearest its 2 m sample (`LeafStand.Nearest`, grown 2 samples past the leaf so the drawn
  slope reaches its edges). Off the leaf: no slope. Everything keyed by a `TileId` (`PisteIndex`, `SlopeMarkers`,
  `PisteLayer`) reads it unchanged.
- **The skier's lookup** (`PisteIndex.At`): the ground's tile under the point (`ContentName(TileAt)`) and the point in
  its frame (`GlobalPos.InTileEn`). `SnowService.TryAt` and `Surfaces` evaluate the snow at `WorldOrigin.AltitudeOf`,
  never the world's y (an earth frame's y is a height over its own tangent plane).
- **The layer** (`PisteLayer`, given the `EarthWorld`): polls the leaves round the camera by their stand-in names,
  tags each leaf's ground mesh (`IWorldGround.GroundAt`) and its furnished roads (`EarthWorld.RoadsAt`). The leaf's
  mesh is in its anchor frame, the stand-in's square less 500 m: the leaves' ground material has `piste_offset`
  (500, 500) (`piste_snow.gdshaderinc`; 0 for a tile's mesh and the roads, which are in the stand-in's frame). The
  mesh's local y is the height over the ellipsoid, so a layer's altitudes go into the state texture shifted by the
  geoid there (`ShiftOf`, ~50 m).
- **The poles**: a stand-in's 1000 m square overlaps its neighbours' (a leaf is ~610 m): `SlopeMarkers` keeps only the
  poles whose ground tile is the stand-in's own leaf, so none stands twice. A tile's pole spots are kept as meta
  (`SlopeMarkers.PolesMeta`): a headless MultiMesh keeps no transforms.
- **The ski map** (#1737, `SlopeMap.PaintLeaves`): a pixel's place is the skier's local east and north
  (`GlobalPos.Enu`), its leaf `CubeKey.FromEcef`, its slope the stand-in's at the skier's place in that leaf's frame plus
  the pixel's offset (a leaf's axes are within ~0.01° of the skier's over the map). Per leaf on the map once a paint.
- **Ski races** (#1737, `RaceRoute.BuildPisteOnLeavesAsync`, when the source is a `StandInChunkSource`): the leaves
  within 6 rings of the start's (~3.7 km); every run moved into the start leaf's stand-in frame
  (`PisteCourse.Runs(tiles, place)`), the height at a point from the grid of the leaf it is on, the course's points
  placed by that frame's E/N. The server's `RaceManager` reads the ground's content (stand-ins under earth).
- **Fixture line runs**: `FixtureCourse.Slope.Line` makes a slope a line of its outline's width along it (its raster
  still the outline's): the fun course's "Neon run" is one, so a ski race has a run to chain.
- **Check**: `--pistecheck --chunks fixture:fun --frame earth|flat` (the course's slope found, drawn, poled; the ski
  map shows it under the skier; a 300 m ski race goes down it; the jetty's boats afloat), `--pisteat E,N[,ALT]` for a real slope: La Tzoumaz on B3's row-7 leaves
  (`--leaves D:/UnitSportSwitzerland/test_b3/row7/cube`, the only D: leaves with slopes; `test_b3/row7/lv95` is the
  same region flat) draws the same runs, poles and snow state (corduroy, ice 0.45 on 15 Jan) as flat.
