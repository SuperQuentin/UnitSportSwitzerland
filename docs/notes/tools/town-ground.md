# Town ground, road profiles, reach fade (#1419 phase 2)

Three changes after the pads (`building-pads`), measured with `--groundcheck` (`terrain/ground-contact`).

## Smoothed road profile (`RoadProfile`, called in `RoadExtractor.Emit`)

- Each draped piece (one TLM line clipped to a tile) is averaged along its arc length with a Gaussian before `LimitGrade`.
  Sigma by class (`RoadProfile.SigmaFor`, window about 4 sigma): motorway, expressway, railway 10 m; ramp, major, road
  7.5; minor, lane 6; track 5; paths, links, squares 4; rack/funicular 4, mountain paths 3. None for bridges, tunnels,
  stairs, aerial, walls, watercourses and bisses.
- **Fixed points**: the piece's ends (a line end, a tile seam) and every interior vertex where another line ends
  (`_allEnds`, the junctions on it) keep their draped height exactly. Each run between them is extended by an odd
  reflection about its end points (an end point weighs a whole span), so the average there is the end itself and a
  straight grade passes unchanged (`TownGroundTests`). Junction heights, tile seams and the bridge/tunnel
  `ApproachDelta` (measured at the ends) are as before. Smoothing per tile piece, not per line: a line across a batch
  border would otherwise see the drape only on one side.
- **Cap**: at most `MaxLower` 0.5 m under the drape (a crest; a cliff-lip path is not pulled down the face) and
  `MaxRaise` 1.0 m over it (a sag). RoadGen's `RoadHeights` offsets apply on top as before.

## Reach fade of the road blend (`TerrainMeshBuilder.RoadBlend`, `ReachWeight`)

- `RoadBlend` carries a `Weight` per cell: 1 under a road and on its slopes, a smoothstep to 0 over the last
  `ReachFadeM` (2.5 m, at most half the reach: 4.5..7 m for roads, 1.5..3 m for minor ones and pad rims). `BlendedHeight`
  = ground + weight * (clamp - ground). The 1:1 / 2:3 limits hold in full inside; a slope that missed the ground at its
  reach no longer ends in a step. A cell takes the strongest road's weight; every other pass (caps, walls, islands,
  bores, pavement) weighs 1. Walls are unchanged (they free the ground past the face).

## Joint ground solve in towns (`TownGround` maths, `TownGroundStage` I/O)

- **Order** (`Preprocessor.RunFeatures`): restore town ground, restore pads -> buildings -> pads -> roads -> road
  network -> **town ground** -> cover -> water. Runs with the pads (`--no-grade` skips both). `--town-ground` reruns it
  alone on a built region (undo, then solve). Records in `<temp>/townground/E_N.grd`, the pads' format and undo rule.
- **Inputs** per tile, over a window of the tile plus `Margin` (152) cells into its 8 neighbours: the heights after the
  pads (each neighbour's, held in memory before its column is written), the runtime blend of every final `.road`
  (`ComputeRoadBlend`, linked into the preprocessor), footprints of the `.bldg` files, `UrbanField.Weight`.
- **Constraints**: road cells (blend `Lo == Hi`: ribbon with sidewalks, caps, corners) fixed at `Lo` (the collision
  floor) in the solve, **their stored height not changed** (writing the visual height there floated and buried doors
  beside sidewalks: Lausanne stride-1 doors 3/8 -> 8/20); footprint + `FootprintShoulder` (2 m) keep the pad height and
  win over a road cell; kept at their height: 5 m round a retaining wall's face, bridges and tunnels + 3 m, streams + 2 m,
  one-sided blend cells (over a bore, under an island).
- **Free cells**: within `Reach` 15 m of a road or footprint cell, urban weight > 0. Laplace by red-black SOR,
  `Iterations` 60, omega 1.8, from the original heights (last sweep under 7 mm on the test tiles). Change = urban weight
  x smoothstep over the outer `FadeM` 5 m of the reach x (solution - original), capped at 2 m. Rural ground untouched.
- **Seams**: a sweep carries a value 2 cells, so a cell depends on nothing past 2 x 60 + 15 cells; with the same inputs
  in both windows (fixed tile order for a shared vertex, global parity) the border vertices come out bit-identical
  (`TownGroundTests`, `VerifySeams` OK). Rebuilds are byte-identical.
- **Shoulder** tuning (Lausanne, strip steps / doors stride 1, 2, 4, 10): 0 m 65,751 / 7/21 5/17 11/27 362/232 (gaps
  2,592); 1 m 75,321 / 5/8 2/11 12/20 355/184; 1.5 m 77,226 / 2/8 2/7 11/19 353/181; 2 m 78,926 / 3/8 2/7 11/17 350/181;
  3 m 81,405. The shoulder is half the town (footprint + 2 m), and Lausanne's strip below the #1418 build needs it gone.
- **Cost**: 0.40-0.45 s per tile (window build and solve; window 1,305 squared, ~360k free cells per tile here, mean
  over the 27 test tiles, towns and mountain mixed), 6-7 s for the 27 at 16 jobs, plus the stage's input loads.
  Nationwide (~42,000 tiles) at most ~5 h of CPU, ~20 min at 16 jobs; a tile with no road or footprint in town still
  builds its window, so that is an upper bound.
- Not done: biharmonic smoothing (the membrane is C0 at constraint edges), door spots as constraints (they are planned
  in game code on the stored heights).
