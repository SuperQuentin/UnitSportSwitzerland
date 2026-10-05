# Farming by hand (#494)

- **Items** (`ItemUse.Farm`, dispatched in `ItemController.UseSlot` to `Farming/HandFarming.Use`):
  the **hoe** ploughs (tills) the cell 1.8 m ahead, a **seed** (`FarmTables.CropOf`) sows a ploughed
  cell, **fertiliser** feeds the sown cells of the 3x3 round it (one item a use). A short bar
  (plough 1.2 s, sow 0.6 s, fertilise 0.9 s; walking 0.9 m away cancels), then one
  `FarmField.SweepLv95` stroke, so hand work takes the same prediction and server path as the
  machines. One seed item covers `FarmTables.CellsPerSeed` (50) cells: the remainder is kept per
  crop for the session, and the next seed item opens when it runs out.
- **Harvest**: hold Gather (G / pad X / VR X) on a ripe cell: `Loot/Gathering` offers "Hold to
  harvest wheat" (`Resource.Crop`, a 1.4 s bar), then `HandFarming.HarvestAt` harvests the cell and
  gives `FarmRules.HandYield` items of `FarmTables.YieldOf` (the machine's yield doubled, at least
  `HandYieldMin`: grain 1, potatoes and vegetables 2, beet 3) through `ItemController.Give`
  (dropped at the feet when the pack is full). Grass is not harvested by hand (that is the mower's).
- **Prompts** (`InputHints`): "[LMB] Till the soil" / "Sow wheat" / "Spread fertiliser" while a farm
  item is held over a cell it can work; Gathering's own line for the harvest. Devices: `use_item`
  and `gather`, no new action (rows in `xr/vr-action-map`).
- **Readout**: on foot, the cell ahead shows "Wheat — ripe 100%", "Potatoes — stubble",
  "Meadow — mown 40%" (`HandFarming.Describe`) on a HUD line above the prompts; the text is set
  only when it changes.
- **Not yet**: the item definitions, icons and shop rows of the farm items are another part of #494;
  until they land `ItemDefs.Get(ItemId.Hoe)` is null, so the pack path (`UseSlot`) cannot start a
  stroke, and `--farmcheck` drives `HandFarming.Use(player, -1, item)` (no pack) directly.
