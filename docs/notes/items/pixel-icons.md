# Pixel icons (`src/Items/ItemIcons.cs`)

- `ItemIcons.Get(ItemId)` -> cached 16x16 `Texture2D` (null only for `None`). `SlotDrawing.DrawSlot`
  draws it with nearest filtering at an integer scale; hotbar, pack, carried stack and wheel share it.
- **Add an icon**: a `string[]` in `BuildGrids()`: one char per pixel, `.` transparent, 16 chars per row,
  up to 16 rows (the drawing is auto-centred). Palette chars (light from the top left, 1 px `k` outline):
  `k` outline; `w a g G d` white/light/mid/dark grey/near-black; `r R q` red/dark/light;
  `o O y Y l` orange/dark/yellow/dark/pale; `n N t T` brown/dark/tan/light tan; `b B c C` blue/dark/cyan/pale;
  `e E u` green/dark/light; `p P v s` purple/dark/light/pink.
  `Map(grid, from, to)` recolours a grid, `Mirror` doubles a half, `Round(p => ...)` paints discs with auto outline.
- **Fallback**: an item with no grid gets `Generic(def.Tint, def.Glyph, ShapeFor(def))`: a silhouette
  (`IconShape`: Box, Can, Bottle, Pouch, Scrap, Tool; chosen by category/use) shaded from the tint, plus the
  glyph in a built-in 3x5 font (A-Z, 0-9, `+`, `-`). `ItemIcons.Generic(tint, glyph, shape)` is public for map
  markers or loot UI.
- **Held mesh**: `ItemDefs.HandMesh` default case builds a thin card from the icon pixels (one vertex-coloured
  box per horizontal run), because the hand material ignores textures.
- **Check**: `<godot> --headless --path . -- --iconsheet` writes `test_output/iconsheet.png` (authored left,
  generated fallback right, enum order) and exits 1 on a malformed grid or an item without an authored icon.
