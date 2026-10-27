# /locate and chat coordinates (#1277)

`/locate <thing> [+ selector ...] [in <town>]`: the nearest things of a kind, searched by the machine that owns the
world (the dedicated server; the client offline) in the map data it already holds. Every answer line ends in `@E,N`
(LV95 metres), and any chat line carrying one, whoever typed it, gets **Teleport / Track / Map / Copy** under it.

## Files

- Pure, tier 0 (`LocateTests`):
  - `src/World/Locate/LocateQuery.cs`: the vocabulary (English only, decided) and the parser.
  - `LocateCoords.cs`: `@E,N` find, format, highlight and label; distance; compass.
  - `BuildingIndex.cs`: per building, the plan centre, kind, floors, year, height and flags (`Church`, `Spire`, `GarageRoll`, `Bank`), bucketed per km, with a cache format.
  - `LocateEngine.cs`: the search and the answer's lines.
- Godot side:
  - `BuildingIndexBuilder.cs`: builds the index from `.bldg` tiles, and its cache.
  - `LocateService.cs`: the node. It gathers the lists, runs the garage check, and answers.
  - `LocateClient.cs`: `/track`, `/map`, and the map's pin.
  - `LocateCheck.cs` and `LocateNetProbe.cs`: the checks.

## Sources (nothing new from the preprocessor; decided)

| Words | Source |
|---|---|
| town (and peaks, passes) | `places.json` |
| peak, pass, summit cross, waterfall, lake, tower, church, chapel, castle, covered bridge, dam, station, bus stop, landing, cable car, hut | `PoiCatalogueHost.Current` (`pois.bin`). A noun only these answer says so when the region has no `pois.bin` |
| harbour | `Landings` + POI ship landings, deduplicated at 60 m |
| castle | `Castles.List` (the solids), the POIs too |
| cinema, bowling, arcade, restaurant, cafe, bar, pub, nightclub, beer garden, fast food | `Venues.List` |
| stadium, arena, open air | `StadiumCatalog` |
| ikea | `Landmarks.Ikea` |
| airport | `AirportStands.Instance.Index` (where the airports system runs) |
| steamer | live: `VehicleManager`'s `VehicleBody` of `RideKind.Steamer` at each search (a steamer someone is riding is not listed) |
| building, house, apartment building, commercial, industrial, farm, school/public, garage, construction site, bank, church | the building index |

Selectors stack (`+`, `with`, `and`, or before the noun):
- `spire`
- `parking entrance`
- `tall` (≥ 30 m)
- `old` (< 1900)
- `new` (≥ 2010)
- `<n> floors`
- `<n> m`

## The building index

- **Built by the server at boot** on a quarter of the cores, from its own tiles (`LocalChunkSource`, not the shared 32 MB cache).
- **Cached** at `user://cache/locate/buildings-<key>.bin`. The key is a SHA-256 over the manifest's tile hashes, `BuildingIndex.Version` and `BuildingIndexBuilder.BuildingRules`. A manifest without hashes (a fixture) builds every time.
- **Offline**, the client builds it on the first `/locate` (`OnDemand`). Online it never does.
- **Measured** (Lake Geneva set, 825 tiles, 86 520 buildings):
  - Decoding alone: 0.7 s.
  - First version of the index (`BuildingTypes.Detect` on every tile): 18.9 s. Most of that was `PlanBox.Of`.
  - Now: 5.8 s. Boxes are made only for the kinds a rule reads (`BuildingTypes.ChurchPart`, plus apartments and commercial), and the church grouping (`BuildingTypes.Churches`, extracted from `Detect`, same result) runs only on tiles with a Sacral solid.
  - About 50 s for the dev region on its first boot (estimated). Read back from the cache: 0.12–0.14 s for the Lake Geneva set.
- **Round the players first** (owner's call). The index fills in place (`BuildingIndex.Growing`, `AddTile` under a lock), its tiles read in `TileQueue` order:
  - nearest the players (their positions once a second, `LocateService` `_Process`; the region's start before anyone is in);
  - an asker's surroundings ahead of everyone (`Prefer`).
- **A partial answer** (`LocateEngine.Partial`):
  - With 3 hits and no unread tile nearer than the third (`BuildingIndex.CoveredRadius`: how far round the point every tile is in), it is the true nearest and stands alone.
  - Otherwise (fewer than 3, or an unread tile nearer) it says so: "The building index isn't finished (everything within 3.2 km is in): these are the nearest found so far, a nearer one may still come. Still indexing the buildings: 40 %, about 15 s left."
  - The time left (`LocateEngine.Indexing`) is the pace so far carried over the tiles left, given only after 8 tiles and a second: after one tile it said 2 min for a 6 s build.
  - A parking answer is judged the same way, once its doors are checked.
  - Measured in Lausanne with no cache: 1 of 825 tiles in, `/locate apartment building` already had its 3 true nearest; the next queries carried the note until their rings were in.
- **Bump `BuildingRules`** when the church grouping, the steeple rule or the garage roll changes.
- **Spire.**
  - The church group has a tower solid, or the hall carries its steeple in its own solid (`Steeple`): rising at least `max(12 m, 0.9 × its narrow side)` above the eave, with the top 3 m no wider than 8 m.
  - Lausanne cathedral (75 m over a 36 m eave, one solid) needed the second rule: with towers alone the region had two churches with a spire.
- **Parking entrance.**
  - The index only says the roll and the box take a ramp (`GarageRule.RampFor`, either side as the front).
  - `LocateService.GaragesAsync` then runs the real footprint (`BuildingFootprint.Compute`, a tile's candidates together, its road index built once) on the 40 nearest candidates. It keeps the first 3 with a ramp door and **moves each hit onto the door**.
  - About 2 s in Lausanne.

## Chat

**Commands**
- `/locate`, also `/find`: server, or offline `LocateLocal`.
- `/go @E,N`: server `CommandGo` on `/city`'s terms (`MayTeleport`, refused under `--no-teleport`, admins always). `/tp E N` stays admin-only.
- `/track @E,N [name] | off` and `/map @E,N [name]`: this client alone (`LocateClient.Run` in `ChatManager.Send`).
  - Track sets `AdventureService.TrackPlace`, so navigation's marker, compass and gust follow it (#1226). The same point again untracks it.
  - Map opens `AdventureMap.OpenAt` with a red pin (`LocateMapPin`, an `IMapOverlay`). It needs `AdventureMap.Available`.

**`ChatUi`**
- Colours every `@E,N` amber.
- Draws each one with a pin before it (`Icons.Pin`); pin and coordinates are one link (`PushMeta`, "E,N|name"), kept on one line (a no-break space, a word joiner after the comma).
- No buttons under the line (owner's call): a click on the link opens a small choices list beside it (`ShowChoices`): Teleport (hidden when `Permissions.CanTeleport` is false), Track, Show on map, Copy. Esc / B or a click beside it closes it, the chat staying open.
- A scrollback line holding a point takes focus, so a pad reaches it; A opens the choices of its first point (`OpenChoicesOn`).
- The choices send the typed commands, so the server's rules apply.
- The name a choice passes is `LocateCoords.LabelOf`: an answer's place name, "Anna's point" for a said line, else the coordinates.

**The "/" picker (pad and VR)**
- A toggle left of the input shows the completions as focusable buttons (`HFlowContainer`), then Back (one word), Send and Close.
- A bare `/` lists every command, uncapped.
- The quick menu's **Chat** wedge opens the chat on the picker with its first button focused (`ChatUi.OpenForPad`). Pad B (`ui_cancel`) closes it.
- VR sees the same panel on `XrUi` and presses with the ray.
- Free text (a name, a town) still has no pad or VR keyboard: `xr/vr-action-map` "chat".

## Controls

- **Keyboard and mouse:** `/locate …` typed, Tab completion, a point's pin or coordinates clicked.
- **Pad:** hold Back → Chat → D-pad and A through the picker; up to a line with a point, A, then its choices. B closes.
- **VR:** Menu hold → Chat → the ray.

## Bots (#837)

`LocateService.FindAsync(query, e, n, max)` is a plain server method; there is a row in `bots/bot-capability-map`.

## How to check

- `tools/test.sh unit`: `LocateTests`.
- Quick: `--locatecheck --world fixture --chunks fixture:garage --pois fixture --systems ui`. It covers the index, the fixture castle and lake, the blocks' garage doors, the cache round trip and completion.
- On a real region, `--chunks <dir> --at E,N --locatecheck "q1|q2"` prints the answers and their times; add `--locateearly` (and delete `user://cache/locate/`) to ask while the index is still filling.
- `--chatcheck` covers completion, a point's link opening its choices (and Esc closing them first), and the picker walked by its buttons.
- Screenshots: `--chatopen 40 --chatsend "/locate castle" [--chatchoices] --uishot <png> 44`; `--chatpicker "<text>"` for the picker.
- Net: `tools/locatenetcheck.sh` checks the server's answers, that the client built no index, and that `/go` lands on the garage door.
