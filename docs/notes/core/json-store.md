# JSON saves go through `Core/JsonStore` (#221, PR #228)

## Rule

- Persist every JSON file (anything under `user://` or a server data folder that must survive a
  restart) **only** with `Core.JsonStore.Save(path, value, options)`.
- **Never** save with `Godot.FileAccess.Open(..., ModeFlags.Write)` + `StoreString`,
  `File.WriteAllText` / `File.WriteAllBytes` of JSON, or a hand-made `.part` + `File.Move`.
- Never build `new JsonSerializerOptions { ... }` per save: use the file's own
  `private static readonly JsonSerializerOptions`, `JsonStore.Indented`, or none (compact, default naming).
- `path` may be an OS path or `user://...`; `Save` creates the folder. It **throws**: keep the
  caller's `try/catch` + `GD.PushWarning`. Reading stays as it is (`FileAccess`/`File.ReadAllText`).

## Why

Godot `FileAccess` Write and `File.WriteAllText` truncate the file first: a crash, kill or full disk
mid-write left an empty or half file, and the next start lost the bank, inventory, journal, server
list... (8 such writers found in #221). `Save` writes `<path>.<guid>.part` then moves it over the
target, so the file is always the old or the new version. No perf numbers: this is a safety change.

## Same logic, preserved

- Same bytes on disk: UTF-8 without BOM (what `StoreString` wrote) and each file keeps its options,
  so old saves load unchanged (`placedcheck` before/after: `cmp` identical).
- The temp name is unique on purpose: `CdLibrary` saves the same index from a worker thread and the
  main thread. A fixed `path + ".part"` lets one save move the other's temp away (the bug in
  `docs/notes/net/terrain-streaming.md`).
- Trap: `JsonSerializer.Serialize<T>` uses the **static** type. Pass the same expression the old
  code serialized (`this`, `data`, `_accounts`), not a base type or `object`, or properties go missing.
- Trap: changing a file's options (naming policy, `WriteIndented`, ignore conditions) changes its
  format; old saves may stop loading. Keep them.

## Migrating old code / open branches

After rebasing onto main, grep:
`rg -n "ModeFlags.Write|WriteAllText\(|\.part\"|new JsonSerializerOptions" src --glob "*.cs"`.
Every hit that writes a persisted JSON file becomes one `JsonStore.Save` (or `SaveAsync`) call. Already done on main:
`PlacedObjects`, `BirdJournal`, `ServerBook`, `PlayerRegistry`, `OccasionConfig`, `OccasionHunt`,
`PhotoStore`, `CdLibrary`.

Also done (#221 round 2): `Bank`, `Inventory`, `LootService`, `GameSettings` (`Save` and `SaveOnly`),
`CdBurner` (CD info). Left: `src/Interiors/InteriorManager.cs` layout cache (`path + ".part"` +
`File.WriteAllTextAsync` + `File.Move`; already atomic, in open PR #269): make `InteriorLayout.Json`
`internal`, then `JsonStore.Save(path, layout, InteriorLayout.Json);`.

Saves made while playing (a plant, a claim, a deposit) use `JsonStore.SaveAsync`: same file, same
bytes, written by the background writer (`perf-saves-background`).

A branch that adds a new save: use `JsonStore.Save` from the start.

## How to check

- `dotnet build UnitSportSwitzerland.csproj`, then the grep above shows no persisted-JSON writer.
- `tools/placedcheck.sh` saves, restarts the server and reloads (RESULT: ok). To keep the real saves
  untouched on Windows, `export APPDATA=<abs path>/test_output/appdata` first: `user://` lands there.
  For a format check, run it on main and on the branch and `cmp` the JSON files.
