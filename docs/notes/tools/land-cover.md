# Land cover

- **Land cover**: **six** TLM area layers are rasterised onto the 501x501 vertex lattice ->
  `.cover` (deflate, ~2 KB/tile) -> baked into terrain vertex colours by `CoverPalette`.
  They are drawn in order of increasing specificity, each overriding the last:
  `tlm_bb_bodenbedeckung` (forest, rock, scree, boulders, water, wetland, glacier,
  snowfield) -> `tlm_areale_nutzungsareal` (vineyard, orchard, nursery, allotment,
  cemetery, park, quarry, landfill, industrial, institutional, clearcut, military) ->
  `tlm_areale_freizeitareal` (sports ground, golf, pool, campsite, zoo) ->
  `tlm_bauten_sportbaute_ply` (the pitch itself, tighter than the surrounding ground) ->
  `tlm_bauten_verkehrsbaute_ply` (runways, grass strips, station platforms) ->
  `tlm_areale_verkehrsareal` (parking, last so a car park beats everything).
  Unmapped ground falls back to altitude bands — TLM maps **no arable parcels at all**, so
  farmland, meadow and the ground between village houses genuinely are not in the data.
- **Cover overrides** (#84): `docs/data/cover_overrides.json` holds hand-traced polygons (LV95 E,N
  rings, a `CoverClass` name, a `source` note each) for ground TLM does not map, e.g. the parking
  rows at Riddes house 15, traced from the owner's aerial photo. `CoverExtractor.StampOverrides`
  stamps them after the six layers (so they win) and before the trees (so a row paved over an
  orchard is not planted), never over Water (`MarkIn`). Picked up automatically when the
  preprocessor runs from the repo root; `--cover-overrides <file>` points elsewhere. Parking
  classes get the bay pattern from `CoverFormat.PatternFor` like TLM car parks. Redo only the
  touched tile with `--cover-only --tiles-file` (see `commands`). The bay grid is world-aligned,
  so bays in a row that is not N-S/E-W do not follow the row.
