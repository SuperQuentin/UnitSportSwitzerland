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
Every hit that writes a persisted JSON file becomes one `JsonStore.Save` call. Already done on main:
`PlacedObjects`, `BirdJournal`, `ServerBook`, `PlayerRegistry`, `OccasionConfig`, `OccasionHunt`,
`PhotoStore`, `CdLibrary`.

Left on purpose because open PRs were editing them during #228. The author of the named PR migrates
the file after rebasing (no conflict expected: main did not touch these lines):

| File (PR) | Today | Replace with |
|---|---|---|
| `src/Items/Bank.cs` `SaveAccounts` (#219) | `DirAccess.MakeDirRecursiveAbsolute(...)` + `FileAccess.Open(file, Write)` + `StoreString(Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }))` | `JsonStore.Save(file, _accounts, JsonStore.Indented);` in a `try/catch` with `GD.PushWarning("[bank] could not write ...")`; drop the `MakeDir` |
| `src/Items/Inventory.cs` `Save`/`ToJson` (#225, #222, #188, #180) | `FileAccess.Open(File, Write)` + `f?.StoreString(ToJson())`; `ToJson` builds new options | Move the options (`WriteIndented = true`, `WhenWritingNull`) to a `private static readonly JsonSerializerOptions SaveJson`; build the `SaveData` in a helper; `JsonStore.Save(File, data, SaveJson)` in a `try/catch`. Keep `ToJson()` (same helper + `SaveJson`) if anything else calls it |
| `src/Loot/LootService.cs` `Save` (#219, #201, #197) | `CreateDirectory` + `path + ".part"` + `WriteAllText(Serialize(t))` + `File.Move` | `JsonStore.Save(PathFor(k), t);` (compact; keep the `catch`) |
| `src/Interiors/InteriorManager.cs` layout cache (#219, #197) | `path + ".part"`, `await File.WriteAllTextAsync(tmp, layout.ToJson())`, `File.Move` | Make `InteriorLayout.Json` `internal`, then `JsonStore.Save(path, layout, InteriorLayout.Json);` (a small sync write; already atomic, so low priority) |
| `src/Core/GameSettings.cs` `Save` (#148) | `FileAccess.Open(File, Write)` + `StoreString(Serialize(this, JsonOptions))` | `JsonStore.Save(File, this, JsonOptions);` (keep the `try/catch`) |

A branch that adds a new save: use `JsonStore.Save` from the start.

## How to check

- `dotnet build UnitSportSwitzerland.csproj`, then the grep above shows no persisted-JSON writer.
- `tools/placedcheck.sh` saves, restarts the server and reloads (RESULT: ok). To keep the real saves
  untouched on Windows, `export APPDATA=<abs path>/test_output/appdata` first: `user://` lands there.
  For a format check, run it on main and on the branch and `cmp` the JSON files.
