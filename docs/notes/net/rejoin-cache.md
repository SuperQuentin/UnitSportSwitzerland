# Rejoining a server: what the client keeps, what it stops waiting for (#1380)

A player who joined a server before spawns faster by reusing that visit's data and by not waiting
for what spawning does not need. `tools/joinbench.sh` measures it (cold = empty `--cache`, warm =
the same cache again; `[world] ready after N s`).

- **The tile index is cached per server.** `ClientTerrainSync` saves the manifest it received as
  `server-manifest-<address>-<world>-<hash>.json` in the cache dir (`JoinCache.ManifestFileName`;
  key = address:port + the world id, so a hosted test world on 127.0.0.1:7777 never reads the
  open world's index). `ClientWorld` merges it into `ChunkManager._available` at boot
  (`MergeServerIndex`), before the first frame: the rings ask for tiles at once and the loading
  screen does not wait for the server (`CachedIndexMerged` makes `IndexFinished` true). The old
  `server-manifest.json` stays for offline play (it needs the origin check; a client with no local
  terrain never matched it, so every join re-merged 6,699 tiles).
- **One digest instead of re-downloads** (`AssetKind.Digest` 19, `AssetDigest`, `JoinCache`): the
  server answers with the SHA-256 of the index as it serves it (file or `ManifestOverride`) and of
  places, landings, ropeways, pois, atlas facts and the horizon (hashes cached by length and time:
  the horizon is 11 MB). No new RPC and no protocol bump, as for `HttpBase`: an older server answers
  "missing" and the client fetches everything as before. A file the digest does not list is not asked
  for. A cached copy with the server's hash is not fetched (its `onDone` still runs on the cached
  bytes so a boot that read the local source reloads it; not the horizon, which the world read at boot,
  so there is no second horizon pass).
- **The region files are fetched in parallel** (`Task.WhenAll`), beside the mirror probe, and a busy
  server is asked again (three tries) instead of reading as "the server has none".
- **Changed tiles** (`JoinCache.ChangedTiles`): by content hash when both indexes carry one (manifest
  v2), else by height range. A changed tile loses its cached `.terr`, `.terrc` and `.water`.
  Limit: a tile the server dropped stays in `_available` for the session (an index only ever adds);
  a tile already loaded keeps its old data until the rings unload it.
- **The generated horizon's cache keeps both passes** (`FallbackChunkSource.SaveHorizonCache`): the
  boot pass and the pass after the index merge used to each save only their own blended tiles and
  erase the other's, so the second pass recomputed ~4.5 s at every launch. Now the file holds the
  union (cut back to this process's blends only past four times as many) and is read in one go.
  With the index merged at boot there is usually one pass.
- **The loading screen** (`ClientWorld.TrackLoading`): a join (and any menu launch) is Ready once the
  near tile is playable and the body is grounded; the horizon lattice finishes behind. The 25 s
  timeout stays. A command-line solo run (shots, probes) still waits for the horizon.
  The `DrawingHorizon` stage only shows when the horizon is unknown and nothing near is ready.
- **The server** sends `Arrival.Rules` before the player is spawned (`ServerWorld.OnPeerAccepted`)
  and logs `[server] peer N accepted: rules sent at X ms, player spawned and state sent at Y ms`.
  It is still before `Sleepers.WakeAt` (same ordered channel), see `net/arrival`.
- **Not a blocker:** the generated horizon's first pass runs on a pool thread (`HorizonLayer.Reload`),
  it never held "connecting" (the client logs `connected` before the pass ends); only its CPU use
  mattered, and a warm cache makes it ~1 s.

## Measured (Oct 2026, `tools/joinbench.sh`, 3 rounds, medians; Windows, busy machine)

Loopback server with the real terrain (6,699 tiles, `--stream-bandwidth 50`), client with no terrain of its
own and a scratch `--cache`; the cold run stays on until the horizon passes saved their caches.
`[world] ready after`:

| | Before (main) | After |
|---|---|---|
| Cold (empty cache) | 30.1 s (14.9-33.0) | 24.2 s (16.9-29.0) |
| Warm (same cache) | 15.2 s (10.8-19.2) | 1.3 s (0.8-1.5) |

Cold is dominated by the streaming and the CPU shared with the server on loopback, so its spread is
wider than the change. Warm: the index and region files come from the cache, the horizon is read once,
and the screen no longer waits for the lattice. Server side, `OnPeerAccepted` costs 2-3 ms
(`rules sent at 0 ms, ... state sent at 3 ms`): moving the rules first was free to do but is not where
the time went.
