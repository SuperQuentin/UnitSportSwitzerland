# Carrying a height across a plan-view move is only safe where the ground is flat

- **Carrying a height across a plan-view move is only safe where the ground is flat.** The
  rewrite keeps each vertex's altitude from the original line at the nearest point — which is
  right, because the originals hold the drape, the surveyed deck heights and the approach ramps
  that re-draping would destroy. Region-wide that costs a mean of 8 mm and a p99 of 9 cm over
  9.3 M samples, but the worst case was **12 m**: a footpath on a cliff lip, moved 0.48 m, where
  swissALTI3D drops tens of metres between adjacent cells. Two bounds fix it — `MaxOffset` keeps
  smoothing inside the road's own width, and the rewriter's cliff guard snaps the remainder back
  onto the surveyed line (230 vertices in the whole country). Measure this with
  `--rewrite --dry-run`; never assume it.
