# Inventory and items (`src/Items/`)

Inventory data, held items and the item controller.

## Architecture

- **Inventory** (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus an 18-slot pack,
  stacks, `Changed` — saved to `user://inventory.json` by item **name** (a starter kit when absent).
  Local only, never replicated; what is in the hand is, as `FootPlayer.HeldItemId`, so others see it.
  `ItemController` (owned by `ClientWorld`, player resolved per frame, falling back to whoever owns
  the current camera so probes work) does the items: binoculars (Aim → 9° FOV, `ScopeView` puts
  third person at the eye), camera (Aim frames, Use saves `user://photos/*.png` after
  `FramePostDraw`), GPS (LV95/altitude/heading readout), Swiss flag (plant on ground flat enough to
  stand, Use on a planted one picks it up), energy bar / water (heal). Everything it pushes on the
  player (`FovOverride`, `ScopeView`, `LookScale`) is re-asserted every frame, so dropping Aim or
  mounting needs no special case. **Items work on foot only** — the shoulders they use (RB use,
  LB aim) are trick/boost when mounted. `HeldItemVisual` draws the item: a swaying viewmodel on the
  camera in first person, else on `FootPlayer.HandLocal` (the wrist from the same rig the body is
  posed from). Controls: **1–6** / wheel / D-pad → select, **hold X / D-pad ←** radial quick wheel
  (aim with mouse or right stick, release), **I / Tab / Back** inventory. Screenshot with
  `--ride foot,5,out.png` plus `--hold <item>`, `--aim`, `--inventory`.
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
- **Money is a counter, not an item** (issue #32): francs added go to `Inventory.Cash` (shown by the
  hotbar and in the panel; old saves with francs in a slot are migrated on load) and are **lost when
  knocked out** (`ItemController`, on `FootPlayer.KnockedOut`'s rising edge). **Claim** moves them to
  the account kept by `Items/Bank` at `World/Bank`: online on the server per player name in
  `user://bank/accounts.json` (the balance is sent once `ChatManager.NameAssigned` fires, because the
  name is the key and is not known on connect), offline in `user://account.json`. Cash leaves the
  pocket only when the server answers. The server cannot verify the amount — the inventory is the
  client's — and a name is not a password.
- Checks: `<godot> --headless --path . -- --invcheck` (every cursor operation on scratch inventories,
  never the save); `<godot> --path . -- --ride foot,60 --invuicheck` (synthetic mouse events at the
  real slot rectangles: drag and drop, click-carry, spread, shift-click, right-click);
  `--connect <host> --econcheck <admin password>` against a server started with that password
  (non-admin vehicle refused, `/login` flips the flag, admin vehicle spawned, cash claimed to the
  server account). `--invuicheck`/`--econcheck` use a scratch inventory (`Inventory.Scratch`,
  `Persist = false`); `--econcheck` leaves its test deposit in the server's accounts file.
