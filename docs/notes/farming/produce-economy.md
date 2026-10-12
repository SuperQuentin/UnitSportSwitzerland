# Farm produce economy (#494)

- **Items** (`Items/Item.cs`, ids 300-340 (200-223 are fish and toys from #493 / #501), drawn icons in `ItemIcons`): `Value` is the producer price of one.
  Seeds (Produce, `ItemUse.Farm`, stack 20): wheat 14, barley 12, maize 12, seed potatoes 12, rapeseed 14,
  sunflower 13, sugar beet 6, vegetables 10, peas 14. Harvests (Produce, Material): wheat 25 (a 50 kg sack,
  ~0.5 CHF/kg), barley 22, maize 22, potatoes 5 (10 kg), rapeseed 42, sunflower seeds 38, sugar beet 3, carrots 6,
  hay bale 40 (stack 3), peas 28. Milled (Produce): flour 7, rapeseed oil 7, sugar 3, maize meal 8. Dishes (Food,
  Consume): baked potato 6/heal 25, rösti 20/60, polenta 11/45, popcorn 8/15, vegetable soup 14/55, raclette 22/75.
  Hoe (Gear, `Farm`, 25 CHF), Fertiliser (Produce, `Farm`, 12 CHF, stack 20). One seed sows 50 cells (~800 m²).
  No carried weight exists in the item model, so sacks have none.
- **Recipes** (`Crafting/Recipes.cs`; every farm recipe is worth at least its ingredients, `--invcheck`): by hand,
  a seed kept from a harvest (1 wheat -> 2 seeds, 2 potatoes -> 1 seed potato, 1 rapeseed -> 3...); workbench:
  wheat -> 4 flour, maize -> 3 meal, rapeseed -> 6 oil, beet -> 1 sugar, 2 scrap + plank -> hoe; fire: 1 flour +
  water bottle -> 3 bread, baked potato, rösti (2 potatoes + oil), polenta (meal + water), popcorn (maize + oil
  -> 4), vegetable soup, raclette (cheese + 2 potatoes). Fertiliser is shop-only (`NeverCrafted`, `ShopOnly`).
- **Farm co-op** (`ShopType.FarmCoop = 12` (11 is IKEA, #501), name "Farm co-op", olive sign): rural tiles only (`ShopTables.RuralOnly`,
  like the gun shop): a `Commercial` building 8/107 of the rural weights, or an `Agricultural` building of at least
  120 m² 12 % of the time (GWR agricultural buildings already reach runtime as `BuildingKind.Agricultural`). Adding
  it to the rural weights re-draws the shop types of rural tiles. Sells the nine seeds, hoe, fertiliser, rope and a
  fuel can (cash under 20 CHF, card too above, price = value x 1.1-1.4); buys every Produce item at the usual 35 %.
  Groceries now also stock potatoes, carrots and flour. Smart binoculars list its lines like any shop.
- **Delivery** (`Farming/FarmMarket`): `NearCoop(at)` = a co-op door within `DeliverReach` (25 m) in `DoorIndex`
  (the door entry carries its `ShopType`), cached 0.5 s / 4 m. `Deliver` pays `ShopTables.DeliveryPrice` = the
  **full value per unit** (floored for the load) because it is a weighed delivery, not the counter's 35 %; only
  harvests are taken (`FarmTables.IsHarvest`; seeds, fertiliser and flour are not, on the client and the server). `ShopService.Deliver` asks the server (`RequestDeliver`, with the co-op door key the client sees); the server ignores the claimed
  position online and uses the peer's replicated player node (reach + 12 m slack), then answers `Delivered`; the
  client adds `Francs` to its pocket on the answer and calls `done(total)`. Offline the same methods run locally.
  Not in the shop ledger (nothing is stocked).
- **A dedicated server draws no buildings** (`ChunkManager.BuildMeshes` off), so its `DoorIndex` is empty: it
  used to refuse every delivery online. Now a door in its own index (offline, a listen host, a check's stand-in)
  serves, else the server plans the building the client named (`InteriorManager.GetOrCreate`) and pays only if its
  layout is a `FarmCoop` and the peer is within reach of its outside door (`OutsideDoorAt`).
- **Stand-in co-op for checks** (fixture worlds have no buildings): `FarmMarket.StandIn(door, outward)` files a
  co-op door under the made-up tile `StandInTile` (-1, -1); `--farmcoop E,N` (LV95, fixture worlds only) puts one
  there, facing south (its yard), on the server and every client (`StandInFromArgs`, from `ClientWorld`/`ServerWorld`).
- **Delivering a tipping trailer** is tipping it (`FootPlayer.Tip`, `machines`): the bin goes up and the load is sold.
- **Market prices and more buyers** (`selling`): the co-op's counter and loads now follow `FarmPrices` (season:
  harvest glut × 0.8, stored crops in spring × 1.2; the co-op's 1-2 wanted crops of the farm week +30..60 %);
  `ShopTables.DeliveryPrice(category, value, count)` is the neutral full value, the market overload and
  `ShopTables.CounterPrice` carry month, co-op and week. Specialty buyers (sugar factories, mills) take loads and
  sacks for a premium; a farm stand (item 340) sells to passers-by; co-ops post delivery contracts.
- **Loot**: seeds, fertiliser and a rare hoe in barn crates/shelves and a little in garage shelves (`FarmKit`);
  potatoes, carrots, flour in cellar and pantry shelves (`Roots`). This shifts those containers' rolls.
- **Checks**: `tools/test.sh unit` (`FarmEconomyTests`), `--invcheck` (items, icons, save by name, recipe values),
  `--shopcheck` (barns become co-ops with counters, rural only, delivery beats the counter), `--iconsheet`;
  `--tractorcheck` (offline: refusals far away / seed / flour, the tipper and the combine paid units × value at a
  stand-in) and `tools/tractornetcheck.sh` (online: the server pays a tipped trailer, refuses a claimed position
  and seed). `CHUNKS=<terrain_chunks> tools/coopnetcheck.sh [E,N]` (real map, by hand, ~2 min, not in
  `checkmap.txt`): the dedicated server's planned-co-op path. One client finds the nearest co-op among its
  loaded doors (default spot 2585000,1208000 by Aarberg: 21 co-ops in 5451 doors, the nearest
  2584_1208_189 at 374 m), delivers 40 wheat from a combine 12 m out in its yard (the server plans
  the building and pays 1000 CHF naming it), and is refused the co-op's key from 200 m off and a plain
  building's key at its own door. Passed Oct 2026.
