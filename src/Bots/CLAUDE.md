# Bots (`src/Bots/`)

Index only: one line per note in `docs/notes/bots/<name>.md`. Read a note only when the task touches
its topic; search with `grep -ril <word> docs/notes/bots`. Every new action, item, mount or
interactable is decided for bots too: `docs/notes/general/new-feature-bots.md`.

- `architecture` — server-run FootPlayer (authority 1, `npc_1_n`, id -(1000+n), `BotData`), layers: body seams (`WalkControls`, `FootActions`, facing), `BotHands`, `Bot.Inventory`, `IBotBrain` per mode (`WanderBrain`), `BotProfile`, `BotDirector` (LOD, `Died`/`Downed`), `BotTag`, relay filters on server-run bodies
- `server-collision` — `ChunkManager.CollisionByRadius`: collision within 300 m of a bot (look-ahead, none in the air, released at 400 m), proxies plain anchors, roads/holes/trees on the server; float precision and interiors still open
- `brain` — BrBrain (phase 2): BotSenses (sight cone + ray, hearing, memory), BrTactics scores (pure), BotSkills (MoveTo with wall following, crate takes), streets (RoadNetwork.Path, GoTo), fighting (reaction, aim error, bands, strafing), the drop (board, jump 0.7 s apart, wingsuit, canopy), play-out
- `hits` — players hit bots (targets, server applies `ShotHit`), bots fire (`BotHands`, `PlayerHits` sink, `ItemEvents.ServerHit`/`ServerSend`), PvP lets bots be hit anywhere, never `RpcId` a bot id
- `battle-royale` — `/br bots`, top-up at GO in the first circle, server does death box/down/zone/revive for bots, `BotKills` "10 kills (3 bots)", `[bot]` marks, no careers, ends when no player is left
- `commands` — `/bot add|arm|fire|clear|list`, `--bots`/`--botsat`/`--botlog`/`--brbots`, `tools/botcheck.sh`, `tools/brbotcheck.sh`, unit tests
- `bot-capability-map` — the living table: every action, item, mount, vehicle and interactable, its seam, server path, sense, skill and status (ok / gap / n/a)
