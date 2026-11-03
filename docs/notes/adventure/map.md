# The adventure map (#1224)

`src/Adventure/Places/AdventureMap.cs`, a `Screen` on the shell's stack; `src/Ui/MapView.cs` (the shared maths).

## Rules

- **M opens it while adventuring** (`AdventureMap.Available`: a places service and `AdventureLog.Adventuring`);
  M or Esc / B closes it. In a Battle Royale round M stays `BrMap`; with the adventure off, M is the place
  search as before. The side panel's "Town search" button opens the old search (teleporting where the server
  allows it); navigation's travel rules come with #1226.
- **`MapView`** holds the centre, the zoom steps, the size, `ToScreen` / `ToLv95` / `Bounds`, `Pan`, `ZoomAt`,
  `DrawSampled` (a lattice texture sampled for the view) and the canton borders in one `DrawMultiline`.
  `MapCanvas` (MapSetup's tile picker) uses it; `--mapcheck` covers it.
- **Layers, redrawn only on a change** (a pan, a zoom, a fog delta through `FogTexture.Version`, the places
  section): the relief (`MapRelief`), the fog (a child drawn with `fog_map.gdshader`), then the top layer:
  borders, places, the player's arrow, every `IMapOverlay`, the centre cross.
- **Places shown**: the found ones (a light disc with the kind's glyph in red), landmarks sighted under
  revealed fog as a grey "?"; nothing under fog; minor places only when found and at 32 px/km or more; labels
  from 16 px/km, decluttered.
- **`IMapOverlay`** (`Draw(MapView, CanvasItem)`, `Click(Vector2 lv95, MouseButton)`, lv95 relative to
  `AdventureMap.LatticeE/N`): `AdventureMap.AddOverlay` for navigation's pins and travel points.
- **Search** lists only places found or sighted (12 at most). Selecting a place shows its card (`PlaceCard`):
  Track / Untrack (`AdventureService.TrackPlace`, `service`), Show in Atlas.
- **Devices**: mouse drag, wheel, click; pad left stick pans, LB / RB zoom, A selects the place nearest the
  centre cross; touch drag and pinch; VR the `XrUi` panel's ray (the shoulders do not zoom in VR: the wheel or
  the ray). Party members are not drawn yet.

## How to check

`--placescheck` opens it and selects a place (windowed: `test_output/places_map.png`).
