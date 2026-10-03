# Avatars (`src/Avatar/`)

Procedural human, bike and aircraft meshes and their rigs.

Index only: one line per note in `docs/notes/avatar/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/avatar`.

## Architecture

- `avatars` — Avatars: (`src/Avatar/`): procedural low-poly figures and a road bike, built from two primitives only — a tapered...
- `dance-moves` — Dance moves: joint-level spec per style and move, crowd moves (#261), emotes at `EmoteMoves` + catalog index and the #404 moves (YMCA, chicken, cabbage patch, swim, wave, cheer, salute, shrug); `--emotecheck`
- `riding-position-derived-from-bike` — A riding position is derived from the bike, never eyeballed
- `judge-model-proportions-long-lens` — Judge model proportions with a long lens: The avatar preview's focus camera sits 9 m back at 13° FOV,...

- `car-cabin` — Car cabin (#69): hollow body, glass as panes in a second surface, dash/dials/wheel/pedals/mirrors, the driver's seat derived from the body and the figure posed from it; `--cockpitcheck`
- (`VehicleDeck`/`DeckBuilder`, a vehicle's walkable deck built with its model: see the player note `walk-aboard`)
- `heavy-cabin` — Truck cabs and bus driver's place and saloon (#157): hollow cabs with panes, derived seat and flat wheel (hands' reach at `MaxGrip`), air gauge and gear display, binnacle square to the dials, 2+2 bus seats, seat anchors for passengers
- `cockpit-kit` — Wheel, column, dials, needles, lamps, pedals and mirrors of cars and heavies come from `CockpitKit` + a per-vehicle `CockpitSpec`; never copy them into a cabin; no static field built from another partial's statics (#221)
- `item-arm-poses` — Held items pose the arms (ItemArmPose, replicated ItemAction) and the held mesh follows the hand basis
- `body-shape` — The figure's body (#394): builds (`Physique`), lofted trunk (`Torso`, spine 0-4) and head (`Head`), limb `Zones`/`LimbBand`, hands, boots, hair and `HairCover`; no allocation per rebuild; `--bodies` pages
- `face-atlas` — Pixel faces (#394): `FaceAtlas` drawn as text, `FaceBand` UVs, finish id 11, magenta iris painted in the eye colour; every figure mesh needs `FigureMaterial`
- `appearance` — Who a figure is (#394): `Appearance` packed in `FootPlayer.AppearanceBits` from `GameSettings`, Body row in the inventory, `Register`/`ForRider` registry for rides, seeds for NPCs and ghosts
- `cartoon-outline` — Cartoon's ink outline: a next pass on the figure material, round loft normals, `NoNormal` vertices left alone
- `clothing` — Clothes (#251): Garments catalog, WearSlot, Outfit bits (OutfitBits), AppendDressed from the rig, open Skirt primitive, finishes in vertex alpha + FigureMaterial/avatar.gdshader; `--outfitcheck`, `--avatars … --outfits`

## Gotchas

- `perf-pose-mesh-cache` — Never a new ArrayMesh per frame: rebuild in place (`into:`) only when the pose key changes, remote figures 15 Hz far/off-view, cranks/legs/drivers cached by quantised pose (#221)
- `perf-shared-materials` — `HumanMeshBuilder.Material()` is one shared instance: never modify it, a variant is its own material (#221)
- `stopped-figure-slow-walk` — A stopped figure is not a slow walk: `HumanMeshBuilder.Cadence` has a floor — it must, or a figure inching forward...
- `gait-solved-from-no-slip` — A gait is solved from the no-slip constraint, and the arithmetic has two traps
- `avatar-meshes-authored-facing-z` — Avatar meshes are authored facing +Z; a Godot node faces −Z
- `crank-turning-wrong-way-instantly` — A crank turning the wrong way is instantly obvious to anyone who rides
- `meshscratch-boxes-render-inside-out` — MeshScratch winding, fixed: every face clockwise from outside (Godot front face); `--meshcheck` signed-volume check (#54), smooth primitives too (#311); overlays must stand ~1 cm proud or they z-fight at distance
