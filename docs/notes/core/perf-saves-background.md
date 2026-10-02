# Saves made while playing go through the background writer (#221)

## Rule

- A save triggered by gameplay (plant/take a placed object, a deposit/withdrawal, a loot take, an
  occasion claim, anything a player can repeat) calls `Core.JsonStore.SaveAsync(path, value, options, onError)`,
  never `JsonStore.Save` on the main thread. Users today: `PlacedObjects`, `Bank`, `LootService`, `OccasionHunt`.
- `SaveAsync` serializes **now** (that string is the snapshot; later changes to `value` are not in it)
  and hands the text to `Core.SaveQueue`: one background task, files written atomically
  (`SaveQueue.WriteAtomic`) in the order first queued, the newest text per file wins.
- Write errors arrive in `onError` **on the writer thread**: only log there (`GD.PushWarning`/`PushError`),
  touch no node. Serializing errors still throw at the call, keep the `try/catch`.
- Never mix `Save` and `SaveAsync` on the same path (a sync save could land before an older queued one).
- Anything that must read the file back in the same run calls `SaveQueue.Flush()` first.
- Settings, inventory, one-off writes from worker threads stay on `JsonStore.Save` (rare, client side).

## Why

Measured (Stopwatch, main-thread time per save, Windows, NVMe, indented JSON):

| file | sync `Save` | `SaveAsync` |
|---|---|---|
| placed, 10 objects | 1.13 ms | 0.05 ms |
| placed, 200 | 1.10 ms | 0.35 ms |
| placed, 2000 | 5.09 ms | 3.28 ms |
| bank, 200 accounts | 0.74 ms | 0.04 ms |
| bank, 2000 | 1.24 ms | 0.22 ms |

The file create/write/move cost (~0.6-1 ms each, more with an antivirus) left the server frame on
every plant/take/claim (investigation #8). Serializing stays on the main thread: past ~1000 entries
in one file, snapshot (deep copy) on the main thread and serialize in the writer instead.

## Same logic, preserved

- Same bytes: the same `JsonSerializer.Serialize(value, options)` call as before, UTF-8 without BOM.
- Nothing lost on quit: `Main._ExitTree` (after every child's, Godot exits children first) calls
  `SaveQueue.Flush()`; `AppDomain.ProcessExit` also flushes. A `kill -9` loses at most the saves of
  the last few milliseconds (the old synchronous save could be cut mid-file the same way, minus the atomicity).
- Order: one writer, per-file FIFO, last write wins; never an older text over a newer one.
- `SaveQueue` is plain .NET (no Godot), linked into the tier-0 tests (`SaveQueueTests`).

## Migrating old code / open branches

- grep `JsonStore.Save(` in code a player can trigger repeatedly (RPC handlers, item actions):
  replace with `JsonStore.SaveAsync(path, value, options, e => GD.PushWarning($"[area] could not write {path}: {e.Message}"))`.
- A branch that edited `Bank.SaveAccounts`, `LootService.Save`, `PlacedObjects.Save` or
  `OccasionHunt.Save`: keep main's `SaveAsync` line, re-apply your data change around it.
- Open PR #269 touches `PlacedObjects.cs` elsewhere (reach check, `Reposition`): no overlap expected.

## How to check

- `tools/test.sh unit` (`SaveQueueTests`: last write wins over 3000 interleaved saves, `Flush` waits
  for 20 x 2 MB files, a failed write is reported and the queue goes on).
- `APPDATA=<abs>/test_output/appdata tools/placedcheck.sh`: save, server restart, reload: RESULT ok.
