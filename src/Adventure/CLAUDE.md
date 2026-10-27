# The Adventure layer (`src/Adventure/`)

Per-player progress kept by the server, the adventure events, quests; the Journal and the quest HUD are
in `src/Ui/` (`AdventureLog`, `QuestsPage`, `QuestTracker`, `QuestToasts`). Plans: `docs/plans/adventure-*.md`.

Index only: one line per note in `docs/notes/adventure/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/adventure`.

## Architecture

- `progress-store` — `SectionKey<T>` declared in each system's own file, `Get` / write / `Touch`, versioned sections that migrate themselves, unknown and newer sections kept raw, the file envelope (`AdventureBook`)
- `service` — `AdventureService` at World/Adventure: server keeps progress (offline the client, peer 1 key `local`), keyed by the proven PlayerKey or `acct:<id>` on `--accounts required`, session progress until proof, `Active(peer)` off in a BR round, `SectionSync` 4 Hz, 5 s saves, the server API other plans call, `TrackedTarget` and `TrackPlace`, `SyncsItself` / `Loaded` / `Saving` for plans with their own sync, protocol 50 (51 with places)
- `adventure-events` — raise on the server only, after writing your section; new events are records in the raising plan's file; the shared set; the bus drops inactive peers'
- `quests` — quests are catalog data; step kinds and their plans (offered only when those run), `QuestEngine` rules, rewards applied once (cash to the account), hand-overs checked by place, sources, the checks' `ProbeQuests`; places are canonical POI ids, reached by finding them

- `travel` — the travel network (#1226): points, two-tick unlocks, Free / Network / Off, `MayTravel` and its refusals, trips (charge, `Arrived`, curtain), GA and half-fare cards, signs, the board, bots, protocol 52
- `fares` — `assets/adventure/fares.json`: SBB distance tariff × 1.25, ship tariff, cable-car lines, card discounts and prices, the "verify" values
- `pins` — `PinBook` (30, notes, icons), placed on the map (right click, pad X, "Pin the centre"), server-checked, shared with the party (`PartyPinsState`, `Parties.StateSent`)
- `compass` — `Ui/CompassStrip` + `ICompassSource` (BR and free roam), redraw on change only, the VR watch bezel; the target marker (edge ellipse, a target behind points down); the guiding gust (N, palm up, shader, settings)

## Commands

- `commands` — `--systems ...,adventure`, `--adventure-test`, `--adventure-dir`, `--journal`; `--adventurecheck` (quick) and `tools/adventurenetcheck.sh` (net); `--pois fixture|<file>`, `--placescheck` (quick) and `tools/placesnetcheck.sh` (net); `--adventure-travel`, `/travel`, `/pin`, `--navcheck` and `--travelcheck` (quick), `tools/navnetcheck.sh` (net)
