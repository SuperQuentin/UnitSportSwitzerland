# Radio sparkles: beat-synced glints on a playing radio (#387)

- **What**: star glints (gold, cyan, pink, white) rise and twinkle round a radio while it plays,
  burst outward and grow on every beat, and step their colours round the palette each beat. On the
  world radio (`RadioBody`), the radio in a figure's hand (`HeldItemVisual.StepSparkles`, not the
  first-person viewmodel) and on the back (`FootPlayer.Back`). Fade in 0.4 s, out 0.7 s.
- **The beat** is the caller's `RadioBody.BeatOf` on the shared clock, the same as the bounce and
  the dancers: in time on every screen, nothing on the wire. World radio: only while its speaker
  really plays (not while the CD downloads); hand/back: while the stack has a `RadioPlay`.
- **Why so cheap**: no `GPUParticles3D` (compute pass + buffers per emitter) or `CPUParticles3D`
  (simulation per frame). `RadioSparkles` (a `MeshInstance3D`) shares one static mesh of 40 quads
  and one `ShaderMaterial` across every radio; `shaders/radio_sparkles.gdshader` derives each glint
  from `TIME` and two per-quad hashes (UV2), the `ps1_snowfall` approach, in the radio's local space
  (so the floating origin does not matter). Per radio: one additive draw call, `Visible = false`
  while silent, `VisibilityRangeEnd` 40 m, a `CustomAabb` because the shader moves the vertices.
  Per frame: `on` / `pulse` / `beat` instance uniforms through static `StringName`s, each only when
  it changed; no allocation.
- **Tuning**: `rise`/`size` uniforms in the shader, spawn ring in `RadioSparkles.SharedMesh`,
  pulse decay `Exp(-phase * 6)` in `Step`.
- **Check**: `tools/sparklecheck.sh` (tier full), i.e. `<godot> --path . -- --sparklecheck --world fixture` (windowed: nothing is built
  headless): a radio stood before the camera plays the chess type beat; sparkles at full while
  playing, gone after Stop; writes `test_output/sparkles_beat.png` and `sparkles_offbeat.png`.
