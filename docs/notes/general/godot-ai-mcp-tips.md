# godot ai mcp tips

- Availability: the MCP answers only while a Godot editor has the project open, and it drives
  that editor's project. Working in a worktree (`../UnitSportSwitzerland-<issue#>`)? Check with
  `editor_state` / `session_manage` that the open project path is that worktree, otherwise edits,
  runs and screenshots hit the main checkout. No editor open: fall back to the CLI commands.

- godot-ai MCP: `game_eval` needs `Engine.get_main_loop().root` (no bare `root`) and
  TAB indentation; `editor_manage monitors_get` reads the EDITOR process, not the game —
  use `Performance.get_monitor` inside `game_eval` for game metrics.
