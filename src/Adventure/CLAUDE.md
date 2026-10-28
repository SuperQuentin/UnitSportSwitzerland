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
- `travel` — the travel network (#1226): points, two-tick unlocks, Free / Network / Off, `MayTravel` and its refusals, trips (charge, `Arrived`, curtain), GA and half-fare cards, signs, the board, bots, protocol 52
- `fares` — `assets/adventure/fares.json`: SBB distance tariff × 1.25, ship tariff, cable-car lines, card discounts and prices, the "verify" values
- `pins` — `PinBook` (30, notes, icons), placed on the map (right click, pad X, "Pin the centre"), server-checked, shared with the party (`PartyPinsState`, `Parties.StateSent`)
- `compass` — `Ui/CompassStrip` + `ICompassSource` (BR and free roam), redraw on change only, the VR watch bezel; the target marker (edge ellipse, a target behind points down); the guiding gust (N, palm up, shader, settings)
- `versteckis` — hide-and-seek as village props (#1268): `VersteckisManager` (lobby, rounds, rotation, Serve* paths, 4 Hz checks with a 1 s speed window, `InterestService.Withheld` in Hide, equal pay), `VsGround`/`HidePlan` decor and objectives from the tiles, `VsDisguise` mesh swap, `VsHud`, protocol 57, `--versteckischeck`, `tools/versteckisnetcheck.sh`, `tools/versteckisbotcheck.sh`
- `together` — the together rule (party, 60 m, cap 3), the group yield (gathering, harvest, fishing, rare finds, quest cash), the week's chest and the punch card (#1266)
- `errands` — errand givers at huts, stations and landings, the dialogue, the daily Postkarten (8 per 5 km cell per Swiss day, 3 a day), the friend bonus, canton reputation (#1267)
- `links` — hands and the rope (`LinkBook`, `Tether`, belays, `ArmHold`, `RopeNode`), the leg-up, two-handle gates, roped chests, hidden crevasses (stumble fallback)
- `fondue` — the shared pot: spots, ingredients, la huit, dips, the lost bread and its forfeit, `--forfeits`, `--fondueseed`
- `mentors` — milestones, newcomer and veteran, the eight `GarmentStyle.Together` cosmetics, titles
- `alcohol` — a real blood alcohol level (#1271): `Bac` (grams, absorption, Widmark, elimination), the `alcohol` section, effects on the intents (lag, drift, stumbles, no climbing, passing out), the electronic and single-use breathalyzers, `--alcohol off|mild|real`, protocol 61

## Commands

- `commands` — `--systems ...,adventure`, `--adventure-test`, `--adventure-dir`, `--journal`; `--adventurecheck` (quick) and `tools/adventurenetcheck.sh` (net); `--pois fixture|<file>`, `--placescheck` (quick) and `tools/placesnetcheck.sh` (net); `--adventure-travel`, `/travel`, `/pin`, `--navcheck` and `--travelcheck` (quick), `tools/navnetcheck.sh` (net); `--rewardspots fixture|<file>`, `--rewardcheck` (quick) and `tools/rewardnetcheck.sh` (net); `--givers fixture`, `--errandcheck` (quick) and `tools/errandnetcheck.sh` (net); `--alcoholcheck` (quick) and `tools/alcoholnetcheck.sh` (net)
