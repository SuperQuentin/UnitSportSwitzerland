# Loot and gathering (`src/Loot/`)

Lootable furniture and outdoor gathering.

## Architecture

- **Loot** (`src/Loot/`): lootable furniture in every generated interior (fridge, wardrobe,
  nightstand, desk, shelf, crate/rack, workbench, car, shop counter, hay bale, altar). Francs found go
  to the cash counter, not a slot (see `src/Items/CLAUDE.md`). **E** facing
  one indoors opens `LootUi`; E at the front door still leaves (`InteriorManager.AtExit` wins).
  Items: food/water/medical (Consume), francs, scrap, minerals, vehicle parts (`ItemUse.Material`,
  `ItemCategory`, CHF `Value` for later trade) — `ItemId` 7–36, appended. **Contents are computed,
  never stored**: `LootTables.Roll` is seeded by building key + furniture index + restock epoch
  (24 h, staggered per building by its hash), so the server (`World/Loot`, offline the client
  itself) only remembers a *taken* bitmask per container in `user://loot/E_N.json`, and only
  grants a take to a peer standing in that interior. Tables = item tier × container pools ×
  `BuildingKind` factor, scaled by `LootTables.Abundance`: a house has ~24 lootable pieces (six
  wardrobes, six nightstands) and a school a desk per classroom, so each building is budgeted
  `6 + 3·floors` containers' worth. Tune with `<godot> --path . -- --lootstats [epochs]` (real
  buildings around the spawn, prints per-kind averages, non-zero exit if an average house leaves
  its target: ~3.5 food, 1.5 drinks, 8 scrap, 1 mineral, 0.2 parts, 30 CHF). `--interiorcheck`
  also searches a container and takes everything (this adds items to the real inventory).
  `--lootepoch N` pretends N restocks have passed. Multiplayer path is untested with two clients.
- **Gathering** (`src/Loot/Gathering.cs`, hold **G / pad X** on foot outdoors — pad X is only
  tuck/sprint when mounted): a bar fills (water 1.2 s, stone 1.6 s, tree 2.2 s), moving 0.9 m
  cancels. What is offered comes from real data, in priority order: **water** where the cover
  raster is `Water` ahead at about foot height (not from a bridge) or a `Watercourse`/`Bisse`
  segment of the `.road` tile is within width/2 + 1.6 m — streams are too narrow for the raster;
  **firewood** 2–4 from a `.trees` tree within 2.3 m; **stone** 1–3 (quarry 2–3, +gravel bags on
  loose ground) on Rock/Scree/Boulders/Quarry cover; **dead wood** 1–2 on wooded cover with no tree
  in reach. `ChunkManager.TryGetCover` reads the raster (only kept for fine-stride tiles, i.e. near
  a player). Trees and streams are fetched per tile through the cached source and trees bucketed
  into 10 m cells. **Local only**, like the inventory: each 8 m spot / tree gives a few harvests
  (stone 4, tree 2, water unlimited) and regrows after 20 min, per session. A tree within reach
  outranks rock underfoot. Check: `<godot> --path . -- --gathercheck[,out.png] [--at E,N]` finds a
  shore, a flat treeless rock patch and a tree near the spawn and harvests each (adds to the real
  inventory; steep scree slides the player off the spot, so the probe picks flat rock).
