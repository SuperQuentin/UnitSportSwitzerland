# Multiplayer and network streaming (`src/Net/`)

Client-authoritative transforms, terrain/asset streaming, chat, admin, RPC pitfalls. **Every new feature must be tested here, see the root `CLAUDE.md`.**

Index only: one line per note in `docs/notes/net/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/net`.

## Architecture

- `multiplayer` — Multiplayer: client-authoritative transforms, MultiplayerSpawner + Synchronizer, ENet port 7777. Server runs...
- `terrain-streaming` — Terrain streaming: (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves generated files to clients...
- `invites` — One invite system (#840): `InviteBook`/`PartyBook` (pure, unit-tested), `InviteService` at World/Invites (kinds register an accept), `Parties` at World/Parties; kinds br/tp/tphere/party/fight/race, `/invites /accept /decline /tpa /tphere /party`, toasts, F2 / Shift+F2, `tools/invitecheck.sh`
- `chat-commands` — Chat and commands: (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at `World/Chat` — the path must...
- `admin` — Admin: (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge; the display name is a...
- `vision-interest` — Vision-based interest: the server decides who sees whom (size at render resolution, sky, line of sight, race); out of view = despawned
- `entity-interest` — Vehicles, dropped items and radios exist on a peer only within the range they stay 1.5 px wide at its lens (hysteresis, look-ahead, 32 spawns a round, fade before the edge); `Vis` + server `Relay` per entity (`EntityNet`), the awake slot set sent to all (#689)
- `perf-visibility-on-change` — Player synchronizers keep `VisibilityUpdateMode.None`: every change to what a peer may see must call `UpdateVisibility` (`RefreshNetVisibility`/`RefreshRelays`) where it happens
- `remote-interpolation` — Remote players are interpolated (NetGlobal/NetVel/NetTime at 30 Hz, Hermite on GlobalPos, bounded extrapolation, smooth render clock)
- `positions-on-the-wire` — Positions cross the network as LV95 doubles (GlobalPos), lists as an anchor + offsets, bodies through NetPlace; never a world Vector3 (#185)
- `protocol-handshake` — Connecting starts with Hello/Welcome (Handshake.Protocol); its RPCs never change; bump the protocol on any wire change
- `load-testing` — Load testing: --swarm bots, --serverstats, --netsmooth, tools/loadtest.sh; before/after numbers at 32 players and a 30-min soak
- `lean-dedicated-server` — Dedicated server: proxy players, fps cap, coarse grids, asset prep off the main thread, throttled vehicles
- `lan-discovery` — mDNS browse for `_unitsport._udp` (avahi on the server); the Multiplayer screen lists LAN servers, legacy unicast queries, `--discovercheck`
- `server-query` — UDP status query on port + 1 (`USQ1`/`USR1` + JSON): LAN broadcast list, saved servers' players and ping, `--server-name`, `--query-bind`
- `status-page` — `--status-file` JSON (players, names, version) + `tools/deploy/web/index.html` (clips from `tools/web-media.sh`, people in four styles from `tools/web-people.sh`, #998) served by the deploy's Caddy at `/`, downloads from the latest GitHub release (#740)
- `live-map` — `--livemap-file`: `players.json` every 2 s (`Net/LiveMapFile`), `tools/deploy/web/map.html` (Leaflet, swisstopo grey map, players live, 4G coverage per operator from OFCOM's sites via `tools/deploy/antennas.py`), Caddy paths, `tools/livemapcheck.sh` (#896)
- `house-layouts` — `houses.html` + `tools/LayoutService` at `/api/` (SQLite or PostgreSQL): find a building by address, claim it (email link), draw its floors over a plan (polygon rooms, ruler, two-point plan alignment), moderators approve; `house-layouts.json` -> `--house-layouts`, the game plans that EGID's interior from it, polygons cut into strips (`InteriorGenerator.House.cs`, `RoomStrips`), `--houselayoutcheck`, `tools/layoutservicecheck.sh`, deploy (#1186)
- `udp-receive-loop` — every UDP reader goes through `Udp.ReceiveLoop(udp, token, handler)`; never hand-write a ReceiveAsync loop
- `http-tiles` — tiles from a static HTTP mirror (`--tiles-url`, `AssetKind.HttpBase`, `HttpAssetSource`): probe, 404 final, ENet fallback, 8 failures drop it; Caddy on the deploy host (#651)
- `region-file-sync` — region-wide files a client pulls once (horizon, places) go through `ClientTerrainSync.SyncFileAsync`
- `hosting` — Host from the menu: the client starts itself headless as a server (`--parent-pid` watchdog), joins it, kills it on leave
- `clock-sync` — One shared clock: `ClockSync.ServerNow` from min-RTT ping/pong samples; song position, beat phase and time of day are functions of it, never of anything local or summed per frame; server Unix stamps compare on `ServerUnixNow` (#452)
- `perf-server-frame-metrics` — Server cost = `--serverstats` per-frame busy + `[stats] slow frame` lines; wrap periodic jobs in `ServerStats.Ran`; never the 1 s-max `TimeProcess` monitor
- `perf-no-main-thread-periodic-jobs` — No per-player `GD.Print` on a server timer (blocks ms on Windows), file writes on a worker, `/proc` reads Linux-only
- `perf-mcp-logger-gated` — godot_ai `game_helper` adds its Logger only when `EngineDebugger.is_active()`; keep the gate on addon updates (it leaked every log line)
- `perf-player-snapshot-size` — `BodyPose`/`TrainPose` go as one `float[] NetPose` (quat + offset + squash, train angles only on a train); no Transform3D on the wire
- `perf-relay-delta-interval` — `RelayNear`/`RelayFar` check their OnChange properties at 10 Hz (`DeltaInterval = 0.1f`); per-packet state stays `Always`
- `perf-interest-round` — `InterestService.Evaluate` reads each target once per round (`_at`/`_agl`/`_ride`), `Together` once per pair, cached `_sight`; nothing per pair from a node
- `arrival` — The server decides where a joining player starts (`--spawn E,N`) and whether players may teleport (`--no-teleport`): `World/Arrival.Rules` on accept, the client's loading screen waits for it; `--at` still wins; `tools/arrivalcheck.sh` (#799)
- `sleepers` — A player who leaves lies asleep where they were (saved), and wakes there on return; identity is a per-install ECDSA key proven on join, never the name (#644)
- `server-update` — `/update` (admin): newest GitHub release fetched while players play, then kick all and quit; `start-server.sh run` switches `current` and restarts (#730); automatic every 10 min, restart only once empty, admins told (`ServerAutoUpdate`, #1088)
- `nakama` — Nakama in Docker (`tools/nakama/up.sh`, `check.sh`): accounts and `course:<id>` leaderboards for the trainer edition; only race servers write results (`submit_result` with the http key, a player session gets 403), the ride stays on ENet (#1121)
- `accounts` — Offline (default) or online players (Settings › Account, `user://online_account.json`); server `--accounts off|optional|required --nakama-url`: `World/Accounts` after the version check checks the session token by reading the account from Nakama; the client joins only on its Welcome (#1123)
- `console-link` — `World/ConsoleLink`: join for the chat only from a client of any version, no body, one's own name and rights (admins.json, /login), two RPCs that never change; Multiplayer › console button or the refusal banner (#1088)

## Commands

- `commands` — Commands: --admin-password, --bind, --headless, --name, --path, --server, --stream-bandwidth, --server-name, --query-port, --query-bind, --no-query, --parent-pid

## Gotchas

- `quitting-while-tiles-stream-used` — Quitting while tiles stream used to crash the process
- `networked-nodes-created-code-need` — Networked nodes created in code need deterministic names
- `rpc-issued-off-main-thread` — An RPC issued off the main thread does not throw — it never arrives
- `refused-because-busy-reported-does` — "Refused because busy" must not be reported as "does not exist"
- `chunkmanager-records-tile-has-no` — `ChunkManager` records "this tile has no roads/buildings/trees" after ONE empty load
- `connected-server-transient-failure-game` — "Not connected to a server" is NOT a transient failure for a game that has no server
- `places-json-one-asset-ui` — `places.json` is the one asset the UI reads, not the streamer
- `client-needs-own-request-budget` — The client needs its own request budget, not just the server's
- `multiplayersynchronizer-s-own-authority-decides` — A MultiplayerSynchronizer's own authority decides who sends
- `netlink-dead-peer` — A dead ENet peer: GetUniqueId/IsServer/RPC each log an error, per frame a flood; `NetLink.Ready/Online/IsServer`, `GetLocalNetPlayer` null while down
- `loopback-server-test-leaves-manifest` — A loopback server test leaves its manifest in the client's chunk cache
