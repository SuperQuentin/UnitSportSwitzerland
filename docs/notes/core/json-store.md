# JSON saves go through `Core/JsonStore` (#221)

- Persist any JSON file with `JsonStore.Save(path, value, options)`: it writes
  `<path>.<guid>.part`, then moves it over `<path>`, so a crash or a full disk mid-write keeps the
  previous file whole (Godot `FileAccess.Open(..., Write)` truncates first: a crash lost the file).
  Takes OS paths and `user://` ones (`ProjectSettings.GlobalizePath`), creates the folder, writes
  UTF-8 without BOM (same bytes as `FileAccess.StoreString`). It throws: the caller keeps its try/catch.
- Options: keep the file's own `static readonly JsonSerializerOptions` (naming, enum converters), or
  `JsonStore.Indented`, or none (compact, default naming). Never `new JsonSerializerOptions` per save.
- Users: `PlacedObjects`, `BirdJournal`, `ServerBook`, `PlayerRegistry` (admins), `OccasionConfig`,
  `OccasionHunt`, `PhotoStore` sidecars, `CdLibrary` indexes. Not yet (open PRs touched them at the time):
  `GameSettings`, `Bank`, `Inventory`, `LootService`, `InteriorManager` (the last two already atomic).
- Check that exercises it: `tools/placedcheck.sh` (saves, restarts the server, reloads). To keep a
  run off the real saves, set `APPDATA` (Windows) to a folder under `test_output/` first: `user://`
  then lands there.
