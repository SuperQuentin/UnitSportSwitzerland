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

- `rewards` — rewards (#1227) computed per tile from the coarse terrain, POIs and roads, never stored; ids anchored to 10 m cells or POIs; claims checked against the server's own placement and the body's server position; Luxurious editions every 1 August; the wire and the client's drawing
- `chests-and-seals` — the four tiers' loot (`Roll` and `Chance` share the table, pity, duplicates to crystal), the opening ceremony, the five seals in `SealBook` (co-op within 60 m, forgotten after 10 min), guides led by the server on the shared clock
- `collectibles` — edelweiss picked by the gather hold (progress, not items), offered five at a time at crosses and chapels for the stamina plan's `"stamina"` reward, cairns per canton, the fauna page
- `trials` — trials made from their id (run, glide), timed and paced by the server, par and medals, the per-server board, ghosts; the plan's drive, downhill and ride are follow-ups
- `photo-spots` — the 30 curated views for review (checked against the terrain), the server's judge, viewpoint spots
- `reward-gadgets` — Bergkristall, the treasure detector, the travel flag, the bottled gust, the Alpine set (ItemId 400-410)

## Commands

- `commands` — `--systems ...,adventure`, `--adventure-test`, `--adventure-dir`, `--journal`; `--adventurecheck` (quick) and `tools/adventurenetcheck.sh` (net); `--pois fixture|<file>`, `--placescheck` (quick) and `tools/placesnetcheck.sh` (net); `--rewardspots fixture|<file>`, `--rewardcheck` (quick) and `tools/rewardnetcheck.sh` (net)
