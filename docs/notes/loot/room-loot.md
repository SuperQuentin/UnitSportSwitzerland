# Room- and kind-aware loot (#165)

- **Pools by room**: `LootTables.RoomContainers[(FurnitureType, RoomType)]` overrides `Containers[type]`
  (a garage/workshop shelf is hardware and parts, a cellar shelf or crate the pantry — tins, bottled water —
  plus salt and firewood, a living-room shelf sweets/gadgets/medical and logs for the stove, an office shelf
  gadgets and wire, a hall shelf coats, road salt and a bike part, a child's-bedroom desk sweets and gadgets,
  a barn crate rope/firewood/apples). Not listed = the old per-type container.
- **The room is derived, never stored**: `InteriorLayout.RoomOf(f)` = the room of the piece's floor holding its
  centre, or a taller room from below reaching into it (`Span`). So no plan field, and `Roll`, `Chance`,
  `ChanceFrancs`, `ContentsOf`, `BuildingChance` all take/compute the same room and cannot drift
  (`--lootchancecheck` has room cases).
- **Bags** (#208): wardrobes and hall/lobby shelves can hold any bag, nightstands and bedroom desks a pouch or a handbag (the items `bags` note).
- **Clothes** (#251): wardrobes roll 1-2 times from `Clothes` 70 (every plain/gothic/kawaii look; tiers
  Basic common, styled uncommon) and `Specials` 1.6 (the finishes, tier rare), plus cloth/medical/scrap/bags.
  Washing machines and dryers give `Laundry` (tees, polos, shorts, socks). About 63% of house wardrobes
  hold something to wear and ~1.5% a special (`--lootchancecheck` cases WhiteTee, BuckleCorset, RainbowTee).
  Category `Clothing` is in no category pool, like bags.
- **Kind extras** (`InteriorGenerator.KindExtras`): pieces a building kind adds after a room's own (shop store:
  rack + crate; works store/workshop: rack + crate; farm store/workshop: bale + crate; office in a
  shop/works/civic: a filing shelf; restaurant kitchen: second fridge + shelf). Placed last, so they only use space left.
- **Budget per kind** (`LootTables.BudgetFactor`): `Abundance` = (6 + 3·floors)·factor / lootable pieces;
  Commercial 1.3, Industrial 1.2, Annex/Garage 0.7. Locked containers are neither counted nor scaled
  (`AbundanceFor` = 1 for them).
- **Plan version 11** (#273): shops get a counter, PAUSA vending machines in lobbies (the loot `shops`, `vending` notes).
- **Plan version 7** (`InteriorLayout.CurrentVersion`): stored plans regenerate, so furniture indexes change and old
  take masks no longer line up — harmless, masks only live one restock period.
- `--lootstats` also prints, per kind, % of buildings with a gun locker / safe and shotguns/shells per hundred.
  At Riddes (2026-10): houses 40% lockers / 15% safes, apartment blocks ~96% (one try per floor), shops 74% safes.
  House targets still pass (minerals 0.6, parts 0.12 — the low ends: they now come from cellars, halls and
  stoves, not every living-room shelf). A running occasion (Halloween) inflates food: read the date.
