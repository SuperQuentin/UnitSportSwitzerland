# Farming (`src/Farming/`)

Farm fields from the federal land-use data (#494): 4 m cells, server-owned state, drawing, hand
farming; the machines call `FarmWork.Sweep`. Index only: one line per note in
`docs/notes/farming/<name>.md`. Read a note only when the task touches its topic.

- `fields-runtime` — `fields_E_N.fld` through every chunk source, `FieldTile` owner maps near the camera, shown stage = stored else NaturalStage(month), `FarmWork.Sweep`, per-chunk worker-built meshes (lod, rebuild rules, grass not drawn), measured cost
- `field-state-net` — server-owned sparse `FieldCells` in `user://farm/E_N.json`, Subscribe/Work/Cells RPCs, prediction with seq + pending, loose server checks (90 m), snapshots for late joiners, protocol 24, `tools/farmnetcheck.sh`
- `hand-farming` — hoe / seed / fertiliser through `ItemUse.Farm` and a short bar, seed remainder (50 cells an item), harvest with the gather hold, the pack path and when a stroke stops, prompts, the "Wheat — ripe 100%" readout
- `produce-economy` — farm items and values, recipes, the farm co-op shop (rural), `FarmMarket.Deliver` pays full value per unit, the dedicated server plans the co-op, `--farmcoop` stand-in; checks
- `commands` — `--farmmonth`, `--farmdir`, `--farmfresh`, `--farmstats`, `--farmdraw`, `--farmcheck [shots]`, `--handfarmcheck` (the loop through the pack), `--farmperf`, `tools/farmnetcheck.sh`, `FarmTests`
- `machines` — the Fendt tractor (102), Claas combine (103), plough / drill / mower on a rigid `Coupling.ThreePoint` and the tipping trailer (`TrailerCatalog` 6-9): draft on soil only (none on tarmac), stepless ratio counts, the mower out to the right, lowered = kneel bit, tank in flags / trailer code, `StepFarm` sweeps, auger (parked or driven tipper, through the server), tipping the bin, sacks on foot, co-op delivery, `--tractorcheck [slope]` (ridge, paved strip), `tools/tractornetcheck.sh`
- `selling` — `FarmPrices` (season, co-op wish list per farm week, counter / load / stand prices), specialty buyers at real places (sugar factories, mills; LV95 from OSM; office + sign by an access road, `FarmBuyerYards`, nearest three in the co-op panel, `--buyercheck`), the farm stand (`PlacedKind.FarmStand`, `FarmStands` crates and honesty box, passers-by), delivery contracts, controls, `--sellcheck`, `tools/sellnetcheck.sh`
