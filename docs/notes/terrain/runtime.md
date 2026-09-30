# Runtime

- **Runtime** (`src/`): `Terrain/ChunkManager` streams LOD rings around anchors (workers
  build arrays, main thread commits ≤2 meshes + 1 collision per frame);
  `HeightMapShape3D` collision on d≤1 tiles (CollisionShape3D scale derived from
  `ChunkFormat.SpacingM`, currently 1 m — see the road-collision-merge entry above for how it
  gets blended toward road height, and a second small `ConcavePolygonShape3D` body for bridge
  decks);
  `shaders/ps1_terrain.gdshader` does vertex snap, flat shading via derivatives, palette
  bands, Bayer dither, fog. Fidelity knobs: `rendering/scaling_3d/scale` (0.75) and the
  per-shader `snap_resolution` (640x480) — lower both for a grittier PS1 look, raise for
  crispness.
  **The game renders at a fixed 1152x648 and Godot scales that to the window**
  (`display/window/stretch/mode = "viewport"`). Not `canvas_items`: there the 3D renders at the
  window's real size, so the same world is sharper on a 1440p monitor than on a laptop and the
  PS1 look drifts with the display. `aspect = "expand"` means a non-16:9 window gets a wider or
  taller view rather than black bars — measured 1152x648 in a 1920x1080 window and 1152x864 in an
  800x600 one, no distortion either way. Side effect worth knowing: `--shot` and the video
  exporter now always write frames at the internal resolution, whatever the window is. LOD rings live in `LodPolicy` (stride 1 underfoot, out to 40 m quads at d=9). `Core/Main` boots ServerWorld (`--server` /
  dedicated_server feature) or ClientWorld (`--connect host[:port]`, offline otherwise).
