# UnitSportSwitzerland

Godot 4.7 C# multiplayer game streaming real swissALTI3D terrain as low-poly PS1-style
world. Long-term goal: all of Switzerland navigable. Plan: `~/.claude/plans/i-want-to-build-dynamic-metcalfe.md`.

Pipeline in one line: swissALTI3D/swissTLM3D/GWR data -> `tools/TerrainPreprocessor` -> binary tiles in
`terrain_chunks/` -> streamed at runtime by `src/Terrain/ChunkManager` -> low-poly world with roads,
buildings, trees, water; players on foot, on mounts or flying, over ENet multiplayer.

## Where the notes live (loaded on demand)

This file is always loaded and is kept short. The architecture, commands and gotchas live in a
`CLAUDE.md` next to the code they describe. Claude Code loads a subdirectory `CLAUDE.md`
automatically when you read or edit files in that subtree, so **working in a directory brings its
notes with it**. If you need a system's background before touching its files, open its file
directly. Do not `@import` them here: imports load eagerly and defeat the split.

| Working on | Notes (load automatically when you touch files there) |
|---|---|
| `tools/` (preprocessor, RoadGen, MapSetup, formats, `.road`/`.cover`/`.trees`/`.bldg`, France, places) | `tools/CLAUDE.md` |
| `src/Terrain/` (ChunkManager, LOD, meshes, collision, horizon, coarse tiles) | `src/Terrain/CLAUDE.md` |
| `src/Net/` (multiplayer, chunk streaming, chat, admin, RPC) | `src/Net/CLAUDE.md` |
| `src/Player/` (on foot, mounts, bike, skis, flight, feel, tricks) | `src/Player/CLAUDE.md` |
| `src/Vehicles/` (parked vehicles, damage, wrecks) | `src/Vehicles/CLAUDE.md` |
| `src/Avatar/` (human, bike, aircraft meshes, gait) | `src/Avatar/CLAUDE.md` |
| `src/Audio/` (synthesis, engines, ambience) | `src/Audio/CLAUDE.md` |
| `src/Gpx/` (replay, cinema, lens, video export) | `src/Gpx/CLAUDE.md` |
| `src/World/` (day/night, traffic, tree collision) | `src/World/CLAUDE.md` |
| `src/Combat/` (aerial combat, tracers, drones) | `src/Combat/CLAUDE.md` |
| `src/Birds/` (Swiss species, hunting, journal, bird strikes) | `src/Birds/CLAUDE.md` |
| `src/Items/`, `src/Loot/` (inventory, loot, gathering) | `src/Items/CLAUDE.md`, `src/Loot/CLAUDE.md` |
| `src/Core/` (modes, menu, input, settings, perf, teleport, spawn) | `src/Core/CLAUDE.md` |

When you add or learn something worth recording, put it in the `CLAUDE.md` of the directory whose code
it describes, not here. Keep this root file to what every task needs.

## Language

Everything written in this repo (code, comments, docs, issues, PRs, commit messages) is in English.

## Feature workflow (required)

Several people work on this repo in parallel, so every new feature follows these steps:

1. **Check existing issues first.** Before building anything, search open and recently closed
   issues (`gh issue list --state all --search "<keywords>"`) and open PRs (`gh pr list`) for
   work that overlaps: same feature, same files, or the same system (e.g. `ChunkManager`,
   `FootPlayer`, the `.road` format). If something overlaps, stop and coordinate on that issue
   rather than building a parallel version.
2. **Create an issue before writing code.** Use `gh issue create` and describe what the feature
   does and which files and systems it touches, so the next person's search finds it.
3. **Work on a new branch, never on `main`.** Branch from an up-to-date `main`, named after the
   issue (e.g. `feat/<issue#>-short-name`), and reference the issue in commits and in the PR
   (`Closes #<issue#>`).
4. **Test it in multiplayer.** Every new feature must be verified with a dedicated server and at
   least one client on loopback (`<godot> --headless --path . -- --server` plus
   `<godot> --path . -- --connect 127.0.0.1`, or a scripted two-process check), checking the
   feature on the **remote** peer: replication, authority, animation sync, damage. Something that
   works offline can still be invisible, unsynced or duplicated for the other player. The PR states
   what was verified in multiplayer and what could not be. See `src/Net/CLAUDE.md` for the network
   model and its pitfalls.

## Working with subagents

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
- Parallel agents share one session usage limit: fanning out 6+ agents at once can exhaust it

## Commands

- **Output of any check or probe goes in `test_output/`** (gitignored, with a `.gdignore` so Godot
  never imports it): soundcheck WAVs, `--shot`/`--ride`/`--flycheck` screenshots, test exports.
  Never write them to the project root or a temp path that can end up inside the repo.
- Build game: `dotnet build UnitSportSwitzerland.csproj`
- Dedicated server: `<godot> --headless --path . -- --server [--port N]` (`--generated-world` to run
  one with no terrain at all, on generated ground)
- Client: `<godot> --path . -- --connect 127.0.0.1` (no args = offline, T toggles
  spectator/on-foot)
- Godot exe: `C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe`
  On WSL the executable is just `godot` (see the memory note "WSL Godot setup").

More commands (preprocessing, `--shot`, `--fly`, `--ride`, `--probe`, replay flags, ...) are in the
`CLAUDE.md` of the area they check.

## Cross-cutting gotchas

- **Never call `LookAt` on data-driven transforms.** A degenerate target makes Godot raise
  an error, and an error raised inside a C# callback can take the whole runtime down
  ("Fatal error. Internal CLR error." with a stack ending in `DebuggingUtils.GetCurrentStackInfo`).
  Build the basis manually and guard the degenerate cases — see `TrackPlayback.SafeBasis`.
- `ressources/` (sic) and `terrain_chunks/` have `.gdignore` so the editor never imports
  them; don't move data without keeping those.
- French locale machine: never parse/format floats without InvariantCulture (preprocessor
  sets InvariantGlobalization).
- godot-ai MCP: `game_eval` needs `Engine.get_main_loop().root` (no bare `root`) and
  TAB indentation; `editor_manage monitors_get` reads the EDITOR process, not the game —
  use `Performance.get_monitor` inside `game_eval` for game metrics.

## Checks

- **Headless runs of several checks exit 139 (segfault) after printing their result** —
  `--mantlecheck` on main does it too, so it predates the sync/hitbox work. Read the RESULT line,
  or run them windowed (exit code 0).

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
