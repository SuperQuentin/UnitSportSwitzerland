# Playable bar and arcade games (#1324, #1326, epic #1322)

Status: **built** (2026-11-03): the framework (#1453), pool (#1459), darts (#1461), table football (#1468), air hockey
(#1469), coins, tickets and the prize counter (#1472), the claw machine (#1473), the cabinets' games (this plan's last PR).
As built: `docs/notes/terrain/table-games.md`. Open: the VR hand ways (each game's row in `xr/vr-action-map` says gap),
bots walking in to play (#1330).

## Decisions (owner, 2026-11-03)

- **One PR per game**: a framework PR first, then pool, darts, table football, air hockey, the claw, the
  cabinets, tickets and the prize counter, each merged as it is finished.
- **Coins**: the change machine swaps carried francs (`Inventory.Cash`) for a new `Batzen` coin item, 1 CHF =
  1 coin; the machines take coins. Apart from the festival books' Batzen.
- **Prizes**: new original items (plush animals, a keyring, a bouncy ball, sweets), the bigger ones dearer in
  tickets; the claw's prizes are plush.

## The framework (`src/Interiors/Games/`)

- **`TableGames`** (`World/TableGames`, server + every client, RPCs by node path): per (venue key, furniture
  index) a session: the game (pure), its seats (peer ids, bots below `BotBase`), whose turn. Every action is a
  `Serve*(peer, key, index, ...)` a bot can call; the RPC handler only forwards. State packed on change to the
  peers inside (`InteriorManager.SpaceOf`), a client asks on walking in, forgets on leaving.
- **Two kinds of game**:
  - *turn games* (pool, darts, the claw's drop): a shot or throw is a few integers on the wire; the server
    resolves it with a pure solver, sends the shot and the result; every peer animates the shot from the
    same numbers and ends on the server's result (no drift kept).
  - *live games* (table football, air hockey, the claw's moving): the server steps a pure solver at 120 Hz,
    players send their input 30 times a second (unreliable), the server sends poses 20 times a second, the
    watchers show them a little behind. A game with nobody at it sleeps.
- **`ITableGame`**: `Pack`/`Unpack`, `Act(seat, action, args)`, `Step(dt)` for live ones, `Bot(seat, rng)` for
  an opponent with a skill spread.
- **Rigs**: a node per piece built by `InteriorNode` (as `PinballCabinet`): what moves is nodes or a MultiMesh,
  set from the state; the room's mesh stops drawing those moving parts.
- **`TableGamePlay`**: stepping up (E / Y, a VR hand) puts the camera on the game, a HUD with `InputHints`
  rows, Esc / B steps back; each game gives its controls and camera.

## The games

- **Pool (8-ball)**: a pure 2D solver (balls sliding to rolling, cushions, pockets as jaws and a drop),
  1 ms steps, a shot = (angle, power, spin x/y) as integers. Rules: break, open table, groups, fouls (scratch,
  wrong ball first, no rail), ball in hand, the 8 last. Aim with the stick or mouse, power on a held swing.
  VR: hold the cue in both hands, push to strike.
- **Darts (501, double out)**: aim at the board, a steadiness wobble, the throw lands where the server's scatter
  puts it; the score from the board's segments (doubles, trebles, bull 25/50), bust rules. VR: throw the dart
  (the hand's last 0.1 s).
- **Table football**: two to four players, each on rods (2 players: four rods each; 4: two each), slide and
  kick; the ball a disc on the pitch, the figures as boxes swinging with their rod; goals counted on the
  table's beads. VR: grip the rods.
- **Air hockey**: two mallets following the stick or mouse inside their half, the puck on air, goals to 7.
  VR: hold the mallet.
- **The claw**: coin in, 30 s to move, drop: down, close, a grip chance by the prize's place, up, to the chute;
  the prize is an item. VR: the joystick gripped.
- **Cabinets**: original small games drawn into the screen's texture only while someone plays (a racer, a
  shooter, a rhythm game by the cabinet's kind); a score pays tickets.
- **Tickets and the prize counter**: tickets an item paid by the games; the counter swaps them for prizes.

## For every game

Keyboard, pad and VR decided together (`xr/vr-action-map`), a bot opponent with a skill spread
(`bots/bot-capability-map`), synthesised sounds in the sound player, nothing that moves rebuilds its mesh,
a pure core with unit tests, a probe (`--<game>check`) and a net check of the remote peer's view.
