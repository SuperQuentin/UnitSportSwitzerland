# Multiplayer and network streaming (`src/Net/`)

Client-authoritative transforms, terrain/asset streaming, chat, admin, RPC pitfalls. **Every new feature must be tested here, see the root `CLAUDE.md`.**

Index only: one line per note in `docs/notes/net/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/net`.

## Architecture

- `multiplayer` — Multiplayer: client-authoritative transforms, MultiplayerSpawner + Synchronizer, ENet port 7777. Server runs...
- `terrain-streaming` — Terrain streaming: (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves generated files to clients...
- `chat-commands` — Chat and commands: (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at `World/Chat` — the path must...
- `admin` — Admin: (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge; the display name is a...

## Commands

- `commands` — Commands: --admin-password, --bind, --headless, --name, --path, --server, --stream-bandwidth

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
