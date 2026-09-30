# A land-use polygon must never overwrite Water in the cover raster

- **A land-use polygon must never overwrite Water in the cover raster.** The layers are stamped
  in order of increasing specificity, which is right for land use — an allotment inside a park
  should win — but a `nutzungsareal` polygon is an administrative boundary, not a ground surface,
  and several are drawn straight across a river. The gravel extraction areas beside the Rhône at
  Riddes are mapped as `Abbauareal` *over the water*, which erased the river from the raster: the
  Rhône rendered as a gap in the middle of its own course. `CoverExtractor.MarkIn` now refuses to
  overwrite `Water`. A quarry does not flow.
