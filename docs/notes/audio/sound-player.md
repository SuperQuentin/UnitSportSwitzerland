# Sound player (`--sounds`)

- `<godot> --path . -- --sounds` (VS Code task `run: sound player`): every synthesised game sound by
  category, with its waveform, length, peak and RMS. Left/Right (A/D, D-pad) step through a category
  and play the sound, Up/Down (W/S, D-pad) switch category, Enter/Space/A replays it (stops a loop),
  -/+ the volume (starts at -12 dB), a click on the list plays that sound. A dev tool: keyboard and
  pad only, no VR way (`src/Audio/SoundPlayer.cs`).
- `--sounds,check` (headless works) builds every sound without playing, prints
  `[sounds] RESULT: ok`, FAILED on a NaN, a clipped (> 1.0001) or a silent one. About 390 sounds.
- **No list to keep** (like the model viewer, `avatar/model-viewer`): static `SfxBank` and
  `AudioStreamWav` members of any `UnitSport` class are found by reflection, a bank as one entry per
  variant, under the declaring class as category; a stream that is a bank's variant or the cache
  field of a property is listed once. A sound with arguments gets a set: tag a static method
  `[SoundShowcase("Category")]` (`SoundShowcaseAttribute.cs`) returning
  `IEnumerable<(string Category, string Name, Func<float[]> Make)>`, built lazily on selection. Sets so
  far: engines through every voice, surfaces, occasions, the steamer, bird songs (`Soundcheck.cs`,
  `AmbienceDsp`). Put a new set next to its builder, over the registry or enum.
- **Rule: a new sound is in the player.** A new static bank or loop shows by itself; a sound built per
  call (a surface, a species, a voice) needs a set. Sounds that live only in a node's `_Ready` (a
  radio, a CD, a live stream) are not here.
