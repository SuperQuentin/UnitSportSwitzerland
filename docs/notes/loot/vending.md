# PAUSA vending machines (#273)

- **Brand**: PAUSA, in the Swiss station-machine style (a Selecta-like red cabinet), never the
  Selecta name or logo. `FurnitureType.VendingMachine` (appended), 0.9 × 0.8 × 1.85 m, on a wall.
- **Where** (`InteriorGenerator`, plan v11): the building's own dice (`Fnv.Unit(key|vending)`, so it
  moves nothing else) against `VendingChance`: civic 35 %, shop/office building 20 %, works 15 %;
  added by `Pieces(..., vending)` after a room's own pieces in the first ground-floor lobby or hall
  (works: the workshop or store) with room left. `--shopcheck` measures ~34 / 15 / 14 % on
  synthetic buildings (a full lobby loses some).
- **Look** (`InteriorMeshBuilder.Vending`, vertex-coloured boxes, front +Z): red cabinet, white band
  with PAUSA in red 3×5 block letters, glass over 4 rows × 6 spirals with coloured packets and code
  labels, the keypad column (green LCD, 12 keys, coin slot, change cup), the pickup flap.
- **Contents**: 24 slots A1-D6, each one item from `ShopTables.VendingTable` (ice tea, crisps, gummy
  bears, isotonic drink — `ItemId 160-163`, Consume, 16×16 icons, only here — plus chocolate, energy
  bar, waters), 3-8 of it; seeded per machine and epoch like a shop, with its own mark-up. Cash only.
- **Hors service**: `ShopTables.OutOfOrder` (6 % of machines, for the whole restock period).
- **Buying** (`Loot/VendingUi`): type a code (the pad, or A-D, 1-6, Enter, Backspace), OK pays; one
  item per purchase; it falls into the flap (a short tween). 4 % stick (`StuckChance`, the server's
  roll; `--shopstuck` forces the next one): paid and counted sold, kept by `ShopLedger` (in memory,
  cleared by a restock); "Hit the machine" (or Space) drops it, with 15 % a bonus one if the spiral
  still holds one.
- **Checks**: `ShopTests` (24 slots of 3-8, cash only, exclusives only in machines, stuck + bump),
  `--shopcheck` (placement shares). `tools/shopcheck.sh` tries a machine in the 60 nearest buildings
  that may have one and skips it when there is none (the generated village usually has none), so the
  machine's panel has not been exercised over the network yet.
