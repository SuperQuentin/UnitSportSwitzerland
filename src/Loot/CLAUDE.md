# Loot and gathering (`src/Loot/`)

Lootable furniture and outdoor gathering.

Index only: one line per note in `docs/notes/loot/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/loot`.

## Architecture

- `loot` — Loot: (`src/Loot/`): lootable furniture in every generated interior (fridge, wardrobe, nightstand, desk, shelf,...
- `gathering` — Gathering: (`src/Loot/Gathering.cs`, hold G / pad X on foot outdoors — pad X is only tuck/sprint when mounted): a...
