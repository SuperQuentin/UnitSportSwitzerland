# Region-wide files from the server: `ClientTerrainSync.SyncFileAsync`

## Rule
- A new region-wide file a client pulls once per session (tile 0,0 of an `AssetKind`, like horizon
  and places) MUST go through `SyncFileAsync(kind, fileName, what, missingMsg, ct, onDone)` in
  `src/Net/ClientTerrainSync.cs`; put only the file-specific log and event in `onDone`.
- Do not copy `SyncHorizonAsync`/`SyncPlacesAsync` bodies again.

## Why
#221 investigation 3/3, cluster #15: the two methods were the same 30 lines. Pure consolidation, no runtime change.

## Same logic, preserved
- The file lands in `TerrainPaths.FindCacheDir()` under its format's `FileName` BEFORE `onDone` runs,
  so `HorizonReceived` / `PlacesReceived` listeners can read it from disk.
- Server without the file: one `GD.Print("[stream] <missingMsg>")`, no event. Write failure: one
  `PushWarning("could not cache the <what>")`, no event.
- `onDone` runs on the sync task's thread (`ConfigureAwait(false)`), as before: listeners must defer to the main thread.

## Migrating old code / open branches
- A branch editing `SyncHorizonAsync` or `SyncPlacesAsync` bodies conflicts: put a fetch/write change
  into `SyncFileAsync`, a file-specific change into that file's `onDone` lambda.
- A branch adding a third copy (`grep -n "FetchAsync(AssetKind\." src/Net/ClientTerrainSync.cs`):
  replace it by a `SyncFileAsync` call.

## How to check
Server with real chunks + `--connect 127.0.0.1` client: the client log shows
`[stream] place index received: N towns` and `[stream] horizon received: N KB`.
