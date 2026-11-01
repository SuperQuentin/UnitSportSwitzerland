# Building pads: the grading stage (#1419)

- **What**: `GradeStage` (tools/TerrainPreprocessor) writes a pad under every building into the tile heights, so the coarse
  rings, the collision and the roads draped afterwards all see it. Maths in `BuildingPad` (plain C#, `BuildingPadTests`).
- **Level** (`BuildingExtractor.OutlineGround`): terrain sampled every 1.5 m along the bottom edges of the solid's wall faces
  (not the 3x3 box), `BuildingPad.ChooseGround` picks the candidate of least cost: cut/fill up to the cap, what the cap leaves
  counted once for a cut and twice for a fill (a gap under the base is the visible one). Base = level - `FoundationDepth` (0.8 m,
  semantics as before). A solid with no wall bottoms falls back to the median of the box.
- **Pad**: inside the outline and `Shoulder` (3 m) past it each cell moves to the level by at most `Cap` (1.5 m); past that the same
  change fades with a smoothstep over `Apron` (3 m). The shoulder keeps the ground 0.8 m in front of any wall on the sill level
  and flat for a 2-4 m lattice cell; the rest of the height difference stays with the #1417 footings and plinths.
- **Overlaps**: a cell takes the average of the pads' changes weighted by `a` (1 + 6 x depth up to 1 m inside, the fade
  outside) and scaled by the strongest pad's weight, so a footprint dominates its own ground and two neighbours meet without a
  step. A pure function of the heights and the footprints in a fixed order (owner tile E, N, building index).
- **Outline**: wall triangles' edges on the base (both ends within 5 cm of `MinY`); the loops must close (every endpoint twice) for
  the even-odd inside test, else the plan of the roof/floor triangles tells inside. Under-construction sites get no pad.
- **Order** (`Preprocessor.RunFeatures`): `Restore` (raw heights back) -> buildings extracted for every batch (seated on raw
  heights) -> `GradeStage.Run` -> roads per batch (draped on the graded grids) -> network -> town ground
  (`town-ground`, undone before the pads) -> cover -> water. `--no-grade` skips
  Restore and Run (the buildings then run first, then the roads). A cover-only or tlm-less pass grades after its building loop.
- **Tile edges**: a pad reaches into the neighbour. Each tile is graded from the footprints of its own and its 8 neighbours'
  `.bldg` in one global order, from the original height of the shared vertex (bit-identical in both tiles), and the requested
  tiles' neighbours are graded with them, so shared border vertices come out identical (`TerrainBuild.VerifySeams` runs at the
  end; `BuildingPadTests` checks a pad across a border). Limit: a tile terrain-rebuilt on its own beside graded neighbours has
  its seam copied from them; rebuild terrain with `--force` over a region, then run the feature pass.
- **Re-runnable**: `<temp>/grade/E_N.grd` holds (cell, original, graded) of every cell changed; `Restore` puts a cell back only if
  it still holds the graded value, so a later lake bed is not undone and a second run never stacks a pad (byte-identical reruns).
- **Format**: no version bump. The `.terr` layout, the quantisation and the manifest are unchanged and so is what a height means (metres
  of ground); only values near buildings differ. The content hashes of the manifest change, so a client refetches the tiles.
  `.terrc`, the manifest's min/max and `horizon.bin` are refreshed by the stage.
- **Cost** (3 tiles + 24 neighbours): 3-4 s for the grade pass; the building extraction runs once per batch and the roads one more
  grid load per batch.
- Measures and numbers: `terrain/ground-contact`.
