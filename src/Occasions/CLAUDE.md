# Occasions: seasonal and community events (`src/Occasions/`)

Time-limited themes and events (#18): the calendar, replication, per-player opt-out, and each
occasion's content (props, sky, creatures, sounds, loot, hunt, hats).

Index only: one line per note in `docs/notes/occasions/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/occasions`.

## Architecture

- `occasions` — Occasions: (`src/Occasions/`, #18): time-limited themes and events — Halloween (1 Sep–31 Oct) and Christmas (1...

- `ufo-sightings` — the `ufo` occasion (#863): first Friday nights (`fri#1` .. `+1`), the server plans per 2 km cell and 75 s slot, every peer plays `SightingPath` from the shared clock, real beams via `TractorBeams.Scripted`, a landed saucer is a parked UFO to board, facets and opt-out, bots, `--sighting`, `Shot.Ufos`

- `festival-framework` — festivals (#1269 part 1): `FestivalDef` (sites, day table, milestones, show), `FestivalOccasion` + the `Games` facet (Off hides the bunting, never the score), the tokens `04-mon#3` and `easter-46`, relocation to the largest town, the `festivals` section and Batzen (cap 600, 1 Batzen = CHF 1, converted once after the end), the party board keyed by sorted member keys, `FestivalService.Award`, the Journal's Festival tab, `--festival test`, protocol 58

## Commands

- `commands` — Commands: --avatars, --date, --decorlog, --hats, --headless, --huntcheck, --occasion, --occasioncheck, --path, --shot, --sighting, --soundcheck

## Gotchas

- `new-color-has-alpha-1` — `new Color(r, g, b)` has alpha 1, and `ps1_prop` reads vertex alpha as "this is a light"
- `headless-runs-cannot-read-multimesh` — Headless runs cannot read MultiMesh transforms back
- `live-start-stop` — `/occasion start` shows the look whatever the preference; stop tears down; GPU fence timeouts, not occasions, made the broken shapes (#881)
