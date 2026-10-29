# Inventory

- **Inventory** (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus a 27-slot pack (more with a bag: the `bags` note),
  stacks (`ItemStack(Id, Count, Data)`: `Data` is optional per-instance data, a photo id; stacks
  only merge when `SameKind` = same Id and same Data, in every cursor op, `Add(ItemStack)` and
  `Room(id, data)`), `Changed` — saved by item **name** plus `Data` when set (`ToJson`/`FromJson`; old saves without it load as
  plain stacks; a starter kit when absent).
  Local only, never replicated; what is in the hand is, as `FootPlayer.HeldItemId`, so others see it.
  **One save per server** (#1100): `user://inventory.json` offline, `user://inventory/servers/<host>_<port>.json`
  keyed by the endpoint when `WorldLaunch.Networked` (`Inventory.Load(serverKey)`, called from `ClientWorld._Ready`
  with `Launch.Endpoint`) — the bank account was already per server, by player name, so the pocket items and
  cash backing it are too, instead of one save shared by every server ever joined.
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
  posed from). Controls: **1–6** / **Alt + wheel** / D-pad → select (the plain wheel zooms the camera, #1369),
  **`,`** / the number of the slot in hand / **hold D-pad →** / VR: hold the grip at the right hip → holster and
  draw (`Inventory.Holstered`: the selection stays, `HeldId` reads `None`, so the hand is empty to every user and the
  replicated `HeldItemId` is 0; Use draws and uses at once, Aim draws, any slot pick draws; **auto holster** after
  `ItemController.AutoHolsterSeconds` (8 s) idle, `GameSettings.AutoHolster`, never while aiming, scoped, photo, mid-use,
  a throw, fishing, a phone, in a match, or with an item that works while held: readout, build, gadget, detector, farm tool, traffic tool, placeable, round tool, hammer, rod, smart binoculars).
  The hotbar follows `GameSettings.Hotbar` (Always / Fade: 3 s after a change then a tween fade / Hidden; touch always);
  the item in hand is always in the `HandChip` bottom right (icon, name, slot, holster key; dimmed when holstered),
  **hold X / D-pad ←** **hold X / D-pad ←** radial quick wheel
  (aim with mouse or right stick, release), **I / Tab / Back** inventory (a pad's Back opens it on the release: a hold is the quick menu,
  `ui/quick-menu`; Minecraft-style: see
  the `cursor-inventory` note; money: the `cash-account` note). Something new that does not fit (a developed photo, a flag picked up, `/spawn`, a radio picked up, the hunting-season gun) goes through `ItemController.Give`: the rest is dropped on the ground in front of you instead of lost. Screenshot with `--ride foot,5,out.png` plus
  `--hold <item>`, `--aim`, `--inventory`.
