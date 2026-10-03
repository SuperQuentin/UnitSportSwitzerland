# House instruments and the running tap (#433)

- `Audio/InstrumentSynth` bakes every sound on first use (cached per instrument and note), with
  `Dsp.Rate` mono:
  - **Piano**: six partials, each stretched by `sqrt(1 + 0.0006 k²)` (real strings are slightly
    inharmonic), amplitude `1/k^1.3`. High partials and high notes die faster. 3.2 s at C2 down to
    1.2 s at C7, plus a short low-passed noise "hammer".
  - **Keyboard**: an electric piano. A sine phase-modulated by its octave for the first ~0.2 s,
    plus fast-fading bell partials (2× and 7.1×) and a 4.5 Hz tremolo.
  - **Drums** (`DrumNames`): `SfxRecipe`s.
    - kick: a sine sliding 150→48 Hz with drive, and a noise click;
    - snare: a 230→180 Hz triangle plus high-passed noise;
    - closed and open hats: high-passed noise with a short and a long decay;
    - two toms: sine slides;
    - crash: a 2.2 s noise wash;
    - ride: a square ping with an inharmonic sine and a wash.
  - **Water**: a 2 s `Dsp.Loop` of noise band-passed at 350–3200 Hz, with two slow level wobbles.
- Notes run C2..C7 (`LowNote`/`HighNote`). The keys start at C4, `NoteOf` = 60 + 12·octave + key.
  Each note is its own tuned sample: no pitch shifting, so timbre and decay stay right in every octave.
- `Audio/PropSpeaker`: an `AudioStreamPlayer3D` on the Sfx bus behind `Hearing` (muffled by walls,
  heard across linked doors, silent in the street).
  - An instrument plays an `AudioStreamPolyphonic` with 24 voices; `Strike` adds a stream with
    velocity → dB.
  - A tap plays its loop.
  - Set `Position` before `AddChild` (`hearing`).
- `--soundcheck` writes `piano_<midi>`, `epiano_<midi>` (C2..C7), `drum_<name>` and `tap_water` WAVs.
