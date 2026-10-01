# The panel works like Minecraft's

- **The panel works like Minecraft's** (issue #32). A stack taken out of a slot rides on the cursor
  (`Inventory.Carried`, saved with the rest and returned to the slots on close) and every gesture is
  one `Inventory` operation: `PrimaryClick` (LMB / pad A: pick up, put down, merge with the rest left
  carried, swap), `SecondaryClick` (RMB / pad X: take half rounded up, put one down), `QuickMove`
  (shift-click / pad Y: hotbar <-> pack), `Collect` (double-click), `SwapWithHotbar` (1–6 over a slot),
  `Distribute` (drag with a stack on the cursor: evenly with LMB, one each with RMB — the panel
  restores the `Snapshot` taken at the press before every redistribution, so a slot crossed twice is
  not counted twice), `Trash`/`Untrash` (the bin keeps the last stack). A press on a slot with an
  empty cursor picks up, and releasing over another slot drops there: that is the drag and drop.
  Middle-click uses an item. **The mouse is hit-tested by `InventoryUi._Input`, not by the slot
  buttons**: Godot keeps sending a press's events to the control that took it, so a slot never hears
  the pointer arrive during a drag that began elsewhere; the buttons keep focus for the pad.
  `Batch()` folds a drag's many changes into one `Changed` and one save.
- **Ground** (#208): a click outside the panel with a stack on the cursor drops it (right click: one), and so does
  releasing a dragged stack off the panel; Q over a slot drops one, Ctrl+Q the stack; the card's Drop button does
  it on a pad. `Inventory.TakeCarried(one)` takes it off the cursor, `ItemController.DropStack` puts it in the world;
  if that fails it goes back in the pack. Closing with a stack that no longer fits drops it too
  (`ReturnCarried` returns the leftover; `Bin` keeps it if it cannot be dropped).
- **Look** (#208): the menus' glass panel (`UiTheme.Get()` on the panel root, `UiKit` cards), rounded slot tiles
  drawn by `SlotDrawing` (amber = selected / carried, red = a full bin), pack 9 columns showing only the rows in use;
  slots shrink to fit the screen height when a big bag adds rows. Right column: bag slot + bin, item card
  (icon, kind, blurb, Use/Wear, In hand, Drop), money card, photo album.
