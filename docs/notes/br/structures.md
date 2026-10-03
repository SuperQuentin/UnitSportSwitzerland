# Battle Royale structures and match materials (#276, part 6 of the #270 epic)

- **Prefabs** (`src/BattleRoyale/BrPrefabs.cs`, plain C#, `BrPrefabsTests`): lists of building pieces (`BuildGrid`)
  plus `GadgetSpot`s, in the structure's frame. Every prefab must stand by the grid's own rules (the test).
  | Prefab | Pieces | Gadget |
  |---|---|---|
  | Lookout tower (6 storeys, 14.4 m) | 2-cell stair tower, window walls, railed platform | zipline from the top |
  | Ski jump (2 storeys) | the same tower, shorter | launch pad on top, chevrons downhill |
  | Avalanche barrier | 4 metal walls in a row, each on its legs | |
  | Army checkpoint | 3 x 3 ring of sandbag half walls, open for the road, 2 metal walls | camo net |
  | Scout fort | 2 x 2 wood: door, windows, deck with railing | rope ladder, trampoline |
  | Scaffolding | 5 bays x 3 storeys of metal decks, posts, stairs | |
  | Footbridge | 12 wood cells grounded at both ends, railed | |
  Footbridge = wood's span x 2: break one end and the 4 nearest cells fall.
- **Placement** (`BrStructures.Place`, server, after `BrSites` in `BrManager.SpawnLoot`): region tiles' height grids
  (`ChunkGrid.SampleMeshHeight`, exact), cover, buildings, roads/watercourses, the 100 m horizon lattice for relief.
  Towers on local summits (no higher point 200 m round, 40 m over the 400 m ring, 0.25/km², max 6, 600 m apart) with
  the cable landing 70-130 m down the fall line where `Gadgets.ZipProblem` accepts it; ski jumps on open slopes of
  0.45-0.85 with a wood within 60 m (max 3); 3 staggered barrier rows on slopes over 30° above 1,800 m (max 8 sites);
  checkpoints on Major/Road points, the road along local Z (max 4); scout forts where 6 of 8 points 35 m round are
  wooded (max 4); scaffolding beside every `UnderConstruction` building (max 10); footbridges where a watercourse
  has both banks level within 2 m and 5 m over the water (max 3). Origins are cell 0's corner on the ground; yaw
  turns local −Z toward the open side (`BrSites.YawFacing`); `taken` keeps them apart.
- **Spawning** (`BrManager.SpawnStructures`): `Structures.SpawnPrefab` = a match structure owned by nobody (any
  entrant may break it, nobody may take it), grown at once, sent as one `AddPieces` RPC (also on join for undamaged
  pieces). Gadgets via `PlacedObjects.ServerPlace` owned by `PlacedObjects.MatchOwner` ("(match)"): never saved,
  cleared in `ClearLoot` (`ClearOwner`). Match structures go with the match (`Structures.ClearMatch` from the sweep).
- **Rubble**: a match piece that breaks or falls leaves `RubbleShare` (40 %) of its materials
  (`Structures.Rubble` → a `CrateStyle.Pile` crate "the rubble" from `ServerWorld`).
- **Materials**: supply crates add planks (60 %, 10-20) or stone (20 %, 12-24); army crates metal (scrap + screws)
  or sandbags; gathering in a match gives 3 planks per tree, 1 per dead wood, 4 stone per rock. Match players start
  with a hammer and 15 planks (#274).
- **Offline**, a lone player may break match structures (only probes make them).
- **Not yet**: structures on the BR maps, avoiding roads/buildings beyond spacing, a ski-jump inrun shaped to the slope,
  the measured counts on real regions (only the generated world and the probe ran).
- **Checks**: `tools/test.sh unit` (`BrPrefabsTests`); `--prefabcheck --systems ui,physics,build` (every prefab
  spawned the match way, drawn whole and standing, 5 match gadgets, tower wall broken → rubble with planks,
  bridge end → 4 cells fall, all cleared; `shots` windowed writes `test_output/prefab_*.png`).
