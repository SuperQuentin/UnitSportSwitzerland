# The globe's geomorph: no pop between levels (#1656)

The globe (`World/Globe/Globe`) splits a tile into its four children when a drawn cell would cover more than
`SplitPixels` (6) pixels: a tile of drawn cell `c` splits nearer than `c * focal / SplitPixels`, so its parent splits
nearer than twice that. Before #1656 a split or a merge swapped the ground at once, and skirts a third of a cell deep
hid the cracks between levels.

- **Each vertex knows where its parent draws it** (`Globe.Morph`, CUSTOM0 as four floats): the offset in the tile's
  anchor frame from its own point to the parent's surface there (an even vertex: the parent's height at that lattice
  point; an odd one: the middle of the parent's edge; an odd-odd one: the middle of the parent's quad diagonal
  (i, j)-(i+1, j+1), as the parent triangulates), and its drawn cell in `w`. Two tiles sharing an edge read the same
  parent points there, so the morph never opens a seam. Only tiles drawn at `Cells` (64) cells (not the far leaves at
  stride 16, not tiles built from their ancestors alone). A parent the pyramid lacks (past its levels: the shipped base
  alone, the far fill) is taken as `Synthesise` draws it, from its own ancestors (`Globe.ParentOf`).
- **The shader** (`shaders/common/geomorph.gdshaderinc`, in `globe.gdshader` and in the terrain body under
  `globe_morph`, set on the earth globe's terrain material only): `k = smoothstep(1.3 S, 1.9 S, distance)` with
  `S = cell * world_globe_morph.x` (focal / split pixels, written by `Globe.Plan` on change); a tile is its parent's
  shape before its parent merges it. `world_globe_morph.y` (the far leaves' band, `FarLeafM`): no morph within it (tiles
  there split for the leaves, not for their size), eased over 30 % more.
- **A late child grows in**: a child that comes after the split was due (loading, streaming) starts in its parent's
  shape and grows into its own over `GrowS` (0.4 s): `instance uniform globe_young`, 1 when it replaces its parent
  (`Globe.Grow`, judged against the visibility before the plan), stepped each frame (`GrowStep`).
- **Skirts stay as a crack guard**, a tenth of a cell (`SkirtCells`): two levels can still meet outside the morph's range
  where tiles split for drawn leaves (#1800).
- The CPU replica for the probe: `Globe.DrawnSurfaceAt` (the deepest visible tile, morphed, its lake's surface over it).
- `--globeclimb FROM,TO,SECONDS` (`World/GlobeClimb`): an exponential climb over the start looking down 60°, 7 x 7
  points across the view (laid again every 30 % of climb; the deepest level read 0.8 altitudes ahead, since the player's body keeps the near
  leaves under the nadir), each frame's change in height over the ellipsoid where a
  point's level changed, in screen pixels; `--nomorph` turns the morph and the grow-in off. Changes within one tile (the
  morph's own motion, the lakes' surfaces hidden at 15 km) are not pops.
