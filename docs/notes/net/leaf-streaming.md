# Leaf streaming: the server sends cube leaves to clients that lack them (#1655, G2 C7)

Under `--frame earth` a client without the region's `.cubl` bundles (a joining player, the phone) gets its
ground from the server, as flat tiles stream through `ChunkStreamer` (`net/terrain-streaming`). Without it,
D (#1500, earth by default, the LV95 runtime deleted) would leave them with no ground.

## How it works

- **The index first.** On join `ClientTerrainSync` (`WantsLeaves`, set by `ClientWorld` under earth) asks for
  `AssetKind.LeafIndex` (20): the server's `LeafIndex` (`src/Terrain/Leaves/LeafIndex.cs`), every leaf it holds on its
  own disk with its entry's size and its bundle's stamp (`LeafIndex.StampOf`: FNV-1a over the bundle's length and index
  rows). Built in memory from the server's `LeafStore` on the first ask (`ChunkStreamer.Leaves`, set by `ServerWorld`
  under earth), sent through the ordinary `RequestAsset`, deflated. 18 bytes a leaf.
- **The source chain.** `LeafStore.UseRemote(index, fetch)`: the leaves on this copy's disk first (never fetched), then
  the server's. `Has`/`IsReal`/`Keys`/`Count` cover both (`IsLocal`, `LocalKeys`, `RemoteCount` tell them apart). A
  remote entry is read through a `LeafFetch`: `NetworkChunkSource.FetchLeaf`, the tile cache, then the stream.
- **A leaf by key.** `RequestCubeAsset(requestId, kind, face, level, x, y, section)` (protocol 95): kind
  `AssetKind.Leaf` (21), section 0 the whole entry, else an entry holding that one `LeafKind` section
  (`LeafStore.OneSection`). `LeafStore.HeightsOf` asks for `Heights` alone (the globe's leaf level, the blend), `RoadsOf`
  for `Roads` alone (the traffic's graphs), `Get` for the whole. The answer is the ordinary `BeginAsset`/`AssetFragment`/
  `AssetMissing`/`AssetBusy`, metered per peer on channel 2. `ChunkStreamer.FetchCubeAsync` is the client side; the pyramid
  (#1656) takes the same RPC with its own kind (anything else answers "missing" today).
- **The cache.** `AssetStream.LeafCacheName`: `leaf_F0_L14_x_y_<stamp>[.s<section>].culf` in the tile cache
  (`chunk_cache`, `--cache`), beside the flat tiles: the same cap, Clear, trim and `SessionBytes`. A whole entry serves a
  section ask too. A rebuilt bundle changes the stamp: its leaves miss and are fetched again; the old files age out by
  the trim. A whole entry must be the index's size and carry its key (`Fits`), or it is not used.
- **Retries.** As for tiles (`NetworkChunkSource.FetchWithRetryAsync`: slots, coalescing, backoff, "the server has none"
  remembered). A leaf that still did not come returns null, and nothing remembers it as empty: `LeafStore.Get`,
  `LeafBlend.Summary`, `LeafCollisionSource.Prepared` and `EarthLeaves.Get`/`HeightsOf` drop a null instead of keeping
  it (`EarthLeaves` never merges the fill into a real leaf that did not come), `EarthWorld` asks again on its next plan,
  the globe frees the request (`_leafMissed`), and a collision patch built with no ground is built again when its leaf
  lands (`CollisionPatches.Rebuild(emptyOnly)`).
- **When the index lands** (`ClientWorld`'s `LeafIndexReceived`, main thread): `UseRemote` returns the leaves that
  changed; `EarthWorld.Refresh` drops what was made from the old view (the fill where a real leaf now stands, the blend's
  summaries, prepared collision), reads every loaded leaf again in place (`_stale`: the old one stays until the new one
  lands, a read begun before is dropped by `_generation`), and rebuilds every collision patch without releasing it (the new
  faces replace the body); `Globe.RefreshLeaves` does the same for its leaf patches. The same index again changes nothing.
- **A server on leaves alone** starts without flat tiles under `--frame earth` (`ServerWorld`: built leaves are terrain
  enough).

## Data controls (`core/data-usage`)

- Cache cap, Clear, trim and the session counter: the tile cache's, unchanged.
- **Low data:** the near leaves (900 m round each focus) are the only ground and always stream; a leaf's heights alone
  (the globe's leaf level past them) come from the disk and the cache only (`FetchLeaf`). At the region's border the fill's
  blend then reads a real neighbour only once it is held.
- The metered warning on join is the flat world's (`StreamEstimate`); its numbers are the flat world's.

## Checks

- `tools/leafstreamcheck.sh` (full tier, B4's leaves on D:): a server on leaves alone, client A with the leaves, client B
  with none and an empty cache; both run `--earthcheck` (ground, collision against heights, a dropped ball, streaming
  ahead of a body, roads and doors furnished), B again from its cache. `[earthcheck] streamed ...` lines give the data.
- `LeafStreamTests`: the index, the source chain (a leaf that did not come is asked again; sections alone; local first).

## Numbers

Geneva (B4's 2,412 leaves, the densest of the D: region), `tools/leafstreamcheck.sh`, three Godots and a server on one PC
(Nov 10 2026):
- The index: 42 KB.
- Client B with nothing (fill off): ground under the start at 20.8 s against 18.3 s for A on its own leaves; 16.3 MB on
  arrival; 94.9 MB in all over 4.5 km at 30 m/s, the ground never missing (0.0 s), the same rays, ball, re-anchor and
  furniture (roads in 16 leaves, 788 doors) as A.
- B again from its cache, fill on: whole leaves all from the cache; 133 heights sections (16 MB) the fill's blend reads
  round the real leaves.
- A whole Geneva leaf is ~2 MB raw; a heights section ~120 KB raw.
- An earlier run, before the two section slots and with the fill on: 194 heights asks (the globe, the blend) shared the
  slots with the near ground, arrival at 100 s for 57.5 MB.

## Not done

- The pyramid (`cube_global`) still ships as files: #1656 streams it by quadtree request through the same RPC.
- One index for the whole region (about 2 MB raw for Switzerland's ~110 000 leaves); a planet's would go per bundle.
- No HTTP mirror for leaves (entries, not files).
