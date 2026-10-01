# Trees

- **Trees** come from three sources into one `.trees` file, told apart by `Kind`:
  **0/1** random scatter in the wooded classes, **2** orchards and nurseries planted on a
  world-anchored grid (6 m / 4 m — a random scatter at the same density reads as scrub, the
  rows are the point), **3** `tlm_bb_einzelbaum`, TLM's 11.5 M *surveyed* single trees in
  villages, along field boundaries and beside roads. All three respect
  `CoverStage.BuildRoadMask`. `ChunkNode.SetTrees` builds **two** MultiMeshes per tile
  because a MultiMesh carries exactly one mesh: a cone for 0/1 and a bipyramid crown on a
  trunk for 2/3, since a broadleaf drawn as a spire turns an orchard into a plantation.
  **The cone stands on a trunk too** (foliage from 28% of the height): it used to reach down to
  15%, so a 25 m spruce was 13 m wide at eye level and walled in every forest trail. Same apex and
  ring, so the canopy from above is unchanged; 20 triangles per conifer instead of 10, no measured
  frame-time cost. **`ps1_tree` also dissolves trees close in front of ANY camera** (`near_fade*`
  uniforms: fully gone inside 1.5 m, solid past 8 m, only within the forward cone so the
  periphery still encloses you) with the same Bayer discard as the sightline cut. It is pure
  view-space shader maths, so on foot, the ride chase cam, free fly and the replay cameras all
  get it with no C# per frame.
  Current region: ~40 M trees, of which 0.87 M planted and 1.65 M surveyed.
  Beyond 220 m each tree is drawn as a ray-traced billboard instead (`styles/tree-lod`).
