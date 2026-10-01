# Cellars, shelters and room variety (#213)

- **Cellars**: `InteriorLayout.Below` floors sit under the ground floor. Floor `i` stands at
  `InteriorLayout.FloorY(i)` = `(i - Below) · StoreyHeight`; the entrances are on `GroundFloor`
  (`Floors[Below]`). Anything that turns a floor index into a height must go through `FloorY`
  (mesh builder, lock doors, `LootService.NearestOf`, probes); `f.Floor * StoreyHeight` is wrong
  in a house with a cellar. `InteriorManager.ExitAt` only lets you out from the ground floor.
- **Who gets one** (`InteriorGenerator.Cellars`, own seed `key|cellar`): houses 65%, apartment
  blocks 85%, `Other` 50-70%; never single-room plans. The cellar is just one more floor at the
  bottom of the cored plan's stair stack (the same switchback), so a one-storey house with a cellar
  gets a stairwell. If the plan fails with the cellar it is retried without one.
- **What is in it** (`CellarProgram`): a house always has a laundry, often (45%) the civil-defence
  shelter, and a shuffled pick, by area, of guest room, home cinema, carnotzet, music room and
  storage cellar. A block of flats has the shared laundry, storage compartments and always the
  shelter. No windows below ground; shelter and vault never get windows.
- **Logical order**: each side's rooms are fed to the treemap street end first (`Depth`): laundry,
  storage cellar and banking hall toward the street, shelter, vault and carnotzet at the back.
- **Shelter door**: every door of a `Shelter` room gets a baked steel frame and an open armoured
  leaf flat against the wall (`InteriorMeshBuilder.BlastDoor`); `InteriorGenerator.BlastLeaf` keeps
  that strip free of furniture. Gun lockers prefer the shelter, then the cellar (the army rifle).
- **Above ground**: houses sometimes get a pantry off the kitchen, a study instead of an office, a
  playroom instead of the last child's bedroom; flats sometimes a study.
- **New furniture** (`FurnitureType` appended, never reordered): washing machine, dryer, ironing
  board, bunk bed, water tank, wine rack, barrel, drum kit, piano, keyboard, guitar stand,
  amplifier/speaker, acoustic foam (wall panel, no collision), cinema screen (no collision),
  armchair, bookcase, toy box, teller desk, vault safe. Loot pools by piece and by room in
  `LootTables` (shelter shelf = emergency supplies, carnotzet = cheese, laundry = cloth...).
- **Loot budget** counts storeys above ground only (`Floors.Count - Below`), or a cellar would
  make a house richer. House targets in `--lootstats` still pass.
- **Plan version 8**: stored plans regenerate. Saved loot records carry the plan version and older ones
  are ignored (`LootService.Current`), so renumbered furniture never inherits another piece's taken or
  cracked state.
- **Check**: `--lootstats` also prints cellar/shelter rates per kind, the room mix, the banks, how
  many plans fail `InteriorValidator`, and writes SVG plans (banks, cellar houses, flats) to
  `test_output/rooms/` (render: Edge `--headless=new --screenshot=x.png file:///...svg`).
  Riddes (2026-10): houses 61% cellar / 27% shelter, flats 84% / 84%, 0 of 1325 plans invalid.
