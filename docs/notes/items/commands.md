# Commands

- `<godot> --headless --path . -- --invcheck`: every cursor operation on scratch inventories, never
  the save.
- `<godot> --path . -- --ride foot,60 --invuicheck`: synthetic mouse events at the real slot
  rectangles (drag and drop, click-carry, spread, shift-click, right-click).
- `--connect <host> --econcheck <admin password>` against a server started with that password:
  non-admin vehicle refused, `/login` flips the flag, admin vehicle spawned, cash
  deposit refused outside a bank (#213); the counter itself: `tools/bankcheck.sh` (loot `banks` note).
- `--invuicheck`/`--econcheck` use a scratch inventory (`Inventory.Scratch`, `Persist = false`).
- `GODOT=<exe> tools/useanimcheck.sh`: loopback server + A (first person: drink, eat, GPS, hat, clothes) + B (remote: sees Mouth arm pose, hat, OutfitBits); screenshots in `test_output/useanim_*.png`.
- `<godot> --headless --path . -- --iconsheet`: renders every item icon to `test_output/iconsheet.png` (see `pixel-icons`).
- `tools/radiocheck.sh` (`CHUNKS=<terrain_chunks dir>` from a worktree): dedicated server with `--cdfixture <wav>` (repeatable),
  `--radiocheck thrower --radiopersonal <wav>` (headless) and `--radiocheck watch` (windowed) on loopback; see `radio`.
- `tools/dropcheck.sh` (`CHUNKS=<terrain_chunks dir>` from a worktree): server + windowed `--dropcheck thrower`
  (drops, full wind-up throw, stack drop; `test_output/dropcheck_aim.png`) + windowed `--dropcheck watch`
  (wind-up pose, items settled, points at and picks up the bars; `test_output/dropcheck_point.png`); see `throw-drop`.
  Offline: `<godot> --path . -- --ride foot,60 --dropcheck solo` (outline + pick-up, `test_output/dropcheck_solo.png`).
- `tools/carcdcheck.sh` (`CHUNKS=` likewise, port 7811): car stereo CDs, `--carcdcheck driver` (headless) /
  `watch` (windowed); `<godot> --path . -- --carcdcheck shots` offline screenshots of the radio panel; see `radio`.
- `GODOT=<exe> tools/gunshotcheck.sh [--gunside]`: server + A (shouldered shotgun, fires, rate limit) + B (screenshots A's body, counts Shot events); pictures `test_output/gunshot_*.png` (see `shotgun-feel`).
- `GODOT=<exe> tools/pvpcheck.sh`: two loopback runs (PvP on, then off): rifle, pistol through a vest, knife, kill credit, replicated Down; see `combat/pvp-weapons`.
- `GODOT=<exe> tools/plantcheck.sh`: server + A (flag ghost, plant, pull-up) + B (remote view, spawn effect count); see `flag-plant`.
- `GODOT=<exe> tools/placedcheck.sh [E,N]`: loopback server + three clients for item events and placed
  objects (late join snapshot, owner-only photo removal, persistence across a server restart, a Polaroid
  image fetched by hash); see `item-net-events`.
- `<godot> --path . -- --ride foot,120 --view first --photocheck --photo-dir <abs dir>`: the Polaroid offline
  with screenshots (see `polaroid`).
- `tools/bonkcheck.sh` (net tier, no terrain) and `--interactcheck` (offline, windowed, `--view third`, needs `--chunks`): thrown hits over loopback, and the #261 interactions photographed; see `throw-hits`.
