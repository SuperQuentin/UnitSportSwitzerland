# The earth fill stands on the globe's pyramid (#1678)

Since #1656 the pyramid is a chain (`net/pyramid-streaming`): the fill stands on this copy's own full-lattice tiles
(`PyramidSource.Base`), and online on the server's (`ServerBase`, after its index; none: the generator's, as the server).

Under `--frame earth` the generated fill (the leaves within 40 km of the start that the region does not hold) is built
on the best ground the game has, the `cube_global` pyramid, as the user decided on #1678. The flat world's fill is
untouched (`FallbackChunkSource` / `ProceduralWorld.Blend` on LV95).

- **`Leaves/PyramidBase`** reads the pyramid at a face's global lattice point (bilinear in the finest level holding the
  point with all four samples, NaN-safe) and its lakes (`CubeManifest.Lakes`, a height within 2 cm of one is that lake).
  Godot-free, thread-safe. `Open` refuses a pyramid whose finest level is under 9 (the shipped levels 0-2 know less than
  the generator's own 500 m relief) and `res://`: then the fill is the generator's, blended over the old 3 km band.
- **`Leaves/PyramidFill`** moves a generated leaf onto it: `h = P + D(G - L)`, `L` the generator's ground smoothed over
  ~150 m on a window of the global lattice (samples every 32 vertices, two box passes; a lake's surface, not its bed),
  `D` a soft clamp to 12 m. Only its short relief rides on the pyramid: `G` less its 500 m macro relief kept the
  valleys it presses flat along its own rivers, which are not the pyramid's, and dug a trench 50-70 m deep along the
  Lavaux shore (the additive trap of `blend-convex-not-additive`). The detail is dropped over the generator's own
  lakes and fades in over 150 m from the pyramid's shore. A pyramid lake is water at its level over a bed
  `WaterBed.OpenDepth(distance to the shore)`; the distances are exact (a distance transform) on a window 305 m round
  the leaf, so neighbours agree; beyond that the bed stays at ~71 m. Cover is Water there and the generator's lake
  (weight over 0.05: its ragged rim too) becomes Open; trees and buildings in a lake are dropped, everything else
  (roads too) moves with the ground. Forest, rock and snow stay the generator's: the pyramid's colour past the leaves
  is Blue Marble's 500 m pixel, which reads a dark shore as forest (#1677), and the generator's classes go with its trees.
- **The sea (#1808)**: `cube_global` holds the sea at its surface, the geoid (`CubeGlobal`: `max(H, 0) + N`, EGM2008), so
  the fill read flat ground there and laid the generator's grass, trees and buildings over every ocean. `PyramidBase.SeaSurface`
  (`Egm2008.Undulation`: a 0.25° grid of ETOPO 2022's geoid embedded in `TerrainFormat`, 0.3 m at the 99th percentile,
  1.2 m at worst) makes pyramid ground within `SeaTolM` (1.5 m) of the geoid sea, at the geoid's level, through the
  lakes' path: the shelving bed, Water cover, trees and buildings dropped, the shore's detail fade, the globe's far fill
  water. `SeaOver` reads the geoid at a window's four corners and interpolates (it bends by millimetres over a few km),
  not once per vertex. A leaf that is all sea has no roads; a coastal one keeps them, the drowned ones on the bed (as in
  a lake; cutting road records by segment would break their cross-references). `tools/farfillcheck.sh` fails where the
  pyramid's ground is sea and the leaves are dry: the open-sea spot `AT=426597,-230351` (west of Morocco) failed at all
  1,681 points before; the coast at Agadir `909680,-472439`, the Pacific `-13287549,7940380`. The globe's own sea
  (its tiles' water, the coast past the fill) is #1802's. Low coasts within 1.5 m of the sea read as sea.
- **`LeafBlend` on the pyramid**: no 3 km band; a 400 m convex hand-over `h = (1 - F) g + F T`. `T` is the real ground
  continued from each block's nearest point (slope carried over 60 m) near the seam, then the pyramid plus the real
  ground's difference from it (`Resid`, a lake's bed among it). A lake bed is carried into the fill's lake only: on
  the fill's land the real lake counts as the pyramid's surface. Pyramid lake water keeps its level whatever the bed.
- **The globe** draws a fill leaf's lakes from its own water (`EarthLeaves.WaterOf`, a dry one when it has none): the
  made leaf's, or the pyramid's lakes over the far heights (`GeneratedLeaves.Far`). A fill leaf still draws no bed lake
  (`beds:` off, #1669). With the pyramid's lake levels known, ground that is none of them drops the colour image's
  water weight. Under a tile's lake an edge vertex sinks as deep as its neighbours on its side, and the skirts hang from
  the sunk ground: as a shore 3 m under the surface, every leaf edge across a lake drew a pale line.
- **The pyramid past the leaves is the generator's own macro relief since #1746** (the 500 m Swiss relief, no longer
  ETOPO's 1.8 km cells; `tools/cube-tiles`), so `P` and the generator's `G` share their broad shapes there. The fill still
  drops `G`'s relief between ~150 m and 500 m and clamps the rest to 12 m: far mountains past the region read smoother
  than flat's generated ones (#1748, a change to #1678's rule for the user to decide).
- **The pyramid had to be fixed first** (`tools/cube-tiles`): past B4's leaves its level 13 blended into ETOPO in 16
  rings, each a step (13-40 m east of Lavaux, the fill drew them as terraces), and ETOPO's 1.8 km cells left 1.5-2 km
  of ground 3-35 m over Léman below Lavaux. `--cube-swiss` now blends with a continuous field and snaps swissTLM3D's
  big lakes (`--tlm`).

- **Walls in the fill are the pyramid's (#1778)**: the fill leaves agree on every edge, so a wall along a straight line
  (a V where two meet) is the pyramid stepping, where `PyramidBase` falls back to a coarser level that disagrees with
  the finer one. `tools/fillseamscheck.sh` (BlendCheck `--fill-seams`, `--why` prints every level at the worst step)
  measures it at the playtest's spots: steps between samples, the leaves' edges, the far ground, and the coarse levels'
  mean miss along the leaves' edges (the globe's ledge, #1779). The fix is in the pyramid's build (`tools/cube-tiles`).

Checks: `PyramidFillTests` (sampler, distance transform, fill, edges, hand-over); `BlendCheck --leaf-blend --at
2547200,1147800 --side 5 --pyramid D:/UnitSportSwitzerland/cube_global --out DIR` (the Lavaux staircase: seam p99
34 cm against 34 on the old blend, edges 0.01 cm, the shaded relief with water in blue).
