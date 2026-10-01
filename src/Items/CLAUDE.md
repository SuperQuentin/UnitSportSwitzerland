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
- `radio` — Radio: a thrown RigidBody world item (thrower simulates the fall, server owns what plays), plays in the hand too (stack data + FootPlayer.HeldRadio), RadioSpeaker keyed by CD id (the track-change bug), volume, E to dance; `tools/radiocheck.sh`
- `polaroid` — Camera prints photos: ItemId.Photo + ItemStack.Data, develop animation, album, sticking, PhotoTransfer image sharing, --photocheck
- `item-net-events` — ItemEvents.Send (shot, photo flash relayed to others) and PlacedObjects (server-kept, saved flags/photos, kind factories)
- `flag-plant` — Swiss flag: placement ghost (green/red, pick-up halo), raise-and-stab / pull-up strokes, replicated Plant arms, spawn thud + dirt
- `smart-binoculars` — Optic item: pick a target item, see the loot chance (%) on buildings in view (LootTables.Chance)

## Commands

- `commands` — Commands: --invcheck, --invuicheck, --econcheck, --iconsheet, tools/placedcheck.sh, --photocheck, tools/gunshotcheck.sh, tools/useanimcheck.sh, tools/radiocheck.sh, --cdfixture
