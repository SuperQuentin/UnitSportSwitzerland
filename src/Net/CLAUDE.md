# Multiplayer and network streaming (`src/Net/`)

Client-authoritative transforms, terrain/asset streaming, chat, admin, RPC pitfalls. **Every new feature must be tested here, see the root `CLAUDE.md`.**

## Architecture

- **Multiplayer**: client-authoritative transforms, MultiplayerSpawner + Synchronizer,
  ENet port 7777. Server runs ChunkManager with BuildMeshes=false (grid-only, for
  height queries around players).
- **Terrain streaming** (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves
  generated files to clients that lack them. `IChunkSource` was already the seam, so
  `NetworkChunkSource` decorates `LocalChunkSource` with three tiers — shipped -> cache
  (`user://chunk_cache/`, overridable with `--cache`) -> server. The transfer unit is the
  **raw file**, cached under its ordinary filename, so the ordinary decoders read a streamed
  tile exactly like a shipped one and the server does no decoding. Files are sliced into 24 KB
  fragments on transfer channel 2 (bulk data on the default channel head-of-line blocks every
  position update behind it), deflated when that helps, and CRC-checked before being cached.
  `ClientTerrainSync` fetches the server manifest on join and merges its tile list into
  `ChunkManager._available` — without that merge the LOD rings skip unknown tiles and nothing
  is ever requested. It also saves that index to the cache, so tiles streamed in an earlier
  session are reachable offline.
- **Chat and commands** (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at
  `World/Chat` — the path must match, because Godot routes RPCs by node path. Clients only
  submit text and render replies; **every** decision (permissions, names, teleport
  destinations) is taken on the server, since a client-side permission check is one the client
  can edit. **Enter** opens the input, **/** opens it pre-filled, Up/Down walk the history.
- **Admin** (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge;
  the display name is a *request* that the server sanitises and deduplicates. Operators come
  from `user://admins.json` (granted on join) or `/login <pw>` against `--admin-password`.
  Without that argument `/login` is disabled entirely. `Net/ServerConsole` reads the dedicated
  server's own stdin on a background thread (`Console.ReadLine` blocks, so it cannot be on the
  main loop) and runs commands as peer id 0, which is always an operator — that is how the
  first admin gets granted on a fresh server. `PlayerInfo.AdminChanged` (any grant or loss: join,
  `/login`, `/admin`) makes the server's `ChatManager` send that client `AdminStatus`, which sets
  `Core/Permissions` so its menus can follow; `IsAdminPeer` exposes the check to other server
  systems. `NameAssigned` fires when a peer's name is set, for the bank, whose accounts are keyed
  by it.

## Commands

- Dedicated server with operators:
  `<godot> --headless --path . -- --server --admin-password <pw>`; type commands straight into
  its stdin (`/admin add <name>`, `/say ...`, `/tpall <town>`). Client: add `--name <n>`.
- Server binds the IPv6 wildcard (`::`, dual-stack) so it answers on every interface including
  Tailscale; `--bind <ip>` restricts it to one. **ENet is UDP** — a forwarded port must be a
  UDP rule and TCP-only tunnels (ngrok free, Cloudflare Tunnel) cannot carry it.
  `--stream-bandwidth <MB/s>` caps terrain streaming per client; the 3 MB/s default is
  24 Mbit/s each and is a LAN figure.

## Gotchas

- **Quitting while tiles stream used to crash the process.** `ChunkStreamer.FetchAsync` runs on
  worker threads and defers onto the main one; the workers outlive the tree, and deferring onto a
  freed native object is a 0xC0000005, not a managed exception. `_shuttingDown` is set in
  `_ExitTree` so the workers stop queueing before Godot frees anything.
- **Networked nodes created in code need deterministic names** — auto names
  (`@MultiplayerSynchronizer@N`) differ per process and break replication by path.
- **An RPC issued off the main thread does not throw — it never arrives.** `ChunkManager` loads
  and meshes tiles on the thread pool, so `ChunkStreamer.FetchAsync` runs there; the request
  and the connectivity check are both `CallDeferred` onto the main thread. The symptom of
  getting this wrong is silence: the tile simply stays blank forever.
- **"Refused because busy" must not be reported as "does not exist".** The first version sent
  one `AssetMissing` for both, and `NetworkChunkSource` cached it, so a momentary backlog
  blanked those tiles for the whole session. There is now a separate `AssetBusy`, and only a
  permanent miss is remembered.
- **`ChunkManager` records "this tile has no roads/buildings/trees" after ONE empty load.**
  Correct for local files, wrong over a network, so `NetworkChunkSource` retries transient
  failures internally rather than letting a null reach the manager.
- **"Not connected to a server" is NOT a transient failure for a game that has no server.**
  This one cost ten minutes on every cold start and hid for months behind plausible explanations.
  `NetworkChunkSource` falls through to the network whenever a local file is absent — and a
  `.holes` file is absent for **6,067 of 6,699 tiles**, because almost nothing has a tunnel. With
  no server, `ChunkStreamer` reported "not connected" as *transient*, so `FetchLoopAsync` retried
  five times with backoff — 0.4 + 0.9 + 2 + 4 = **7.3 s per tile** — while holding one of the six
  global fetch slots. Six slots over 7.3 s is a hard ceiling of **0.8 tiles per second** whatever
  the disk does. Measured before the fix: 887 s of worker time, **100%** of it in
  `holes+cover`, with `.terr` reads at 0%. After: 0.0 s, and a cold start of **0.6 s** where the
  budget had been 600. The fix is `ChunkStreamer.ServerReachable` — a per-frame snapshot, because
  connectivity is only knowable on the main thread — and `ObtainAsync` returning null immediately
  when there is no peer at all. Deliberately *no peer* rather than *not currently connected*: a
  client mid-join has a peer whose status is `Connecting`, and a null reaching `ChunkManager` is
  recorded as "this tile has no roads" for the rest of the session.
  **The lesson generalises**: profile the load path before optimising it. Tile size, mesh cost and
  LOD radius are the obvious suspects and were together under 5% of the time.
- **`places.json` is the one asset the UI reads, not the streamer** — so it was silently left
  out of `AssetKind` and a streaming client connected fine, pulled terrain fine, and showed an
  empty Tab search. It is now `AssetKind.Places`, fetched during sync into the cache, and
  `PlaceSearchUi.ReloadIndex()` re-reads it (the UI is built long before the connection).
- **The client needs its own request budget, not just the server's.** The LOD rings reach nine
  tiles out, so arriving somewhere new makes 361 tiles want their .terr at once — ~177 MB.
  Unbudgeted, the client floods the server, most requests are refused, and the retries fight:
  measured 1,135 refusals in 30 s while only 33 MB arrived. A six-slot semaphore in
  `NetworkChunkSource` took the same window to 226 MB at the full bandwidth cap.
- **A MultiplayerSynchronizer's own authority decides who sends.** Children added after
  the parent's `SetMultiplayerAuthority` default to server authority — set it explicitly.
- **A loopback server test leaves its manifest in the client's chunk cache.** `ClientTerrainSync`
  saves the server's index as `server-manifest.json`, and the next *offline* boot merges it,
  retires the generated world and loads a tile that does not exist — an empty world, `prims=4`.
  Give a test client its own `--cache <scratch dir>`, or delete that file afterwards.
