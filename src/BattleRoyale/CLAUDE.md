# Battle Royale (`src/BattleRoyale/`)

Index only: one line per note in `docs/notes/br/<name>.md`. Read a note only when the task touches
its topic; search with `grep -ril <word> docs/notes/br`. Weapons and PvP: `docs/notes/combat/pvp-weapons.md`.

- `match` — BrManager at World/BattleRoyale: BrState JSON, phases, drop, lent inventory, StayDown, death report, PvpRules.Override, spectating, BrHud, --brpace
- `zone` — ZoneSchedule (seeded, ServerNow-driven, nested circles, 8-phase timetable, damage on the owner), ZoneWall shader
- `region` — BrRegion.Pick: random square from places.json towns + manifest tiles, hard rules, scoring, 8 km anti-repeat
- `map` — BrMapImage (hillshade + cover + roads + buildings from the chunk source), Minimap, BrCompass strip, BrMap on M (grid, towns, zoom/pan, waypoint), no /city in a match
- `loot` — MatchLoot tables + LootTables.MatchEpoch (region buildings, in-memory masks), BrCrates (death boxes, supply/army crates, airdrops; loot panel crate mode; Changed before Granted), BrLoot (road points, crates, vehicles, drops)
- `commands` — /br verbs, --brcheck, tools/brcheck.sh
