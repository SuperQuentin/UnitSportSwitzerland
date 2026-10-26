# The Adventure layer (`src/Adventure/`)

Per-player progress kept by the server, the adventure events, quests; the Journal and the quest HUD are
in `src/Ui/` (`AdventureLog`, `QuestsPage`, `QuestTracker`, `QuestToasts`). Plans: `docs/plans/adventure-*.md`.

Index only: one line per note in `docs/notes/adventure/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/adventure`.

## Architecture

- `progress-store` — `SectionKey<T>` declared in each system's own file, `Get` / write / `Touch`, versioned sections that migrate themselves, unknown and newer sections kept raw, the file envelope (`AdventureBook`)
- `service` — `AdventureService` at World/Adventure: server keeps progress (offline the client, peer 1 key `local`), keyed by the proven PlayerKey or `acct:<id>` on `--accounts required`, session progress until proof, `Active(peer)` off in a BR round, `SectionSync` 4 Hz, 5 s saves, the server API other plans call, `TrackedTarget`, protocol 50
- `adventure-events` — raise on the server only, after writing your section; new events are records in the raising plan's file; the shared set; the bus drops inactive peers'
- `quests` — quests are catalog data; step kinds and their plans (offered only when those run), `QuestEngine` rules, rewards applied once (cash to the account), hand-overs checked by place, sources, the checks' `ProbeQuests`

## Commands

- `commands` — `--systems ...,adventure`, `--adventure-test`, `--adventure-dir`, `--journal`; `--adventurecheck` (quick) and `tools/adventurenetcheck.sh` (net)
