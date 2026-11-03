# The Swiss Atlas (#1224)

`src/Adventure/Places/AtlasPage.cs` (the Journal's Atlas tab), `PlaceCard.cs` (the card, shared with the map),
the pure `AtlasProgress.cs` (chapters, completion, milestones) and `AtlasFacts.cs` (texts, the cards file).

## Rules

- **The tab**: `AtlasPage.Register()` (the client world) -> `AdventureLog.RegisterTab("atlas", "Atlas", 20, ...)`,
  shown while adventuring. Left: the six chapters (`AtlasChapters`: Romandie, Mittelland, Central, Eastern,
  Ticino, Alps; each canton in one) with only the cantons this map has, each a button with a Landmarks bar
  (tier 1, 2 and curated, found / this map's) and a Map bar (`fog`). Right: the canton's landmarks as stamps,
  then its tier 2 places by kind (found and sighted ones shown, all counted), then "Minor places" folded. A
  stamp opens its card; "Show on map" opens the adventure map on it. Rebuilt on `SectionChanged("places")` only.
- **Counted against this server's catalogue** ("of this map"): ids are stable, so a bigger map later keeps
  every stamp.
- **Milestones** at 25, 50, 75 and 100 % of a canton's landmarks, each raised once (`places.Milestones[canton]`
  keeps the highest); a chapter whose cantons on this map are all at 100 % raises `ChapterCompleted` once
  (`Milestones["chapter:<name>"]`). The records are in `PlacesProgress.cs`.
- **Card text**, three layers: a curated card's fact (`res://assets/adventure/atlas_cards.json`), else
  `AtlasFacts.For(poi, catalogue, wiki)`: a generated line (a summit's rank by height in its canton on this map
  and the nearest station, a lake's area, a cable car's rise and length from its other end, a station's nearest
  summit...) plus the Wikidata facts (year, length, area). English, invariant numbers, a thin space as the
  thousands mark.
- **Curated cards** (`{ alias, id, title, fact }`): read at runtime by `PoiCatalogueHost.ApplyCards` before the
  catalogue is shared: the aliases resolve, and the POIs are landmarks (`IsLandmark`: a stamp post) whatever
  tier the file gave them. The first 35 (dev region and nationwide icons; ids computed from the TLM uuids and
  checked against the dev region's `pois.bin`); the rest of the ~200 is a later content PR. The quests' places
  must be card ids (`QuestCatalogTests`). Facts to double-check in review: the TLM's Dufourspitze 4 633 m and
  Mönch 4 106 m against the usually quoted 4 634 m and 4 107 m.
- **Wikidata facts** (`atlas_facts.json`, the preprocessor's `--wikidata`): beside `pois.bin`, synced to clients
  as `AssetKind.AtlasFacts = 18` (`net/region-file-sync`), read lazily by `AtlasFactsStore` from the
  `--pois <file>` directory, the chunk dir, then the cache. Missing: the generated line alone.
- **Stamp** (`PlaceCard.StampMark`): drawn in `_Draw` (rings, the kind's glyph, the canton), red ink when
  found, faint when not. The card says the date and how ("on foot", "on skis", "by air"...).

## How to check

`AtlasProgressTests`, `AtlasFactsTests` (invariant under `fr-CH`, Wikidata by tag and hash, card problems, the
shipped cards well formed); `--placescheck` opens the tab (windowed: `places_atlas.png`, `places_atlas_card.png`).
