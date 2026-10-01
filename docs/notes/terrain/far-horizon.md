# Far horizon

- **Far horizon** (`HorizonLayer`, `horizon.bin`, `tools/TerrainFormat/HorizonFormat.cs`): every tile
  decimated to a **100 m lattice** (11x11 samples, 242 B) and packed into ONE region-wide file by the
  preprocessor's `--horizon` pass (also run at the end of a full build and of `--coarse`; 6,699 tiles ->
  1.6 MB in 3.6 s, read from the `.terrc` companions since stride 10 divides 100). The client reads it
  once and meshes **10x10 km blocks** (101x101 verts, altitude-band colours only — no cover) out to
  `HorizonKm` (setting, default 60, `--horizon <km>`), one block committed per frame. It is what makes
  the world an open map: the snow peaks 50 km down the Rhône are on screen for ~100 draws. The blocks
  use their **own** `ps1_terrain` material instance carrying a **per-tile coverage texture**
  (`HorizonLayer.SetCovered`, one R8 texel per km tile over the region, sampled by world XZ) inside
  which the shader `discard`s — so the lattice never shows through a tunnel floor or a carved portal,
  and the tile material (`use_cover = false`) never pays for the test. A texel is set the frame a
  tile's surface mesh commits and cleared when it unloads. **Not a ring rectangle**: that was tried
  first and was wrong both ways — it dropped the horizon under tiles that had not arrived yet (a
  visible gap while loading) and kept it under tiles that had left the rings but not yet unloaded. Streamed like `places.json` (`AssetKind.Horizon`, fetched during `ClientTerrainSync`,
  `HorizonReceived` -> `HorizonLayer.Reload`). Camera `Far` follows it (`GameSettings.CameraFar`).
- **Streaming the blocks**: up to `ProcessorCount / 2` (2..8) blocks mesh at once, a freed slot is
  refilled the next frame (the old 0.5 s tick with 4 slots trickled 169 blocks in over 21 s), and
  commits run within a 3 ms frame budget. The loading screen waits for all of them (~2 s).
- **Generated horizon cache** (`FallbackChunkSource`, `chunk_cache/generated-horizon-<mvid>.bin`): the
  ~44,000 generated 100 m tiles cost ~7 s of every core per launch, so they are saved after the
  first. Named after the build's module id (any rebuild regenerates; old files are deleted on save);
  blended tiles are keyed by a hash of the real knots they blend on. Cache everything or nothing that
  touches the generator: with the plain tiles cached, the 180 blended ones alone took ~4 s, because
  the plain pass is what used to warm the generator's lattice cache. Warm launch: 0.3 s.
