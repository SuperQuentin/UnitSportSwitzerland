# Aerial combat (`src/Combat/`)

Index only: one line per note in `docs/notes/combat/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/combat`.

## None

- `pvp-weapons` — Foot weapons vs players (#178): WeaponDef table, PlayerHits trace, Hit item event relayed to the victim only, /pvp + --pvp, TakeDamage(attacker, cause), Died, Armor, replicated Down, StayDown/Eliminated/Respawn, tools/pvpcheck.sh
- `hits` (bots) — bots are targets (the server applies the hit) and fire back through `BotHands`, `ItemEvents.ServerHit`/`ServerSend`; `PvpRules` lets bots be hit anywhere (#837)
- `fist-fight` — Tekken / Mortal Kombat 1v1 (#495): E at a player or `/fight` challenges, accept the same way; server-run `FightMatch` (rounds, HP, timer, FINISH THEM), strikes checked by reach, rate and the victim's replicated `FightPose`; poses through the dance layer, side-on camera, `FightHud`, three-device controls, `tools/fightnetcheck.sh`
- `aura-battle` — 1v1 aura battle (#907): `/aura` or Activities challenges (invite kind `aura`); plan 3-6 moves in secret, then cut them with one key (`aura_next`) on the downbeats; server-run `AuraMatch`, presses graded on its clock, score part by part (answers, chains, try-hard, a capped crowd from Cheer emotes); `AuraPose` 5000+ through the dance layer; `tools/auranetcheck.sh`
- `aerial-combat` — Aerial combat: (`src/Combat/`, `World/Combat` on server and clients — RPCs route by path): fire (LMB / RB) in the...
- `perf-player-snapshot` (player) — `CombatManager` targets, wing hits and exclude lists read `PlayerSnapshot.Of`, reuse `_targets` and its ray queries; no group scan per tracer (#221)
