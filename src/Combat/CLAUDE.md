# Aerial combat (`src/Combat/`)

Index only: one line per note in `docs/notes/combat/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/combat`.

## None

- `pvp-weapons` — Foot weapons vs players (#178): WeaponDef table, PlayerHits trace, Hit item event relayed to the victim only, /pvp + --pvp, TakeDamage(attacker, cause), Died, Armor, replicated Down, StayDown/Eliminated/Respawn, tools/pvpcheck.sh
- `aerial-combat` — Aerial combat: (`src/Combat/`, `World/Combat` on server and clients — RPCs route by path): fire (LMB / RB) in the...
- `perf-player-snapshot` (player) — `CombatManager` targets, wing hits and exclude lists read `PlayerSnapshot.Of`, reuse `_targets` and its ray queries; no group scan per tracer (#221)
