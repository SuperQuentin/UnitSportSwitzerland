# Inventory and items (`src/Items/`)

Inventory data, held items and the item controller.

Index only: one line per note in `docs/notes/items/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/items`.

## Architecture

- `inventory` — Inventory: (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus an 18-slot pack, stacks, `Changed` —...
- `viewmodel-poses` — ViewPose enum, SetPose/PlayOneShot, aim poses, viewmodel drawn over the world (depth squeeze, own layer vs portals); use one-shots (eat, hat), GPS screen, binocular sway
- `cursor-inventory` — The panel works like Minecraft's: a carried stack on the cursor, click/right-click/shift/double-click/drag...
- `pixel-icons` — 16x16 item icons: grids + palette, generic fallback, held card, --iconsheet
- `shotgun-feel` — Shotgun ADS pose + bead reticle, recoil/camera punch, pump fore-end + sound, rate limit, eye-origin shots, remote jolt
- `camera-zoom` — Camera focal-length zoom (wheel while aiming), LookScale, viewfinder readout + autofocus hunt, --zoom
- `cash-account` — Money is a counter, not an item: `Inventory.Cash`, lost when knocked out, claimed to the server-kept account...
- `radio` — Radio: a thrown RigidBody world item (thrower simulates the fall, server owns what plays), plays in the hand too (stack data + FootPlayer.HeldRadio), RadioSpeaker keyed by CD id (the track-change bug), volume, E to dance; music-picker panel (search, now playing, prev/next, modes once/repeat/list/shuffle), car stereo CDs (FootPlayer.CarCd, R in a vehicle); `tools/radiocheck.sh`, `tools/carcdcheck.sh`
- `polaroid` — Camera prints photos: shoots only through the viewfinder, PhotoCapture renders the eye's view (no HUD), ItemId.Photo + ItemStack.Data, develop, album, sticking, wall posters, PhotoTransfer sharing, --photocheck
- `item-net-events` — ItemEvents.Send (shot, photo flash relayed to others) and PlacedObjects (server-kept, saved flags/photos, kind factories)
- `flag-plant` — Swiss flag: placement ghost (green/red, pick-up halo), raise-and-stab / pull-up strokes, replicated Plant arms, spawn thud + dirt
- `throw-drop` — Drop (Q / Ctrl+Q / pack panel), pointing outline (inverted hull overlay), E pick-up, grenade-style charged throw (arc, ring, shoulder cam, wind-up arm pose), local proxy + remote prediction, CCD + under-terrain rescue, ImpactFx; `tools/dropcheck.sh` (#206)
- `smart-binoculars` — Held at a building's door (or inside): reads out its loot table (LootTables.BuildingTable); no aim, no zoom (#165)

## Commands

- `commands` — Commands: --invcheck, --invuicheck, --econcheck, --iconsheet, tools/placedcheck.sh, --photocheck, tools/gunshotcheck.sh, tools/useanimcheck.sh, tools/radiocheck.sh, tools/carcdcheck.sh, --carcdcheck shots, --cdfixture, tools/dropcheck.sh
