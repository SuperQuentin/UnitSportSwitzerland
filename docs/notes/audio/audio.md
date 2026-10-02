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
  bus reverb (indoors/tunnel/forest/valley/high). Open air is dry (wet 0): the slap-back rays tilt
  ~11° up and a valley needs 3 of 4 hits, or every hillside read as a hall. `ReverbZones.Enclosure`
  (0 open .. 1 indoors) also scales the PS1 voice's built-in SPU tail. `Ambience` (volume setting): cowbells on pasture
  700-2450 m, a Farnell bubble brook near watercourses, per-forest bird species seeded by tile,
  church bells on the hour at towns < 1.5 km (local clock — the game has none), alpine rockfall.
  Check: `<godot> --headless --path . -- --soundcheck test_output/soundcheck` writes every bank variant and a 7 s
  rev sweep per voice × profile as WAV, `[soundcheck] RESULT: ok`, FAILED on NaN or clipping.
- **The water's sounds (#380)**, rendered as the game plays them into `water_*.wav` (with
  `--water-sounds` only those): the swim banks (splash, stroke, gasp), `WadeBank`, `HullSlapBank`,
  the steamer's paddles at a quarter, half and full shaft (resampled at `PaddlePitch`), a whistle
  blast (`WhistleShape` per frame) and its engine from STOP to full ahead and back to slow through
  each voice. **Nobody can hear them here, so judge them by numbers**: peak, RMS, crest, clipped
  samples, DC, spectral centroid and band shares, spectral peaks (pitch), the envelope's beat
  rate (autocorrelation) and its depth (10th to 90th percentile of a 20 ms RMS envelope), and a
  waveform + spectrogram PNG to look at: `python tools/soundstats.py test_output/sound [png dir]`
  (numpy, scipy, matplotlib: a scratch venv). What it caught: the steam engine (a petrol model at 3 Hz: a 2 dB dip in a roar),
  the paddles' beat at low speed, the whistle's dead start and stop, a gasp with a quarter of its
  energy under 100 Hz (wind on a microphone). A steam engine is `EngineProfile.Steam`: puffs of
  hiss through the funnel comb, the chip voices gated by the puff (`EngineFrame.SteamGate`).
