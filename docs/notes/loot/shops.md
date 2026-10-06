# Shops and card payment (#273, crafting part 3 of #270)

- **Which buildings**: `ShopTables.TypeFor(key, kind, area, bank, rural)`, a pure function of the
  tile like `IsBank`: every `Commercial` building that is not a bank is a shop, its type drawn from
  the key's hash (grocery 35, hardware 15, kiosk 15, pharmacy 10, sport 8, boutique 8, electronics 6,
  gun shop 3 only on a rural tile: `IsRural` = fewer than 250 buildings in its `.bldg`). A `Garage`
  or `Annex` of at least 30 m² is a mechanic's one time in four (`Garage`: sells and buys vehicle
  parts). `BuildingFootprint.ShopOf` gives it to the door (`DoorSpot.Shop`) and to the plan
  (`InteriorLayout.Shop`, set by `InteriorGenerator.Generate(fp, b, rural)`).
- **Counter**: a shop room already has a `ShopCounter`; `InteriorGenerator.ShopCounter` guarantees
  one on the ground floor (a garage has none of its own), like the bank's `Counter`. In a shop the
  counter is the shop: `LootService.NearestContainer` skips it, `LootTables.BuildingChance` too, so
  its till is no longer searched. Plan version 11.
- **Sign**: `Interiors/BankSigns` draws (to 200 m, #553) a coloured plate with the shop's name over every shop door.
- **Stock is computed, never stored**: `ShopTables.Stock(key, furniture, epoch, type, value, season)`,
  seeded like loot (`LootTables.Epoch`: 24 h, staggered per building, `--lootepoch`). One slot per
  catalogue line (`ShopLine(Id, Chance, Min, Max)`), stock 0 when not carried that period; every line
  draws the same numbers either way, so slots never move. `ShopTables.Chance` is its exact closed form.
- **Prices**: `ItemDef.Value` × a per-building mark-up 1.1-1.4 (`Markup(key)`), whole francs, at least 1.
  Camera (160), GPS (120) and binoculars (60) got values for this. Selling back: 35 % of the value,
  rounded down (`SellPrice`), only the categories the shop buys (`Buys`: grocery/kiosk food and
  water, pharmacy medical, hardware scrap and minerals, sport/gun/electronics gear, boutique
  clothing and cosmetics, garage parts). `--shopcheck` checks nothing sells back above its price.
- **Payment** (`PaymentFor`): machines, kiosks, groceries and anything under 20 CHF: cash only; from
  20 CHF the other shops take the card too; the **shotgun is card only**, and only in the hunting
  season (`ShopService.HuntingSeasonIn`: months with at least half the game birds open = September
  to January; `--birdmonth N`). The card is the bank account (`Items/Bank`): the **server** debits it
  (`Bank.Charge`, no overdraft) and answers the new balance (`Bank.Report`).
- **Shop-only** (`ShopTables.ShopOnly`, all in `Recipes.NeverCrafted`): camera (electronics), shotgun
  and shells (gun shop; both still in gun lockers), hiking pack (sport; still a very rare loot bag),
  the **Swiss army knife** (`ItemId 164`: carried anywhere in the pack, a chopped tree gives one more
  log, `Loot/Gathering`), the boutique's rare clothes (the finishes; `--shopcheck` keeps
  `SpecialClothes` equal to `Garments.IsSpecial`). Blank CDs were left out: burning a CD downloads a
  link, and gating it on an item would change the radio's flow and `tools/radiocheck.sh`.
- **Server** (`Loot/ShopService`, `World/Shops`): keeps only sold counts per slot
  (`ShopLedger`, `user://shops/E_N.json` as `[epoch, plan version, sold...]`; another epoch = a
  restock, another plan version = ignored). Before a sale it checks the peer stands in that building
  (`InteriorManager.SpaceOf`, like the bank's `InBank`), the slot's stock, the payment rule and, on
  the card, the balance. **Cash is the client's**: the server cannot see the pocket (as with a bank
  deposit), so the client checks it and pays only when the server answers; selling back likewise
  trusts the client's pack. A sale pushes the new counts to everyone else inside (`SoldCounts`).
  Offline the client plays the server through the same methods; the card is this machine's account.
- **UI** (`Loot/ShopUi`, UiTheme glass): E at the counter; catalogue (what is left, price, Cash /
  Card buttons; shift-click 5) next to the pack lines the shop buys (Sell, shift = all, or drag a
  line onto the catalogue). E, Tab or Esc closes; walking away too.
- **Smart binoculars** at a shop's door: the shop's type, every line with its price there and its
  chance in stock (instead of the loot table), and "a PAUSA machine" when the building has one.
- **Generated world**: a third of the houses in a village's middle are `Commercial` (by a position
  hash, so nothing else moves): village shops (and some banks) without map data.
- **Checks**: `tools/test.sh unit` (`ShopTests`: prices, payment, stock determinism, exact chance vs
  sampling, never crafted, vending exclusives, type weights, ledger: sell out, restock, card decline,
  stuck + bump); `--shopcheck` (quick, no world: real item rows, clothes list, synthetic buildings
  through the generator: counters, garages, machine shares); `tools/shopcheck.sh` (net: server on
  `--generated-world --admin-password shopcheck --shopstuck`, `--shopnet A|B`: A buys a slot empty
  for cash, one line on the card, the server's balance drops, sells back; B walks in after and sees
  the slot sold out; saves in `test_output/shopcheck_appdata`, never the real ones).
- **Fishing** (#493): sport shops sell the rod, spinners and dough bait, groceries perch and whitefish; both lines appended at the end of their catalogues (slot indices are the ledger's keys). Fish sell back as Food (the `items/fishing` note).
