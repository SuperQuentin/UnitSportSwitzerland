# Barracks items and the recruit's uniform (#716)

Items for a night in the barracks and the recruit's kit (the Drognens music clip, and play).

- **Items** (`ItemId` 350-353, append only): `PlayingCards` (a Jass deck, held as a fanned hand of five),
  `PokerChips` (two stacks of coloured chips), `BeerBottle` (33 cl, brown, label band), `Gamelle`
  (the army's three-part mess tin, olive aluminium, carried by its wire bail). Cards and chips are
  `ItemUse.Material`, category Cosmetic: they do nothing, but throw like any material. The Gamelle is
  Material, Gear. The beer is `ItemUse.Consume`, category Water, Heal 10, stack 6.
- **Held meshes**: `Items/BarracksMeshes.cs`, called from the `ItemDefs.HandMesh` switch. Real sizes, vertex
  colours, no texture. `MeshScratch.Build` turns a mesh half a turn about Y, so the holder (at +Z) sees what
  is authored at -Z and his right is authored -X: the cards are laid out in the holder's terms and put
  through that turn in `AppendCards` (faces to the holder, backs to everyone else). Icons: the four
  grids at the end of `ItemIcons.BuildGrids`, with three olive palette chars `m M h`.
- **Drinking**: Consume + `ItemAction == 2` is the `ItemArmPose.Mouth` pose on every peer (`FootPlayer.TargetArmPose`),
  so the beer needs nothing of its own there. In first person the bottle tips like the water bottle
  (`HeldItemVisual.PoseTransform`, `ViewPose.Mouth` for `WaterBottle or BeerBottle`).
- **Garments** (`Avatar.Garments`, append only, codes per slot): `TazJacket` = 354 (Top 23, new shape
  `GarmentShape.FieldJacket`: stand collar, zip, breast pockets with flaps, hem band, cuffs),
  `TazTrousers` = 355 (Bottom 11, `Cargo` with its thigh pockets), `ArmyTee` = 356 (Top 24, plain olive
  `TShirt`). Style Basic: common in wardrobes, sold at the sport shop and the boutique.
- **TAZ 90 is real camouflage**: `Finish.Camo = 15`, drawn by `shaders/body/avatar.gdshaderinc` on colour A
  like the other patterns (checker, stripes, studs): 3-D value noise in the figure's own space, light
  green (A, `Garments.TazGreen`), dark green, brown and black blotches with hard edges, so it moves with
  the figure and wraps every limb with no seam. The icon (`ClothingIcons.CamoPixel`) is the same colours
  by fixed pixels. It is a pattern, not a special: B and C stay plain (`Cols.Of`).
- **Where they are**: shops (`ShopTables`, lines appended so sold-count slots do not shift): beer at grocery
  and kiosk; cards and chips at the kiosk; Gamelle and the three garments at the sport shop (army
  surplus); the garments also at the boutique. Not in the PAUSA machine (`VendingTable`: its picks hang on
  the table's total weight, and a machine selling beer is not Swiss). Loot (`LootTables`): pools `Beer`
  (fridge, break-room fridge, carnotzet shelf), `Games` (nightstand, toy box, locker, carnotzet and
  break-room shelves), `MessKit` (locker, shelter shelf). Beer is kept out of the `Water` pool (a tank or a
  barrel does not hold beer). The garments are in the wardrobe pools by their style.
- **Viewer**: `--models,<dir> --modelsonly Beer` (every `ItemId` shows by itself; the viewer lifts a held mesh
  onto its floor, so a hanging Gamelle is not under it); `--modelsonly TAZ` and every other look: the
  `[Showcase("Clothes")]` set in `HumanMeshBuilder.Showcase.cs` (one figure per garment). Whole outfits:
  `--avatars 1.5 out.png --outfits 0 --focus 15 --count 2 --view 180` (the two recruits).
- **Remote check**: `tools/useanimcheck.sh` (full tier) has A drink a beer and B must see `ItemAction 2` / Mouth with a `BeerBottle` held;
  B polls the live remote body of A (`Other()`), because A's body is replaced on B when it leaves and re-enters its view.
- **Tests**: `BarracksTests` (pinned numbers, where it is sold, appended slots); `--outfitcheck` builds the
  garments in every pose and reads the Camo finish back; `--iconsheet`, `--shopcheck`, `--lootchancecheck`.
