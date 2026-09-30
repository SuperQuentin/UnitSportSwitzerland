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
- `tools/radiocheck.sh` (`CHUNKS=<terrain_chunks dir>` from a worktree): dedicated server with `--cdfixture <wav>`,
  `--radiocheck thrower` (headless) and `--radiocheck watch` (windowed) on loopback; see `radio`.
