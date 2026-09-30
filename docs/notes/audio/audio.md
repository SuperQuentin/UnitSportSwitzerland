# Audio

- **Audio** (`src/Audio/`, all synthesised, no audio files; every player routes to the `Sfx` bus
  that `SfxBus.Ensure()` creates at boot). **One-shots vary**: `SfxBank` bakes 6-8 variants per sound,
  each drawing its own parameter jitter before any noise, and `Pick` never repeats the last one and
  adds ±4% pitch / ±1.5 dB. `SfxSynth.XxxBank` for the classics; the old properties return variant 0.
  The trick chime climbs a major pentatonic on a streak (`PlayerFeel.PlayChime`).
  **Engines are live** (`EngineSynth`, an `AudioStreamGenerator` filled from `_Process`): firing
  pulses at rpm·cyl/2 with uneven cylinders, a fixed exhaust comb resonance (it must NOT follow rpm —
  that is what a pitch-shifted loop got wrong), intake noise, prop beat, decel crackle; the
  turboshaft does blade slap + whine. Its per-sample `EngineFrame` goes to an `IChipVoice` picked by
  `GameSettings.EngineVoice` (Settings dropdown, `--voice realistic|ps1|nes|sid|genesis`): PS1 SPU
  (real 28-sample ADPCM codec + gaussian playback, default), NES 2A03 (period-register pitch steps,
  short-mode LFSR, nonlinear mixer), C64 SID 6581 (hard sync + resonant filter), YM2612 (4-op FM,
  feedback, PSG noise, 9-bit ladder DAC). `Surfaces.At` picks footstep/landing banks and ski hiss
  colour from road-under-feet then cover (`Surfaces.Origin` must be set); `ReverbZones` eases the
  bus reverb (indoors/tunnel/forest/valley/high). `Ambience` (volume setting): cowbells on pasture
  700-2450 m, a Farnell bubble brook near watercourses, per-forest bird species seeded by tile,
  church bells on the hour at towns < 1.5 km (local clock — the game has none), alpine rockfall.
  Check: `<godot> --headless --path . -- --soundcheck test_output/soundcheck` writes every bank variant and a 7 s
  rev sweep per voice × profile as WAV, non-zero exit on NaN or clipping.
