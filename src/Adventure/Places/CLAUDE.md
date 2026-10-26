# Adventure places (`src/Adventure/Places/`, #1224)

The places of the Adventure layer: the POI catalogue now; discovery, fog, viewpoints, the adventure map
and the Atlas follow (`docs/plans/adventure-places.md`). How `pois.bin` is built is in `tools/CLAUDE.md`.

Index only: one line per note in `docs/notes/adventure/<name>.md`.

- `poi-catalogue` — `PoiCatalogue` (Resolve by tag + hash and aliases, `Near` without allocation, tiles, cantons, lakes, `Fixture`), `PoiCatalogueHost` (boot load, `PoisReceived`, `--pois fixture`), `AssetKind.Pois = 17`, `--poischeck`
- `poi-pipeline` — `PoiStage` / `pois.bin`: sources, ids, tiers, cantons, counts nationwide and for the dev region, `--wikidata`
