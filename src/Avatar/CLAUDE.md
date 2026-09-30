# Avatars (`src/Avatar/`)

Procedural human, bike and aircraft meshes and their rigs.

Index only: one line per note in `docs/notes/avatar/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/avatar`.

## Architecture

- `avatars` — Avatars: (`src/Avatar/`): procedural low-poly figures and a road bike, built from two primitives only — a tapered...
- `riding-position-derived-from-bike` — A riding position is derived from the bike, never eyeballed
- `judge-model-proportions-long-lens` — Judge model proportions with a long lens: The avatar preview's focus camera sits 9 m back at 13° FOV,...

## Gotchas

- `stopped-figure-slow-walk` — A stopped figure is not a slow walk: `HumanMeshBuilder.Cadence` has a floor — it must, or a figure inching forward...
- `gait-solved-from-no-slip` — A gait is solved from the no-slip constraint, and the arithmetic has two traps
- `avatar-meshes-authored-facing-z` — Avatar meshes are authored facing +Z; a Godot node faces −Z
- `crank-turning-wrong-way-instantly` — A crank turning the wrong way is instantly obvious to anyone who rides
- `meshscratch-boxes-render-inside-out` — `MeshScratch` boxes render inside out: nothing inside a box is hidden, so never put a moving part away inside the body
