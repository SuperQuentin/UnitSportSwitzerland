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
