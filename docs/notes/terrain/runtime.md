# Runtime

- **Runtime** (`src/`): `Terrain/ChunkManager` streams LOD rings around anchors (workers
  build arrays, main thread commits meshes against a ms budget and collision one piece a
  frame, see `perf-collision-commits`);
  `HeightMapShape3D` collision on d≤1 tiles (CollisionShape3D scale derived from
  `ChunkFormat.SpacingM`, currently 1 m — see the road-collision-merge entry above for how it
  gets blended toward road height, and a second small `ConcavePolygonShape3D` body for bridge
  decks);
  `shaders/ps1_terrain.gdshader` does vertex snap, flat shading via derivatives, palette
  bands, Bayer dither, fog. Fidelity knobs: `rendering/scaling_3d/scale` (0.75) and the
  per-shader `snap_resolution` (640x480) — lower both for a grittier PS1 look, raise for
  crispness.
  **The 3D renders at the window's real pixels times `Scaling3DScale`; the UI lays out at a
  fixed 1152x648 and is scaled to the window** (`display/window/stretch/mode = "canvas_items"`,
  `aspect = "expand"`: a non-16:9 window gets a wider or taller view, no bars, no distortion).
  It was `viewport` until #306: everything drew into a fixed 1152x648 that was upscaled, so
  "100 %" was never native and the non-PS1 styles looked blurry. PS1 keeps its fixed low
  resolution through the render scale (`core/settings`), so its dither stays as coarse; `--shot`, the
  probes' PNGs and the video exporter write frames at the window's size. LOD rings live in `LodPolicy` (stride 1 underfoot, out to 40 m quads at d=9). `Core/Main` boots ServerWorld (`--server` /
  dedicated_server feature) or ClientWorld (`--connect host[:port]`, offline otherwise).
