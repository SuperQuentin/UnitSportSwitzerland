# Camera sightline cut

- **Camera sightline cut** (`ChunkManager.SetSightlineCut`, driven per frame by
  `PlaybackCamera.UpdateSightlineCut`): when a ray from the camera to the runner's head hits
  something, a corridor along that segment is **dissolved with a Bayer-dither `discard`** in
  `ps1_building`/`ps1_tree`. Dither, not alpha: those shaders are `unshaded` with no blend mode,
  the trees are one MultiMesh sharing a single material so per-instance transparency is not
  available, and a dithered dissolve is already this renderer's visual language. One uniform write
  reaches the whole streamed world however much has loaded since. The radius is **ramped**, never
  switched - a corridor that snaps open reads as geometry popping out of existence - and it settles
  to exactly 0 so the shaders take their disabled branch. **Terrain and roads are deliberately
  excluded**: dissolving ground opens a hole straight through to the sky, which looks far worse
  than the hillside it was hiding, and a camera behind a ridge is already rejected outright by
  `ShotContext.CanSee` before the shot is committed. A road lying flat never occludes anything.
