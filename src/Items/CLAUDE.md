# Inventory and items (`src/Items/`)

Inventory data, held items and the item controller.

Index only: one line per note in `docs/notes/items/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/items`.

## Architecture

- `inventory` — Inventory: (`src/Items/`): `Inventory` is pure data — a 6-slot hotbar plus an 18-slot pack, stacks, `Changed` —...
- `cursor-inventory` — The panel works like Minecraft's: a carried stack on the cursor, click/right-click/shift/double-click/drag...
- `cash-account` — Money is a counter, not an item: `Inventory.Cash`, lost when knocked out, claimed to the server-kept account...

## Commands

- `commands` — Commands: --invcheck, --invuicheck, --econcheck
