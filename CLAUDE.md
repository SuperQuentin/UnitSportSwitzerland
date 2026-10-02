# UnitSportSwitzerland

Godot 4.7 C# multiplayer game: real swissALTI3D/swissTLM3D/GWR data -> `tools/TerrainPreprocessor` ->
tiles in `terrain_chunks/` -> streamed by `src/Terrain/ChunkManager` -> low-poly PS1-style Switzerland,
on foot, mounted, driving or flying, over ENet multiplayer.

## Notes: read on demand, never up front

Knowledge lives in ~180 micro notes, `docs/notes/<area>/<name>.md`, one topic each. Each code
directory's `CLAUDE.md` (auto-loaded when you touch files there) is only an **index**: one line per
note. Read a note only when the task needs it; find one with `grep -ril <word> docs/notes`.
Areas: tools, terrain, net, player, vehicles, avatar, audio, gpx, world, combat, br, birds, items, loot,
occasions, core, ui, xr, styles, general. New knowledge goes in a new or existing note plus one index line — never in this file.

`docs/notes/general/`: `subagents` (model choice, fan-out limits), `never-lookat-data-driven`,
`invariant-culture-floats` (French locale), `gdignore-data-dirs`, `godot-ai-mcp-tips`,
`headless-exit-139` (read the RESULT line), `godot-exe`, `graphify` (optional),
`worktrees` (main checkout stays on `main`), `local-release` (`tools/release.sh` builds and uploads a release, run by hand),
`linux-deploy` (`tools/deploy-linux.sh` builds and deploys the Linux server over SSH),
`testing` (test tiers, `tools/test.sh unit|quick|net|full`, path-to-check map, resource guard),
`dead-code-and-shared-helpers` (use `Terrain.Format.SwissProjection`, `TileId.ReadList`; prove a member unused before deleting it),
`test-systems-optin` (every probe declares `--world flat|fixture` / `--systems`, the lightest that works; driving checks run on fixture courses),
`perf-no-per-frame-allocations` (static `StringName`, no LINQ/strings/lists per frame, UI text and shader params only on change).

## Rules

- **English only** in the repo: code, comments, docs, issues, PRs, commits.
- **Feature workflow** (several people work in parallel):
  1. Search issues and PRs first (`gh issue list --state all --search "<kw>"`, `gh pr list`); if work
     overlaps (same feature, files or system), coordinate on that issue instead.
  2. `gh issue create` before coding: what it does, which files and systems it touches.
  3. Branch from up-to-date `main` as `feat/<issue#>-name`, in a **worktree**
     (`../UnitSportSwitzerland-<issue#>`): the main checkout stays on `main`
     (`docs/notes/general/worktrees.md`). Never commit features on `main`; `Closes #N` in the PR.
     **Push local commits on feature branches whenever possible**, so others can build on them
     and a local crash loses nothing.
- **Simple tasks** (docs/notes tweaks, one-line fixes) skip the workflow: commit straight on `main`
  in the main checkout and push. No issue, branch, worktree or PR.
  4. **Test the cheapest tier that can catch the bug** (`docs/notes/general/testing.md`):
     `tools/test.sh quick` on every change; **tier 2 (`tools/test.sh net`) when the change touches
     network/authority/replicated state**, checking the feature on the **remote** peer (replication,
     authority, animation, damage). The PR says what was and was not verified. Network model:
     `src/Net/CLAUDE.md`.
- Check and probe output goes in `test_output/` (gitignored), never the repo root.

## Commands

- Build: `dotnet build UnitSportSwitzerland.csproj`
- Server: `<godot> --headless --path . -- --server [--port N] [--generated-world]`; client: `<godot> --path . -- --connect 127.0.0.1`
  (no args = offline). `<godot>` is `godot` on WSL; Windows path: `docs/notes/general/godot-exe.md`.
- Area-specific commands and checks: the `commands` note of that area.
- **Godot MCP** (`godot-ai` tools: scene, nodes, run game, screenshots, logs): available only while
  the Godot editor has the project open, and it acts on whatever project that editor loaded. In a
  worktree, make sure the editor has that worktree's project open (not the main checkout) before
  trusting it. Tips: `docs/notes/general/godot-ai-mcp-tips.md`.

## graphify (optional)

Only if `graphify` is installed and `graphify-out/graph.json` exists locally (it is not in the repo):
`graphify query "<question>"` can orient you before grepping. Otherwise search as usual. Details:
`docs/notes/general/graphify.md`.
