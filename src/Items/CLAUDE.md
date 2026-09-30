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
  (aim with mouse or right stick, release), **K / Back** inventory (click to pick up, click to place:
  same item stacks, else swap; right-click uses). Screenshot with `--ride foot,5,out.png` plus
  `--hold <item>`, `--aim`, `--inventory`.
