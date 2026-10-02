# Avatars (`src/Avatar/`)

Procedural human, bike and aircraft meshes and their rigs.

Index only: one line per note in `docs/notes/avatar/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/avatar`.

## Architecture

- `avatars` — Avatars: (`src/Avatar/`): procedural low-poly figures and a road bike, built from two primitives only — a tapered...
- `riding-position-derived-from-bike` — A riding position is derived from the bike, never eyeballed
- `judge-model-proportions-long-lens` — Judge model proportions with a long lens: The avatar preview's focus camera sits 9 m back at 13° FOV,...

- `car-cabin` — Car cabin (#69): hollow body, glass as panes in a second surface, dash/dials/wheel/pedals/mirrors, the driver's seat derived from the body and the figure posed from it; `--cockpitcheck`
- (`VehicleDeck`/`DeckBuilder`, a vehicle's walkable deck built with its model: see the player note `walk-aboard`)
- `heavy-cabin` — Truck cabs and bus driver's place and saloon (#157): hollow cabs with panes, derived seat and flat wheel (hands' reach at `MaxGrip`), air gauge and gear display, binnacle square to the dials, 2+2 bus seats, seat anchors for passengers
- `item-arm-poses` — Held items pose the arms (ItemArmPose, replicated ItemAction) and the held mesh follows the hand basis
- `clothing` — Clothes (#251): Garments catalog, WearSlot, Outfit bits (OutfitBits), AppendDressed from the rig, open Skirt primitive, finishes in vertex alpha + FigureMaterial/avatar.gdshader; `--outfitcheck`, `--avatars … --outfits`

## Gotchas

- `stopped-figure-slow-walk` — A stopped figure is not a slow walk: `HumanMeshBuilder.Cadence` has a floor — it must, or a figure inching forward...
- `gait-solved-from-no-slip` — A gait is solved from the no-slip constraint, and the arithmetic has two traps
- `avatar-meshes-authored-facing-z` — Avatar meshes are authored facing +Z; a Godot node faces −Z
- `crank-turning-wrong-way-instantly` — A crank turning the wrong way is instantly obvious to anyone who rides
- `meshscratch-boxes-render-inside-out` — MeshScratch winding, fixed: every face clockwise from outside (Godot front face); `--meshcheck` signed-volume check (#54); overlays must stand ~1 cm proud or they z-fight at distance
