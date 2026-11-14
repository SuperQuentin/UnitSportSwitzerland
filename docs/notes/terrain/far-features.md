# Far features: what stands on the ground, baked into the pyramid (#1657)

Under `--frame earth` the globe past the leaves drew bare ground: a streaming client (whose far leaves are never furnished,
`earth-far-view`) saw no town, forest or street beyond the near leaves. Phase 1 bakes them into the pyramid's levels 9-13.

- **Format** (`TerrainFormat/CubeFarFeatures`, `CUBF` entries, `.cubf` bundles at each level's `bundleLevel`, the
  manifest's `levels[].features` = 64): per vertex of the 65² lattice the globe draws, forest, built and paved shares
  (0-255) and the skin (0.25 m steps, up to 63.75 m), four Brotli'd byte planes. `At(i, j)` samples it bilinearly.
- **Bake**: `--cube-features --cube-leaves DIR[,DIR] --cube-out PYRAMID [--cube-levels 9-13] [--jobs N]`
  (`TerrainPreprocessor/CubeFeatureStage`). Each leaf to 64² cells (9.4 m): canopy share from the cover (`CanopyShare`), the
  trees' heights where records stand else the class's (`DefaultTreeH`), paved share from the cover, roofs (up-facing
  triangles over the solid's base, split under a quarter cell, at the cell of their middle) with their height over the
  ground, capped at 40 m, none over NaN ground. Level 13: each vertex the mean of the four cells round it; coarser: the
  tent mean of the nine finer vertices (a vertex on a tile edge read from whichever finer tile exists). Tiles: every
  ancestor of a leaf and its eight neighbours. The `.cubf` files are made whole each run.
  Nov 14 2026, B4's 2,412 leaves on a copy on E: (`E:\UnitSportSwitzerland\scratch\far-features`): 16 s, peak 1.8 GB,
  3.0 MB (levels 13-9: 557, 174, 59, 25, 11 tiles).
- **Chain and stream** (`net/pyramid-streaming`): `PyramidSource.Features`, section 13 (`FeaturesSection`), the index's
  bit 1 (`PyramidIndex.FeaturesBit`) beside the colour's bit 0, the stamp over the features too.
- **Globe** (`Globe.Build`'s `stand`): each vertex raised by its skin (the water and colour tests keep the ground's
  height); the palette's forest weight at least the baked canopy; roofs (`RoofColour`) and paving blended over
  (`StandColour`); normals from the bare ground (`groundPos`: the skin is lit by its facets, never painted as rock); the
  morph's parent with its own skin (`ParentOf`); a leaf-level tile takes its parent's, bilinear (`FromParent`). Far
  leaves (local, within `FarLeafM`) keep their real trees and boxes, no skin.
- Found on the way: roofs measured against a border leaf's NaN ground stood white spikes 64 m high (the skin's cap) and
  the skin's slopes read as rock; both fixed as above.
- Checks: `FarFeaturesTests`; `tools/pyramidstreamcheck.sh` requires section-13 entries in B's cache when the server's
  pyramid has features; shots: a client with no local leaves at Geneva, 1.5-15 km.
- **Phase 2 (#1908): the far band for a streaming client.** A real leaf the server holds is a far leaf (its cover, its
  trees as billboards, its buildings as boxes within `FarBuildingsM`) within `Globe.StreamedFarLeafM` (4 km) of the
  camera; the ground splits down to it there (`FarSplit`, `HoldsRemoteReal`, built again when the leaf index lands).
  `LeafStore.Sections` asks a remote leaf section by section (heights, water, cover, trees, buildings), never the whole
  entry; each section caches. Not under Low data (`StreamsFarBand`). Past 4 km the baked features draw it.
  `tools/farbandstreamcheck.sh`: a client with the leaves and one streaming them, the same shots over Geneva; at Geneva
  (Nov 14, `--buildingrings 3`) the cover, water and trees cost under 2 MB for ~75 leaves, the heights 22 MB (the globe's
  leaf level already fetched most of them).
- Phase still to come (#1657): a detail-distance setting that cuts the furnished leaves on weak devices, the baked levels
  taking over.
