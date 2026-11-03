# Adventure places (`src/Adventure/Places/`, #1224)

The places of the Adventure layer: the POI catalogue, discovery, fog, viewpoints and markers, the adventure map
and the Atlas (`docs/plans/adventure-places.md`). How `pois.bin` is built is in `tools/CLAUDE.md`.

Index only: one line per note in `docs/notes/adventure/<name>.md`.

- `poi-catalogue` — `PoiCatalogue` (Resolve by tag + hash and aliases, `Near` without allocation, tiles, cantons, lakes, `Fixture`), `PoiCatalogueHost` (boot load, `PoisReceived`, `--pois fixture`), `AssetKind.Pois = 17`, `--poischeck`
- `poi-pipeline` — `PoiStage` / `pois.bin`: sources, ids, tiers, cantons, counts nationwide and for the dev region, `--wikidata`
- `places` — the travel rule (`PlacesRules`: on foot or a light aircraft), `PlacesService` at World/Adventure/Places (1 Hz discovery, two hits, jumps and `Arrived`, the places section, events, `"reveal"`), markers and E / VR grip, `ServerInteract`, the stamp toast, bots' `Enroll`, protocol 51
- `fog` — 250 m cells in `FogMask`, the fog section saved but never `SectionSync`ed (`FogFull` / `FogDelta`), the L8 texture and `fog_map.gdshader`
- `map` — M opens `AdventureMap` while adventuring, `MapView` shared with MapCanvas, layers redrawn on change, `IMapOverlay` for navigation, search and Track
- `atlas` — the Journal's Atlas tab: chapters, Landmarks and Map bars, stamps, cards (curated `atlas_cards.json`, generated lines, Wikidata `atlas_facts.json` as `AssetKind.AtlasFacts`), milestones
