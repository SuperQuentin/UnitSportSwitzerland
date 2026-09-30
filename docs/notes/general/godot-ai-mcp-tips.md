# godot ai mcp tips

- godot-ai MCP: `game_eval` needs `Engine.get_main_loop().root` (no bare `root`) and
  TAB indentation; `editor_manage monitors_get` reads the EDITOR process, not the game —
  use `Performance.get_monitor` inside `game_eval` for game metrics.
