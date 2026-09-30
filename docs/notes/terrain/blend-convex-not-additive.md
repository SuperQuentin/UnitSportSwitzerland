# Blend two terrains with a convex mix, never an additive correction

- **Blend two terrains with a convex mix, never an additive correction.** The first blend was
  `G + (Rs − Gs)`: keep the generator's relief, shift it to meet the real low-pass. Every synthetic
  check passed — seams, resolutions, slope bound — and it still dug a **trench 150 m below the Rhône**
  on real tiles at Riddes, found only because a `--ride bike` dropped from 539 m to 324 m. Where a
  steep generated flank meets a real valley floor, the flank's fall away from the seam is kept at
  full size, so the ground drops below both surfaces. `(1 − W)·G + W·R` always lies between them.
  `tools/BlendCheck` now checks exactly that ("blended ground past both surfaces"), and the lesson
  generalises: test a terrain blend on the worst mismatch, a steep generated slope meeting flat real
  ground, not on offset copies of similar ground.
