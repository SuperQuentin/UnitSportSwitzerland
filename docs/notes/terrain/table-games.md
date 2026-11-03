# Table games: pool, darts, table football, the arcade (#1324, #1326, epic #1322)

The playable pieces apart from bowling and pinball. Plan: `docs/plans/playable-bar-and-arcade-games.md`.
Code: `src/Interiors/Games/`, one folder per game beside it.

- **`TableGames`** (`World/TableGames`, server + every client): per (venue plan key, furniture index) one
  `TableGame` the server owns: seats (peer ids in play order, 0 empty), `Up` (whose turn), the game's state.
  Actions are `ServeSit`, `ServeLeave`, `ServeAddBot`, `ServeAct(peer, key, index, action, args)` and, for live
  games, `ServeInput`; the RPCs only forward, a bot calls them with its id. A change is packed (`TableGame.Pack`)
  to the peers inside (`InteriorManager.SpaceOf`); a client asks for the venue's games on walking in and forgets
  them on leaving. Live games are stepped at 120 Hz while `Awake`, poses out at 20 Hz (unreliable).
- **Turn games** resolve an action at once on the server; the state carries what every peer needs to animate it
  (the shot and where things were) and the result. **Live games** stream poses.
- **Bots**: seat ids at `TableGames.BotBase` (-2 000 000) and below, no body, no RPC. A seated player alone adds
  one with E / Y; the server asks `TableGame.Bot(seat, now, rng)` ten times a second and plays what it returns;
  bots leave with the last human, and give their seat to a human who asks for it.
- **`GameCatalog`**: one `GameSpec` row per game: the furniture type, where a body may use it from (`UseFrom`,
  in the piece's frame: back −Z, front +Z), the outline box, the rig (`TableRig`, built by `InteriorNode`
  per piece) and the controls (`TableGamePlay.Mode`).
- **Playing** (`TableGamePlay`): E / Y at the piece takes a seat (or watches a full game), the camera on the game,
  the game's controls read in its `Mode`; E / Y again with a free seat adds a bot; V / R3 the other view; Esc / B
  steps back and gives the seat up. Phone: `TouchMode.Bowl` (the stick aims, the Bowl button strikes).

## Pool (eight-ball)

- **Solver** (`PoolSolver`, pure, `PoolTests`): balls as discs on the cloth (piece frame: x across, z along), 0.5 ms
  steps; each ball's `V` and its spin's contact velocity `W`: sliding (μ 0.2) until W = V, then rolling (0.011);
  follow/draw is W at the strike (±1.25 V); ball-ball smooth (e 0.95, the spin stays: draw comes back); cushions
  keep 0.72, broken at the six pockets' mouths (corner 6.2 cm, side 5.8 cm). The cloth is the piece less its 12 cm rails.
- **Rules** (`PoolGame`): break from behind the head string (the 8 on the break re-racks), open table until a pot of a
  group, fouls (scratch, nothing hit, wrong ball first, no cushion after contact) give ball in hand anywhere, the 8
  wins only after the group, a shot after the game's end racks again (the loser breaks). Alone: practice.
- **Wire**: a shot is (angle in thousandths of a degree, power 0-1000 to 8 m/s, follow -1000..1000); the state carries
  the table before the shot and after; every peer replays from before (`PoolRig`), 0.22 s after the cue starts forward,
  and ends on the server's table (the replay matches to the millimetre: same code, same numbers).
- **Bot** (`PoolGame.Bot`): ghost-ball pots on clear lines, best cut over distance, a ~1.3° spread; else a hit on its
  nearest ball. Two bots finish a game in 17-28 shots.
- **Rig** (`PoolRig`): the balls one MultiMesh (a stripe shader on the instance's custom data, turned as they roll), the
  cue a node (the stroke, the local player's aim), the aim line to the first ball; sounds `PoolSounds` (tip, clack,
  cushion, pocket). The room mesh no longer draws loose balls on the table.
- **Controls** (`PoolPlay`): down behind the cue ball along the aim; A / D or the stick aims, Sprint fine, the arrows or
  right stick nudge; W / S follow or draw; hold Jump to swing the power, let go to strike; in hand from above, the stick
  moves the ball, Jump puts it down. VR: the pad (`XrPad`); the cue in both hands is the target.
- **Checks**: `--poolcheck --systems ui` (two bots through the rig; headless a MultiMesh reads its instances at the
  origin, so it reads `PoolRig.Shown`), `tools/poolnetcheck.sh` (BowlinGland, two clients and a bot), model viewer
  Venues › Pool.

## Darts (501, double out)

- **Board** (`DartBoard`, pure, `DartsTests`): mm from the bull, x right, y up; segments clockwise from 20, treble
  99-107, double 162-170, bulls 15.9 / 6.35 mm; oche 2.37 m, bull 1.73 m. `Aim(value, mult)` is a bed's middle.
- **Rules** (`DartsGame`): up to four, three darts a visit, bust below 0, to 1, or to 0 off a single; a throw after the
  leg starts the next (the winner's next throws first); the HUD shows a checkout (`Checkout`). Alone: practice.
- **Wire**: a throw is the aim in tenths of a mm; the server adds a human's 5 mm hand spread (the reticle's sway is
  the rest of the skill; a bot's 17 mm sway is in its aim) and scores where it lands.
- **Rig** (`DartsRig`): the face is a disc with the beds, the spider and the numbers drawn once into a shared texture
  from the same scoring code (the check reads its pixels back); a dark backing; three dart nodes (one shared mesh)
  flown 0.4 s in an arc and left in the board until the visit's darts are pulled; the aim ring; sounds `DartsSounds`
  (thunk, a knock off the surround, the pull). The room mesh draws only the cabinet.
- **Controls** (`DartsPlay`): from the oche, a 38° view; the stick moves the aim, which sways; hold Jump to steady it
  (~1.5 s, then it tires), let go to throw. V / R3 a close look. VR: the pad; throwing by hand is the target.
- **Checks**: `--dartscheck --systems ui`, `tools/tablenetcheck.sh` (any turn game: `GAME=DartBoard` by default,
  `GAME=PoolTable` works too; the whole packed game compared after every change), model viewer Venues › Darts
  (`--modelsyaw 0 --modelslook 0,1.73,0.02,0.9` faces the board).
- **Reach**: a `GameSpec.Reach` per game; E searches `GameCatalog.MaxReach` and the game's `UseFrom` decides (a board
  from its throw line, 3.2 m).

## Table football

- **Game** (`FoosballGame`, pure and live, `FoosballTests`): the pitch 1.11 x 0.63 m (x along, z across), goals 19 cm,
  ball 17 mm; eight rods (1, 2, 3, 5, 5, 3, 2, 1, red's goalie at −x), each a slide and a turn θ (0 hanging); a figure
  blocks while |θ| < 0.55, its foot reaching sin θ x 7.5 cm; a kick swings at 28 rad/s and the foot's speed goes into
  the ball. A ball still for 2.5 s is dead and served again from the side (the middle of two rods is out of every
  foot's reach). First to 10. Seats: 0 red, 1 blue run all their rods; 2 and 3, if taken, the front rods (midfield,
  attack).
- **Wire**: a seat's controls are two bytes (slide −127..127 as +z, kick and lift flags), 30 a second, unreliable; the
  poses (ball, its speed, the rods) 48 bytes, 20 a second; goals and calls in the state, on change.
- **Bot** (`FoosballGame.Bot`, asked ten times a second): lines up the nearest figure of its rod nearest the ball with
  where the ball was a moment ago, kicks a ball just in front, lifts a front rod for a ball behind it.
- **Rig** (`FoosballRig`): the whole table (the venues place it 1.5 x 0.8 and 0.75 x 1.4: laid along the longer side),
  box and pitch as shared meshes, each rod a node carrying its figures and handle (red's handles at −z), the ball a
  node, the score as beads; poses eased and the ball carried on by its speed. The room mesh draws nothing for it.
- **Controls** (`FoosballPlay`): from your side, looking across; W / S push and pull, Jump kicks, Sprint lifts.
- **Checks**: `--foosballcheck --systems ui` (both orientations, two bots to ten), `GAME=TableFootball
  tools/tablenetcheck.sh` (each player's push seen by the other, the ball streamed, the same score, a bot's rods).

## Air hockey (#1326)

- **Game** (`AirHockeyGame`, pure and live, `AirHockeyTests`): the field inside the rails (piece less 8 cm a side; x
  across, z along), puck 31 mm, mallets 48 mm, goal slots 28 cm; the puck keeps 0.9 off a rail and takes the mallet's
  speed (up to 7 m/s); each mallet stays in its half; seat 0 defends −z. First to 7; a goal serves the puck, still, in
  the conceder's half.
- **Wire**: a seat's controls are its mallet's velocity (two sbytes of 3.2 m/s), 30 a second; poses 32 bytes, 20 a second.
- **Bot**: comes at a puck on its side from behind towards the other goal, else guards a quarter of the way out.
- **Rig** (`AirHockeyRig`): puck and mallets nodes (eased, the puck carried on by its speed), the scorer a lit board on
  a post at the table's side (both faces), clack and goal sounds. The room mesh draws the table with the goals' slots
  in its end rails, no longer a puck and mallets.
- **Controls** (`AirHockeyPlay`): from your end; the stick drives the mallet as you see it, Shift full speed.
- **Checks**: `--airhockeycheck --systems ui`, `GAME=AirHockey tools/tablenetcheck.sh` (the live branch reads table
  football's rods; air hockey's own live checks are the probe's for now).
