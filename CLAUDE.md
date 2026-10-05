# UnitSportSwitzerland

Godot 4.7 C# multiplayer game: real swissALTI3D/swissTLM3D/GWR data -> `tools/TerrainPreprocessor` ->
tiles in `terrain_chunks/` -> streamed by `src/Terrain/ChunkManager` -> low-poly PS1-style Switzerland,
on foot, mounted, driving or flying, over ENet multiplayer.

## Notes: read on demand, never up front

Knowledge lives in ~180 micro notes, `docs/notes/<area>/<name>.md`, one topic each. Each code
directory's `CLAUDE.md` (auto-loaded when you touch files there) is only an **index**: one line per
note. Read a note only when the task needs it; find one with `grep -ril <word> docs/notes`.
Areas: tools, terrain, net, player, vehicles, avatar, audio, gpx, world, combat, br, birds, items, crafting, build, loot,
occasions, core, ui, xr, styles, farming, general. New knowledge goes in a new or existing note plus one index line — never in this file.

`docs/notes/general/`: `subagents` (model choice, fan-out limits), `never-lookat-data-driven`,
`invariant-culture-floats` (French locale), `gdignore-data-dirs`, `godot-ai-mcp-tips`,
`headless-exit-139` (read the RESULT line), `godot-exe`, `graphify` (optional),
`worktrees` (main checkout stays on `main`), `local-release` (`tools/release.sh` builds and uploads a release, run by hand),
`linux-deploy` (`tools/deploy-linux.sh` builds and deploys the Linux server over SSH),
`twoclient-checks` (server + two-client `tools/*check.sh` go through `tools/lib/twoclient.sh`),
`testing` (test tiers, `tools/test.sh unit|quick|net|full`, path-to-check map, resource guard),
`fast-checks` (the quick tier runs `--fixed-fps 60`: time waits with `GameClock.Now`, wait for threads on the wall clock),
`dead-code-and-shared-helpers` (use `Terrain.Format.SwissProjection`, `TileId.ReadList`; prove a member unused before deleting it),
`test-systems-optin` (every probe declares `--world flat|fixture` / `--systems`, the lightest that works; driving checks run on fixture courses),
`perf-no-per-frame-allocations` (static `StringName`, no LINQ/strings/lists per frame, UI text and shader params only on change),
`perf-221-migration` (**read before merging main into a branch started before Oct 2026**: every #221 rule note and the rebase order),
`new-action-three-devices` (every new key or interaction: keyboard, gamepad and VR decided together).

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
- **Every new action or interaction is designed for keyboard, gamepad and VR together**: before
  coding a new key, decide its pad button and its VR way (grip the thing, or the pad through
  `XrPad`), show it with `InputHints`, and add its row to `xr/vr-action-map`
  (`docs/notes/general/new-action-three-devices.md`).
- **Simple tasks** (docs/notes tweaks, one-line fixes) skip the workflow: commit straight on `main`
  in the main checkout and push. No issue, branch, worktree or PR.
  4. **Test the cheapest tier that can catch the bug** (`docs/notes/general/testing.md`):
     `tools/test.sh quick` on every change; **tier 2 (`tools/test.sh net`) when the change touches
     network/authority/replicated state**, checking the feature on the **remote** peer (replication,
     authority, animation, damage). The PR says what was and was not verified. Network model:
     `src/Net/CLAUDE.md`.
- Check and probe output goes in `test_output/` (gitignored), never the repo root.
- **In conversation, issues and PRs are always links**, e.g.
  [#346](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/346), never a bare `#346`
  (on GitHub itself, in issue/PR/commit text, `#N` already links).

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
