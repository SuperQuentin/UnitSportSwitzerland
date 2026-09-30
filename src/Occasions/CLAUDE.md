# Occasions: seasonal and community events (`src/Occasions/`)

Time-limited themes and events (#18): the calendar, replication, per-player opt-out, and each
occasion's content (props, sky, creatures, sounds, loot, hunt, hats).

Index only: one line per note in `docs/notes/occasions/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/occasions`.

## Architecture

- `occasions` — Occasions: (`src/Occasions/`, #18): time-limited themes and events — Halloween (1 Sep–31 Oct) and Christmas (1...

## Commands

- `commands` — Commands: --avatars, --date, --decorlog, --hats, --headless, --huntcheck, --occasion, --occasioncheck, --path, --shot, --soundcheck

## Gotchas

- `new-color-has-alpha-1` — `new Color(r, g, b)` has alpha 1, and `ps1_prop` reads vertex alpha as "this is a light"
- `headless-runs-cannot-read-multimesh` — Headless runs cannot read MultiMesh transforms back
