# Commands

- `<godot> --headless --path . -- --invcheck`: every cursor operation on scratch inventories, never
  the save.
- `<godot> --path . -- --ride foot,60 --invuicheck`: synthetic mouse events at the real slot
  rectangles (drag and drop, click-carry, spread, shift-click, right-click).
- `--connect <host> --econcheck <admin password>` against a server started with that password:
  non-admin vehicle refused, `/login` flips the flag, admin vehicle spawned, cash claimed to the
  server account. Leaves its test deposit in the server's accounts file.
- `--invuicheck`/`--econcheck` use a scratch inventory (`Inventory.Scratch`, `Persist = false`).
