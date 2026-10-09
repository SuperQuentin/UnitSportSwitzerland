# Crafting core (#271, part 1 of the #270 epic)

- **Recipes are data**: `src/Crafting/Recipes.cs`, plain C# with no Godot (linked into the unit tests,
  with `src/Items/ItemId.cs`, where the `ItemId`/`ItemCategory`/`ItemUse` enums now live for that reason).
  `Recipe(Out, Count, Station, In[], Seconds, Salvage)` plus `Extra` (salvage gives several outputs;
  `Recipes.Outputs(r)` lists them all). Add a recipe = one line in `Recipes.All`, in panel order.
- **Stations** (`[Flags] Station`): `Hands` anywhere; `Workbench` when a `FurnitureType.Workbench` stands
  within 2.2 m on the player's floor, whichever way they face (`CraftStations.At`, through
  `LootService.NearestOf(..., reach, facing: false)`). E on the bench still searches it as loot.
  A placed field workbench within 2.2 m counts too. `Fire` (#272): a burning placed campfire or a
  kitchen stove within 3 m (the `campfire` note). `CraftStations.Where` names what is in reach for the header.
- **Craft** (`Recipes.Craft(store, r, times, here)`): counts first, takes all the inputs, then gives the
  outputs; the game's `InventoryStore` gives through `ItemController.Give`, so a full pack drops the
  rest at your feet instead of losing it. Only **plain stacks** count and are taken
  (`Inventory.CountPlain`/`TakePlain`: no `Data`, so a photo is never an ingredient; bag and worn
  slots excluded), from the end of the pack first so the hotbar keeps what is in reach.
- **Local only**, like the inventory: nothing is sent to the server.
- **Never crafted** (`Recipes.NeverCrafted`): guns, ammunition (shells included), vest, flare gun,
  camera, hiking pack, money, photos, seasonal treats and hats, the Swiss army knife and the PAUSA snacks (#273); clothes and cosmetics by category
  (checked in `--invcheck`). They are found, or bought in shops (#273).
- **Salvage** (workbench) takes one part apart; it always gives back less CHF value than the part
  (`--invcheck` compares `ItemDef.Value`s).
- **UI**: third column of the inventory panel (`InventoryUi.Crafting.cs`), 250 px so the panel still
  fits 1152 px. Rows by section (Hands, Workbench, Fire, Salvage), have/need per ingredient in red when
  short, greyed rows out of reach with the station named. Make = one batch after `Seconds`, shift-click
  = as many as the ingredients allow; closing the panel stops it.
- **Checks**: `tools/test.sh unit` (`CraftingTests`: well-formed, never-crafted, atomic, full pack
  drops, salvage outputs), `--invcheck` (real values/categories, real `Inventory`, photo untouched).
  Screenshot: `--systems ui,physics --ride foot,2,test_output/craft_panel.png --inventory`
  (`--world flat` builds no UI).
- **Hidden rows** (#493): `Recipe.OnlyWhenHeld` shows a row only while its first ingredient is in the pack: one cook row per fish species (the `items/fishing` note) without burying the Fire section; its `Key` adds that ingredient.
- **Farm recipes** (#494): seeds from harvests by hand, milling at the workbench, farm dishes at a fire (bread and popcorn make several); fertiliser is never crafted. See `farming/produce-economy`.
