# Commands

- `<godot> --headless --path . -- --invcheck`: every cursor operation on scratch inventories, never
  the save.
- `<godot> --path . -- --ride foot,60 --invuicheck`: synthetic mouse events at the real slot
  rectangles (drag and drop, click-carry, spread, shift-click, right-click).
- `--connect <host> --econcheck <admin password>` against a server started with that password:
  non-admin vehicle refused, `/login` flips the flag, admin vehicle spawned, cash claimed to the
  server account. Leaves its test deposit in the server's accounts file.
- `--invuicheck`/`--econcheck` use a scratch inventory (`Inventory.Scratch`, `Persist = false`).
- `<godot> --headless --path . -- --iconsheet`: renders every item icon to `test_output/iconsheet.png` (see `pixel-icons`).
- `GODOT=<exe> tools/gunshotcheck.sh [--gunside]`: server + A (shouldered shotgun, fires, rate limit) + B (screenshots A's body, counts Shot events); pictures `test_output/gunshot_*.png` (see `shotgun-feel`).
- `GODOT=<exe> tools/placedcheck.sh [E,N]`: loopback server + three clients for item events and placed
  objects (late join snapshot, owner-only photo removal, persistence across a server restart, a Polaroid
  image fetched by hash); see `item-net-events`.
- `<godot> --path . -- --ride foot,120 --view first --photocheck --photo-dir <abs dir>`: the Polaroid offline
  with screenshots (see `polaroid`).
