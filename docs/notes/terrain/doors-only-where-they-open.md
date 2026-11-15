# A facade door is drawn only where it opens (#1766, #1767)

The doors are placed from the walls (`BuildingFootprint.ComputeDoors`, #498/#1304) before any plan exists, and a
building's **extra** door (slot 1 and up) the plan fits no doorway for reads as locked (`ServeDoor`: "This door is
locked."): 1,050 of 4,371 street doors round Geneva (24 %, `--outlinesurvey --town Geneva`, Nov 14), five of the
first six tried in the old town. From inside, the wall shows no doorway where the facade has a door (#1767). The
causes are many (a flat's room right behind it, a corridor whose wall stops 1-2 m short, no room within 20 m:
`doors-planned`), so fitting cannot promise a doorway; what can be promised is that a door without one is not drawn.
The main door (slot 0) always has its doorway, so every building keeps a way in.

## How

- **`DoorLocks`** (plain C#, `DoorLocksTests`): per tile, which slots of which building have no doorway, with the
  building's print (`InteriorLayout.Matches`' terms: triangles, heights, group print, house layout). Kept in
  `user://door_locks/v<plan version>/E_N.txt`, one line a building (`index print slot…`): a new generator version
  judges everything again, a changed building is judged again. Thread-safe; `Changed(tile)` when a tile's locked set
  changes.
- **`DoorWarden`** (client node, `ClientWorld`): every 0.25 s the nearest building within 160 m of the camera with a
  drawn extra door not judged this session goes to a thread of its own at below-normal priority: the stored plan if it
  still matches, else `InteriorGenerator.Generate` (not stored: the plans of a whole town would be tens of MB), and
  each group member's doors are recorded. A tile that gained a locked door is built again 2 s after the last change
  (`ChunkManager.RebuildTile`; under earth `EarthWorld.RefreshDoors`, which re-prepares its leaves' stands).
- **The filter** `DoorWarden.Openable(tile, doors)` right after `ComputeDoors`, where the drawn list is made: the flat
  tile build (drawing and the collision-only build), and the leaf's stand (`LeafStand`, whose doors the leaf draws and
  collides with). The door index is that list, so a left-out door has no E prompt, no lantern, no stoop.
- `DoorLocks.Shared` is set before the first tile is built (`UseRecords`), so a place visited before is drawn right
  from the start; only a first visit shows a door that then goes, 2 s after it was judged, up to 160 m away.
- Off in test runs unless `--doorwarden` (a probe's or a shot's doors must not change under it); `--no-doorwarden`
  turns it off; `--doorwarden-trace` prints each building and the state every 10 s.

## Check

`--doorwardencheck [--doorwardenlocked] --at E,N --chunks …` (headless): a camera on the ground at the start, the
warden settled, then every drawn door within 90 m is asked of the real `InteriorManager`: its plan must have a doorway
for it. `--doorwardenlocked` also needs some door left out.
Measured Nov 14 at the issue's spot (2501414,1117284, fresh records): flat 23 buildings planned in 36 s (median
300 ms, worst 1.7 s), 21 doors left out, the tile built again 4 s after the warden settled, 15 of 15 drawn doors
within 90 m open; earth 23 planned, 25 left out, 9 s, 13 of 13.

`--outlinesurvey --town Geneva --key E_N_I --only --grid` plans one building as the game does (on the terrain grid:
without it a hillside building's doors and plan can differ) and prints its drawn doors against the plan's doorways.
`2501_1117_78` (#1766's door `_2`): slots 1 and 2 locked, a flat's WC and living room right behind them.

## Known limits

- A dedicated server draws nothing and runs no warden: its bots (#837) may still walk to a locked door and be
  refused, as before. The server's leaf stands keep every door's stoop collision.
- `RebuildTile` in the flat world builds the tile's meshes again and leaves its collision: a left-out door's stoop
  keeps its collision until the tile is next built from scratch (the record is on disk by then).
- A far tile drawn without the terrain grid (stride > 1) can number a hillside building's doors otherwise than the
  plan (the stilts and buried-side rules read the grid): the filter may leave out another of its doors there, far away.
