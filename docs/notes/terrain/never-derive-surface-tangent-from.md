# Never derive a surface tangent from the screen-space normal

- **Never derive a surface tangent from the screen-space normal.** The derivative normal
  jitters per pixel, so a facade grid built on it turns to confetti. Bake per-vertex UVs
  from the exact triangle normal instead, and fade fine patterns out with `fwidth` before
  they hit the 0.35x internal render resolution.
