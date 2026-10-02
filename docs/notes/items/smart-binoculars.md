# Smart binoculars (`src/Items/SmartBinocularsHud.cs`, `ItemId.SmartBinoculars = 51`)

- Reworked in #165: **no aiming, no zoom, no target picker**. The old optic (9 deg FOV, a % marker over every
  building in view, a searchable target item) was unusable: too much zoom, and no way to tell which marker was
  which building. Now an `ItemUse.Readout` item (held up like the GPS, `ViewPose.Read`) that reads out **one**
  building: the one the player is inside, else the door within `DoorReach` (8 m, `DoorIndex.Nearest`).
- Panel (top right, CanvasLayer 11, under the inventory): building kind, "N containers, M locked", and
  `LootTables.BuildingTable(layout)` — every target item with P(at least one container has it when restocked),
  best first, 12 rows, bar + %. Francs included; gun locker / safe contents included (they are just containers).
- **Plans are fetched, not assumed**: `InteriorManager.GetOrCreate` (may generate on a worker or come from the
  server); "scanning..." until it lands; plans and tables cached by key (tables recomputed after 60 s, as running
  occasions change the odds). Nothing runs on the dedicated server (client-only child of `ItemController`).
- **Only odds, never contents**: no roll or taken mask is read.
- `ItemController`: `_smart.Held` + `_smart.Player` each frame; the GPS screen text / third-person readout are
  gated on `ItemId.Gps`, not on `ItemUse.Readout`, so the binoculars do not show coordinates.
- At a shop's door (#273) the panel reads the shop instead: its type, every catalogue line with its price there and the chance it is in stock (`ShopTables.Chance`), and "a PAUSA machine" when there is one (the loot `shops` note).
- Found as a very rare loot entry (`Optics` pool: desk, some shelves, safes), not sold.
- Checked by `tools/locksynccheck.sh` (client A reads the building at its door; the table must list what its
  locked container gives) — see the loot `locked-containers` note. Screenshot `test_output/locksync_A_scan.png`.
