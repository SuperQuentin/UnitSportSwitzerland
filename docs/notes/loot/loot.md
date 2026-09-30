# Loot

- **Loot** (`src/Loot/`): lootable furniture in every generated interior (fridge, wardrobe,
  nightstand, desk, shelf, crate/rack, workbench, car, shop counter, hay bale, altar). **E** facing
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
  `--lootepoch N` pretends N restocks have passed. Francs found go to the cash counter, not a slot
  (the items `cash-account` note). Two players on one container: the `two-players-one-container` note.
