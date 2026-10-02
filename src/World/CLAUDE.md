# World systems (`src/World/`)

Day/night, traffic, tree collision and player races.

Index only: one line per note in `docs/notes/world/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/world`.

## Architecture

- `day-night` — Day/night: (`World/DayNight`): four global shader uniforms declared in `project.godot` `[shader_globals]` —...; indoors: sky-coloured glass, room-lit cameras, open doors light the street at night (#134); `/time` set/add/speed, server-owned once set (#202)
- `traffic-trains` — Traffic and trains: (`World/Traffic`, `World/LaneGraph`): local and cosmetic per client (not replicated), but solid...
- `traffic-and-races` — Traffic around a race (#85, #159): drivers react only to racers they can see, give way at junctions, no spawns in front of fast racers; racers sense traffic joining and across bridged junctions, pass standing traffic, retire when wrecked; before/after numbers
- `trees-solid` — Trees are solid: (`World/TreeColliders`, issue #14): no per-tile tree collision — 100k+ trees a forest tile. A pool...
- `races` — Races between players in anything (`World/RaceManager`, `RaceCourse`, `RaceGates`, `Player/GatePilot`): concurrent races, NPC entrants, road routes and air gates,...
- `npc-arrivals` — Race NPCs drive in to the grid: (`World/NpcArrival`, #51) out of sight on the race road, from behind or from ahead with a three-point turn, handbrake turn or donut; `--arrivalcheck`...
- `npc-handoff` — Race NPCs and races belong to nobody (#50): simulator = authority, 600 m zone, handoff on leave/crash/distance, host migration
- `africa-twin-egg` — A random Africa Twin in a parking bay at Riddes (#55): `TileEntered`, server-placed, server authority at rest, `--eggcheck`
- `perf-traffic-tick` — Traffic per tick: road sampled only near an obstacle, gap check on the X-sorted `_byX` window, cars drawn to 600 m, shared cached meshes (#221)
- `water-field` — Water (#299): `WaterField` API (TryLevelAt, IsUnderwater, Height/Normal/Velocity on the server clock), `WaveSpectrum` the one source of the wave constants (pushed as shader globals), sea state (`/seastate`, `--sea-state`, replicated), floating-origin safe, underwater look, `/water`, checks
- `perf-racenpc-server-physics` — `RaceNpc` has no physics step on the dedicated server (a data proxy, never the simulator) (#221)
- `perf-race-tick` — `RaceManager.ServerTick` per frame: no LINQ/`GetNodeOrNull`; node lookups on the 1 s review pass (#221)
- `perf-player-snapshot` (player) — `Traffic` obstacles come from the tick's `PlayerSnapshot` into reused lists, never a group scan + `ToList` per tick (#221)
