# Judge a terrain blend by shaded relief, and measure what the eye finds

- **Judge a terrain blend by shaded relief, and measure what the eye finds.** Every seam, resolution
  and slope check passed while hillshades showed streaks, a comb along the edges and creases. Each
  became a number in `tools/BlendCheck` (seam kink, streak RMS against the ground's own relief along
  lines parallel to an edge) and `--render` writes the before/after hillshades to
  `test_output/blend/`. Two traps in the measures themselves: a moving-average high-pass lets km-scale
  relief through and reads it as streaks (use a local quadratic fit), and continuing a steep 1 m
  slope across the whole detail band extrapolates metres — keep a continued slope to one coarse
  cell.
