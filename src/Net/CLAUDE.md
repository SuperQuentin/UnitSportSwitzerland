# Multiplayer and network streaming (`src/Net/`)

Client-authoritative transforms, terrain/asset streaming, chat, admin, RPC pitfalls. **Every new feature must be tested here, see the root `CLAUDE.md`.**

Index only: one line per note in `docs/notes/net/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/net`.

## Architecture

- `multiplayer` — Multiplayer: client-authoritative transforms, MultiplayerSpawner + Synchronizer, ENet port 7777. Server runs...
- `terrain-streaming` — Terrain streaming: (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves generated files to clients...
- `chat-commands` — Chat and commands: (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at `World/Chat` — the path must...
- `admin` — Admin: (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge; the display name is a...
- `vision-interest` — Vision-based interest: the server decides who sees whom (size at render resolution, sky, line of sight, race); out of view = despawned
- `perf-visibility-on-change` — Player synchronizers keep `VisibilityUpdateMode.None`: every change to what a peer may see must call `UpdateVisibility` (`RefreshNetVisibility`/`RefreshRelays`) where it happens
- `remote-interpolation` — Remote players are interpolated (NetGlobal/NetVel/NetTime at 30 Hz, Hermite on GlobalPos, bounded extrapolation, smooth render clock)
- `positions-on-the-wire` — Positions cross the network as LV95 doubles (GlobalPos), lists as an anchor + offsets, bodies through NetPlace; never a world Vector3 (#185)
- `protocol-handshake` — Connecting starts with Hello/Welcome (Handshake.Protocol); its RPCs never change; bump the protocol on any wire change
- `load-testing` — Load testing: --swarm bots, --serverstats, --netsmooth, tools/loadtest.sh; before/after numbers at 32 players and a 30-min soak
- `lean-dedicated-server` — Dedicated server: proxy players, fps cap, coarse grids, asset prep off the main thread, throttled vehicles
- `lan-discovery` — mDNS browse for `_unitsport._udp` (avahi on the server); the Multiplayer screen lists LAN servers, legacy unicast queries, `--discovercheck`
- `server-query` — UDP status query on port + 1 (`USQ1`/`USR1` + JSON): LAN broadcast list, saved servers' players and ping, `--server-name`, `--query-bind`
- `udp-receive-loop` — every UDP reader goes through `Udp.ReceiveLoop(udp, token, handler)`; never hand-write a ReceiveAsync loop
- `region-file-sync` — region-wide files a client pulls once (horizon, places) go through `ClientTerrainSync.SyncFileAsync`
- `hosting` — Host from the menu: the client starts itself headless as a server (`--parent-pid` watchdog), joins it, kills it on leave
- `clock-sync` — One shared clock: `ClockSync.ServerNow` from min-RTT ping/pong samples; song position and beat phase are functions of it, never of anything local
- `perf-server-frame-metrics` — Server cost = `--serverstats` per-frame busy + `[stats] slow frame` lines; wrap periodic jobs in `ServerStats.Ran`; never the 1 s-max `TimeProcess` monitor
- `perf-no-main-thread-periodic-jobs` — No per-player `GD.Print` on a server timer (blocks ms on Windows), file writes on a worker, `/proc` reads Linux-only
- `perf-mcp-logger-gated` — godot_ai `game_helper` adds its Logger only when `EngineDebugger.is_active()`; keep the gate on addon updates (it leaked every log line)
- `perf-player-snapshot-size` — `BodyPose`/`TrainPose` go as one `float[] NetPose` (quat + offset + squash, train angles only on a train); no Transform3D on the wire

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
