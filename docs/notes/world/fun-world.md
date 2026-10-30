# The fun world (#1396)

- **What**: a small world in the world picker, first under the open world (`WorldChoice.FunId` = `fun`, above the
  "Test worlds" heading: `WorldChoice.FirstTestWorld`), where the game's activities sit a short walk or drive apart, in a
  synthwave look. It is a fixture course like the test worlds (`Terrain/Fixture/FunCourse`), so it plays solo, hosted
  and with `--test-world fun`, its saves under `user://worlds/fun/`.
- **Layout** (metres from the spawn, X east, Y north; `FunCourse`'s constants):

  | Where | What | Built from |
  |---|---|---|
  | The boulevard (Y -20), north side | bowling "Strike City", cinema "Laser Palace", arcade "Pixel Paradise", "Café Miami" and "Sunset Diner" (terraces), "Club Turbo" | course blocks + `FixtureCourse.Venues` → `Interiors.Venues.Override` |
  | West | race track (two 14 m halves of an oval, one loop for `RaceRoute`), kart loop (8 m) in its infield, 12 80s-90s cars in a pit row, 8 karts | Major / Minor roads with `RoadWidths`, `FixtureCourse.Fleet` |
  | All round | the grand circuit (`FunCourse.CircuitCorners` filleted by `Circuit()`): ~9 km, 12 m, anticlockwise, one closed edge of its own; a chicane and a dip on the south main straight, a sweeper, esses and a crest on the east, a chicane and a crest on the north, the bus stop on the west; a 10-car grid behind the start | a Major road on the ground function + `Swells` |
  | North | runway (Y 690, 1.4 km), apron with five small planes and a helicopter; the air rings start over it | cover + fleet; `FunRings` |
  | North-west | snow hill (110 m over 400 m), a groomed slope, a T-bar up its east edge | the ground function, `FixtureCourse.Slopes` → `PisteTile`s, a `SkiLift` road |
  | North-east | "Neon Arena", its stage always playing | `FunCourse.ArenaAt` → `StadiumCatalog.UseWorld`, `ConcertDirector.WorldSeed` |
  | East | PvP block (`FunCourse.PvpBox`): a tower, two sheds, crates, low walls; six weapon crates | blocks; `FunCrates`, `PvpRules.Zone` |
  | South-east | stunt park: three kickers, a gap jump, a half-pipe through a mesa, a bowl | the ground function (no loops: terrain cannot) |
  | South | truck yard: a dock row to reverse a semi into between parked trailers, an L alley, trailers to back under between containers, a box for the drawbar train | blocks, painted `Lines`, fleet with `Train` codes |
  | South-west | lake (r 220 m), beach, a jetty with boats, a slipway with a pickup and its speedboat trailer at the top | the ground and water functions, `Jetties` |
  | South-centre | festival field: the Älplerfest all year, every game open (flags, sawdust ring, alphorns, the stone, fireworks) | `FunWorld.Enter` re-registers its def, `OccasionManager.WorldStarted` |

- **What a course hands the game, process-wide**: its venues (`Venues.Override`), its stadium (`StadiumCatalog.All` =
  `Real` + the arena at the end, so the real ones keep their wire indices; `StadiumClip`'s outlines follow the list) and
  its fleet (`FixtureChunkSource.Fleet`, read by `DormantVehicles.Fill` on every peer, slots `fix_fun_<n>`, respawning).
  Set by the source's constructor on the server and every client alike, undone by `FixtureChunkSource.ForgetWorld` in
  `WorldStatics.Reset`. A check's own `Venues.Override` is left alone.
- **The rules** (`World/Fun/FunWorld`, `Enter` as `WorldChoice.Active` is set on either side, `Leave` on reset):
  - the sky held at `DuskHour` (19.6: the sun just down, the blue hour; the retro sun is the sky shader's, on the
    horizon where the sun set): the server's `WorldClock` started there and stopped, every client's
    `DayNight.HourOverride`; `DayNight.WorldGrade` swaps the palette for a pink light and a violet haze. A stopped
    clock never crosses the festival's show hour: no closing fireworks (a `/time` move starts them);
  - the adventure layer is on here (`Systems.On`), unlike the test worlds: the festival needs it;
  - PvP only when shooter and victim both stand in the block (`PvpRules.Zone`, after a mode's `Override`), from the
    server's own bodies (`FunWorld.Players`); the crates there may be looted by anyone in the block, refilled 90 s after
    they empty or a match clears them.
- **The look** (`World/Fun/FunBackdrop`, client, built once from the course):
  - sky `shaders/fun_sky.gdshader` (violet dome, stars, a striped retro sun on the horizon where the sun is), glow on, set
    again on a restyle (`ClientWorld.ApplyStyle` → `Restyle`);
  - `fun_grid`: a 30 km plane 0.8 m under the flat following the camera (snapped 100 m), lines every 20 m fading by
    2.6 km, a hole over the course's tiles (the lake bed is under the plane's height);
  - `fun_wire`: a ring of wireframe mountains 5.2-7.4 km out, low toward the sun (barycentrics in the vertex colours);
  - palms and lamp posts (MultiMesh), neon tubes (`neon_tube`) along every road edge, the runway, the venues' rooflines,
    round the PvP block, stunt park, yard and festival field; the sign over the avenue.
  Model viewer: "Fun world palm", "Fun world lamp posts", "Fun world mountains", the arena under "Stadium".
- **Air rings** (`World/Fun/FunRings`, client only, no network): 15 rings (`FunCourse.Rings`); the first starts (and
  restarts) the clock, the next one is pink and larger, a panel on the right shows ring and time and the session's best.
  Passing = the body within ring radius + 2 m of its centre. Nothing to press: any device.
- **Bots**: nothing new to do; the race track, the festival games, the bowling lanes and the PvP block are the existing
  activities' own bot seams. A bot is fair game anywhere, as everywhere (`PvpRules.Allows`).
- **Checks**: `FunCourseTests` (venues in their blocks, fleet clear of blocks and water, slope and lift, rings, PvP block,
  what the source hands over and takes back); `--worldpickcheck --world fixture` for the picker. Shots:
  `--test-world fun --shot-queue <file>` (the plaza: `0,506,75,-6,0,8,...`).
- **Not done**: loops (the ground is a height function), scoring for the trailer puzzles, horses (no mount exists).
