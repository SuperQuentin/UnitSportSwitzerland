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
