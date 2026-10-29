# Pinball for real (#1325, epic #1322)

Playable machines in every venue with a `FurnitureType.Pinball` (arcades, bowling centres, bars). Research,
sizes and the four tables: `pinball`. Plan: `docs/plans/playable-pinball.md`. Code: `src/Interiors/Pinball/`.

- **Pure core** (unit tested, `PinballSolverTests`, `PinballRulesTests`): `PinballTable` (the table as data, playfield
  frame: x across, y up the table, h above it, metres), `PinballLayouts` (the four tables on a shared lower third),
  `PinballSolver` (1 ms steps, swept-circle CCD on walls and posts, driven flippers, ramps as 1D tracks, scoops,
  nudge and the tilt bob), `PinballRules` (modern-lite), `PinballGame` (the two together; the wire formats),
  `PinballScreen` (backbox + lamps, packed), `PinballFont` (5 x 7 glyphs and 7-segment digits).
- **Tuning that matters**: flipper 70 rad/s at 15 000 rad/s², bounce 0.55 off the moving surface, 0.997 of the
  sliding kept a step (more friction and the ball never rides the bat); walls keep 0.998, rubbers 0.995 (a ball
  rolling along a guide touches it every step: 0.99 slowed a plunge round the arch to a stop). The inlane guide
  ends 2.6 cm over the flipper's pivot, so a raised flipper cradles the ball on the bat, not on its round base
  (a ball there never moves: rotating a disc about its centre pushes nothing). Godot's `Vector2.Up` is (0, -1).
- **Laying out shots**: each flipper's cradle shots span 70-112° and inlane balls 56-124°: each flipper covers the
  centre and the far side. Orbits start ~0.64 m up the side walls, below them a rubber deflector turns a ball
  coming down a side into the playfield instead of the outlane. `Every_shot_is_reachable_from_the_flippers`
  proves every named shot; `PINBALL_PNG=<dir> dotnet test --filter Draw_tables` draws the tables and their shots.
- **The cabinet** (`PinballCabinet`): the piece's frame (back −Z, player +Z), playfield front at (0, 0.80, 0.60)
  tilted 6.5°. One static mesh per table (body, field, plastic ramps) shared by every machine; flippers, balls,
  drops, spinners, plunger are nodes; lamps two MultiMeshes (colours set on change); the backbox
  (`PinballBackbox`) an image drawn on change through `shaders/pinball_display.gdshader` (reels, segments,
  round dots, LCD). Decals are thin plates, never quads on a surface; the backglass art 9 mm a layer (zfight).
  Headless, the lamps' MultiMesh instances read at the playfield's origin: most of the tables' z-fight baseline.
  The table of a piece: `PinballTables.TableOf` (`PinballLayouts.TableIn`: the venue's machines in a row by x then z take
  the tables in turn from one the first machine's place picks, so up to four are all different and neighbours never
  match); the room's mesh draws nothing for it.
- **Net** (`PinballTables`, `World/Pinball`): the player up's client runs the table (`PinballPlay`) and sends
  20 frames a second (events + poses); the server mirrors the rules from the events (`PinballGame.Mirror`: same
  events, same score), keeps the log, hands the table to the next player's client (`Run` with the log,
  `PinballGame.Resume`), skips a player gone (left the venue, stepped away, 6 s silent: `SkipMark` in the log),
  relays poses + events (unreliable, for watchers' balls and sounds) and the screen on change (reliable) to the
  peers inside (`SpaceOf`); a client asks on walking in. Up to four players: Start in the first ball adds one,
  pressed by the player up's client (`Join`) so the add is in the log. Markers in the log: `LeftPress`,
  `RightPress` (lane change), `StartPress`, `SkipMark`.
- **Playing** (`PinballPlay`): E / Y at the machine's front (`PinballTables.TryUse`), the eye view or straight down
  (V / R3), flippers A / D / arrows / Left and Right Shift (bound with a key location: one side each, the hint says
  "Left Shift") / LB RB / LT RT, plunger Space / S / Enter (kept from the chat at a machine, `PinballPlay._Input`) /
  pad A / right stick back, nudge W / pad X
  (Q / R or the left stick for the side), Start E / Y, Esc / B back. VR: the head stays the camera, grips and
  triggers flip (they are the pad's shoulders, `XrPad`). Sounds `PinballSounds` via `PinballCabinet.Audio`.
- **`/pinball`** (offline): `list`, `play <n>`, `start`, `robot` (a robot at the flippers), `view`. BowlinGland
  (Gland): `--origin 2510000,1140000`, shot `429.1,i1.6,-978.3,-5,43.4,3,<png>`, then machines #7 and #8 at plan
  x 39.16 / 35.69, z 16.02, turned 2: the eye pose `35.69,l1.5,14.93,-21.5,180`, straight down
  `35.69,l2.05,15.80,-83.5,180` (a shot camera is not the play camera: shots take the queue's pose).
- **Checks**: `--pinballcheck --systems ui` (cabinets played through their nodes: flippers, balls, drops, lamps,
  the backbox, no mesh rebuilt, the screen over the wire), `--zfightcheck Pinball`, `--sounds,check`.
- **High scores** (`PinballHighScores`, `PinballScoreBook`, unit tested): the best five per machine, keyed like the
  table choice (plan key, furniture index, and the table: a plan that puts another table there starts afresh), kept by
  the server in `user://pinball_scores.json`. A game over offers every player's score under their own name (the
  chat name, no initials), a new one is told to its player. The table rides the machine's `State` (and a peer walking
  in gets every machine of the venue with scores, played or not); offline the local book goes onto the machines.
  The backbox's attract cycle, 3 s a step: title or game over, press start or match, the high scores, each era its
  way (reels turned to the best four with "HIGH SCORE TO DATE", the alphanumeric line naming each champion in turn,
  the dot-matrix's list, the LCD's results board). Model viewer: Venues › Pinball attract.
