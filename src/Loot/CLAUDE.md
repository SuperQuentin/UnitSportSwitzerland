# Loot and gathering (`src/Loot/`)

Lootable furniture and outdoor gathering.

Index only: one line per note in `docs/notes/loot/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/loot`.

## Architecture

- `loot` — Loot: (`src/Loot/`): lootable furniture in every generated interior (fridge, wardrobe, nightstand, desk, shelf,...
- `gathering` — Gathering: (`src/Loot/Gathering.cs`, hold G / pad X on foot outdoors — pad X is only tuck/sprint when mounted): a...
- `room-loot` — Pools by room (a garage shelf is tools, a cellar shelf supplies), kind extras, budget per kind, plan v7 (#165); wardrobes give clothes, rare specials (#251)
- `br/loot` (in docs/notes/br/) — a Battle Royale match overrides the epoch and the tables of the buildings in its region (LootTables.MatchEpoch, MatchLoot), masks in memory only; crates reuse the loot panel (LootService.OpenCrate)
- `vehicles/pallets` (in docs/notes/vehicles/) — a pallet a forklift has moved is no container any more (`LootService.Moved`, #583)
- `locked-containers` — Gun lockers and safes: dial mini-game, unlock bit in the take mask, server checks the combination, door replicated; `tools/locksynccheck.sh`
- `banks` — Banks (#213): IsBank pick + door sign, teller desk is the only place to deposit/withdraw (server checks InBank), vault safes = dial + Simon (length by value); `tools/bankcheck.sh`
- `shops` — Shops (#273): type per Commercial building by hash (gun shop rural), garages, door signs, stock seeded per epoch + exact Chance, server keeps sold counts (`ShopLedger`), price = value × 1.1-1.4, sell 35 %, cash/card rule (card = bank account, server debits), shotgun card-only in season, ShopUi; `--shopcheck`, `tools/shopcheck.sh`
- `vending` — PAUSA vending machines (#273): civic/office/works lobbies by seed, the red cabinet mesh, A1-D6 slots of the vending table (4 exclusives), hors service, stuck + hit, VendingUi
- `two-players-one-container` — Two players, one container: server grants each stack once and pushes `Taken` to others inside...

## Gotchas

- `offline-take-all-needs-sync-layout` — Offline "take all" needs `InteriorManager.GetOrCreate` to answer synchronously on a known plan
