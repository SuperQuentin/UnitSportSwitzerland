# Inventory and items (`src/Items/`)

Inventory data, held items and the item controller.

Index only: one line per note in `docs/notes/items/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/items`.

## Architecture

- `inventory` — Inventory: (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus an 18-slot pack, stacks, `Changed` —...
- `viewmodel-poses` — ViewPose enum, SetPose/PlayOneShot, aim poses, clipping via half-scale viewmodel
- `cursor-inventory` — The panel works like Minecraft's: a carried stack on the cursor, click/right-click/shift/double-click/drag...
- `pixel-icons` — 16x16 item icons: grids + palette, generic fallback, held card, --iconsheet
- `cash-account` — Money is a counter, not an item: `Inventory.Cash`, lost when knocked out, claimed to the server-kept account...
- `radio` — Radio: the first world item (thrown RigidBody, thrower simulates the fall, server owns what plays), CDs from the shared library on the shared clock, E to dance; `tools/radiocheck.sh`

## Commands

- `commands` — Commands: --invcheck, --invuicheck, --econcheck, --iconsheet, --radiocheck, --cdfixture
