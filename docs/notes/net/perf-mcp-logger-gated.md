# MCP game logger only with the editor debugger (#221, PR #233)

## Rule
- `addons/godot_ai/runtime/game_helper.gd` attaches its `Logger` (`OS.add_logger`) **only when
  `EngineDebugger.is_active()`**. The gate is marked `UnitSportSwitzerland (#221)`. Re-apply it after
  every update of the vendored addon, until upstream ships it.
- Never add a project `Logger` that queues lines unless something always drains the queue.

## Why
Only the editor debugger drained the queue. On a dedicated server, an exported client or a
command-line run, every log line (plus a GDScript backtrace per warning) stayed in memory for the
life of the process. That is an unbounded leak on a server that runs for days. After the fix, a
soak of 4 players × 10 min kept the working set flat at 309 MB from t=120 s to t=600 s, with the
heap between 114 and 123 MB.

## Same logic, preserved
- A game launched from the editor (debugger active) still sends `print` output and errors to the
  MCP tools, and still attributes script errors to evals. `_logger` is null-checked everywhere it is read.
- The message capture (screenshots, evals) is still registered, with or without the debugger.

## Migrating old code / open branches
- After an addon update, run `grep -n "OS.add_logger" addons/godot_ai/runtime/game_helper.gd`. The
  call must sit under `if EngineDebugger.is_active():`. If upstream restructured `_ready`, put the
  same gate around their `add_logger` call. No open PR touched the addon when #233 was opened.

## How to check
`<godot> --headless --path . -- --server` prints
`[godot_ai game_helper] registered mcp capture (debugger active=false, logger=false)`.
