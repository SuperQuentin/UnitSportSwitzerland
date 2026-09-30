# Smart binoculars (`src/Items/SmartBinocularsHud.cs`, `ItemId.SmartBinoculars = 51`)

- An `ItemUse.Optic` item: Aim gives the normal binocular overlay (9 deg), plus the HUD. Found as a very rare
  loot entry (desk 1.5, shelf 1 of pool weight; ~5 per hundred houses), not sold.
- **Target**: `LootTables.Targets()` (every pooled item + francs). **Use (LMB)** while aimed opens the picker:
  type to search, wheel / arrows / D-pad move, Use / Enter picks, Esc / Tab closes. The choice is kept in
  `user://smart_binoculars.cfg`. The picker holds `UiFocus` (typing never walks) and keeps the optic raised
  (`ItemController`: `picking`).
- **Markers**: every door in `DoorIndex` within 400 m and on screen (no occlusion test), at most 14, each a
  boxed % coloured low (red) to high (green) via `BuildingChance`; the one nearest the screen centre also shows
  its three best container types ("fridge x40 34%" = chance some piece of that type has it).
- **Plans are fetched, not assumed**: `InteriorManager.GetOrCreate` (may generate on a worker), at most 3 starts
  per second and 2 in flight, nearest the centre first; "..." until it lands; results cached by door key (cleared
  past 400). Never blocks a frame; nothing runs on the dedicated server (client-only child of `ItemController`).
- **Only odds, never contents**: the math is `LootTables.Chance` (see the `loot` note), no roll or taken mask is read.
- Screenshot: `--ride foot,16,out.png --give SmartBinoculars --hold SmartBinoculars --view first --aim
  --chunks <terrain_chunks> [--smarttarget Bread] [--smartpicker]`. `--give <item>` (dev) puts one in hotbar slot 1.
