# Selling farm produce (#494)

Four ways to sell beyond the co-op counter's flat 35 %. All server-authoritative: the server prices
and pays by answering, the client adds the cash to its pocket (as at a shop); offline the client
plays the server through the same methods. Pure rules in plain C# (unit-tested, `FarmSellingTests`):
`FarmPrices`, `FarmBuyers`, `FarmContracts`, `FarmStandRules`. Nodes: `World/FarmSales` (loads,
buyers on foot, contracts) and `World/FarmStands` (stands), created under `Systems.Farming`.

## Prices: one function (`Farming/FarmPrices`)

- **Farm calendar** (`FarmCalendar`): a farm day = 24 min of server time (the default day length,
  whatever the sky clock does), a farm week = 7 of them. Season = the month the fields show
  (`FarmSales.Month`: `FarmField.Month`, else `--farmmonth` / today). Clock: `FarmSales.Now`
  (`ClockSync.ServerUnixNow`; `ClockOverride` for checks).
- **Season**: a harvest in its harvest months (where `FarmTables.NaturalStage` is Ripe; hay June to
  August) × 0.8 (glut); a crop that keeps (all but carrots) in March-May × 1.2; products 1.
- **Wish list**: each co-op (keyed by its building key and the week, like `ShopTables.Markup`) wants
  1-2 harvests a week at +30..60 % (`FarmPrices.Wishes`).
- `Delivery` (a load: full value × season × wish × buyer premium, floored), `Counter` (35 % × the same
  factor), `Stand` (70 % × season, at least 1). `ShopTables.DeliveryPrice(..., item, month, coop, week,
  premium)` and the new `ShopTables.CounterPrice` call them; the old `DeliveryPrice(category, value,
  count)` is the neutral price. `ShopService.SellPriceOf` / `ServeSell` use `CounterPrice` with the
  shop's key; `ServeDeliver` asks `FarmSales.PriceLoad` once it resolved the co-op (or none).
- Shown: the co-op panel's "Buys…" line gets `CoopPanel.MarketLine` (wanted crops, season), each pack
  line a `WANTED +40 %` tag; the machine's delivery prompt (`PlayerFeel` → `FarmSales.DeliveryHint`,
  built only on change) says "DELIVER 40 Wheat to the farm co-op: 1550 CHF  WANTED +55 %".

## Specialty buyers at real places (`Farming/FarmBuyers`)

Pay a premium for one kind of goods; take loads (combine, tipper, tipping, through `ShopService.Deliver`:
`FarmMarket.NearCoop` is true in their yard too) and sacks on foot (E / Y / VR grip at the yard sells
all of the buyer's goods in the pack: `FarmSales.SellHere` → `AskSellHere`, the server reads the
peer's own body position, not the claim). Only goods they buy; others: "takes only sugar beet".

| Buyer | Goods | Premium | LV95 (E / N) | Reach | Source (OSM extract `ressources/data/osm/switzerland-latest.osm.pbf`) |
|---|---|---|---|---|---|
| Schweizer Zucker AG, Aarberg BE | sugar beet | 1.3 | 2587711.0 / 1209764.5 | 150 m | relation 17792349 "Zuckerfabrik Aarberg", centroid 7.276937 E 47.038800 N |
| Schweizer Zucker AG, Frauenfeld TG | sugar beet | 1.3 | 2707961.5 / 1268240.9 | 150 m | way 243688664 "Rübenlager", operator Zuckerfabrik Frauenfeld (the beet yard; the named node 2510630775 is 190 m east) |
| Swissmill, Zürich | wheat, barley, maize | 1.15 | 2682140.2 / 1249358.5 | 60 m | way 554219427, industrial=grinding_mill, operator Coop |
| Emmental-Mühle AG, Schüpbach BE | wheat, barley | 1.1 | 2622853.6 / 1197438.4 | 60 m | way 245720805, industrial=grinding_mill |
| Oleificio SABO, Horn TG | rapeseed, sunflower seeds | 1.2 | 2751595.8 / 1262754.9 | 80 m | node 8107445017 "Oleificio Sabo" |
| Moulin et Huilerie de Sévery VD | rapeseed, sunflower seeds | 1.15 | 2524164.2 / 1157924.2 | 60 m | way 296573291, product=oil |

- Found with GDAL (`ogr2ogr` from QGIS, `GDAL_DATA=<qgis>/apps/gdal/share/gdal`) on the PBF: names
  like `%Zucker%`, `%Mühle%`, `%Moulin%`, tags `industrial=grinding_mill`, `sugar`, `product=oil`.
  WGS84 → LV95 with swisstopo's approximate formulas (~1 m). TLM3D has no factory names, GWR none.
- **Left out** (could not be placed reliably from the data): Groupe Minoteries (no OSM feature),
  Meyerhans Mühlen (an untagged area east of the town it is known for), the many small and
  historic mills, Zuckermühle Rupperswil (a sugar *refinery*, not a beet buyer).

## Farm stand (Hofladen, self-service)

- **Item** `ItemId.FarmStand` (240, value 30, workbench: 8 planks, 10 screws, 1 scrap), `ItemUse.Place`
  → `PlacedKind.FarmStand` (11) through the placeable path (`Placeables`, `FlagGhost`, `ItemController`):
  placed, saved (`user://placed`), sent on join, packed up with Use and an empty hand like the field
  workbench, refused while stock or cash is on it (`FarmStands.RemoveProblem` from `PlacedObjects.ServeRemove`).
- **Site** (`Build.Gadgets.Check` → `FarmStands.SiteProblem`, server): within 60 m of a road (not
  rail), measured on the chunk source's road tiles (the server prefetches the tiles under every
  player every 2 s; a tile not in yet answers "try again"), 20 m from another stand. **No land
  ownership exists in the game**, so "your own land" is not enforced: any outdoor spot by a road.
- **State** (`StandState`, server-owned, `user://farm/stands.json`, offline `stands_offline.json`,
  `--selldir` for checks): 6 crates × 40, cash box, road metres and buildings within 300 m (from the
  map's building tiles, never `DoorIndex`: a dedicated server draws no doors), last tick, takings.
- **Passers-by** (server tick 5 s, `FarmStandRules.Advance`, deterministic: expected sales add up per
  crate, every whole one sold): 6 an hour a crate × road factor (1 within 15 m, 0.4 at 60 m) × town
  factor (1 + buildings/20, max 3) × 2 for cooked dishes; at most 12 h caught up. Price `FarmPrices.Stand`.
  Stockable: harvests but hay, and flour .. raclette.
- **Requests** (`AskOp`: stock, take back, buy, collect → `Answer`): server checks the peer's body is
  within 6.5 m (LV95), the owner (by display name, like placed objects) for stock / take / collect;
  anyone else buys, the money goes into the owner's box. Pack and pocket are the client's: it takes
  the items or pays once the server agreed.
- **Replication**: every change goes to every peer (`State`, with `mine` per peer), and a joining peer
  gets all (`FarmStands.SendTo` in `ServerWorld.OnPeerAccepted`). The visual (`FarmStandNode`): table,
  red roof, six crates with a heap in the item's colour scaled to its count, the red honesty box, a
  "HOFLADEN · self-service" board: remote peers see what is for sale.
- **Panel** (`FarmStandUi`, rebuilt on change): owner — crates with Take back, pack produce with Stock
  (shift: all), box with Collect; others — crates with price and Buy (shift: 5).

## Delivery contracts (`Farming/FarmContracts`, kept by `FarmSales`)

- 3 orders per co-op per farm week (keyed like the wish list): a harvest, a count worth 500-1400 CHF
  at full value (multiple of 5, max 400), × 1.3-1.8, within 2-4 farm days. Accepted from the co-op's
  panel (`CoopPanel.Fill`: Accept, then "Your contracts" with delivered / count and time left);
  server checks the peer stands in that co-op (`SpaceOf`, the plan's `Shop`). At most 3 held.
- Filled by deliveries of that item **to that co-op**: counter sales (`ShopService.ServeSell` →
  `FarmSales.Counted`) and loads (`PriceLoad`). Deliveries are paid as usual; complete, the server
  pays the bonus `(multiplier − 1) × count × value` (`Note` RPC, the client adds the cash). Past the
  deadline (sweep every 2 s) it lapses with a note. Kept per player name in `user://farm/contracts.json`
  (offline `contracts_offline.json`).

## Controls (keyboard / pad / VR)

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| set up / pack up a stand | Use (LMB) with the stand / empty hand | RB | R trigger |
| open a stand, sell sacks at a buyer | E | Y | Y, or reach out and grip (`TryInteract(byHand)`) |
| deliver a load at a co-op or buyer | N | X | the destination dash poke, X |
| panels (Buy, Stock, Accept…) | mouse | as the shop panel | laser + trigger |

Prompts through `InputHints` (outdoor prompt: `InteriorManager.UpdatePrompt` → `FarmSales.PromptFor`);
rows in `Core/ControlsHelp` ("Selling farm produce") and `xr/vr-action-map`.

## Checks

- `tools/test.sh unit` (`FarmSellingTests`: season, wish lists, prices through ShopTables, delivery >
  stand > counter, buyers' places, orders, a contract completed and one lapsed, the stand's sales).
- `--sellcheck [shots] --systems ui,physics,loot,farming --farmmonth 7 --selldir test_output/sellcheck_store`
  (quick, offline, ~40 s): a stand-in co-op (`FarmMarket.StandIn`) and a stand-in buyer
  (`FarmBuyers.StandIn`: Aarberg moved by the spawn), loads, the prompt, contracts, sacks sold with E,
  the stand placed with Use (refused at 500 m from a road via `FarmStands.RoadDistanceOverride`),
  stocked, an hour of sales, the box collected, packed up. `shots` (windowed) writes
  `test_output/494-sell-coop-panel.png`, `494-sell-stand-panel.png`, `494-sell-stand.png`.
- `tools/sellnetcheck.sh` (net, fixture `straight`, the server measures the real road): B buys from
  A's stand, A collects, both see the same 27 left; the server's `stands.json` held the stand.
  Its server runs with `--standsquiet` (no passers-by): one comes due within minutes, and a slow
  run under the load of the net tier saw a sale mid-check and the counts drift. The probe waits for
  the stock answer (crate and pack both) and for the prompt instead of fixed times.

## Not done

- No land ownership: stands go anywhere outdoors by a road. No protocol bump (new nodes and RPCs,
  `PlacedKind` 11: as the rest of #494, bump once when the branch merges).
- Specialty buyers have no building, sign or map marker; their yards are invisible circles.
- A stand's passers-by are simulated only while the server runs (12 h catch-up after a restart).
