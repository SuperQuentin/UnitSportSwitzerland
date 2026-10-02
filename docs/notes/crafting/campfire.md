# Campfire, field workbench, torch and cooking (#272, part 2 of the #270 epic)

- **Fire station** (`Station.Fire = 4`, `src/Crafting/Recipes.cs`): within `CraftStations.FireReach` (3 m)
  of a **burning placed campfire** or an interior **`FurnitureType.Stove`** (`LootService.NearestOf(...,
  facing: false)`). `CraftStations.Where(p)` gives the stations and the header label ("At a campfire, a
  field workbench"); the panel polls it once a second while open, so walking up to a fire lights the rows.
- **Cooking** (`Cook(...)` rows, panel section "Fire"): fondue (2 Gruyère + bread + mineral water, heals
  90), hot chocolate (chocolate + mineral water), toasted bread (bread), caramel apple (apple + 2 candy,
  no longer in `NeverCrafted`), mineral water from a water bottle (boiled). Every cooked thing is worth
  at least its ingredients (`--invcheck`; the caramel apple went from 2 to 4 CHF for it).
- **New items** (`ItemId` 151-156; 150 is the hammer's, #274): `Fondue`, `HotChocolate`, `ToastedBread`
  (Consume), `Campfire` and `FieldWorkbench` (`ItemUse.Place`), `Torch` (`ItemUse.Readout`: held up,
  Use does nothing). Drawn icons in `ItemIcons`. Recipes: campfire = 5 firewood + 4 stone, torch =
  firewood + cloth + coal (by hand); field workbench = 6 planks + 10 screws + 2 scrap (workbench).
- **Placing** (`src/Items/Placeables.cs`): one row per placeable item (flag, campfire, field workbench):
  `PlacedKind`, ghost mesh, verb, flatness needed (flag 0.6, others 0.85), lifted-and-stabbed or set
  down. `FlagGhost` (name kept) draws the ghost for whichever is held, and `ItemController.PlaceOrPickUp`
  places it through `PlacedObjects.RequestPlace` (the item leaves the pack at once, comes back on a
  refusal). Holding one and aiming at a placed one of its kind takes it back.
- **Taking back with an empty hand** (`Placeables.TakenByHand`): pointing at a campfire or a field
  workbench, Use puts the fire out / clears the ashes / packs the bench up (`ItemController.TakeBack`).
  A fire is spent (no refund); the bench and a flag come back (`Placeables.Refund`).
- **Placed kinds** (`PlacedKind.Campfire = 3`, `FieldWorkbench = 4`): a campfire's payload is the Unix time
  it was lit, **set by the server** (`CampfireClock.Lit`, the client's payload is ignored), so it burns
  `CampfireClock.BurnSeconds` (20 min) across restarts and peers agree to within clock drift. Removal:
  owner always; anyone once burnt out (`PlacedObjects.AnyoneMayRemove`); the bench owner only.
- **Visuals** (`src/Crafting/StationVisuals.cs`, the `PlacedObjects` factories): stone ring + log teepee,
  a flickering `OmniLight3D` named `FireLight`, flame and smoke `CpuParticles3D` while burning, then an
  ashes mesh (`CampfireNode` checks the clock once a second); a low cylinder collider to aim at. The
  bench: a `MeshScratch` bench on a box collider.
- **Torch light**: `HeldItemVisual.StepTorch` puts an `OmniLight3D` named `TorchLight` at the flame of
  whatever copy of a player holds a torch. It follows the replicated `HeldItemId`: remote peers see it
  with no new state.
- **Checks**: `tools/test.sh unit` (`CraftingTests`: fire station, the recipes, `CampfireClock`),
  `--invcheck` (cooked values, placeables), `--campfirecheck --systems ui,physics` (offline: craft, lay
  the fire through the real path, light present, cook at it, torch light, flag still plants and comes
  up, field workbench set up and packed up with an empty hand, fire put out), `tools/campfirecheck.sh`
  (tier 2: B gets A's fire from the join snapshot, burning, sees A's torch, is refused putting it out,
  sees it go). The net check writes the real `user://placed/server.json` (it ends as it started).
- **Trap**: `PlacedObjects` loads only kinds it knows and rewrites the file on every change, so an
  offline run on a branch without another branch's kinds drops those entries from `offline.json`.
