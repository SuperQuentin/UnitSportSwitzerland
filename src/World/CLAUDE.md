# World systems (`src/World/`)

Day/night, traffic, tree collision and player races.

Index only: one line per note in `docs/notes/world/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/world`.

## Architecture

- `day-night` — Day/night: (`World/DayNight`): four global shader uniforms declared in `project.godot` `[shader_globals]` —...; indoors: sky-coloured glass, room-lit cameras (#134)
- `traffic-trains` — Traffic and trains: (`World/Traffic`, `World/LaneGraph`): local and cosmetic per client (not replicated), but solid...
- `trees-solid` — Trees are solid: (`World/TreeColliders`, issue #14): no per-tile tree collision — 100k+ trees a forest tile. A pool...
- `races` — Races between players in anything (`World/RaceManager`, `RaceCourse`, `RaceGates`, `Player/GatePilot`): concurrent races, NPC entrants, road routes and air gates,...
- `npc-arrivals` — Race NPCs drive in to the grid: (`World/NpcArrival`, #51) out of sight on the race road, from behind or from ahead with a three-point turn, handbrake turn or donut; `--arrivalcheck`...
- `npc-handoff` — Race NPCs and races belong to nobody (#50): simulator = authority, 600 m zone, handoff on leave/crash/distance, host migration
- `africa-twin-egg` — A random Africa Twin in a parking bay at Riddes (#55): `TileEntered`, server-placed, server authority at rest, `--eggcheck`
