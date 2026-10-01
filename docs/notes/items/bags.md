# Bags: more pack slots (#208)

- **Pack size**: hotbar 6 + pack `Inventory.BasePack` 27 (three rows of `PackColumns` 9). A bag worn in the
  **bag slot** adds `ItemDef.PackSlots`: belt pouch +9, handbag +18, backpack +27, hiking backpack +36
  (`MaxPack` 63). `ItemUse.Bag`, `ItemId` 61-64 (53-60 belong to the Battle Royale weapons and flare gun).
- **One array, a moving limit**: `_slots` is always `Size + 1` long (`Size` = 6 + 63 item slots, then
  `BagSlot`); only `Capacity` (= 6 + `PackSize`) slots are in use. Every loop that adds or counts room
  stops at `Capacity`; callers that just scan `inv[i]` for `i < Inventory.Size` still work (the rest is empty).
- **Every bag change goes through `ChangeBag(action)`**: snapshot, apply, `Compact()` stacks beyond the new
  capacity into free/same-kind slots; if one does not fit, restore the snapshot and raise `Refused(why)`
  (the panel toasts it). So a bag never comes off a pack too full to lose its rows, and no stack is ever
  hidden in a locked slot. Ways in: click / right-click the bag slot (take off, or swap with a carried bag),
  shift-click a bag (wears it when none is worn; shift-click the worn one sends it to the first free slot),
  Use / the card's "Wear" (`WearBag`, swaps with the worn one), a number key over the bag slot, drag and drop.
  Non-bags are refused by the bag slot. A carried bag returns to the bag slot on close when none is worn.
- **Saved** like any slot (index `BagSlot` = 69); `FromJson` drops a non-bag found there and compacts.
- **Found** in house loot (`LootTables`, category Gear so only named pools give them): `Bags` pool (all four,
  pouch common .. hiking pack very rare) in wardrobes (10) and hall/lobby shelves (10); `Purses` (pouch,
  handbag) in nightstands (6) and bedroom desks (10).
- `--invcheck` has the bag cases (rows added, compaction, refusal when full, bag-slot-only, save, cursor drop).
