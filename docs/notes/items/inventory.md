# Inventory

- **Inventory** (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus a 27-slot pack (more with a bag: the `bags` note),
  stacks (`ItemStack(Id, Count, Data)`: `Data` is optional per-instance data, a photo id; stacks
  only merge when `SameKind` = same Id and same Data, in every cursor op, `Add(ItemStack)` and
  `Room(id, data)`), `Changed` — saved to `user://inventory.json` by item **name** plus `Data` when set (`ToJson`/`FromJson`; old saves without it load as
  plain stacks; a starter kit when absent).
  Local only, never replicated; what is in the hand is, as `FootPlayer.HeldItemId`, so others see it.
  `ItemController` (owned by `ClientWorld`, player resolved per frame, falling back to whoever owns
  the current camera so probes work) does the items: binoculars (Aim → 9° FOV, `ScopeView` puts
  third person at the eye), camera (Aim frames, Use saves `user://photos/*.png` after
  `FramePostDraw` and prints a Polaroid: the `polaroid` note), GPS (LV95/altitude/heading readout), Swiss flag (plant on ground flat enough to
  stand, Use on a planted one picks it up; planted flags are server-kept and saved, seen by everyone:
  the `item-net-events` note), energy bar / water (heal). Everything it pushes on the
  player (`FovOverride`, `ScopeView`, `LookScale`) is re-asserted every frame, so dropping Aim or
  mounting needs no special case. **Items work on foot only** — the shoulders they use (RB use,
  LB aim) are trick/boost when mounted. `HeldItemVisual` draws the item: a swaying viewmodel on the
  camera in first person, else on `FootPlayer.HandLocal` (the wrist from the same rig the body is
  posed from). Controls: **1–6** / wheel / D-pad → select, **hold X / D-pad ←** radial quick wheel
  (aim with mouse or right stick, release), **I / Tab / Back** inventory (Minecraft-style: see
  the `cursor-inventory` note; money: the `cash-account` note). Something new that does not fit (a developed photo, a flag picked up, `/spawn`, a radio picked up, the hunting-season gun) goes through `ItemController.Give`: the rest is dropped on the ground in front of you instead of lost. Screenshot with `--ride foot,5,out.png` plus
  `--hold <item>`, `--aim`, `--inventory`.
