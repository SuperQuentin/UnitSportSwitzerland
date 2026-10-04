# Battle Royale (`src/BattleRoyale/`)

Index only: one line per note in `docs/notes/br/<name>.md`. Read a note only when the task touches
its topic; search with `grep -ril <word> docs/notes/br`. Weapons and PvP: `docs/notes/combat/pvp-weapons.md`.

- `match` — BrManager at World/BattleRoyale: BrState JSON, phases, drop, lent inventory, StayDown, death report, PvpRules.Override, spectating, BrHud, --brpace
- `zone` — ZoneSchedule (seeded, ServerNow-driven, first circle sized for the field at GO, nested circles, 8-phase timetable, damage on the owner), ZoneWall shader
- `region` — BrRegion.Pick: random square from places.json towns + manifest tiles, hard rules, scoring, 8 km anti-repeat
- `map` — BrMapImage (hillshade + cover + roads + buildings from the chunk source), Minimap, BrCompass strip, BrMap on M (grid, towns, zoom/pan, waypoint), no /city in a match
- `loot` — MatchLoot tables + LootTables.MatchEpoch (region buildings, in-memory masks), BrCrates (death boxes, supply/army crates, airdrops; loot panel crate mode; Changed before Granted), BrLoot (road points, crates, vehicles, drops)
- `sites` — BrSites rules (bunker/high seat/hay stash/SAC box/wreck/fishing hut), BrSiteMeshes + yaw, locked crates (dial), breaking crates, flare gun, ? markers, memoised horizon
- `plane` — BrFlight (seeded line, doors, altitude from the lattice, test pace ×2), BrPlane (the military freighter's model, #420), Board/Carrier/Stowed, E jumps into the wingsuit, push-out, plane camera, map line, no ground check
- `polish` — BrSounds stings + heartbeat + whoosh, airdrop beacon and landing thud, BrPrefs (user://br/settings.json, buttons on the full map), --br auto-join, squads groundwork (TeamSize, AssignTeams, SideOf/Hostile/TeamsAlive, team win, mates on maps), `/br team` picked teams, pings, a lone side plays on (#469), down-not-out and revive (#475)
- `commands` — /br verbs, --brcheck, tools/brcheck.sh
- `structures` — BrPrefabs (pure, unit-tested: tower, ski jump, avalanche barrier, checkpoint, scout fort, scaffolding, footbridge), BrStructures placement rules, SpawnPrefab + match-owned gadgets, rubble piles, match materials (crates, gathering), --prefabcheck
