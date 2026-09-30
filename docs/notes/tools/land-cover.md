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
