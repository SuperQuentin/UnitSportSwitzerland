# Working with subagents (model choice, fan-out limits)

- Use the cheapest model that can do the job. Delegate to `model: "haiku"` for mechanical work
  with no design decisions: transcribing/porting code to an existing pattern, settings/menu
  plumbing, docs updates, running builds and check commands (`--shot`, `--flycheck`, ...) and
  summarising their output, bulk renames, file searches.
- Use `model: "sonnet"` for well-specified implementation inside an interface that already
  exists (one DSP voice, one generator, one mesh builder), where the spec is written down.
- Keep on the main (Opus) model: architecture and interfaces, anything touching threading /
  the streaming loader / networking authority, debugging with unclear causes, and reviewing
  what subagents produced.
- Pin the shared interface before fanning out; give each agent an explicit list of files it may
  create or edit, so parallel agents never write the same file.
- **Haiku currently cannot start in this environment**: the connected MCP servers (kicad, godot-ai,
  Trello, ...) bring the system prompt plus tool definitions to ~230k tokens, over Haiku's 200k
  window, so every Haiku agent fails before its first step ("Prompt is too long"). Until fewer
  MCP servers are enabled for this project, send mechanical work to Sonnet instead.
- Parallel agents share one session usage limit: fanning out 6+ agents at once can exhaust it and kill all of them together. Prefer 2-3 at a time.
- Never kill processes by name or command-line pattern (`taskkill /im`, `Stop-Process` on a match,
  `pkill`): parallel agents run godot at the same time, and a broad match once killed the user's
  browser. Only kill the exact PIDs you launched yourself; prefer `timeout` wrappers so runs end alone.
