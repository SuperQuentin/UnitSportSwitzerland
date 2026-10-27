# Arcades (#1240, epic #1232)

The generator for OSM's `leisure=amusement_arcade` solids (`entertainment-venues`). Research numbers:
`venue-arcade`. Built on the bowling centre's lane frame and rules (`bowling-centres`).

- **Branch**: as bowling and cinema, `TryArcade` before the industrial and flats rules, one level (the
  bottom storey under flats, a 3-5 m blind box when the arcade is its whole building), keeping only a
  plan the validator passes, else the layout back untouched. `ArcadeRules` re-plans arcades only.
- **Rooms**: one `ArcadeHall` from the door's end, at most 32 m a side (a mall's remainder is a plain
  `Hall` behind, through the store, and one beside); a 2.6 m strip across its back of `WC`, `Storage`,
  `Office`, each off the hall. The hall has no windows, whatever the facade: arcades are dark rooms.
- **Hall** (`ArcadeLayout`): the bowling carpet print; by the door the `PrizeCounter` (shelves of plush,
  a clerk, a lit glass case), the `ChangeMachine` and 2-5 `ClawMachine`s; the big machines on the side
  walls, one wall then the other (`Racer` twins, `SimPod`, `DanceMachine`, `BasketballGame`,
  `DrumGame`); a row of `Pinball`s along the back (an eighth of the machines at most); then the
  **islands**: uprights back to back, 3-8 a side, in a grid over the floor with 1.8 m aisles. An island
  goes whole where it and 0.9 m round it are free, or not at all; the length is the one that seats most
  of what the budget leaves, a shorter one when it fits nowhere; the islands are picked checkerboard
  first and spread evenly (an even pick over a linear slot index fell into one column). Then uprights
  along the walls with what is left, and air hockey (1-3) in the open floor between the islands.
- **Budget**: 15 machines to 100 m2 of hall, at most 110 (a 185 m2 hall gets ~30, as real ones).
- **Pieces** (`InteriorMeshBuilder.Arcade`): screens and marquees self-lit; no glass: a pane is window
  glass to the interior shader, which draws the sky in it, so the claw machine's box is an open frame.
  The pinball's body is 1.5 cm in from its piece's sides (pinballs stand in rows 5 cm apart).
- **Light**: tint 3 on the hall's lamps (`RoomLights.Tint`), a violet in the interior shader.
- **Outside**: as bowling (the name and ARCADE big over the door, the joystick icon).
- **Staff**: the clerk at the prize counter (`InteriorNode.Staff`).
- **Checks**: `--arcadecheck --systems ui` (`ArcadeProbe`: small 12x18, big 28x36, along X, a mall, under
  flats, too small; SVG plans in `test_output/arcade/`). `--zfightcheck`: the whole arcade's baseline
  line is the service doors' jamb seams (two room palettes meet in the doorway). Model viewer: every
  piece, and `Interiors / Arcade`; a whole hall from inside renders black there, as the cinema's.
- **Real**: `--venuecheck` finds one building showing an arcade (of 35 points): Billard Bluff, Geneva
  (90x86 m, 31 m: its ground floor). A shot's `i` picks the door nearest by its own rule (here a side
  door 26 m from the venue's), and the camera goes as far in as it stood out: aim at the door it names.
- **Next**: playable machines (each a piece with a stable index, an epic of its own), sound (part 6).
